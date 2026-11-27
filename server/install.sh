#!/usr/bin/env bash
# Sanatate PC - instalare / actualizare server de monitorizare (DigitalSecureIt)
# Ubuntu 22.04 / 24.04, ca root:
#   curl -fsSL https://raw.githubusercontent.com/Catalin-ops/unelte-pc/main/server/install.sh | bash
# Optional, cu domeniul tau:  ... | bash -s -- monitor.domeniu.ro
set -euo pipefail

REPO_RAW="https://raw.githubusercontent.com/Catalin-ops/unelte-pc/main/server"
APP_DIR=/opt/sanatate-monitor
CONF_DIR=/etc/sanatate-monitor
CONF=$CONF_DIR/config.ini
DATA_DIR=/var/lib/sanatate-monitor
BACKUP_DIR=/var/backups/sanatate-monitor
SVC_USER=sanatate

say()  { printf '\n\033[1;32m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m%s\033[0m\n' "$*"; }
ask()  { local q="$1" d="${2:-}" a; read -r -p "$q${d:+ [$d]}: " a </dev/tty || true; echo "${a:-$d}"; }
ask_secret() { local a; read -r -s -p "$1: " a </dev/tty || true; echo >&2; echo "$a"; }

[ "$(id -u)" -eq 0 ] || { echo "Rulează ca root (sudo -i)."; exit 1; }

FIRST=1; [ -f "$CONF" ] && FIRST=0

say "Pachete de sistem"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq python3 sqlite3 curl ufw fail2ban unattended-upgrades ca-certificates >/dev/null
if ! command -v caddy >/dev/null; then
  if ! apt-get install -y -qq caddy >/dev/null 2>&1; then
    apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https gnupg >/dev/null
    curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt > /etc/apt/sources.list.d/caddy-stable.list
    apt-get update -qq && apt-get install -y -qq caddy >/dev/null
  fi
fi
timedatectl set-timezone Europe/Bucharest || true

say "Utilizator și foldere"
id "$SVC_USER" >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin "$SVC_USER"
install -d -o "$SVC_USER" -g "$SVC_USER" -m 750 "$DATA_DIR"
install -d -o "$SVC_USER" -g "$SVC_USER" -m 750 "$CONF_DIR"
install -d -m 755 "$APP_DIR"
install -d -m 700 "$BACKUP_DIR"

say "Aplicația"
curl -fsSL "$REPO_RAW/app/monitor.py" -o "$APP_DIR/monitor.py.new"
python3 -m py_compile "$APP_DIR/monitor.py.new"
mv "$APP_DIR/monitor.py.new" "$APP_DIR/monitor.py"
chmod 644 "$APP_DIR/monitor.py"

# comanda de administrare
cat > /usr/local/bin/sanatate-monitor <<'EOF'
#!/usr/bin/env bash
# sanatate-monitor parola | smtp | raport | test-email | status | jurnal | actualizare
set -e
CONF=/etc/sanatate-monitor/config.ini
run() { sudo -u sanatate SPC_CONFIG=$CONF python3 /opt/sanatate-monitor/monitor.py "$@"; }
case "${1:-}" in
  parola) run parola ;;
  raport) run raport ;;
  test-email) run test-email ;;
  smtp)
    read -r -p "Server SMTP [smtp.gmail.com]: " h; h=${h:-smtp.gmail.com}
    read -r -p "Port [587]: " p; p=${p:-587}
    read -r -p "Utilizator (adresa de e-mail): " u
    read -r -s -p "Parola SMTP (la Gmail: parola de aplicație): " pw; echo
    read -r -p "Expeditor afișat [$u]: " f; f=${f:-$u}
    SPC_CONFIG=$CONF python3 - "$h" "$p" "$u" "$pw" "$f" <<'PY'
import configparser, os, sys
c = configparser.ConfigParser(interpolation=None); f = os.environ["SPC_CONFIG"]; c.read(f, encoding="utf-8")
if not c.has_section("smtp"): c.add_section("smtp")
for k, v in zip(("server", "port", "utilizator", "parola", "de_la"), sys.argv[1:]): c.set("smtp", k, v)
with open(f, "w", encoding="utf-8") as fh: c.write(fh)
PY
    systemctl restart sanatate-monitor; echo "Salvat. Test: sanatate-monitor test-email" ;;
  status) systemctl --no-pager status sanatate-monitor caddy | head -30; systemctl list-timers --no-pager | grep sanatate || true ;;
  jurnal) journalctl -u sanatate-monitor -u sanatate-raport -n 60 --no-pager ;;
  actualizare) curl -fsSL https://raw.githubusercontent.com/Catalin-ops/unelte-pc/main/server/install.sh | bash ;;
  *) echo "Folosire: sanatate-monitor parola | smtp | raport | test-email | status | jurnal | actualizare" ;;
esac
EOF
chmod 755 /usr/local/bin/sanatate-monitor

