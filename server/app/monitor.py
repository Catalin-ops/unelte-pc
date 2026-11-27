#!/usr/bin/env python3
# Sanatate PC - server de monitorizare (DigitalSecureIt)
# Primeste rapoartele agentilor, le organizeaza pe institutii, afiseaza panoul si trimite raportul zilnic.
# Doar biblioteca standard Python 3: nu are nevoie de pachete instalate cu pip.
#
#   python3 monitor.py serve            porneste serverul web (in spatele lui Caddy)
#   python3 monitor.py raport           trimite acum raportul zilnic (--fara-email il afiseaza doar)
#   python3 monitor.py raport-programat trimite raportul o data pe zi, dupa ora din setari (din timer-ul systemd)
#   python3 monitor.py parola           schimba parola de administrator
#   python3 monitor.py test-email       trimite un e-mail de test
import base64, configparser, datetime as dt, hashlib, hmac, html, json, os, secrets, smtplib, sqlite3, sys, threading, time
from email.message import EmailMessage
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

VERSION = "1.0"
CONFIG_FILE = os.environ.get("SPC_CONFIG", "/etc/sanatate-monitor/config.ini")
MAX_BODY = 1024 * 1024
LEVEL_TXT = {2: "Problemă", 1: "Atenție", 0: "Bun", -1: "Info"}

# ------------------------------------------------------------------ configurare
cfg = configparser.ConfigParser(interpolation=None)
def load_config():
    cfg.read(CONFIG_FILE, encoding="utf-8")
    for s in ("server", "smtp", "raport"):
        if not cfg.has_section(s): cfg.add_section(s)
load_config()
def C(sec, key, default=""): return cfg.get(sec, key, fallback=default)
def save_config():
    tmp = CONFIG_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f: cfg.write(f)
    os.chmod(tmp, 0o600); os.replace(tmp, CONFIG_FILE)

DATA_DIR = C("server", "date", "/var/lib/sanatate-monitor")
DB_FILE = os.path.join(DATA_DIR, "monitor.db")
SECRET = C("server", "secret", "").encode() or secrets.token_bytes(32)

def now(): return int(time.time())
def local(ts): return dt.datetime.fromtimestamp(ts) if ts else None
def fmt_time(ts):
    if not ts: return "niciodată"
    d = local(ts); today = dt.date.today()
    if d.date() == today: return "azi, " + d.strftime("%H:%M")
    if d.date() == today - dt.timedelta(days=1): return "ieri, " + d.strftime("%H:%M")
    return d.strftime("%d.%m.%Y %H:%M")
def offline_hours(): return int(C("raport", "ore_offline", "26") or 26)

# ------------------------------------------------------------------ baza de date
_db_lock = threading.Lock()
def db():
    c = sqlite3.connect(DB_FILE, timeout=30)
    c.row_factory = sqlite3.Row
    c.execute("PRAGMA foreign_keys=ON")
    return c

SCHEMA = """
CREATE TABLE IF NOT EXISTS orgs(
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, key TEXT UNIQUE NOT NULL, contact TEXT DEFAULT '',
  max_pcs INTEGER DEFAULT 10, expires TEXT DEFAULT '', active INTEGER DEFAULT 1, report_contact INTEGER DEFAULT 0,
  notes TEXT DEFAULT '', created INTEGER);
CREATE TABLE IF NOT EXISTS machines(
  id INTEGER PRIMARY KEY, org_id INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE, machine_id TEXT NOT NULL,
  machine TEXT, label TEXT, model TEXT, os TEXT, laptop INTEGER, app TEXT,
  first_seen INTEGER, last_seen INTEGER, score INTEGER, level INTEGER, verdict TEXT, last_report INTEGER,
  UNIQUE(org_id, machine_id));
CREATE TABLE IF NOT EXISTS reports(
  id INTEGER PRIMARY KEY, machine_id INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
  received INTEGER, score INTEGER, level INTEGER, payload TEXT);
CREATE INDEX IF NOT EXISTS reports_m ON reports(machine_id, received);
CREATE TABLE IF NOT EXISTS alerts(
  id INTEGER PRIMARY KEY, machine_id INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
  reason TEXT, text TEXT, created INTEGER, emailed INTEGER DEFAULT 0);
CREATE INDEX IF NOT EXISTS alerts_m ON alerts(machine_id, reason, created);
"""
def init_db():
    os.makedirs(DATA_DIR, exist_ok=True)
    with db() as c: c.executescript(SCHEMA)

# ------------------------------------------------------------------ parola si sesiune
def hash_password(pw, salt=None):
    salt = salt or secrets.token_bytes(16)
    h = hashlib.scrypt(pw.encode(), salt=salt, n=2**14, r=8, p=1, dklen=32)
    return "scrypt$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(h).decode()
def check_password(pw, stored):
    try:
        _, s, h = stored.split("$")
        return hmac.compare_digest(hash_password(pw, base64.b64decode(s)).split("$")[2], h)
    except Exception: return False
def sign(v): return hmac.new(SECRET, v.encode(), hashlib.sha256).hexdigest()
def make_session():
    exp = str(now() + 12 * 3600); nonce = secrets.token_hex(8)
    v = exp + "." + nonce
    return v + "." + sign(v)
def valid_session(tok):
    try:
        exp, nonce, s = tok.split(".")
        return hmac.compare_digest(sign(exp + "." + nonce), s) and int(exp) > now()
    except Exception: return False
def csrf_for(tok): return sign("csrf:" + tok)[:32]

_login_fails = {}
def login_blocked(ip):
    t, n = _login_fails.get(ip, (0, 0))
    return n >= 5 and now() - t < 900
def login_failed(ip):
    t, n = _login_fails.get(ip, (0, 0))
    _login_fails[ip] = (now(), (n + 1) if now() - t < 900 else 1)

