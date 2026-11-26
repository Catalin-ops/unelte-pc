// Sanatate PC - Info PC Electric
// Raportul 3D pentru client: o singura pagina .html (model 3D + date + three.js incluse), care merge si fara internet.
// Istoricul verificarilor fiecarui calculator se pastreaza langa setari (pe stick sau in AppData).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace InfoPcDisk
{
    public class SnapPart
    {
        public string Id { get; set; }
        public int Level { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
    }

    public class Snap
    {
        public DateTime Date { get; set; }
        public int Score { get; set; }
        public int Level { get; set; }
        public string Verdict { get; set; }
        public List<SnapPart> Parts { get; set; }
        public Snap() { Parts = new List<SnapPart>(); }
    }

    public class SnapHistory
    {
        public string Machine { get; set; }
        public string Model { get; set; }
        public List<Snap> Snaps { get; set; }
        public SnapHistory() { Snaps = new List<Snap>(); }
    }

    // o componenta din raport (ce apare in lista si, daca are piesa, in modelul 3D)
    class Part
    {
        public string Id, Key, Name, Summary, Status;
        public int Level;
        public List<Card> Cards = new List<Card>();
        public DiskReport Disk;
    }

    public static class Raport3D
    {
        static readonly CultureInfo RO = new CultureInfo("ro-RO");
        static readonly XmlSerializer ser = new XmlSerializer(typeof(SnapHistory));
        const int MaxSnaps = 20;

        static readonly string[] Order = { "cpu", "placa", "ram", "gpu", "discuri", "baterie", "monitor", "retea", "sistem", "stabilitate", "audit" };

        static Section Find(List<Section> secs, string key)
        {
            foreach (Section s in secs) if (s.Key == key) return s;
            return null;
        }

        static List<Part> Parts(List<Section> secs, List<DiskReport> disks)
        {
            List<Part> list = new List<Part>();
            foreach (string key in Order)
            {
                if (key == "discuri")
                {
                    for (int i = 0; i < disks.Count; i++) list.Add(DiskPart(disks[i], i));
                    continue;
                }
                Section s = Find(secs, key);
                if (s == null) continue;
                Part p = new Part();
                p.Id = key; p.Key = key; p.Name = s.Name; p.Summary = s.Summary ?? "";
                p.Level = s.Level; p.Status = s.Status;
                p.Cards.AddRange(s.Cards);
                list.Add(p);
            }
            return list;
        }

        static Part DiskPart(DiskReport r, int index)
        {
            Part p = new Part();
            string ident = !string.IsNullOrEmpty(r.Serial) ? r.Serial : (r.Title ?? "") + "_" + index;
            p.Id = "disk_" + ShortHash(ident);
            p.Key = "disk";
            p.Name = r.TypeBadge + " " + r.SizeText + (string.IsNullOrEmpty(r.Letters) ? "" : " (" + r.Letters + ")");
            p.Summary = r.Title + (string.IsNullOrEmpty(r.Verdict) ? "" : " · " + r.Verdict);
            p.Level = r.Level;
            p.Status = string.IsNullOrEmpty(r.HealthText) ? Lvl.Text(r.Level) : r.HealthText;
            p.Disk = r;
            Card c = new Card(r.Title, r.Subtitle);
            c.SetLevel(r.Level, p.Status);
            c.Add("Tip", r.TypeBadge + (string.IsNullOrEmpty(r.Interface) ? "" : " · " + r.Interface));
            c.Add("Scor sănătate", r.ScoreText);
            c.Add("Temperatură", r.TempText);
            if (r.IsSsd) c.Add("Viață rămasă", r.LifeText);
            c.Add("Ore de funcționare", r.HoursText);
            if (!string.IsNullOrEmpty(r.WrittenText)) c.Add("Date scrise", r.WrittenText);
            if (!string.IsNullOrEmpty(r.ErrorsText)) c.Add("Erori", r.ErrorsText);
            if (!string.IsNullOrEmpty(r.Serial)) c.Add("Serie", r.Serial);
            if (r.Reasons != null) foreach (string x in r.Reasons) c.Note(x);
            p.Cards.Add(c);
            return p;
        }

        static string ShortHash(string s)
        {
            using (SHA1 h = SHA1.Create())
            {
                byte[] b = h.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 5; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // identitatea calculatorului: UUID-ul placii de baza (acelasi si dupa reinstalarea Windows), altfel numele
        public static string MachineId()
        {
            string uuid = "";
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                    foreach (ManagementObject o in q.Get()) { uuid = Convert.ToString(o["UUID"]) ?? ""; break; }
            }
            catch (Exception) { }
            uuid = uuid.Trim().ToUpperInvariant();
            string digits = uuid.Replace("-", "").Replace("0", "").Replace("F", "");
            bool generic = digits.Length == 0 || uuid == "03000200-0400-0500-0006-000700080009";
            return Projects.SafeName(Environment.MachineName) + "_" + ShortHash(generic ? Environment.MachineName : uuid);
        }

        static string HistoryFile(Settings st)
        {
            return Path.Combine(Path.Combine(st.Dir, "Istoric"), MachineId() + ".xml");
        }

        static SnapHistory LoadHistory(string file)
        {
            try
            {
                if (File.Exists(file))
                    using (FileStream fs = File.OpenRead(file)) return (SnapHistory)ser.Deserialize(fs);
            }
            catch (Exception) { }
            return new SnapHistory();
        }

        static void Summary(List<Section> secs, out int score, out int level, out string verdict)
        {
            score = 0; level = -1; verdict = "";
            Section s = Find(secs, "sumar");
            if (s == null || s.Cards.Count == 0) return;
            int.TryParse(s.Cards[0].Badge, out score);
            level = s.Cards[0].Level;
            verdict = s.Cards[0].Title;
        }

        // salveaza starea de azi in istoricul acestui calculator (o singura intrare pe zi) si intoarce istoricul
        public static SnapHistory SaveSnapshot(List<Section> secs, List<DiskReport> disks, Settings st)
        {
            List<Part> parts = Parts(secs, disks);
            return SaveSnapshot(secs, parts, st);
        }

        static SnapHistory SaveSnapshot(List<Section> secs, List<Part> parts, Settings st)
        {
            string file = HistoryFile(st);
            SnapHistory h = LoadHistory(file);
            Snap n = new Snap();
            n.Date = DateTime.Now;
            int score, level; string verdict;
            Summary(secs, out score, out level, out verdict);
            n.Score = score; n.Level = level; n.Verdict = verdict;
            foreach (Part p in parts)
            {
                SnapPart sp = new SnapPart();
                sp.Id = p.Id; sp.Level = p.Level; sp.Status = p.Status; sp.Summary = p.Summary;
                n.Parts.Add(sp);
            }
            h.Snaps.RemoveAll(delegate(Snap x) { return x.Date.Date == n.Date.Date; });
            h.Snaps.Add(n);
            h.Snaps.Sort(delegate(Snap a, Snap b) { return a.Date.CompareTo(b.Date); });
            while (h.Snaps.Count > MaxSnaps) h.Snaps.RemoveAt(0);
            h.Machine = Environment.MachineName;
            Section sys = Find(secs, "sistem");
            if (sys != null && sys.Cards.Count > 0) h.Model = sys.Cards[0].Title;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                using (FileStream fs = File.Create(file)) ser.Serialize(fs, h);
            }
            catch (Exception) { } // stick protejat la scriere: raportul se face oricum, fara istoric salvat
            return h;
        }

        static string Resource(string name)
        {
            Assembly a = Assembly.GetExecutingAssembly();
            using (Stream s = a.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static bool HasModel { get { return Assembly.GetExecutingAssembly().GetManifestResourceInfo("three.min.js") != null; } }

        public static string Build(List<Section> secs, List<DiskReport> disks, List<Rec> recs, Settings st, string client, string notes, bool saveHistory)
        {
            string tpl = Resource("Raport3D.html");
            if (tpl == null) throw new InvalidOperationException("Șablonul raportului 3D lipsește din aplicație.");
            string three = Resource("three.min.js") ?? "";
            List<Part> parts = Parts(secs, disks);
            SnapHistory hist = saveHistory ? SaveSnapshot(secs, parts, st) : null;
            string json = Json(secs, parts, recs, st, client, notes, hist);
            // nimic din date sau din biblioteca nu are voie sa inchida tag-ul <script>
            three = three.Replace("</script", "<\\/script");
            return tpl.Replace("/*THREE_JS*/", three).Replace("/*DATE_RAPORT*/null", json);
        }

        static string Json(List<Section> secs, List<Part> parts, List<Rec> recs, Settings st, string client, string notes, SnapHistory hist)
        {
            int score, level; string verdict;
            Summary(secs, out score, out level, out verdict);
            Section sys = Find(secs, "sistem");
            J j = new J();
            j.O();
            j.K("ver").S(Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            j.K("gen").S(DateTime.Now.ToString("d MMMM yyyy, HH:mm", RO));
            j.K("date").S(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            j.K("client").S(client).K("notes").S(notes);
            j.K("score").N(score).K("level").N(level).K("verdict").S(verdict);

            j.K("firm").O();
            j.K("name").S(st.Company).K("phone").S(st.Phone).K("email").S(st.Email).K("web").S(st.Website).K("addr").S(st.Address).K("tech").S(st.Technician);
            j.K("logo").S(LogoData(st));
            j.C();

            j.K("pc").O();
            j.K("model").S(sys != null && sys.Cards.Count > 0 ? sys.Cards[0].Title : "");
            j.K("os").S(sys != null ? sys.Summary : "");
            j.K("machine").S(Environment.MachineName);
            j.K("laptop").B(Find(secs, "baterie") != null);
            j.C();

            j.K("parts").A();
            foreach (Part p in parts)
            {
                j.O();
                j.K("id").S(p.Id).K("key").S(p.Key).K("name").S(p.Name).K("sum").S(p.Summary).K("l").N(p.Level).K("st").S(p.Status);
                if (p.Disk != null)
                {
                    j.K("disk").O();
                    j.K("type").S(p.Disk.TypeBadge == "HDD" ? "HDD" : p.Disk.IsSsd ? "SSD" : p.Disk.TypeBadge);
                    j.K("nvme").B(p.Disk.IsNvme);
                    j.K("usb").B(p.Disk.Interface == "USB" || p.Disk.TypeBadge == "USB");
                    j.C();
                }
                j.K("cards").A();
                foreach (Card c in p.Cards)
                {
                    j.O();
                    j.K("t").S(c.Title).K("s").S(c.Subtitle).K("st").S(c.Status).K("l").N(c.Level);
                    j.K("p").A();
                    foreach (KV kv in c.Props) { j.A(); j.S(kv.Key).S(kv.Value); j.E(); }
                    j.E();
                    j.K("n").A();
                    foreach (string n in c.NoteList) j.S(n);
                    j.E();
                    j.C();
                }
                j.E();
                j.C();
            }
            j.E();

            j.K("recs").A();
            if (recs != null)
                foreach (Rec r in recs)
                {
                    j.O();
                    j.K("p").N(r.Priority).K("c").S(r.Component).K("a").S(r.Action).K("d").S(r.Detail).K("part").S(r.Part);
                    j.C();
                }
            j.E();

            j.K("hist").A();
            if (hist != null)
                foreach (Snap s in hist.Snaps)
                {
                    j.O();
                    j.K("d").S(s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).K("score").N(s.Score).K("l").N(s.Level).K("verdict").S(s.Verdict);
                    j.K("parts").O();
                    foreach (SnapPart sp in s.Parts) { j.K(sp.Id).A(); j.N(sp.Level).S(sp.Status).S(sp.Summary); j.E(); }
                    j.C();
                    j.C();
                }
            j.E();
            j.C();
            return j.ToString();
        }

        static string LogoData(Settings st)
        {
            try
            {
                string f = st.LogoPath;
                if (f.Length == 0 || !File.Exists(f) || new FileInfo(f).Length > 600 * 1024) return "";
                string ext = Path.GetExtension(f).ToLowerInvariant();
                string mime = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".gif" ? "image/gif" : ext == ".bmp" ? "image/bmp" : "";
                if (mime.Length == 0) return "";
                return "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(f));
            }
            catch (Exception) { return ""; }
        }

        // scriitor JSON minimal; caracterele < > & sunt scrise ca < etc., ca datele sa nu poata inchide <script>
        class J
        {
            readonly StringBuilder sb = new StringBuilder();
            readonly Stack<bool> first = new Stack<bool>();
            bool afterKey;

            void Sep()
            {
                if (afterKey) { afterKey = false; return; }
                if (first.Count == 0) return;
                if (first.Peek()) { first.Pop(); first.Push(false); } else sb.Append(',');
            }
            public J O() { Sep(); sb.Append('{'); first.Push(true); return this; }
            public J C() { sb.Append('}'); first.Pop(); return this; }
            public J A() { Sep(); sb.Append('['); first.Push(true); return this; }
            public J E() { sb.Append(']'); first.Pop(); return this; }
            public J K(string k) { Sep(); Str(k); sb.Append(':'); afterKey = true; return this; }
            public J S(string v) { Sep(); Str(v ?? ""); return this; }
            public J N(int v) { Sep(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
            public J B(bool v) { Sep(); sb.Append(v ? "true" : "false"); return this; }

            void Str(string s)
            {
                sb.Append('"');
                foreach (char ch in s)
                {
                    switch (ch)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '<': sb.Append("\\u003c"); break;
                        case '>': sb.Append("\\u003e"); break;
                        case '&': sb.Append("\\u0026"); break;
                        case '\u2028': sb.Append("\\u2028"); break;
                        case '\u2029': sb.Append("\\u2029"); break;
                        default:
                            if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch);
                            break;
                    }
                }
                sb.Append('"');
            }

            public override string ToString() { return sb.ToString(); }
        }
    }
}