# domeniul: argumentul scriptului, cel salvat, sau adresa gratuita <ip>.sslip.io
IP=$(curl -4 -fsS --max-time 10 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')
OLD_DOMAIN=""; [ -f "$CONF" ] && OLD_DOMAIN=$(sed -n 's/^adresa *= *https:\/\///p' "$CONF" | head -1)
DOMAIN="${1:-${OLD_DOMAIN:-${IP//./-}.sslip.io}}"

if [ "$FIRST" -eq 1 ]; then
  say "Configurare (o singură dată)"
  REPORT_TO=$(ask "Adresa la care primești raportul zilnic")
  echo "Pentru trimiterea e-mailurilor (la Gmail: activează verificarea în 2 pași și creează o parolă de aplicație)."
  SMTP_HOST=$(ask "Server SMTP" "smtp.gmail.com")
  SMTP_PORT=$(ask "Port SMTP" "587")
  SMTP_USER=$(ask "Utilizator SMTP (adresa de e-mail)" "$REPORT_TO")
  SMTP_PASS=$(ask_secret "Parola SMTP")
  ADMIN_PASS=$(python3 -c 'import secrets,string;a=string.ascii_letters+string.digits;print("".join(secrets.choice(a) for _ in range(16)))')
  SPC_CONFIG=$CONF python3 - "$DOMAIN" "$REPORT_TO" "$SMTP_HOST" "$SMTP_PORT" "$SMTP_USER" "$SMTP_PASS" <<'PY'
import configparser, os, secrets, sys
d, to, h, p, u, pw = sys.argv[1:]
c = configparser.ConfigParser(interpolation=None)
c["server"] = {"date": "/var/lib/sanatate-monitor", "port": "8080", "secret": secrets.token_hex(32), "adresa": "https://" + d, "parola_admin": ""}
c["smtp"] = {"server": h, "port": p, "utilizator": u, "parola": pw, "de_la": u}
c["raport"] = {"catre": to, "ora": "18:00", "ore_offline": "26", "alerte": "da"}
with open(os.environ["SPC_CONFIG"], "w", encoding="utf-8") as f: c.write(f)
PY
  chown "$SVC_USER:$SVC_USER" "$CONF"; chmod 600 "$CONF"
  sudo -u "$SVC_USER" SPC_CONFIG=$CONF python3 "$APP_DIR/monitor.py" parola "$ADMIN_PASS" >/dev/null
else
  sed -i "s#^adresa *=.*#adresa = https://$DOMAIN#" "$CONF"
fi

say "Servicii"
cat > /etc/systemd/system/sanatate-monitor.service <<EOF
[Unit]
Description=Sanatate PC - server de monitorizare
After=network-online.target
[Service]
User=$SVC_USER
Environment=SPC_CONFIG=$CONF
ExecStart=/usr/bin/python3 $APP_DIR/monitor.py serve
Restart=always
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$DATA_DIR $CONF_DIR
[Install]
WantedBy=multi-user.target
EOF
cat > /etc/systemd/system/sanatate-raport.service <<EOF
[Unit]
Description=Sanatate PC - raport zilnic
[Service]
Type=oneshot
User=$SVC_USER
Environment=SPC_CONFIG=$CONF
ExecStart=/usr/bin/python3 $APP_DIR/monitor.py raport-programat
EOF
cat > /etc/systemd/system/sanatate-raport.timer <<'EOF'
[Unit]
Description=Sanatate PC - verifica ora raportului zilnic
[Timer]
OnCalendar=*:0/10
Persistent=true
[Install]
WantedBy=timers.target
EOF
cat > /etc/systemd/system/sanatate-backup.service <<EOF
[Unit]
Description=Sanatate PC - backup baza de date
[Service]
Type=oneshot
ExecStart=/bin/sh -c 'sqlite3 $DATA_DIR/monitor.db ".backup $BACKUP_DIR/monitor-\$\$(date +%%Y%%m%%d).db" && find $BACKUP_DIR -name "monitor-*.db" -mtime +14 -delete'
EOF
cat > /etc/systemd/system/sanatate-backup.timer <<'EOF'
[Unit]
Description=Sanatate PC - backup zilnic
[Timer]
OnCalendar=*-*-* 03:15
Persistent=true
[Install]
WantedBy=timers.target
EOF

cat > /etc/caddy/Caddyfile <<EOF
$DOMAIN {
	encode gzip
	header Strict-Transport-Security "max-age=31536000"
	reverse_proxy 127.0.0.1:8080
}
EOF

systemctl daemon-reload
systemctl enable --now sanatate-monitor sanatate-raport.timer sanatate-backup.timer >/dev/null
systemctl restart sanatate-monitor
systemctl enable caddy >/dev/null; systemctl reload caddy 2>/dev/null || systemctl restart caddy

say "Firewall și actualizări de securitate"
ufw allow OpenSSH >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
ufw --force enable >/dev/null
systemctl enable --now fail2ban >/dev/null 2>&1 || true
dpkg-reconfigure -f noninteractive unattended-upgrades >/dev/null 2>&1 || true

say "Verificare"
sleep 2
if curl -fsS --max-time 5 http://127.0.0.1:8080/sanatate >/dev/null; then echo "Aplicația rulează."; else warn "Aplicația nu răspunde: sanatate-monitor jurnal"; fi

echo
echo "------------------------------------------------------------"
echo " Panoul de monitorizare:  https://$DOMAIN"
if [ "$FIRST" -eq 1 ]; then
  echo " Parola panoului:         $ADMIN_PASS"
  echo " (notează-o; o schimbi din panou → Setări sau cu: sanatate-monitor parola)"
fi
echo " Comenzi utile:           sanatate-monitor status | jurnal | test-email | smtp | actualizare"
echo "------------------------------------------------------------"