# ------------------------------------------------------------------ e-mail
def send_email(to, subject, html_body, text_body):
    host = C("smtp", "server")
    if not host or not to: return "SMTP neconfigurat"
    msg = EmailMessage()
    msg["Subject"] = subject
    msg["From"] = C("smtp", "de_la") or C("smtp", "utilizator")
    msg["To"] = to
    msg.set_content(text_body)
    msg.add_alternative(html_body, subtype="html")
    port = int(C("smtp", "port", "587") or 587)
    try:
        if port == 465:
            s = smtplib.SMTP_SSL(host, port, timeout=30)
        else:
            s = smtplib.SMTP(host, port, timeout=30); s.starttls()
        if C("smtp", "utilizator"): s.login(C("smtp", "utilizator"), C("smtp", "parola"))
        s.send_message(msg); s.quit()
        return None
    except Exception as e:
        return str(e)

# ------------------------------------------------------------------ primirea rapoartelor
def org_by_key(c, key):
    o = c.execute("SELECT * FROM orgs WHERE key=?", (key,)).fetchone()
    if not o or not o["active"]: return None, "Codul instituției nu e valid sau instituția e dezactivată."
    if o["expires"] and o["expires"] < dt.date.today().isoformat(): return None, "Licența de monitorizare a instituției a expirat."
    return o, None

def clean_str(v, n=300): return str(v if v is not None else "")[:n]

def ingest(key, data):
    if not isinstance(data, dict) or not data.get("machine_id"): return 400, "Date incomplete."
    with _db_lock, db() as c:
        o, err = org_by_key(c, key)
        if not o: return 403, err
        mid = clean_str(data["machine_id"], 120)
        m = c.execute("SELECT * FROM machines WHERE org_id=? AND machine_id=?", (o["id"], mid)).fetchone()
        if not m:
            n = c.execute("SELECT COUNT(*) FROM machines WHERE org_id=?", (o["id"],)).fetchone()[0]
            if n >= o["max_pcs"]: return 403, "Instituția a atins numărul de calculatoare din licență (%d)." % o["max_pcs"]
            c.execute("INSERT INTO machines(org_id, machine_id, first_seen) VALUES(?,?,?)", (o["id"], mid, now()))
            m = c.execute("SELECT * FROM machines WHERE org_id=? AND machine_id=?", (o["id"], mid)).fetchone()
        prev = c.execute("SELECT payload FROM reports WHERE machine_id=? ORDER BY received DESC LIMIT 1", (m["id"],)).fetchone()
        score = int(data.get("score") or 0); level = int(data.get("level") if data.get("level") is not None else -1)
        payload = json.dumps(data, ensure_ascii=False)
        c.execute("INSERT INTO reports(machine_id, received, score, level, payload) VALUES(?,?,?,?,?)", (m["id"], now(), score, level, payload))
        c.execute("""UPDATE machines SET machine=?, label=?, model=?, os=?, laptop=?, app=?, last_seen=?, score=?, level=?, verdict=? WHERE id=?""",
                  (clean_str(data.get("machine"), 80), clean_str(data.get("label"), 80), clean_str(data.get("model"), 160), clean_str(data.get("os"), 200),
                   1 if data.get("laptop") else 0, clean_str(data.get("app"), 20), now(), score, level, clean_str(data.get("verdict"), 200), m["id"]))
        new_alerts = find_alerts(json.loads(prev["payload"]) if prev else None, data)
        fresh = []
        for reason, text in new_alerts:
            seen = c.execute("SELECT 1 FROM alerts WHERE machine_id=? AND reason=? AND created>?", (m["id"], reason, now() - 86400)).fetchone()
            if seen: continue
            c.execute("INSERT INTO alerts(machine_id, reason, text, created) VALUES(?,?,?,?)", (m["id"], reason, text, now()))
            fresh.append(text)
        name = data.get("label") or data.get("machine") or mid
    if fresh and C("raport", "alerte", "da") == "da":
        threading.Thread(target=email_alert, args=(o["name"], name, fresh), daemon=True).start()
    return 200, "ok"

def find_alerts(prev, cur):
    out = []
    prev_l = {p.get("id"): p.get("l") for p in (prev or {}).get("parts", []) if isinstance(p, dict)}
    for p in cur.get("parts", []):
        if not isinstance(p, dict): continue
        if p.get("l") == 2 and prev_l.get(p.get("id")) != 2:
            note = (p.get("notes") or [""])[0] if isinstance(p.get("notes"), list) and p.get("notes") else p.get("sum", "")
            out.append(("part:" + clean_str(p.get("id"), 60), "%s: %s%s" % (clean_str(p.get("name"), 80), clean_str(p.get("st"), 60), (" — " + clean_str(note, 200)) if note else "")))
    day = cur.get("day") or {}
    if day.get("bsod"): out.append(("bsod", "%d ecran(e) albastru(e) în ultimele 24 de ore" % day["bsod"]))
    if day.get("disk_bad"): out.append(("disk_bad", "Sectoare defecte raportate de disc în ultimele 24 de ore (%d)" % day["disk_bad"]))
    if day.get("whea"): out.append(("whea", "Erori hardware grave (WHEA) în ultimele 24 de ore (%d)" % day["whea"]))
    if day.get("ntfs"): out.append(("ntfs", "Sistem de fișiere corupt (NTFS) în ultimele 24 de ore"))
    return out

def email_alert(org, pc, lines):
    to = C("raport", "catre")
    base = C("server", "adresa")
    text = "Alertă — %s, %s\n\n%s\n\n%s" % (org, pc, "\n".join("- " + l for l in lines), base)
    body = page_mail("Alertă: %s" % pc, "<p style='color:#8e948d;margin:0 0 12px'>%s</p><ul>%s</ul><p><a href='%s' style='color:#d6f27a'>Deschide panoul</a></p>" % (
        e(org), "".join("<li>%s</li>" % e(l) for l in lines), e(base)))
    err = send_email(to, "Alertă: %s — %s" % (pc, org), body, text)
    if err: log("alerta nu a fost trimisa: " + err)

