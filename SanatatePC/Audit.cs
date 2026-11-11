// Sanatate PC - Info PC Electric
// Audit complet: teste reale care ruleaza pe durata aleasa de tehnician (stres procesor + temperaturi,
// test RAM, citirea suprafetei discurilor, urmarirea erorilor de sistem). Durata se imparte intre testele alese;
// daca un test termina mai repede, timpul ramas trece la urmatorul. In raport apare durata reala.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using Microsoft.Win32.SafeHandles;

namespace InfoPcDisk
{
    public class AuditOptions
    {
        public int Minutes = 15;
        public bool Stress = true, Ram = true, Disk = true, Errors = true;

        public int Count { get { return (Stress ? 1 : 0) + (Ram ? 1 : 0) + (Disk ? 1 : 0) + (Errors ? 1 : 0); } }

        public string Names
        {
            get
            {
                List<string> l = new List<string>();
                if (Stress) l.Add("stres procesor");
                if (Ram) l.Add("memorie RAM");
                if (Disk) l.Add("suprafață discuri");
                if (Errors) l.Add("erori de sistem");
                return string.Join(", ", l.ToArray());
            }
        }
    }

    // un rand din fereastra de audit (se actualizeaza in timp real)
    public class AuditStep : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        string name = "", detail = "", state = "", stateColor = "#8A96B0", stateBg = "#1E293B", result = "";
        public string Key;

