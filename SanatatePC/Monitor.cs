// Sanatate PC - DigitalSecureIt
// Monitorizare (Pro): pe calculatoarele unei institutii aplicatia ruleaza ca sarcina programata (SYSTEM),
// citeste starea componentelor si erorile din ultimele 24 de ore si le trimite serverului de monitorizare.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace InfoPcDisk
{
    public class MonitorConfig
    {
        public string Server = "", Key = "", Label = "";
        public bool Enabled { get { return Server.Length > 0 && Key.Length > 0; } }
    }

    public static class Monitorizare
    {
        public const string TaskName = "Sanatate PC Monitorizare";
        public const string TaskBoot = "Sanatate PC Monitorizare (pornire)";
        public const string AgentVersion = "1";

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SanatatePC"); } }
        static string ConfigFile { get { return Path.Combine(Dir, "monitorizare.ini"); } }
        static string LogFile { get { return Path.Combine(Dir, "monitorizare.log"); } }
        public static string AgentExe { get { return Path.Combine(Dir, "SanatatePC.exe"); } }

        public static MonitorConfig Load()
        {
            MonitorConfig c = new MonitorConfig();
            try
            {
                if (!File.Exists(ConfigFile)) return c;
                foreach (string line in File.ReadAllLines(ConfigFile, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "Server") c.Server = v; else if (k == "Cheie") c.Key = v; else if (k == "Eticheta") c.Label = v;
                }
            }
            catch (Exception) { }
            return c;
        }

        static void Save(MonitorConfig c)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigFile, "Server=" + c.Server + "\r\nCheie=" + c.Key + "\r\nEticheta=" + c.Label + "\r\n", new UTF8Encoding(false));
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                List<string> lines = File.Exists(LogFile) ? new List<string>(File.ReadAllLines(LogFile, Encoding.UTF8)) : new List<string>();
                lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text);
                if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
                File.WriteAllLines(LogFile, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public static string LastLog()
        {
            try { if (File.Exists(LogFile)) { string[] l = File.ReadAllLines(LogFile, Encoding.UTF8); return l.Length > 0 ? l[l.Length - 1] : ""; } }
            catch (Exception) { }
            return "";
        }

        public static string NormalizeServer(string s)
        {
            s = (s ?? "").Trim().TrimEnd('/');
            if (s.Length > 0 && !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
            return s;
        }

        // ------------------------------------------------------------ instalare / dezinstalare (cere administrator)
        public static string Install(string server, string key, string label)
        {
            MonitorConfig c = new MonitorConfig();
            c.Server = NormalizeServer(server); c.Key = (key ?? "").Trim(); c.Label = (label ?? "").Trim();
            if (!c.Enabled) return "Completează adresa serverului și codul instituției.";
            if (!Collect.IsAdmin) return "Pentru monitorizare, pornește Sănătate PC ca administrator (clic dreapta → Executare ca administrator).";
            Directory.CreateDirectory(Dir);
            // agentul ruleaza dintr-o copie locala, ca sa mearga si dupa ce scoti stick-ul
            try
            {
                string me = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.Equals(Path.GetFullPath(me), Path.GetFullPath(AgentExe), StringComparison.OrdinalIgnoreCase))
                    File.Copy(me, AgentExe, true);
                Save(c);
            }
            catch (Exception ex) { return "Nu am putut copia aplicația în " + Dir + ": " + ex.Message; }
            string tr = AgentExe.IndexOf(' ') >= 0 ? "\\\"" + AgentExe + "\\\" --monitor-run" : AgentExe + " --monitor-run";
            string err = Schtasks("/Create /F /TN \"" + TaskName + "\" /TR \"" + tr + "\" /SC HOURLY /MO 2 /RU SYSTEM /RL HIGHEST");
            if (err != null) return "Nu am putut crea sarcina programată: " + err;
            err = Schtasks("/Create /F /TN \"" + TaskBoot + "\" /TR \"" + tr + "\" /SC ONSTART /DELAY 0005:00 /RU SYSTEM /RL HIGHEST");
            if (err != null) return "Nu am putut crea sarcina de la pornire: " + err;
            Log("monitorizare activată: " + c.Server + (c.Label.Length > 0 ? " · " + c.Label : ""));
            return null;
        }

        public static string Uninstall()
        {
            if (!Collect.IsAdmin) return "Pornește Sănătate PC ca administrator ca să oprești monitorizarea.";
            Schtasks("/Delete /F /TN \"" + TaskName + "\"");
            Schtasks("/Delete /F /TN \"" + TaskBoot + "\"");
            try { if (File.Exists(ConfigFile)) File.Delete(ConfigFile); } catch (Exception) { }
            Log("monitorizare oprită");
            return null;
        }

        public static bool TaskExists()
        {
            return Schtasks("/Query /TN \"" + TaskName + "\"") == null;
        }

        // null = reusit, altfel mesajul de eroare
        static string Schtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string e = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    return p.ExitCode == 0 ? null : (e.Trim().Length > 0 ? e.Trim() : "cod " + p.ExitCode);
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ------------------------------------------------------------ rulare agent (fara interfata)
        public static int RunAgent()
        {
            MonitorConfig c = Load();
            if (!c.Enabled) { Log("nu e configurat"); return 2; }
            string err = Send(c);
            Log(err == null ? "raport trimis" : "eroare: " + err);
            return err == null ? 0 : 1;
        }

        // colecteaza si trimite; null = trimis
        public static string Send(MonitorConfig c)
        {
            List<DiskReport> disks; List<Rec> recs;
            List<Section> secs = MainApp.CollectAll(true, delegate(string s) { }, out disks, out recs);
            string json = Payload(secs, disks, recs, c);
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(c.Server + "/api/v1/raport");
                rq.Method = "POST";
                rq.ContentType = "application/json; charset=utf-8";
                rq.Headers["X-Cheie-Institutie"] = c.Key;
                rq.UserAgent = "SanatatePC-Agent/" + AgentVersion;
                rq.Timeout = 30000;
                byte[] body = Encoding.UTF8.GetBytes(json);
                rq.ContentLength = body.Length;
                using (Stream s = rq.GetRequestStream()) s.Write(body, 0, body.Length);
                using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse()) return (int)rs.StatusCode / 100 == 2 ? null : "HTTP " + (int)rs.StatusCode;
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                if (r != null)
                {
                    string msg = "";
                    try { using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) msg = sr.ReadToEnd(); } catch (Exception) { }
                    if (msg.Length > 200) msg = msg.Substring(0, 200);
                    return "serverul a răspuns " + (int)r.StatusCode + (msg.Length > 0 ? ": " + msg : "");
                }
                return ex.Message;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static string Payload(List<Section> secs, List<DiskReport> disks, List<Rec> recs, MonitorConfig c)
        {
            int score, level; string verdict;
            Raport3D.Summary(secs, out score, out level, out verdict);
            Section sys = null, bat = null;
            foreach (Section s in secs) { if (s.Key == "sistem") sys = s; if (s.Key == "baterie") bat = s; }
            Raport3D.J j = new Raport3D.J();
            j.O();
            j.K("v").N(1).K("agent").S(AgentVersion).K("app").S(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            j.K("machine_id").S(Raport3D.MachineId()).K("machine").S(Environment.MachineName).K("label").S(c.Label);
            j.K("model").S(sys != null && sys.Cards.Count > 0 ? sys.Cards[0].Title : "").K("os").S(sys != null ? sys.Summary : "");
            j.K("laptop").B(bat != null);
            j.K("uptime_h").N((int)TimeSpan.FromMilliseconds((uint)Environment.TickCount).TotalHours);
            j.K("score").N(score).K("level").N(level).K("verdict").S(verdict);
            j.K("parts").A();
            foreach (Part p in Raport3D.Parts(secs, disks))
            {
                j.O();
                j.K("id").S(p.Id).K("key").S(p.Key).K("name").S(p.Name).K("l").N(p.Level).K("st").S(p.Status).K("sum").S(p.Summary);
                j.K("notes").A();
                foreach (Card cd in p.Cards) if (cd.Level >= 1) foreach (string n in cd.NoteList) j.S(n);
                j.E();
                j.C();
            }
            j.E();
            j.K("recs").A();
            foreach (Rec r in recs) { j.O(); j.K("p").N(r.Priority).K("c").S(r.Component).K("a").S(r.Action).K("d").S(r.Detail).K("part").S(r.Part); j.C(); }
            j.E();
            DayEvents(j);
            j.C();
            return j.ToString();
        }

        // erorile din ultimele 24 de ore din jurnalul System
        static void DayEvents(Raport3D.J j)
        {
            const string d = "TimeCreated[timediff(@SystemTime) <= 86400000]";
            j.K("day").O();
            j.K("bsod").N(Count("*[System[(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and " + d + "]]", null));
            j.K("power_loss").N(Count("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and " + d + "]]", null));
            j.K("whea").N(Count("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and " + d + "]]", null));
            j.K("disk_bad").N(Count("*[System[Provider[@Name='disk'] and EventID=7 and " + d + "]]", null));
            j.K("disk_retry").N(Count("*[System[Provider[@Name='disk'] and (EventID=153 or EventID=51 or EventID=11) and " + d + "]]", null));
            j.K("ntfs").N(Count("*[System[(Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and EventID=55 and " + d + "]]", null));
            Dictionary<string, int> src = new Dictionary<string, int>();
            int errs = Count("*[System[(Level=1 or Level=2) and " + d + "]]", src);
            j.K("sys_errors").N(errs);
            List<KeyValuePair<string, int>> top = new List<KeyValuePair<string, int>>(src);
            top.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            j.K("top").A();
            for (int i = 0; i < top.Count && i < 5; i++) { j.A(); j.S(top[i].Key).N(top[i].Value); j.E(); }
            j.E();
            j.C();
        }

        static int Count(string xpath, Dictionary<string, int> bySource)
        {
            int n = 0;
            try
            {
                EventLogQuery q = new EventLogQuery("System", PathType.LogName, xpath);
                using (EventLogReader r = new EventLogReader(q))
                {
                    EventRecord e;
                    while (n < 1000 && (e = r.ReadEvent()) != null)
                    {
                        n++;
                        if (bySource != null)
                        {
                            string s = e.ProviderName ?? "?";
                            int v; bySource.TryGetValue(s, out v); bySource[s] = v + 1;
                        }
                        e.Dispose();
                    }
                }
            }
            catch (Exception) { }
            return n;
        }

        // ------------------------------------------------------------ linie de comanda
        // SanatatePC.exe --monitor-run
        // SanatatePC.exe --monitor-install --server=https://... --cheie=COD [--eticheta="Secretariat 1"]
        // SanatatePC.exe --monitor-uninstall
        public static int? HandleCommandLine()
        {
            string[] a = Environment.GetCommandLineArgs();
            bool run = false, inst = false, un = false;
            string server = null, key = null, label = null;
            foreach (string x in a)
            {
                if (x.Equals("--monitor-run", StringComparison.OrdinalIgnoreCase)) run = true;
                else if (x.Equals("--monitor-install", StringComparison.OrdinalIgnoreCase)) inst = true;
                else if (x.Equals("--monitor-uninstall", StringComparison.OrdinalIgnoreCase)) un = true;
                else if (x.StartsWith("--server=", StringComparison.OrdinalIgnoreCase)) server = x.Substring(9);
                else if (x.StartsWith("--cheie=", StringComparison.OrdinalIgnoreCase)) key = x.Substring(8);
                else if (x.StartsWith("--eticheta=", StringComparison.OrdinalIgnoreCase)) label = x.Substring(11);
            }
            if (run) return RunAgent();
            if (inst)
            {
                License l = Licensing.Load();
                if (l == null || !l.IsPro) { Log("instalare refuzată: monitorizarea cere licența Pro"); return 3; }
                string err = Install(server, key, label);
                if (err != null) { Log("instalare eșuată: " + err); return 1; }
                return RunAgent();
            }
            if (un) return Uninstall() == null ? 0 : 1;
            return null;
        }
    }
}