def log(msg): print(time.strftime("%Y-%m-%d %H:%M:%S"), msg, flush=True)
def e(v): return html.escape(str(v if v is not None else ""), quote=True)

# ------------------------------------------------------------------ pagini
CSS = """
:root{color-scheme:dark;--bg:#08090a;--s1:#111313;--s2:#1b1e1d;--ln:#272b29;--ink:#eef0ea;--mut:#8e948d;--lime:#d6f27a;--ok:#7fd39b;--amb:#f0c93e;--red:#f0555c}
*{box-sizing:border-box;min-width:0}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.5 "Segoe UI",system-ui,sans-serif}
a{color:var(--ink)}header{display:flex;gap:16px;align-items:center;flex-wrap:wrap;padding:14px max(4vw,16px);border-bottom:1px solid var(--ln)}
header b{font-weight:600}header nav{display:flex;gap:14px;margin-left:auto;flex-wrap:wrap}header nav a{color:var(--mut);text-decoration:none}header nav a:hover{color:var(--ink)}
main{padding:24px max(4vw,16px);max-width:1280px;margin:0 auto}h1{font-size:30px;font-weight:500;margin:0 0 6px;line-height:1.1}h2{font-size:20px;font-weight:500;margin:28px 0 10px}
.mut{color:var(--mut)}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(270px,1fr));gap:12px;margin-top:18px}
.card{background:var(--s1);border:1px solid var(--ln);border-radius:16px;padding:16px;text-decoration:none;display:block}.card:hover{border-color:#3a3f3c}
.card h3{margin:0 0 8px;font-size:18px;font-weight:500}.stats{display:flex;gap:14px;flex-wrap:wrap;font-size:14px}
.dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:6px;background:#5f6862}.l0{background:var(--ok)}.l1{background:var(--amb)}.l2{background:var(--red)}.off{background:#5f6862;outline:2px solid var(--red)}
.chip{display:inline-block;font-size:13px;padding:2px 10px;border-radius:20px;border:1px solid var(--ln);color:var(--mut);white-space:nowrap}
.c0{color:var(--ok)}.c1{color:var(--amb);border-color:rgba(240,201,62,.4)}.c2{color:var(--red);border-color:rgba(240,85,92,.45)}
table{width:100%;border-collapse:collapse;font-size:15px}th,td{text-align:left;padding:10px 8px;border-bottom:1px solid var(--ln);vertical-align:top}th{color:var(--mut);font-weight:400;font-size:14px}
td.num{text-align:right;white-space:nowrap}.scroll{overflow-x:auto}
form.box,.box{background:var(--s1);border:1px solid var(--ln);border-radius:16px;padding:18px;margin-top:14px}
label{display:block;font-size:14px;color:var(--mut);margin:10px 0 4px}input,select,textarea{width:100%;font:inherit;font-size:16px;color:var(--ink);background:#0d0f0f;border:1px solid var(--ln);border-radius:10px;padding:10px}
button,.btn{font:inherit;min-height:44px;padding:0 16px;border-radius:12px;border:1px solid var(--ln);background:var(--s2);color:var(--ink);cursor:pointer;text-decoration:none;display:inline-flex;align-items:center}
button.pri{background:var(--lime);color:#111;border-color:var(--lime);font-weight:600}button.del{color:var(--red)}
.row{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px}.msg{padding:10px 14px;border-radius:10px;background:#1c2a12;margin-top:12px}.err{background:#2a0e0e}
code,pre{font-family:ui-monospace,Consolas,monospace;font-size:13px;background:#0d0f0f;border:1px solid var(--ln);border-radius:8px;padding:2px 6px;overflow-wrap:anywhere}pre{padding:12px;white-space:pre-wrap}
ul{padding-left:20px}@media(max-width:600px){table{font-size:14px}h1{font-size:26px}}
"""

def page(title, body, logged=True):
    nav = "<nav><a href='/'>Instituții</a><a href='/alerte'>Alerte</a><a href='/setari'>Setări</a><a href='/iesire'>Ieșire</a></nav>" if logged else ""
    return ("<!DOCTYPE html><html lang='ro'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>"
            "<title>%s · Monitorizare</title><style>%s</style></head><body><header><b>Sănătate PC</b><span class='mut'>Monitorizare</span>%s</header><main>%s</main></body></html>"
            % (e(title), CSS, nav, body))

def page_mail(title, inner):
    return ("<!DOCTYPE html><html><body style='margin:0;background:#08090a;color:#eef0ea;font:15px/1.5 Segoe UI,Arial,sans-serif'>"
            "<div style='max-width:720px;margin:0 auto;padding:24px'><div style='color:#8e948d;font-size:13px'>Sănătate PC · Monitorizare</div>"
            "<h1 style='font-size:24px;font-weight:500;margin:6px 0 16px'>%s</h1>%s</div></body></html>" % (e(title), inner))

def status_of(m):
    if not m["last_seen"] or now() - m["last_seen"] > offline_hours() * 3600: return "off"
    return "l%d" % max(0, m["level"] if m["level"] is not None else 0)

def org_stats(c, org_id):
    ms = c.execute("SELECT * FROM machines WHERE org_id=?", (org_id,)).fetchall()
    st = {"total": len(ms), "l0": 0, "l1": 0, "l2": 0, "off": 0}
    for m in ms: st[status_of(m)] += 1
    return st

def problems(payload):
    try: d = json.loads(payload)
    except Exception: return [], {}
    return [p for p in d.get("parts", []) if isinstance(p, dict) and (p.get("l") or 0) >= 1], d.get("day") or {}