        void Set(ref string f, string v, string prop)
        {
            if (f == v) return;
            f = v;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(prop));
        }

        public string Name { get { return name; } set { Set(ref name, value, "Name"); } }
        public string Detail { get { return detail; } set { Set(ref detail, value, "Detail"); } }
        public string State { get { return state; } set { Set(ref state, value, "State"); } }
        public string StateColor { get { return stateColor; } set { Set(ref stateColor, value, "StateColor"); } }
        public string StateBg { get { return stateBg; } set { Set(ref stateBg, value, "StateBg"); } }
        public string Result { get { return result; } set { Set(ref result, value, "Result"); } }

        public void Waiting() { State = "În așteptare"; StateColor = "#8A96B0"; StateBg = "#1E293B"; }
        public void Running() { State = "Rulează"; StateColor = "#93C5FD"; StateBg = "#172554"; }
        public void Done(int level) { State = Lvl.Text(level < 0 ? -1 : level); StateColor = Lvl.Fg(level); StateBg = Lvl.Bg(level); }
    }

    public class AuditItem
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public int Level { get; set; }
        public List<KV> Props { get; set; }
        public List<string> Notes { get; set; }
        // lucrarea recomandata (daca e cazul)
        public int RecPriority { get; set; }
        public string RecComponent { get; set; }
        public string RecAction { get; set; }
        public string RecDetail { get; set; }
        public string RecPart { get; set; }

        public AuditItem() { Props = new List<KV>(); Notes = new List<string>(); Level = -1; Subtitle = ""; RecAction = ""; }

        public void Add(string k, string v)
        {
            KV kv = new KV(); kv.Key = k; kv.Value = string.IsNullOrEmpty(v) ? "—" : v; kv.ValueColor = "#E6EAF2";
            Props.Add(kv);
        }

        public void Rec(int priority, string component, string action, string detail, string part)
        {
            if (RecAction.Length > 0 && RecPriority <= priority) return;
            RecPriority = priority; RecComponent = component; RecAction = action; RecDetail = detail ?? ""; RecPart = part ?? "";
        }
    }

    public class AuditResult
    {
        public DateTime Start { get; set; }
        public double Seconds { get; set; }
        public int PlannedMinutes { get; set; }
        public bool Stopped { get; set; }
        public string Machine { get; set; }
        public string Tests { get; set; }
        public List<AuditItem> Items { get; set; }

        public AuditResult() { Items = new List<AuditItem>(); Machine = Environment.MachineName; Tests = ""; }

        static readonly CultureInfo RO = new CultureInfo("ro-RO");
        static string FilePath { get { return Path.Combine(Licensing.AppDataDir, "audit.xml"); } }

        public static string Duration(double sec)
        {
            int s = (int)Math.Round(sec);
            if (s < 60) return s + " s";
            return (s / 60) + " min" + (s % 60 > 0 ? " " + (s % 60) + " s" : "");
        }

        public int Level
        {
            get { int l = -1; foreach (AuditItem i in Items) if (i.Level > l) l = i.Level; return l; }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Licensing.AppDataDir);
                using (FileStream fs = File.Create(FilePath)) new XmlSerializer(typeof(AuditResult)).Serialize(fs, this);
            }
            catch (Exception) { }
        }

        // ultimul audit facut pe acest calculator in ultimele 24 de ore
        public static AuditResult LoadRecent()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                AuditResult r;
                using (FileStream fs = File.OpenRead(FilePath)) r = (AuditResult)new XmlSerializer(typeof(AuditResult)).Deserialize(fs);
                if (!string.Equals(r.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
                if ((DateTime.Now - r.Start).TotalHours > 24 || r.Start > DateTime.Now.AddMinutes(5)) return null;
                return r;
            }
            catch (Exception) { return null; }
        }

        public string SummaryText
        {
            get
            {
                return Duration(Seconds) + (Stopped ? " (oprit înainte de final)" : "") + " · " + Tests + " · " + Start.ToString("dd.MM.yyyy HH:mm", RO);
            }
        }

        // sectiunea "Audit complet" din aplicatie / raport; adauga si lucrarile recomandate
        public Section BuildSection()
        {
            Section s = new Section("audit", "Audit complet", "");
            s.Description = "Teste rulate efectiv pe acest calculator, pe durata aleasă de tehnician. Durata de mai jos e cea reală.";
            Card head = s.AddCard("Durata auditului: " + Duration(Seconds), "planificat " + PlannedMinutes + " min · " + Start.ToString("dd.MM.yyyy, HH:mm", RO));
            head.Add("Început", Start.ToString("dd.MM.yyyy, HH:mm:ss", RO));
            head.Add("Sfârșit", Start.AddSeconds(Seconds).ToString("dd.MM.yyyy, HH:mm:ss", RO));
            head.Add("Durată reală", Duration(Seconds));
            head.Add("Teste efectuate", Tests);
            if (Stopped) head.Note("Auditul a fost oprit de tehnician înainte de final; rezultatele acoperă doar partea rulată.");
            head.SetLevel(-1, "Info");
            foreach (AuditItem i in Items)
            {
                Card c = s.AddCard(i.Title, i.Subtitle);
                foreach (KV kv in i.Props) c.Add(kv.Key, kv.Value);
                foreach (string n in i.Notes) c.Note(n);
                c.SetLevel(i.Level, null);
                if (i.RecAction.Length > 0) Recs.Add(i.RecPriority, i.RecComponent, i.RecAction, i.RecDetail, i.RecPart);
            }
            s.Finish();
            int bad = 0, warn = 0;
            foreach (AuditItem i in Items) { if (i.Level == 2) bad++; else if (i.Level == 1) warn++; }
            s.Summary = Duration(Seconds) + " · " + Items.Count + (Items.Count == 1 ? " test" : " teste") +
                (bad > 0 ? " · " + bad + " cu probleme" : warn > 0 ? " · " + warn + " de urmărit" : " · fără probleme");
            return s;
        }
    }

    public class AuditRunner
    {
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out long outBuf, int outSize, out int ret, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPos, uint method);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);

        static readonly CultureInfo RO = new CultureInfo("ro-RO");

        public AuditOptions Options;
        public List<DiskReport> Disks = new List<DiskReport>();
        public List<AuditStep> Steps = new List<AuditStep>();
        public AuditResult Result = new AuditResult();
        public volatile bool Cancel, Finished;
        public volatile string Phase = "";
        public DateTime Start, Deadline;
        volatile RamTester ram;

        // ponderi: cat din durata primeste fiecare test activ
        static double Weight(string key) { return key == "ram" ? 4 : key == "stres" ? 3 : key == "disc" ? 3 : 0; }

        public AuditRunner(AuditOptions o, List<DiskReport> disks)
        {
            Options = o;
            if (disks != null) Disks = disks;
            if (o.Stress) AddStep("stres", "Procesor sub sarcină + temperaturi");
            if (o.Ram) AddStep("ram", "Test memorie RAM");
            if (o.Disk) AddStep("disc", "Citirea suprafeței discurilor");
            if (o.Errors) AddStep("erori", "Erori de sistem pe durata auditului");
        }

        void AddStep(string key, string name)
        {
            AuditStep s = new AuditStep(); s.Key = key; s.Name = name; s.Waiting();
            Steps.Add(s);
        }

        AuditStep Step(string key) { foreach (AuditStep s in Steps) if (s.Key == key) return s; return null; }

        public void StartAsync()
        {
            Start = DateTime.Now;
            Deadline = Start.AddMinutes(Options.Minutes);
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            Cancel = true;
            RamTester r = ram;
            if (r != null) r.Cancel = true;
        }

        void Run()
        {
            SetThreadExecutionState(0x80000003);   // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED: PC-ul nu intra in repaus
            try
            {
                Result.Start = Start;
                Result.PlannedMinutes = Options.Minutes;
                Result.Tests = Options.Names;
                List<string> active = new List<string>();
                foreach (AuditStep s in Steps) if (Weight(s.Key) > 0) active.Add(s.Key);

                for (int i = 0; i < active.Count && !Cancel; i++)
                {
                    // timpul ramas se imparte intre testele ramase, dupa pondere
                    double left = (Deadline - DateTime.Now).TotalSeconds, wsum = 0;
                    for (int j = i; j < active.Count; j++) wsum += Weight(active[j]);
                    double share = Math.Max(20, left * Weight(active[i]) / wsum);
                    DateTime until = i == active.Count - 1 ? Deadline : DateTime.Now.AddSeconds(share);
                    if (until > Deadline) until = Deadline;
                    AuditStep st = Step(active[i]);
                    st.Running();
                    AuditItem item = null;
                    try
                    {
                        if (active[i] == "stres") item = RunStress(st, until);
                        else if (active[i] == "ram") item = RunRam(st, until);
                        else if (active[i] == "disc") item = RunDisks(st, until);
                    }
                    catch (Exception ex)
                    {
                        item = new AuditItem(); item.Title = st.Name; item.Notes.Add("Testul nu a putut rula: " + ex.Message);
                    }
                    if (item != null) { Result.Items.Add(item); st.Result = OneLine(item); st.Done(item.Level); }
                }

                // doar urmarirea erorilor a fost aleasa: observam sistemul pe toata durata
                if (active.Count == 0)
                {
                    AuditStep e0 = Step("erori");
                    if (e0 != null) e0.Running();
                    while (!Cancel && DateTime.Now < Deadline)
                    {
                        Phase = "Urmăresc jurnalul de sistem…";
                        if (e0 != null) e0.Detail = "rămas " + AuditResult.Duration((Deadline - DateTime.Now).TotalSeconds);
                        Thread.Sleep(500);
                    }
                }

                AuditStep es = Step("erori");
                if (es != null)
                {
                    es.Running();
                    Phase = "Verific jurnalul de sistem…";
                    AuditItem e = CheckEvents(Start);
                    Result.Items.Add(e);
                    es.Result = OneLine(e);
                    es.Done(e.Level);
                }
                foreach (AuditStep s in Steps) if (s.State == "În așteptare" || s.State == "Rulează") { s.State = "Neefectuat"; s.Result = "Auditul a fost oprit înainte de acest test."; }
                Result.Stopped = Cancel;
                Result.Seconds = (DateTime.Now - Start).TotalSeconds;
                Result.Save();
                Phase = Cancel ? "Audit oprit" : "Audit terminat";
            }
            finally
            {
                SetThreadExecutionState(0x80000000);
                Finished = true;
            }
        }

        static string OneLine(AuditItem i)
        {
            List<string> p = new List<string>();
            if (i.Subtitle.Length > 0) p.Add(i.Subtitle);
            if (i.Notes.Count > 0) p.Add(i.Notes[0]);
            return string.Join(" — ", p.ToArray());
        }

        // ------------------------------------------------------------ stres procesor + temperaturi
        AuditItem RunStress(AuditStep st, DateTime until)
        {
            AuditItem it = new AuditItem();
            it.Key = "stres";
            it.Title = "Procesor sub sarcină";
            PerformanceCounter perf = null;
            try { perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); perf.NextValue(); }
            catch (Exception) { perf = null; }

            Phase = "Citesc temperatura în repaus…";
            Sensors idle = Collect.ReadSensors();
            List<double> temps = new List<double>(), perfs = new List<double>(), gtemps = new List<double>();
            List<DateTime> times = new List<DateTime>();
            int n = Math.Max(1, Environment.ProcessorCount);
            StopFlag flag = new StopFlag();
            Thread[] th = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                th[i] = new Thread(delegate()
                {
                    double x = 1.000001, y = 0.5;
                    while (!flag.Stop)
                        for (int k = 0; k < 200000; k++) { x = Math.Sqrt(x * x + 1.0000001) * 0.9999999; y += Math.Sin(x) * 1e-12; }
                    if (y == 42.4242) Console.Write("");
                });
                th[i].IsBackground = true;
                th[i].Priority = ThreadPriority.BelowNormal;
                th[i].Start();
            }
            DateTime t0 = DateTime.Now;
            try
            {
                while (!Cancel && DateTime.Now < until)
                {
                    Thread.Sleep(2000);
                    Sensors s = Collect.ReadSensors();
                    double p = -1;
                    if (perf != null) { try { p = perf.NextValue(); } catch (Exception) { p = -1; } }
                    times.Add(DateTime.Now);
                    temps.Add(s.CpuTemp > 0 ? s.CpuTemp : -1);
                    gtemps.Add(s.GpuTemp > 0 ? s.GpuTemp : -1);
                    perfs.Add(p);
                    double left = (until - DateTime.Now).TotalSeconds;
                    Phase = "Procesor la 100% pe " + n + " fire · rămas " + AuditResult.Duration(Math.Max(0, left));
                    st.Detail = (s.CpuTemp > 0 ? "temperatură " + s.CpuTemp + " °C · " : "") + (p >= 0 ? "performanță " + Math.Round(p) + "% · " : "") + "rămas " + AuditResult.Duration(Math.Max(0, left));
                }
            }
            finally
            {
                flag.Stop = true;
                foreach (Thread t in th) t.Join(3000);
                if (perf != null) perf.Dispose();
            }
            double dur = (DateTime.Now - t0).TotalSeconds;
            it.Subtitle = "toate cele " + n + " fire la 100% timp de " + AuditResult.Duration(dur);
            it.Add("Durată sub sarcină", AuditResult.Duration(dur));
            it.Add("Fire de execuție încărcate", n.ToString());

            List<double> vt = Valid(temps);
            int idleT = idle.CpuTemp;
            it.Level = 0;
            if (vt.Count > 0)
            {
                double max = Max(vt), endAvg = Avg(Tail(vt, 0.3));
                it.Add("Temperatură în repaus", idleT > 0 ? idleT + " °C" : "—");
                it.Add("Temperatură maximă sub sarcină", Math.Round(max) + " °C");
                it.Add("Temperatură medie la final", Math.Round(endAvg) + " °C");
                if (max >= 95)
                {
                    it.Level = 2;
                    it.Notes.Add("Procesorul ajunge la " + Math.Round(max) + " °C sub sarcină — prea cald, riscă opriri și uzură.");
                    it.Rec(1, "Procesor", "Curățare răcire procesor și pastă termoconductoare nouă", "Ajunge la " + Math.Round(max) + " °C în testul de stres.", "Pastă termoconductoare");
                }
                else if (max >= 88)
                {
                    it.Level = 1;
                    it.Notes.Add("Procesorul ajunge la " + Math.Round(max) + " °C sub sarcină — la limită; răcirea merită curățată.");
                    it.Rec(2, "Procesor", "Curățare praf din răcire / ventilatoare", "Ajunge la " + Math.Round(max) + " °C în testul de stres.", "");
                }
                else it.Notes.Add("Temperatura rămâne în limite normale sub sarcină.");
            }
            else
            {
                it.Level = -1;
                it.Add("Temperatură procesor", "nu e disponibilă");
                it.Notes.Add("Senzorul de temperatură nu e accesibil; pornește LibreHardwareMonitor ca să apară temperaturile în testul de stres.");
            }
            List<double> vg = Valid(gtemps);
            if (vg.Count > 0) it.Add("Temperatură maximă placă video", Math.Round(Max(vg)) + " °C");

            List<double> vp = Valid(perfs);
            if (vp.Count >= 6)
            {
                double startP = Avg(Head(vp, 0.25)), endP = Avg(Tail(vp, 0.3));
                it.Add("Performanță la început / la final", Math.Round(startP) + "% / " + Math.Round(endP) + "%");
                if (startP > 0 && endP < startP * 0.7)
                {
                    if (it.Level < 1) it.Level = 1;
                    it.Notes.Add("Frecvența scade cu " + Math.Round(100 - endP * 100 / startP) + "% după câteva minute de sarcină (throttling) — de obicei din cauza căldurii sau a alimentării.");
                    it.Rec(2, "Procesor", "Verificare răcire și setări de alimentare (procesorul încetinește sub sarcină)", "Performanța scade de la " + Math.Round(startP) + "% la " + Math.Round(endP) + "%.", "");
                }
            }
            return it;
        }

        class StopFlag { public volatile bool Stop; }

        // ------------------------------------------------------------ memorie RAM
        AuditItem RunRam(AuditStep st, DateTime until)
        {
            AuditItem it = new AuditItem();
            it.Key = "ram";
            it.Title = "Test memorie RAM";
            RamTester t = new RamTester();
            t.TargetBytes = RamTester.SuggestedBytes();
            t.Passes = 1000;          // cate treceri incap in timpul alocat
            t.Deadline = until;
            ram = t;
            t.StartAsync();
            while (!t.Finished)
            {
                Thread.Sleep(400);
                if (Cancel) t.Cancel = true;
                Phase = "Memorie RAM · " + t.Phase;
                st.Detail = (t.AllocatedBytes / (double)(1L << 30)).ToString("0.0", RO) + " GB · trecerea " + Math.Max(1, t.CurrentPass) +
                    " · " + t.Errors + (t.Errors == 1 ? " eroare" : " erori") + " · rămas " + AuditResult.Duration(Math.Max(0, (until - DateTime.Now).TotalSeconds));
            }
            ram = null;
            if (t.Error != null)
            {
                it.Subtitle = "nu a putut rula";
                it.Notes.Add(t.Error);
                return it;
            }
            double gb = t.AllocatedBytes / (double)(1L << 30);
            it.Subtitle = gb.ToString("0.##", RO) + " GB testați · " + t.PassesDone + (t.PassesDone == 1 ? " trecere completă" : " treceri complete") + " · " + t.Errors + (t.Errors == 1 ? " eroare" : " erori");
            it.Add("Memorie testată", gb.ToString("0.##", RO) + " GB (memoria liberă, fără cea folosită de Windows)");
            it.Add("Treceri complete", t.PassesDone + " × " + RamTester.PatternsPerPass + " tipare");
            it.Add("Erori găsite", t.Errors.ToString());
            if (t.Errors > 0)
            {
                it.Level = 2;
                it.Notes.Add("Memoria RAM a dat " + t.Errors + (t.Errors == 1 ? " eroare" : " erori") + " — o plăcuță e defectă.");
                it.Rec(1, "Memorie RAM", "Înlocuire plăcuță RAM defectă (testare pe rând)", t.Errors + " erori în testul de memorie.", "Memorie RAM");
            }
            else
            {
                it.Level = t.PassesDone > 0 ? 0 : -1;
                if (t.PassesDone == 0) it.Notes.Add("Testul s-a oprit înainte de o trecere completă.");
                else it.Notes.Add("Nicio eroare în memoria testată.");
            }
            // rezultatul apare si la pagina Memorie RAM
            if (t.PassesDone > 0 || t.Errors > 0)
            {
                RamTestResult r = new RamTestResult();
                r.Date = DateTime.Now; r.TestedGB = gb; r.Passes = Math.Max(1, t.PassesDone); r.Errors = t.Errors; r.Completed = t.PassesDone > 0;
                r.Save();
            }
            return it;
        }

        // ------------------------------------------------------------ suprafata discurilor (doar citire)
        AuditItem RunDisks(AuditStep st, DateTime until)
        {
            AuditItem it = new AuditItem();
            it.Key = "disc";
            it.Title = "Citirea suprafeței discurilor";
            it.Level = 0;
            List<DiskReport> list = new List<DiskReport>(Disks);
            if (list.Count == 0) { it.Level = -1; it.Notes.Add("Nu am găsit discuri de citit."); return it; }
            List<string> subs = new List<string>();
            unread = 0;
            for (int d = 0; d < list.Count && !Cancel; d++)
            {
                double left = (until - DateTime.Now).TotalSeconds;
                DateTime end = DateTime.Now.AddSeconds(Math.Max(5, left / (list.Count - d)));
                if (end > until) end = until;
                ScanDisk(list[d], end, it, st, subs, d + 1, list.Count);
            }
            it.Subtitle = string.Join(" · ", subs.ToArray());
            if (unread == list.Count)
            {
                it.Level = -1;
                it.Notes.Add(Collect.IsAdmin ? "Discurile nu au putut fi deschise pentru citire." : "Citirea suprafeței cere drepturi de administrator. Pornește Sănătate PC cu „Rulează ca administrator”.");
            }
            else if (it.Level == 0) it.Notes.Add("Toate zonele citite au răspuns normal, fără erori.");
            return it;
        }

        void ScanDisk(DiskReport dr, DateTime end, AuditItem it, AuditStep st, List<string> subs, int idx, int count)
        {
            const int Block = 1 << 20;
            string name = dr.Title;
            SafeFileHandle h = CreateFile(@"\\.\PhysicalDrive" + dr.Number, 0x80000000, 3, IntPtr.Zero, 3, 0x20000000, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                it.Add(name, err == 5 ? "necesită drepturi de administrator" : "nu pot deschide discul (cod " + err + ")");
                unread++;
                subs.Add(name + ": necitit");
                return;
            }
            IntPtr buf = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)Block, 0x3000, 0x04);
            try
            {
                long size; int ret;
                if (!DeviceIoControl(h, 0x0007405C, IntPtr.Zero, 0, out size, 8, out ret, IntPtr.Zero) || size < Block)
                {
                    it.Add(name, "nu pot afla capacitatea");
                    return;
                }
                long n = size / Block;
                // ordine imprastiata pe tot discul: (i * pas) mod n, cu pas prim cu n -> fiecare bloc o singura data
                long step = Math.Max(1, (long)(n * 0.6180339887)) | 1;
                while (Gcd(step, n) != 1) step += 2;
                bool ssd = dr.IsSsd;
                int slowMs = ssd ? 150 : 600, verySlowMs = ssd ? 1000 : 2500;
                long done = 0, slow = 0, verySlow = 0, errors = 0;
                double totalMs = 0, maxMs = 0;
                List<string> bad = new List<string>();
                Stopwatch sw = new Stopwatch();
                DateTime last = DateTime.MinValue;
                while (!Cancel && DateTime.Now < end && done < n)
                {
                    long blk = (long)((ulong)done * (ulong)step % (ulong)n);
                    long newPos;
                    sw.Restart();
                    bool ok = SetFilePointerEx(h, blk * Block, out newPos, 0);
                    int read = 0;
                    if (ok) ok = ReadFile(h, buf, Block, out read, IntPtr.Zero) && read == Block;
                    if (!ok)
                    {
                        // a doua incercare, ca sa nu numaram o eroare trecatoare
                        int e1 = Marshal.GetLastWin32Error();
                        ok = SetFilePointerEx(h, blk * Block, out newPos, 0) && ReadFile(h, buf, Block, out read, IntPtr.Zero) && read == Block;
                        if (!ok)
                        {
                            errors++;
                            if (bad.Count < 5) bad.Add("la " + (blk * (long)Block / 1e9).ToString("0.0", RO) + " GB (cod " + e1 + ")");
                        }
                    }
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    totalMs += ms;
                    if (ms > maxMs) maxMs = ms;
                    if (ok && ms > slowMs) slow++;
                    if (ok && ms > verySlowMs) verySlow++;
                    done++;
                    if ((DateTime.Now - last).TotalMilliseconds > 400)
                    {
                        last = DateTime.Now;
                        double pct = done * 100.0 / n;
                        Phase = "Citesc discul " + idx + " din " + count + ": " + name;
                        st.Detail = name + " · " + (done * (double)Block / 1e9).ToString("0.0", RO) + " GB citiți (" + pct.ToString(pct < 10 ? "0.0" : "0", RO) + "% din disc) · " +
                            errors + " erori · " + slow + " zone lente";
                    }
                }
                double gb = done * (double)Block / 1e9;
                double pctDone = done * 100.0 / n;
                double speed = totalMs > 0 ? done * (double)Block / (1 << 20) / (totalMs / 1000) : 0;
                string cover = pctDone >= 99.95 ? "tot discul" : pctDone.ToString(pctDone < 10 ? "0.0" : "0", RO) + "% din disc, răspândit pe toată suprafața";
                it.Add(name, gb.ToString("0.#", RO) + " GB citiți (" + cover + ") · " + speed.ToString("0", RO) + " MB/s la citire aleatoare · " +
                    errors + " erori · " + slow + " zone lente · cel mai lent bloc " + Math.Round(maxMs) + " ms");
                string sub = name + ": " + (errors > 0 ? errors + " erori de citire" : slow > 0 ? slow + " zone lente" : "fără erori");
                subs.Add(sub);
                if (errors > 0)
                {
                    it.Level = 2;
                    it.Notes.Add(name + ": " + errors + (errors == 1 ? " zonă nu a putut fi citită" : " zone nu au putut fi citite") + " (" + string.Join(", ", bad.ToArray()) + "). Discul are sectoare defecte.");
                    it.Rec(1, "Disc", "Backup imediat și înlocuire disc " + name + " (erori de citire)", errors + " zone necitibile la citirea suprafeței.", ssd ? "SSD" : "Disc");
                }
                else if (verySlow >= 3 || (done > 200 && slow * 100.0 / done > 1))
                {
                    if (it.Level < 1) it.Level = 1;
                    it.Notes.Add(name + ": " + slow + " zone se citesc foarte greu (peste " + slowMs + " ms) — semn timpuriu de uzură.");
                    it.Rec(2, "Disc", "Backup și urmărire disc " + name + " (zone lente la citire)", slow + " zone lente la citirea suprafeței.", "");
                }
            }
            finally
            {
                if (buf != IntPtr.Zero) VirtualFree(buf, UIntPtr.Zero, 0x8000);
                h.Dispose();
            }
        }

        int unread;

        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a; }

        // ------------------------------------------------------------ erori de sistem aparute in timpul auditului
        AuditItem CheckEvents(DateTime since)
        {
            AuditItem it = new AuditItem();
            it.Key = "erori";
            it.Title = "Erori de sistem pe durata auditului";
            it.Level = 0;
            Dictionary<string, int> cnt = new Dictionary<string, int>();
            int whea = 0, wheaFatal = 0, disk = 0, gpu = 0, other = 0;
            try
            {
                string t = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                EventLogQuery q = new EventLogQuery("System", PathType.LogName, "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime>='" + t + "']]]");
                using (EventLogReader r = new EventLogReader(q))
                {
                    for (EventRecord e = r.ReadEvent(); e != null; e = r.ReadEvent())
                    {
                        using (e)
                        {
                            string p = e.ProviderName ?? "";
                            int lvl = e.Level ?? 4;
                            if (p.IndexOf("WHEA", StringComparison.OrdinalIgnoreCase) >= 0) { if (lvl <= 2) wheaFatal++; else whea++; }
                            else if (Regex(p, "^(disk|Ntfs|volmgr|storahci|stornvme|iaStor.*|nvme|atapi)$")) disk++;
                            else if (Regex(p, "^(Display|nvlddmkm|amdkmdag|amdwddmg|igfx.*)$")) gpu++;
                            else if (lvl <= 2) other++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                it.Level = -1;
                it.Notes.Add("Nu pot citi jurnalul de sistem: " + ex.Message);
                return it;
            }
            it.Add("Erori hardware (WHEA)", wheaFatal + " grave · " + whea + " corectate");
            it.Add("Erori disc / sistem de fișiere", disk.ToString());
            it.Add("Erori driver placă video", gpu.ToString());
            it.Add("Alte erori în jurnal", other.ToString());
            if (wheaFatal > 0)
            {
                it.Level = 2;
                it.Notes.Add("Au apărut erori hardware grave (WHEA) în timpul testelor.");
                it.Rec(1, "Hardware", "Diagnoză hardware: erori WHEA sub sarcină", wheaFatal + " erori WHEA grave în timpul auditului.", "");
            }
            else if (disk > 0)
            {
                it.Level = 2;
                it.Notes.Add("Discul sau sistemul de fișiere a raportat " + disk + (disk == 1 ? " eroare" : " erori") + " în timpul testelor.");
                it.Rec(1, "Disc", "Verificare disc: erori în jurnal în timpul auditului", disk + " erori de disc în timpul auditului.", "");
            }
            else if (whea > 0 || gpu > 0)
            {
                it.Level = 1;
                if (whea > 0) it.Notes.Add(whea + " erori hardware corectate (WHEA) în timpul testelor — de urmărit.");
                if (gpu > 0) it.Notes.Add("Driverul plăcii video a raportat " + gpu + (gpu == 1 ? " eroare" : " erori") + " — actualizează driverul.");
                if (gpu > 0) it.Rec(2, "Placă video", "Actualizare driver placă video", gpu + " erori de driver în timpul auditului.", "");
            }
            it.Subtitle = it.Level == 0 ? "nicio eroare hardware în timpul testelor" : it.Notes.Count > 0 ? it.Notes[0] : "";
            if (it.Level == 0) it.Notes.Add("Nicio eroare hardware, de disc sau de driver video cât au rulat testele.");
            return it;
        }

        static bool Regex(string s, string pattern) { return System.Text.RegularExpressions.Regex.IsMatch(s, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }

        static List<double> Valid(List<double> l) { List<double> r = new List<double>(); foreach (double d in l) if (d > 0) r.Add(d); return r; }
        static double Max(List<double> l) { double m = double.MinValue; foreach (double d in l) if (d > m) m = d; return m; }
        static double Avg(List<double> l) { if (l.Count == 0) return 0; double s = 0; foreach (double d in l) s += d; return s / l.Count; }
        static List<double> Head(List<double> l, double f) { int n = Math.Max(1, (int)(l.Count * f)); return l.GetRange(0, Math.Min(n, l.Count)); }
        static List<double> Tail(List<double> l, double f) { int n = Math.Max(1, (int)(l.Count * f)); n = Math.Min(n, l.Count); return l.GetRange(l.Count - n, n); }
    }
}
