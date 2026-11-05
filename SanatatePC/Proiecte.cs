// Sanatate PC - Info PC Electric
// Recomandari "Ce trebuie facut" si proiecte (institutii) cu mai multe calculatoare salvate pe stick.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace InfoPcDisk
{
    public class Rec
    {
        public int Priority { get; set; }      // 1 urgent, 2 recomandat, 3 optional
        public string Component { get; set; }
        public string Action { get; set; }
        public string Detail { get; set; }
        public string Part { get; set; }       // piesa pentru oferta, ex. "SSD 512 GB SATA 2.5\""

        public string PriorityText { get { return Priority == 1 ? "Urgent" : Priority == 2 ? "Recomandat" : "Opțional"; } }
        public int Level { get { return Priority == 1 ? 2 : Priority == 2 ? 1 : -1; } }
    }

    public static class Recs
    {
        static readonly object sync = new object();
        static List<Rec> list = new List<Rec>();

        public static void Reset() { lock (sync) list = new List<Rec>(); }

        public static void Add(int priority, string component, string action, string detail, string part)
        {
            Rec r = new Rec();
            r.Priority = priority; r.Component = component; r.Action = action; r.Detail = detail ?? ""; r.Part = part ?? "";
            lock (sync)
            {
                foreach (Rec x in list) if (x.Component == component && x.Action == action) return;
                list.Add(r);
            }
        }

        public static List<Rec> Take()
        {
            lock (sync)
            {
                List<Rec> l = new List<Rec>(list);
                l.Sort(delegate(Rec a, Rec b) { return a.Priority.CompareTo(b.Priority); });
                return l;
            }
        }

        public static string SsdFor(long bytes, bool nvme)
        {
            double gb = bytes / 1e9;
            string cap = gb <= 260 ? "256 GB" : gb <= 520 ? "512 GB" : gb <= 1050 ? "1 TB" : "2 TB";
            return "SSD " + cap + (nvme ? " NVMe M.2" : " SATA 2.5\"");
        }

        // sectiunea "Ce trebuie facut" pentru calculatorul curent
        public static Section BuildSection(List<Rec> recs)
        {
            Section s = new Section("todo", "Ce trebuie făcut", "");
            s.Description = "Lucrările recomandate pentru acest calculator, în ordinea priorității.";
            int u = 0, r = 0;
            foreach (Rec x in recs)
            {
                Card c = s.AddCard(x.Action, x.Component);
                c.SetLevel(x.Level, x.PriorityText);
                if (x.Detail.Length > 0) c.Note(x.Detail);
                if (x.Part.Length > 0) c.Add("Piesă necesară", x.Part);
                if (x.Priority == 1) u++; else if (x.Priority == 2) r++;
            }
            if (recs.Count == 0)
            {
                Card c = s.AddCard("Nu e nimic de făcut", "Calculatorul e în stare bună");
                c.SetLevel(0, null);
            }
            s.Summary = recs.Count == 0 ? "Nicio lucrare necesară" : (u > 0 ? u + " urgente · " : "") + (r > 0 ? r + " recomandate · " : "") + recs.Count + " în total";
            s.Finish();
            if (recs.Count > 0 && u == 0 && r == 0) s.SetLevel(-1);
            return s;
        }
    }

    // ------------------------------------------------------------ inregistrare salvata pentru un calculator
    public class SavedLine
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public int Level { get; set; }
        public string Summary { get; set; }
    }

    public class PcRecord
    {
        public string Label { get; set; }
        public string Room { get; set; }
        public string User { get; set; }
        public string Notes { get; set; }
        public DateTime Date { get; set; }
        public string MachineName { get; set; }
        public string Model { get; set; }
        public string Windows { get; set; }
        public string Cpu { get; set; }
        public string Ram { get; set; }
        public string Gpu { get; set; }
        public string Disks { get; set; }
        public int Score { get; set; }
        public int Level { get; set; }
        public string Verdict { get; set; }
        public List<Rec> Recs { get; set; }
        public List<SavedLine> Lines { get; set; }
        public List<SavedLine> DiskLines { get; set; }

        public PcRecord() { Recs = new List<Rec>(); Lines = new List<SavedLine>(); DiskLines = new List<SavedLine>(); }

        // pentru afisare in lista
        [XmlIgnore] public string File;
        [XmlIgnore] public string StatusText { get { return Lvl.Text(Level < 0 ? 0 : Level); } }
        [XmlIgnore] public string StatusColor { get { return Lvl.Fg(Level < 0 ? 0 : Level); } }
        [XmlIgnore] public string StatusBg { get { return Lvl.Bg(Level < 0 ? 0 : Level); } }
        [XmlIgnore] public string Title { get { return Label + (string.IsNullOrEmpty(Room) ? "" : " — " + Room); } }
        [XmlIgnore] public string Sub
        {
            get
            {
                int u = 0; foreach (Rec r in Recs) if (r.Priority == 1) u++;
                return (Model ?? "") + " · " + Recs.Count + (Recs.Count == 1 ? " lucrare" : " lucrări") + (u > 0 ? " (" + u + " urgente)" : "") + " · " + Date.ToString("dd.MM.yyyy HH:mm");
            }
        }

        static string Sum(List<Section> secs, string key)
        {
            foreach (Section s in secs) if (s.Key == key) return s.Summary;
            return "";
        }

        public static PcRecord FromCurrent(List<Section> secs, List<DiskReport> disks, List<Rec> recs, string label, string room, string user, string notes)
        {
            PcRecord p = new PcRecord();
            p.Label = label; p.Room = room; p.User = user; p.Notes = notes;
            p.Date = DateTime.Now;
            p.MachineName = Environment.MachineName;
            foreach (Section s in secs)
            {
                if (s.Key == "sistem" && s.Cards.Count > 0) p.Model = s.Cards[0].Title;
                if (s.Key == "sumar" && s.Cards.Count > 0)
                {
                    int sc; int.TryParse(s.Cards[0].Badge, out sc);
                    p.Score = sc; p.Level = s.Cards[0].Level; p.Verdict = s.Cards[0].Title;
                }
                if (s.Key == "sumar" || s.Key == "live" || s.Key == "todo") continue;
                SavedLine l = new SavedLine();
                l.Name = s.Name; l.Status = s.Status; l.Level = s.Level; l.Summary = s.Summary;
                p.Lines.Add(l);
            }
            p.Windows = Sum(secs, "sistem");
            p.Cpu = Sum(secs, "cpu");
            p.Ram = Sum(secs, "ram");
            p.Gpu = Sum(secs, "gpu");
            p.Disks = Sum(secs, "discuri");
            foreach (DiskReport r in disks)
            {
                SavedLine l = new SavedLine();
                l.Name = r.Title + " (" + r.TypeBadge + ", " + r.SizeText + (r.Letters.Length > 0 ? ", " + r.Letters : "") + ")";
                l.Status = r.HealthText; l.Level = r.Level;
                l.Summary = "scor " + r.ScoreText + " · " + r.TempText + " · " + (r.IsSsd ? "viață " + r.LifeText + " · " : "") + r.HoursText;
                p.DiskLines.Add(l);
            }
            p.Recs = new List<Rec>(recs);
            return p;
        }
    }

    public class Project
    {
        public string Name;
        public string Dir;
        public List<PcRecord> Pcs = new List<PcRecord>();
        public string Title { get { return Name; } }
        public string Sub
        {
            get
            {
                int bad = 0, warn = 0;
                foreach (PcRecord p in Pcs) { if (p.Level == 2) bad++; else if (p.Level == 1) warn++; }
                return Pcs.Count + (Pcs.Count == 1 ? " calculator" : " calculatoare") + (bad > 0 ? " · " + bad + " cu probleme" : "") + (warn > 0 ? " · " + warn + " de urmărit" : "");
            }
        }
    }

    public static class Projects
    {
        static readonly XmlSerializer ser = new XmlSerializer(typeof(PcRecord));

        // proiectele stau langa aplicatie cand ruleaza de pe stick (sau langa licenta), altfel in Documente
        public static string BaseDir(License lic)
        {
            bool portable = Licensing.ExeOnRemovable || (lic != null && lic.SourcePath.StartsWith(Licensing.ExeDir, StringComparison.OrdinalIgnoreCase));
            if (portable && CanWrite(Licensing.ExeDir)) return Path.Combine(Licensing.ExeDir, "Proiecte");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sanatate PC", "Proiecte");
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string t = Path.Combine(dir, ".scriere_test");
                File.WriteAllText(t, "x");
                File.Delete(t);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static string SafeName(string n)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in (n ?? "").Trim()) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length == 0 ? "Fara nume" : s;
        }

        public static List<Project> List(string baseDir)
        {
            List<Project> l = new List<Project>();
            if (!Directory.Exists(baseDir)) return l;
            foreach (string d in Directory.GetDirectories(baseDir))
            {
                Project p = new Project();
                p.Dir = d;
                p.Name = Path.GetFileName(d);
                string nf = Path.Combine(d, "proiect.txt");
                try { if (File.Exists(nf)) { string n = File.ReadAllText(nf, Encoding.UTF8).Trim(); if (n.Length > 0) p.Name = n; } } catch (Exception) { }
                foreach (string f in Directory.GetFiles(d, "*.xml"))
                {
                    try
                    {
                        using (FileStream fs = File.OpenRead(f))
                        {
                            PcRecord r = (PcRecord)ser.Deserialize(fs);
                            r.File = f;
                            p.Pcs.Add(r);
                        }
                    }
                    catch (Exception) { }
                }
                p.Pcs.Sort(delegate(PcRecord a, PcRecord b) { return string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase); });
                l.Add(p);
            }
            l.Sort(delegate(Project a, Project b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return l;
        }

        public static Project Create(string baseDir, string name)
        {
            string dir = Path.Combine(baseDir, SafeName(name));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "proiect.txt"), name.Trim(), new UTF8Encoding(false));
            Project p = new Project();
            p.Dir = dir; p.Name = name.Trim();
            return p;
        }

        public static string PcFile(Project p, string label)
        {
            return Path.Combine(p.Dir, SafeName(label) + ".xml");
        }

        public static void Save(Project p, PcRecord r)
        {
            Directory.CreateDirectory(p.Dir);
            string f = PcFile(p, r.Label);
            using (FileStream fs = File.Create(f)) ser.Serialize(fs, r);
            r.File = f;
        }
    }
}