def day_text(day):
    bits = []
    if day.get("bsod"): bits.append("%d ecran albastru" % day["bsod"])
    if day.get("power_loss"): bits.append("%d opriri neașteptate" % day["power_loss"])
    if day.get("disk_bad"): bits.append("%d sectoare defecte" % day["disk_bad"])
    if day.get("disk_retry"): bits.append("%d erori de citire disc" % day["disk_retry"])
    if day.get("whea"): bits.append("%d erori hardware" % day["whea"])
    if day.get("ntfs"): bits.append("NTFS corupt")
    if day.get("sys_errors"): bits.append("%d erori în jurnalul System" % day["sys_errors"])
    return ", ".join(bits)

def view_home(c, msg=""):
    orgs = c.execute("SELECT * FROM orgs ORDER BY name COLLATE NOCASE").fetchall()
    cards = []
    for o in orgs:
        s = org_stats(c, o["id"])
        cards.append("<a class='card' href='/institutie/%d'><h3>%s%s</h3><div class='stats'><span>%d calculatoare</span>"
                     "<span><i class='dot l0'></i>%d</span><span><i class='dot l1'></i>%d</span><span><i class='dot l2'></i>%d</span><span><i class='dot off'></i>%d nu au raportat</span></div>%s</a>"
                     % (o["id"], e(o["name"]), "" if o["active"] else " <span class='chip'>dezactivată</span>", s["total"], s["l0"], s["l1"], s["l2"], s["off"],
                        ("<p class='mut' style='margin:8px 0 0;font-size:14px'>Licență până la %s</p>" % e(o["expires"])) if o["expires"] else ""))
    body = "<h1>Instituții</h1><p class='mut'>Verde: în regulă · galben: de urmărit · roșu: problemă · gri: nu a raportat în ultimele %d ore.</p>%s<div class='grid'>%s</div>" % (
        offline_hours(), msg, "".join(cards) or "<p class='mut'>Nicio instituție încă. Adaug-o mai jos.</p>")
    body += org_form(None)
    return page("Instituții", body)

def org_form(o):
    v = (lambda k, d="": e(o[k]) if o else d)
    return ("<form class='box' method='post' action='%s'><h2 style='margin-top:0'>%s</h2><input type='hidden' name='csrf' value='{CSRF}'><div class='row'>"
            "<div><label>Numele instituției</label><input name='name' required value='%s'></div>"
            "<div><label>Număr maxim de calculatoare</label><input name='max_pcs' type='number' min='1' value='%s'></div>"
            "<div><label>Licență valabilă până la (gol = fără limită)</label><input name='expires' type='date' value='%s'></div>"
            "<div><label>E-mail contact instituție</label><input name='contact' type='email' value='%s'></div></div>"
            "<label><input type='checkbox' name='report_contact' value='1' style='width:auto' %s> Trimite raportul zilnic și persoanei de contact</label>"
            "%s<label>Observații</label><textarea name='notes' rows='2'>%s</textarea><p><button class='pri'>%s</button></p></form>"
            % ("/institutie/%d/salveaza" % o["id"] if o else "/institutie/noua", "Setările instituției" if o else "Instituție nouă",
               v("name"), v("max_pcs", "10"), v("expires"), v("contact"), "checked" if o and o["report_contact"] else "",
               ("<label><input type='checkbox' name='active' value='1' style='width:auto' %s> Activă (dacă o dezactivezi, calculatoarele ei nu mai pot trimite)</label>" % ("checked" if o["active"] else "")) if o else "",
               v("notes"), "Salvează" if o else "Adaugă instituția"))

def view_org(c, oid, msg=""):
    o = c.execute("SELECT * FROM orgs WHERE id=?", (oid,)).fetchone()
    if not o: return None
    ms = c.execute("SELECT * FROM machines WHERE org_id=? ORDER BY COALESCE(NULLIF(label,''), machine) COLLATE NOCASE", (oid,)).fetchall()
    rows = []
    for m in ms:
        last = c.execute("SELECT payload FROM reports WHERE machine_id=? ORDER BY received DESC LIMIT 1", (m["id"],)).fetchone()
        probs, day = problems(last["payload"]) if last else ([], {})
        st = status_of(m)
        rows.append("<tr><td><i class='dot %s'></i><a href='/pc/%d'>%s</a><div class='mut' style='font-size:13px'>%s</div></td><td>%s</td><td class='num'>%s</td><td>%s</td><td>%s</td></tr>"
                    % (st, m["id"], e(m["label"] or m["machine"] or "?"), e(m["machine"] if m["label"] else m["model"]), e(fmt_time(m["last_seen"])),
                       e(m["score"]) if m["score"] is not None else "—",
                       ", ".join("<span class='c%d'>%s</span>" % (min(2, p.get("l", 0)), e(p.get("name"))) for p in probs) or "<span class='mut'>—</span>",
                       e(day_text(day)) or "<span class='mut'>—</span>"))
    s = org_stats(c, oid)
    base = C("server", "adresa")
    body = ("<p><a href='/' class='mut'>← Instituții</a></p><h1>%s</h1><p class='mut'>%d din %d calculatoare · %d cu probleme · %d de urmărit · %d nu au raportat</p>%s"
            "<div class='scroll'><table><thead><tr><th>Calculator</th><th>Ultimul raport</th><th class='num'>Scor</th><th>Componente cu probleme</th><th>Erori în ultimele 24 h</th></tr></thead><tbody>%s</tbody></table></div>"
            % (e(o["name"]), s["total"], o["max_pcs"], s["l2"], s["l1"], s["off"], msg,
               "".join(rows) or "<tr><td colspan='5' class='mut'>Niciun calculator încă. Instalează monitorizarea pe calculatoarele instituției cu codul de mai jos.</td></tr>"))
    body += ("<div class='box'><h2 style='margin-top:0'>Instalare pe calculatoare</h2><p>În Sănătate PC Pro: <b>Monitorizare</b> → adresa serverului <code>%s</code> și codul instituției:</p>"
             "<pre>%s</pre><p class='mut'>Pentru multe calculatoare deodată (ca administrator, cu licența Pro lângă aplicație):</p><pre>SanatatePC.exe --monitor-install --server=%s --cheie=%s --eticheta=\"Nume PC\"</pre>"
             "<form method='post' action='/institutie/%d/cheie-noua' style='display:inline'><input type='hidden' name='csrf' value='{CSRF}'><button>Generează alt cod</button></form>"
             " <span class='mut' style='font-size:14px'>Codul vechi nu mai merge, iar calculatoarele deja instalate trebuie configurate din nou.</span></div>"
             % (e(base), e(o["key"]), e(base), e(o["key"]), oid))
    body += org_form(o)
    body += ("<form class='box' method='post' action='/institutie/%d/sterge'><input type='hidden' name='csrf' value='{CSRF}'><h2 style='margin-top:0'>Ștergere</h2>"
             "<p class='mut'>Șterge instituția, calculatoarele și tot istoricul lor. Scrie numele instituției ca să confirmi.</p><input name='confirm' placeholder='%s'><p><button class='del'>Șterge definitiv</button></p></form>" % (oid, e(o["name"])))
    return page(o["name"], body)

