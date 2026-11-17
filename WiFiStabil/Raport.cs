// Wi-Fi Stabil - raportul HTML (grafice + concluzie) si exportul CSV, plus jurnalul Windows pentru Wi-Fi.
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace InfoPcWifi
{
    public static class Report
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly DateTime Unix = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static long Ms(DateTime d) { return (long)(d.ToUniversalTime() - Unix).TotalMilliseconds; }

        public static string Js(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder b = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"') b.Append("\\\"");
                else if (ch == '\\') b.Append("\\\\");
                else if (ch < 32 || ch == '<' || ch == '>' || ch == '&') b.AppendFormat("\\u{0:x4}", (int)ch);
                else b.Append(ch);
            }
            return b.Append('"').ToString();
        }

        // evenimentele Windows de conectare / deconectare Wi-Fi din perioada testului
        public static List<object[]> WlanEvents(DateTime from, DateTime to)
        {
            List<object[]> res = new List<object[]>();
            try
            {
                string q = string.Format(Inv, "*[System[(EventID=8001 or EventID=8002 or EventID=8003) and TimeCreated[@SystemTime>='{0}' and @SystemTime<='{1}']]]",
                    from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000Z", Inv), to.ToUniversalTime().AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss.000Z", Inv));
                EventLogQuery query = new EventLogQuery("Microsoft-Windows-WLAN-AutoConfig/Operational", PathType.LogName, q);
                using (EventLogReader r = new EventLogReader(query))
                {
                    for (EventRecord e = r.ReadEvent(); e != null && res.Count < 500; e = r.ReadEvent())
                    {
                        using (e)
                        {
                            string msg = "";
                            try { msg = e.FormatDescription() ?? ""; } catch (Exception) { }
                            msg = System.Text.RegularExpressions.Regex.Replace(msg, @"\s+", " ").Trim();
                            if (msg.Length > 600) msg = msg.Substring(0, 600) + "…";
                            res.Add(new object[] { e.TimeCreated.HasValue ? e.TimeCreated.Value : DateTime.Now, e.Id, msg });
                        }
                    }
                }
            }
            catch (Exception) { }
            return res;
        }

        public static string Html(Monitor m, Monitor.Snapshot s, string licName, bool final)
        {
            string tpl;
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("raport.html"))
            using (StreamReader rd = new StreamReader(st, Encoding.UTF8))
                tpl = rd.ReadToEnd();

            StringBuilder d = new StringBuilder("{");
            d.Append("\"startMs\":").Append(Ms(s.Start));
            d.Append(",\"endMs\":").Append(Ms(s.End));
            d.Append(",\"final\":").Append(final ? "true" : "false");
            d.Append(",\"pc\":").Append(Js(Environment.MachineName));
            d.Append(",\"ssid\":").Append(Js(s.Ssids));
            d.Append(",\"adapter\":").Append(Js(m.AdapterName));
            d.Append(",\"gw\":").Append(Js(m.Gateway));
            d.Append(",\"targets\":").Append(Js(string.Join(", ", m.Targets)));
            d.Append(",\"interval\":").Append(m.IntervalSec);
            d.Append(",\"prag\":").Append(m.ThresholdSec);
            d.Append(",\"roam\":").Append(s.Roam);
            d.Append(",\"wired\":").Append(m.WiredAlso ? "true" : "false");
            d.Append(",\"lic\":").Append(Js(licName ?? ""));
            d.Append(",\"samples\":[");
            bool firstS = true;
            foreach (Sample x in s.Samples)
            {
                if (!firstS) d.Append(',');
                firstS = false;
                d.Append('[').Append(x.T.ToString(Inv)).Append(',').Append(x.State).Append(',').Append(x.Signal).Append(',').Append(x.GwMs).Append(',').Append(x.NetMs).Append(']');
            }
            d.Append("],\"incidents\":[");
            for (int i = 0; i < s.Incidents.Count; i++)
            {
                Incident x = s.Incidents[i];
                if (i > 0) d.Append(',');
                d.Append('[').Append(Ms(x.Start)).Append(',').Append(Ms(x.End)).Append(',').Append(x.Type).Append(',').Append(x.Ongoing ? 1 : 0).Append(']');
            }
            d.Append("],\"events\":[");
            for (int i = 0; i < s.Events.Count; i++)
            {
                if (i > 0) d.Append(',');
                d.Append('[').Append(Ms(s.Events[i].Time)).Append(',').Append(Js(s.Events[i].Text)).Append(']');
            }
            d.Append("],\"wlan\":[");
            List<object[]> wl = WlanEvents(s.Start, s.End);
            for (int i = 0; i < wl.Count; i++)
            {
                if (i > 0) d.Append(',');
                d.Append('[').Append(Ms((DateTime)wl[i][0])).Append(',').Append((int)wl[i][1]).Append(',').Append(Js((string)wl[i][2])).Append(']');
            }
            d.Append("]}");
            return tpl.Replace("/*__DATA__*/null", d.ToString());
        }

        public static void SaveHtml(string path, Monitor m, Monitor.Snapshot s, string licName, bool final)
        {
            File.WriteAllText(path, Html(m, s, licName, final), new UTF8Encoding(true));
        }

        public static void SaveCsv(string path, Monitor.Snapshot s, int interval)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Data;Ora;Stare;Semnal %;RSSI dBm;Ping router ms;Ping internet ms");
                foreach (Sample x in s.Samples)
                {
                    DateTime t = s.Start.AddSeconds(x.T);
                    w.WriteLine(t.ToString("dd.MM.yyyy") + ";" + t.ToString("HH:mm:ss") + ";" + Monitor.StateName(x.State) + ";" +
                        (x.Signal >= 0 ? x.Signal.ToString() : "") + ";" + (x.Rssi != 0 ? x.Rssi.ToString() : "") + ";" +
                        (x.GwMs >= 0 ? x.GwMs.ToString() : "") + ";" + (x.NetMs >= 0 ? x.NetMs.ToString() : ""));
                }
                w.WriteLine();
                w.WriteLine("Întreruperi");
                w.WriteLine("Început;Sfârșit;Durată (s);Tip");
                foreach (Incident i in s.Incidents)
                    w.WriteLine(i.Start.ToString("dd.MM.yyyy HH:mm:ss") + ";" + i.End.ToString("dd.MM.yyyy HH:mm:ss") + ";" + (int)Math.Round(i.Seconds) + ";" + i.TypeName);
            }
        }
    }
}