def view_pc(c, mid, msg=""):
    m = c.execute("SELECT m.*, o.name AS org FROM machines m JOIN orgs o ON o.id=m.org_id WHERE m.id=?", (mid,)).fetchone()
    if not m: return None
    last = c.execute("SELECT * FROM reports WHERE machine_id=? ORDER BY received DESC LIMIT 1", (mid,)).fetchone()
    d = json.loads(last["payload"]) if last else {}
    parts = "".join("<tr><td><i class='dot l%d'></i>%s</td><td><span class='chip c%d'>%s</span></td><td>%s%s</td></tr>"
                    % (max(0, min(2, p.get("l", 0))), e(p.get("name")), max(0, min(2, p.get("l", 0))), e(p.get("st") or LEVEL_TXT.get(p.get("l"), "")), e(p.get("sum")),
                       "".join("<div class='mut' style='font-size:14px'>• %s</div>" % e(n) for n in (p.get("notes") or [])[:4]))
                    for p in d.get("parts", []) if isinstance(p, dict))
    recs = "".join("<li><b>%s</b> <span class='chip c%d'>%s</span>%s%s</li>" % (e(r.get("a")), 2 if r.get("p") == 1 else 1 if r.get("p") == 2 else 0,
                   "Urgent" if r.get("p") == 1 else "Recomandat" if r.get("p") == 2 else "Opțional", ("<div class='mut'>%s</div>" % e(r.get("d"))) if r.get("d") else "",
                   ("<div class='mut'>Piesă: %s</div>" % e(r.get("part"))) if r.get("part") else "") for r in d.get("recs", []) if isinstance(r, dict))
    day = d.get("day") or {}
    top = ", ".join("%s (%s)" % (e(t[0]), e(t[1])) for t in (day.get("top") or []) if isinstance(t, list) and len(t) == 2)
    hist = c.execute("SELECT date(received,'unixepoch','localtime') AS d, MIN(score) AS s, MAX(level) AS l FROM reports WHERE machine_id=? GROUP BY d ORDER BY d DESC LIMIT 30", (mid,)).fetchall()
    hrows = "".join("<tr><td>%s</td><td class='num'>%s</td><td><span class='chip c%d'>%s</span></td></tr>" % (e(h["d"]), e(h["s"]), max(0, min(2, h["l"] or 0)), LEVEL_TXT.get(h["l"], "")) for h in hist)
    body = ("<p><a href='/institutie/%d' class='mut'>← %s</a></p><h1>%s</h1><p class='mut'>%s · %s · %s · ultimul raport: %s · scor %s</p>%s"
            % (m["org_id"], e(m["org"]), e(m["label"] or m["machine"]), e(m["machine"]), e(m["model"]), e(m["os"]), e(fmt_time(m["last_seen"])), e(m["score"]), msg))
    body += "<h2>%s</h2>" % e(d.get("verdict", ""))
    body += "<div class='scroll'><table><thead><tr><th>Componentă</th><th>Stare</th><th>Detalii</th></tr></thead><tbody>%s</tbody></table></div>" % (parts or "<tr><td colspan='3' class='mut'>Fără date.</td></tr>")
    body += "<h2>Erori în ultimele 24 de ore</h2><p>%s</p>%s" % (e(day_text(day)) or "<span class='mut'>Nicio eroare importantă.</span>", ("<p class='mut'>Surse: %s</p>" % top) if top else "")
    if recs: body += "<h2>Ce trebuie făcut</h2><ul>%s</ul>" % recs
    body += "<h2>Istoric (ultimele 30 de zile)</h2><div class='scroll'><table><thead><tr><th>Zi</th><th class='num'>Scor minim</th><th>Cea mai gravă stare</th></tr></thead><tbody>%s</tbody></table></div>" % hrows
    body += ("<form class='box' method='post' action='/pc/%d/sterge'><input type='hidden' name='csrf' value='{CSRF}'><p class='mut' style='margin-top:0'>Scoate calculatorul din monitorizare (eliberează un loc din licență). "
             "Dacă agentul e încă instalat pe el, va apărea din nou la următorul raport.</p><button class='del'>Scoate calculatorul</button></form>" % mid)
    return page(m["label"] or m["machine"] or "Calculator", body)

def view_alerts(c):
    rows = c.execute("""SELECT a.*, m.label, m.machine, m.id AS mid, o.name AS org FROM alerts a JOIN machines m ON m.id=a.machine_id JOIN orgs o ON o.id=m.org_id
                        ORDER BY a.created DESC LIMIT 200""").fetchall()
    tr = "".join("<tr><td>%s</td><td>%s</td><td><a href='/pc/%d'>%s</a></td><td>%s</td></tr>" % (e(fmt_time(r["created"])), e(r["org"]), r["mid"], e(r["label"] or r["machine"]), e(r["text"])) for r in rows)
    return page("Alerte", "<h1>Alerte</h1><p class='mut'>Problemele grave apărute, cel mult o dată pe zi pentru fiecare problemă și calculator.</p><div class='scroll'><table><thead><tr><th>Când</th><th>Instituție</th><th>Calculator</th><th>Problemă</th></tr></thead><tbody>%s</tbody></table></div>" % (tr or "<tr><td colspan='4' class='mut'>Nicio alertă.</td></tr>"))

def view_settings(msg=""):
    body = ("<h1>Setări</h1>%s<form class='box' method='post' action='/setari'><input type='hidden' name='csrf' value='{CSRF}'><h2 style='margin-top:0'>Raportul zilnic</h2><div class='row'>"
            "<div><label>Trimite raportul la (e-mail)</label><input name='catre' type='email' value='%s'></div>"
            "<div><label>Ora raportului</label><input name='ora' type='time' value='%s'></div>"
            "<div><label>Calculator „nu a raportat” după (ore)</label><input name='ore_offline' type='number' min='3' value='%s'></div>"
            "<div><label>Alerte imediate pentru probleme grave</label><select name='alerte'><option value='da' %s>Da</option><option value='nu' %s>Nu</option></select></div></div>"
            "<p><button class='pri'>Salvează</button></p></form>"
            "<form class='box' method='post' action='/setari/test'><input type='hidden' name='csrf' value='{CSRF}'><h2 style='margin-top:0'>E-mail</h2><p class='mut'>Server SMTP: %s · de la: %s. Datele SMTP se schimbă pe server cu <code>sudo sanatate-monitor smtp</code>.</p><button>Trimite un e-mail de test</button></form>"
            "<form class='box' method='post' action='/setari/parola'><input type='hidden' name='csrf' value='{CSRF}'><h2 style='margin-top:0'>Parola panoului</h2><div class='row'><div><label>Parola actuală</label><input name='vechi' type='password' required autocomplete='current-password'></div>"
            "<div><label>Parola nouă (minim 10 caractere)</label><input name='noua' type='password' required minlength='10' autocomplete='new-password'></div></div><p><button>Schimbă parola</button></p></form>"
            "<p class='mut'>Server de monitorizare %s</p>"
            % (msg, e(C("raport", "catre")), e(C("raport", "ora", "18:00")), e(offline_hours()), "selected" if C("raport", "alerte", "da") == "da" else "", "selected" if C("raport", "alerte", "da") != "da" else "",
               e(C("smtp", "server") or "neconfigurat"), e(C("smtp", "de_la") or C("smtp", "utilizator") or "—"), VERSION))
    return page("Setări", body)

def view_login(err=""):
    return page("Autentificare", "<form class='box' method='post' action='/login' style='max-width:420px;margin:40px auto'><h1>Autentificare</h1>%s<label>Parola</label><input name='parola' type='password' required autofocus autocomplete='current-password'><p><button class='pri'>Intră</button></p></form>"
                % (("<div class='msg err'>%s</div>" % e(err)) if err else ""), logged=False)

def ok_msg(t): return "<div class='msg'>%s</div>" % e(t)
def err_msg(t): return "<div class='msg err'>%s</div>" % e(t)

# ------------------------------------------------------------------ raportul zilnic
def daily_report(send=True):
    with db() as c:
        c.execute("DELETE FROM reports WHERE received < ?", (now() - 90 * 86400,))
        c.execute("DELETE FROM alerts WHERE created < ?", (now() - 90 * 86400,))
        orgs = c.execute("SELECT * FROM orgs WHERE active=1 ORDER BY name COLLATE NOCASE").fetchall()
        total_bad = total_off = 0
        sections_html = []; sections_txt = []; per_org = []
        for o in orgs:
            ms = c.execute("SELECT * FROM machines WHERE org_id=? ORDER BY COALESCE(NULLIF(label,''), machine) COLLATE NOCASE", (o["id"],)).fetchall()
            if not ms: continue
            rows = []; txt = []
            for m in ms:
                st = status_of(m)
                last = c.execute("SELECT payload FROM reports WHERE machine_id=? ORDER BY received DESC LIMIT 1", (m["id"],)).fetchone()
                probs, day = problems(last["payload"]) if last else ([], {})
                dtxt = day_text(day) if st != "off" else ""
                if st == "l0" and not (day.get("bsod") or day.get("disk_bad") or day.get("whea") or day.get("power_loss")): continue
                name = m["label"] or m["machine"]
                if st == "off":
                    total_off += 1; what = "nu a mai raportat din %s" % fmt_time(m["last_seen"])
                else:
                    if st == "l2": total_bad += 1
                    what = "; ".join("%s: %s" % (p.get("name"), p.get("st")) for p in probs)
                    if dtxt: what = (what + "; " if what else "") + dtxt
                color = {"off": "#8e948d", "l2": "#f0555c", "l1": "#f0c93e"}.get(st, "#7fd39b")
                rows.append("<tr><td style='padding:8px;border-bottom:1px solid #272b29'><span style='color:%s'>●</span> %s</td><td style='padding:8px;border-bottom:1px solid #272b29;color:#c9cdc5'>%s</td></tr>" % (color, e(name), e(what)))
                txt.append("  - %s: %s" % (name, what))
            s = org_stats(c, o["id"])
            head = "%s — %d calculatoare: %d în regulă, %d de urmărit, %d cu probleme, %d nu au raportat" % (o["name"], s["total"], s["l0"], s["l1"], s["l2"], s["off"])
            block = "<h2 style='font-size:18px;font-weight:500;margin:22px 0 4px'>%s</h2><p style='color:#8e948d;margin:0 0 8px'>%s</p>%s" % (
                e(o["name"]), e(head.split(" — ", 1)[1]),
                ("<table style='width:100%%;border-collapse:collapse;font-size:14px'>%s</table>" % "".join(rows)) if rows else "<p style='color:#7fd39b;margin:0'>Totul în regulă.</p>")
            sections_html.append(block); sections_txt.append(head + ("\n" + "\n".join(txt) if txt else "\n  Totul în regulă."))
            per_org.append((o, block, head + ("\n" + "\n".join(txt) if txt else "")))
    today = dt.date.today().strftime("%d.%m.%Y")
    summary = ("%d cu probleme" % total_bad if total_bad else "fără probleme grave") + (", %d nu au raportat" % total_off if total_off else "")
    subject = "Raport zilnic monitorizare %s — %s" % (today, summary)
    base = C("server", "adresa")
    body = page_mail("Raport zilnic — %s" % today, "".join(sections_html) or "<p>Nicio instituție cu calculatoare încă.</p>") .replace(
        "</div></body>", "<p style='margin-top:24px'><a href='%s' style='color:#d6f27a'>Deschide panoul</a></p></div></body>" % e(base))
    text = subject + "\n\n" + "\n\n".join(sections_txt) + "\n\n" + base
    if not send: return subject, text
    err = send_email(C("raport", "catre"), subject, body, text)
    log("raport zilnic: " + ("trimis" if not err else "eroare: " + err))
    for o, block, t in per_org:
        if o["report_contact"] and o["contact"]:
            e2 = send_email(o["contact"], "Raport zilnic calculatoare %s — %s" % (o["name"], today), page_mail("Raport zilnic — " + o["name"], block), t)
            log("raport %s: %s" % (o["name"], "trimis" if not e2 else "eroare: " + e2))
    return subject, text

# ------------------------------------------------------------------ server HTTP
class H(BaseHTTPRequestHandler):
    server_version = "SanatateMonitor/" + VERSION
    def log_message(self, fmt, *a): pass

    def ip(self): return self.headers.get("X-Forwarded-For", self.client_address[0]).split(",")[0].strip()
    def cookie(self):
        for part in (self.headers.get("Cookie") or "").split(";"):
            k, _, v = part.strip().partition("=")
            if k == "spc": return v
        return ""
    def logged(self): return valid_session(self.cookie())

    def send(self, code, body, ctype="text/html; charset=utf-8", headers=None):
        if isinstance(body, str):
            body = body.replace("{CSRF}", csrf_for(self.cookie()) if self.logged() else "").encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "same-origin")
        self.send_header("Cache-Control", "no-store")
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)
    def redirect(self, to, cookie=None):
        h = {"Location": to}
        if cookie is not None: h["Set-Cookie"] = cookie
        self.send(303, b"", headers=h)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n > MAX_BODY: raise ValueError("prea mare")
        return self.rfile.read(n) if n else b""
    def form(self):
        f = parse_qs(self.body().decode("utf-8", "replace"), keep_blank_values=True)
        return {k: v[0] for k, v in f.items()}

    def do_GET(self):
        p = urlparse(self.path).path.rstrip("/") or "/"
        if p == "/sanatate": return self.send(200, "ok", "text/plain")
        if p == "/login": return self.send(200, view_login())
        if p == "/iesire": return self.redirect("/login", "spc=; Max-Age=0; Path=/; HttpOnly; Secure; SameSite=Lax")
        if not self.logged(): return self.redirect("/login")
        with db() as c:
            if p == "/": return self.send(200, view_home(c))
            if p == "/alerte": return self.send(200, view_alerts(c))
            if p == "/setari": return self.send(200, view_settings())
            parts = p.strip("/").split("/")
            if len(parts) == 2 and parts[1].isdigit():
                if parts[0] == "institutie": r = view_org(c, int(parts[1]))
                elif parts[0] == "pc": r = view_pc(c, int(parts[1]))
                else: r = None
                if r: return self.send(200, r)
        self.send(404, page("Negăsit", "<h1>Pagina nu există</h1>"))

    def do_POST(self):
        p = urlparse(self.path).path.rstrip("/")
        try:
            if p == "/api/v1/raport":
                key = (self.headers.get("X-Cheie-Institutie") or "").strip()
                try: data = json.loads(self.body().decode("utf-8"))
                except Exception: return self.send(400, "JSON invalid", "text/plain; charset=utf-8")
                code, msg = ingest(key, data)
                return self.send(code, msg, "text/plain; charset=utf-8")
            if p == "/login":
                if login_blocked(self.ip()): return self.send(429, view_login("Prea multe încercări. Așteaptă 15 minute."))
                f = self.form()
                if check_password(f.get("parola", ""), C("server", "parola_admin")):
                    _login_fails.pop(self.ip(), None)
                    return self.redirect("/", "spc=%s; Max-Age=43200; Path=/; HttpOnly; Secure; SameSite=Lax" % make_session())
                login_failed(self.ip())
                return self.send(401, view_login("Parolă greșită."))
            if not self.logged(): return self.redirect("/login")
            f = self.form()
            if not hmac.compare_digest(f.get("csrf", ""), csrf_for(self.cookie())): return self.send(403, page("Eroare", "<h1>Sesiune expirată</h1><p><a href='/'>Înapoi</a></p>"))
            return self.post_admin(p, f)
        except ValueError:
            return self.send(413, "Prea mare", "text/plain")

    def post_admin(self, p, f):
        parts = p.strip("/").split("/")
        with _db_lock, db() as c:
            if p == "/institutie/noua":
                name = f.get("name", "").strip()[:120]
                if not name: return self.send(400, view_home(c, err_msg("Scrie numele instituției.")))
                key = secrets.token_urlsafe(9).replace("-", "x").replace("_", "y").upper()
                cur = c.execute("INSERT INTO orgs(name, key, contact, max_pcs, expires, report_contact, notes, created) VALUES(?,?,?,?,?,?,?,?)",
                                (name, key, f.get("contact", "")[:200], max(1, int(f.get("max_pcs") or 10)), f.get("expires", "")[:10], 1 if f.get("report_contact") else 0, f.get("notes", "")[:1000], now()))
                return self.redirect("/institutie/%d" % cur.lastrowid)
            if len(parts) == 3 and parts[0] == "institutie" and parts[1].isdigit():
                oid = int(parts[1])
                if parts[2] == "salveaza":
                    c.execute("UPDATE orgs SET name=?, contact=?, max_pcs=?, expires=?, report_contact=?, active=?, notes=? WHERE id=?",
                              (f.get("name", "").strip()[:120] or "Fără nume", f.get("contact", "")[:200], max(1, int(f.get("max_pcs") or 10)), f.get("expires", "")[:10],
                               1 if f.get("report_contact") else 0, 1 if f.get("active") else 0, f.get("notes", "")[:1000], oid))
                    return self.send(200, view_org(c, oid, ok_msg("Salvat.")) or "")
                if parts[2] == "cheie-noua":
                    c.execute("UPDATE orgs SET key=? WHERE id=?", (secrets.token_urlsafe(9).replace("-", "x").replace("_", "y").upper(), oid))
                    return self.send(200, view_org(c, oid, ok_msg("Codul a fost schimbat.")) or "")
                if parts[2] == "sterge":
                    o = c.execute("SELECT name FROM orgs WHERE id=?", (oid,)).fetchone()
                    if o and f.get("confirm", "").strip() == o["name"]:
                        c.execute("DELETE FROM orgs WHERE id=?", (oid,))
                        return self.redirect("/")
                    return self.send(200, view_org(c, oid, err_msg("Numele scris nu se potrivește; nu am șters nimic.")) or "")
            if len(parts) == 3 and parts[0] == "pc" and parts[1].isdigit() and parts[2] == "sterge":
                m = c.execute("SELECT org_id FROM machines WHERE id=?", (int(parts[1]),)).fetchone()
                c.execute("DELETE FROM machines WHERE id=?", (int(parts[1]),))
                return self.redirect("/institutie/%d" % m["org_id"] if m else "/")
            if p == "/setari":
                ora = f.get("ora", "18:00")
                if len(ora) != 5 or ora[2] != ":": ora = "18:00"
                cfg.set("raport", "catre", f.get("catre", "").strip()[:200])
                cfg.set("raport", "ora", ora)
                cfg.set("raport", "ore_offline", str(max(3, int(f.get("ore_offline") or 26))))
                cfg.set("raport", "alerte", "da" if f.get("alerte") == "da" else "nu")
                save_config(); set_timer(ora)
                return self.send(200, view_settings(ok_msg("Salvat. Raportul pleacă zilnic la %s." % ora)))
            if p == "/setari/test":
                err = send_email(C("raport", "catre"), "Test — Sănătate PC Monitorizare", page_mail("E-mailul funcționează", "<p>Rapoartele vor ajunge la această adresă.</p>"), "E-mailul functioneaza.")
                return self.send(200, view_settings(ok_msg("E-mail trimis la %s." % C("raport", "catre")) if not err else err_msg("Nu am putut trimite: " + err)))
            if p == "/setari/parola":
                if not check_password(f.get("vechi", ""), C("server", "parola_admin")): return self.send(200, view_settings(err_msg("Parola actuală e greșită.")))
                if len(f.get("noua", "")) < 10: return self.send(200, view_settings(err_msg("Parola nouă trebuie să aibă cel puțin 10 caractere.")))
                cfg.set("server", "parola_admin", hash_password(f["noua"])); save_config()
                return self.send(200, view_settings(ok_msg("Parola a fost schimbată.")))
        self.send(404, page("Negăsit", "<h1>Pagina nu există</h1>"))

def set_timer(ora):
    pass  # timer-ul systemd ruleaza "raport-programat" la fiecare 10 minute si citeste ora din config

def scheduled_report():
    # trimite raportul o singura data pe zi, dupa ora din setari
    load_config()
    ora = C("raport", "ora", "18:00")
    stamp = os.path.join(DATA_DIR, "ultimul_raport")
    today = dt.date.today().isoformat()
    try: last = open(stamp).read().strip()
    except Exception: last = ""
    if last == today or dt.datetime.now().strftime("%H:%M") < ora: return
    daily_report(True)
    with open(stamp, "w") as f: f.write(today)

def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "serve"
    init_db()
    if cmd == "serve":
        port = int(C("server", "port", "8080") or 8080)
        srv = ThreadingHTTPServer(("127.0.0.1", port), H)
        log("server pornit pe 127.0.0.1:%d" % port)
        srv.serve_forever()
    elif cmd == "raport-programat":
        scheduled_report()
    elif cmd == "raport":
        subject, text = daily_report(send="--fara-email" not in sys.argv)
        if "--fara-email" in sys.argv: print(text)
    elif cmd == "parola":
        import getpass
        pw = sys.argv[2] if len(sys.argv) > 2 else getpass.getpass("Parola nouă pentru panou: ")
        if len(pw) < 10: sys.exit("Parola trebuie să aibă cel puțin 10 caractere.")
        cfg.set("server", "parola_admin", hash_password(pw)); save_config(); print("Parola a fost schimbată.")
    elif cmd == "test-email":
        print(send_email(C("raport", "catre"), "Test — Sănătate PC Monitorizare", page_mail("E-mailul funcționează", "<p>OK</p>"), "OK") or "trimis")
    else:
        sys.exit(__doc__ or "comandă necunoscută")

if __name__ == "__main__":
    main()
