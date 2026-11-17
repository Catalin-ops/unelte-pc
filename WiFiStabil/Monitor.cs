// Wi-Fi Stabil - Info PC Electric
// Motorul de masurare: la fiecare secunda citeste placa Wi-Fi (API-ul WLAN din Windows), da ping la router si la internet
// si grupeaza esecurile in intreruperi. Ruleaza pe un fir separat; interfata citeste instantanee (Snapshot).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace InfoPcWifi
{
    // stare: 0 OK, 1 internet cazut, 2 routerul nu raspunde, 3 Wi-Fi (placa) deconectat
    public class Sample
    {
        public double T;          // secunde de la pornire
        public int State;
        public int Signal = -1;   // %
        public int Rssi;          // dBm (0 = necunoscut)
        public int GwMs = -1, NetMs = -1;
    }

    public class Incident
    {
        public DateTime Start, End;
        public int Type;
        public bool Ongoing;
        public double Seconds { get { return Math.Max(1, (End - Start).TotalSeconds); } }
        public string TypeName { get { return Monitor.StateName(Type); } }
        public string StartText { get { return Start.ToString("HH:mm:ss"); } }
        public string EndText { get { return Ongoing ? "acum" : End.ToString("HH:mm:ss"); } }
        public string DurText { get { return Monitor.Dur(Seconds); } }
        public string Color { get { return Monitor.StateColor(Type); } }
    }

    public class NetEvent
    {
        public DateTime Time { get; set; }
        public string Text { get; set; }
        public string TimeText { get { return Time.ToString("HH:mm:ss"); } }
    }

    public class WifiInfo
    {
        public bool Found;            // exista placa Wi-Fi
        public bool Connected;
        public bool AccessDenied;     // Windows 11: lipseste permisiunea de locatie
        public string Adapter = "", Ssid = "", Bssid = "", Band = "", Phy = "";
        public int Signal = -1, Rssi, Channel;
        public int RxMbps, TxMbps;
    }

    public class Monitor
    {
        public int IntervalSec = 1;
        public int TimeoutMs = 1000;
        public int ThresholdSec = 3;
        public string[] Targets = new string[] { "1.1.1.1", "8.8.8.8" };
        public string GatewayOverride = "";
        public double MaxMinutes;      // 0 = nelimitat
        public string SaveDir;         // folderul testului (CSV)

        public DateTime Started, LastTime;
        public volatile bool Running, Finished;
        public string StopReason = "";
        public string LastError = "";
        public volatile string Stage = "";
        static readonly string TraceFile = Environment.GetEnvironmentVariable("WIFISTABIL_TRACE");
        public static void Trace(string m)
        {
            if (string.IsNullOrEmpty(TraceFile)) return;
            try { lock (TraceFile) File.AppendAllText(TraceFile, DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Thread.CurrentThread.ManagedThreadId + "] " + m + Environment.NewLine); } catch (Exception) { }
        }

        // ultimele valori (pentru afisare)
        public WifiInfo Wifi = new WifiInfo();
        public string Gateway = "", AdapterName = "", AdapterKind = "Wi-Fi";
        public int State, GwMs = -1, NetMs = -1;
        public bool WiredAlso;

        readonly object sync = new object();
        readonly List<Sample> samples = new List<Sample>();
        readonly List<Incident> incidents = new List<Incident>();
        readonly List<NetEvent> events = new List<NetEvent>();
        readonly Dictionary<string, bool> ssids = new Dictionary<string, bool>();
        Incident cur;
        int nOk, roam;
        bool gwEver;
        string lastBssid = "", lastSsid = "", lastGw = "";
        Thread thread;
        StreamWriter csv;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string StateName(int s)
        {
            switch (s) { case 0: return "Conectat"; case 1: return "Internet căzut"; case 2: return "Routerul nu răspunde"; case 3: return "Wi-Fi deconectat"; }
            return "";
        }

        public static string StateColor(int s)
        {
            switch (s) { case 0: return "#22C55E"; case 1: return "#EAB308"; case 2: return "#F97316"; case 3: return "#EF4444"; }
            return "#64748B";
        }

        public static string Dur(double sec)
        {
            int s = (int)Math.Round(sec);
            int h = s / 3600, m = (s % 3600) / 60, x = s % 60;
            if (h > 0) return string.Format("{0}h {1:00}m {2:00}s", h, m, x);
            if (m > 0) return string.Format("{0}m {1:00}s", m, x);
            return x + "s";
        }

        // ------------------------------------------------------------ pornire / oprire
        public void Start()
        {
            Started = DateTime.Now;
            LastTime = Started;
            Running = true;
            if (SaveDir != null)
            {
                try
                {
                    Directory.CreateDirectory(SaveDir);
                    csv = new StreamWriter(Path.Combine(SaveDir, "masuratori.csv"), false, new UTF8Encoding(true));
                    csv.AutoFlush = true;
                    csv.WriteLine("Data;Ora;Stare;SSID;BSSID;Semnal %;RSSI dBm;Canal;Rx Mbps;Tx Mbps;Router;Ping router ms;Ping internet ms");
                }
                catch (Exception) { csv = null; }
            }
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "Masurare Wi-Fi";
            thread.Start();
        }

        public void Stop(string reason)
        {
            if (!Running) return;
            StopReason = reason ?? "";
            Running = false;
        }

        void Loop()
        {
            DateTime last = DateTime.MinValue;
            try
            {
                while (Running)
                {
                    DateTime tick = DateTime.Now;
                    if (last != DateTime.MinValue && (tick - last).TotalSeconds > Math.Max(15, IntervalSec * 5))
                    {
                        lock (sync)
                        {
                            CloseIncident(last.AddSeconds(IntervalSec));
                            AddEvent(last, "Măsurarea s-a oprit " + Dur((tick - last).TotalSeconds) + " (calculatorul a fost probabil în repaus); perioada nu e numărată.");
                        }
                    }
                    try { Measure(tick); }
                    catch (Exception ex) { LastError = ex.ToString(); }
                    last = tick;
                    if (MaxMinutes > 0 && (DateTime.Now - Started).TotalMinutes >= MaxMinutes) { StopReason = "durata"; break; }
                    DateTime next = tick.AddSeconds(IntervalSec);
                    while (Running && DateTime.Now < next) Thread.Sleep(50);
                }
            }
            finally
            {
                lock (sync)
                {
                    if (cur != null) CloseIncident(DateTime.Now);
                    LastTime = DateTime.Now;
                }
                try { if (csv != null) csv.Close(); } catch (Exception) { }
                Running = false;
                Finished = true;
            }
        }

        // ------------------------------------------------------------ o masuratoare
        void Measure(DateTime now)
        {
            Stage = "wlan"; Trace("wlan");
            WifiInfo w = Wlan.Query();
            Stage = "placa"; Trace("placa");
            NetworkInterface nic = PickNic(w);
            bool up = nic != null && nic.OperationalStatus == OperationalStatus.Up;
            string kind = nic != null && nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : nic != null ? "Cablu" : "Wi-Fi";
            if (w.Found && w.Connected) up = true;
            string gw = GatewayOverride.Length > 0 ? GatewayOverride : GatewayOf(nic);

            Stage = "ping"; Trace("ping");
            Dictionary<string, int> p = new Dictionary<string, int>();
            if (up)
            {
                List<string> t = new List<string>(Targets);
                if (gw.Length > 0) t.Add(gw);
                p = Pings(t, TimeoutMs);
            }
            int gwMs = -1, netMs = -1, v;
            if (gw.Length > 0 && p.TryGetValue(gw, out v)) gwMs = v;
            foreach (string t in Targets)
                if (p.TryGetValue(t, out v) && v >= 0 && (netMs < 0 || v < netMs)) netMs = v;
            if (gwMs >= 0) gwEver = true;

            int st;
            if (!up) st = 3;
            else if (netMs >= 0) st = 0;
            else if (gw.Length == 0) st = 2;
            else if (gwMs >= 0) st = 1;
            else if (gwEver) st = 2;
            else st = 1;

            Stage = "salvare"; Trace("salvare");
            bool started = false;
            lock (sync)
            {
                if (w.Bssid.Length > 0 && lastBssid.Length > 0 && w.Bssid != lastBssid)
                {
                    roam++;
                    AddEvent(now, "S-a schimbat punctul de acces (AP): " + lastBssid + " → " + w.Bssid + (w.Channel > 0 ? ", canal " + w.Channel : ""));
                }
                if (w.Bssid.Length > 0) lastBssid = w.Bssid;
                if (w.Ssid.Length > 0 && lastSsid.Length > 0 && w.Ssid != lastSsid) AddEvent(now, "S-a schimbat rețeaua Wi-Fi: " + lastSsid + " → " + w.Ssid);
                if (w.Ssid.Length > 0) { lastSsid = w.Ssid; ssids[w.Ssid] = true; }
                if (gw.Length > 0 && lastGw.Length > 0 && gw != lastGw) AddEvent(now, "S-a schimbat routerul (gateway): " + lastGw + " → " + gw);
                if (gw.Length > 0) lastGw = gw;

                if (st != 0)
                {
                    if (cur == null) { cur = new Incident(); cur.Start = now; cur.End = now; cur.Type = st; cur.Ongoing = true; started = true; }
                    else if (st > cur.Type) cur.Type = st;
                    cur.End = now;
                }
                else
                {
                    if (cur != null) CloseIncident(now);
                    nOk++;
                }

                Sample s = new Sample();
                s.T = Math.Round((now - Started).TotalSeconds, 1);
                s.State = st;
                s.Signal = w.Connected ? w.Signal : -1;
                s.Rssi = w.Connected ? w.Rssi : 0;
                s.GwMs = gwMs; s.NetMs = netMs;
                samples.Add(s);
                Trace("masuratoare " + samples.Count + " monitor " + GetHashCode());

                Wifi = w; Gateway = gw; State = st; GwMs = gwMs; NetMs = netMs; LastTime = now;
                AdapterName = nic != null ? nic.Description : w.Adapter;
                AdapterKind = kind;
            }
            if (started && IncidentStarted != null) { try { IncidentStarted(); } catch (Exception) { } }
            Stage = "csv"; Trace("csv");

            if (csv != null)
            {
                try
                {
                    csv.WriteLine(string.Join(";", new string[] {
                        now.ToString("dd.MM.yyyy"), now.ToString("HH:mm:ss"), StateName(st), w.Ssid.Replace(";", ","), w.Bssid,
                        w.Connected && w.Signal >= 0 ? w.Signal.ToString() : "", w.Connected && w.Rssi != 0 ? w.Rssi.ToString() : "",
                        w.Channel > 0 ? w.Channel.ToString() : "", w.RxMbps > 0 ? w.RxMbps.ToString() : "", w.TxMbps > 0 ? w.TxMbps.ToString() : "",
                        gw, gwMs >= 0 ? gwMs.ToString() : "", netMs >= 0 ? netMs.ToString() : "" }));
                }
                catch (Exception) { }
            }
            if (samples.Count % 30 == 1) WiredAlso = HasWired(nic);
            Stage = "gata"; Trace("gata");
        }

        public event Action IncidentStarted;

        void AddEvent(DateTime t, string text)
        {
            NetEvent e = new NetEvent(); e.Time = t; e.Text = text;
            events.Add(e);
        }

        void CloseIncident(DateTime end)
        {
            if (cur == null) return;
            if (end < cur.Start) end = cur.Start;
            cur.End = end;
            cur.Ongoing = false;
            incidents.Add(cur);
            if (SaveDir != null)
            {
                try
                {
                    string f = Path.Combine(SaveDir, "intreruperi.csv");
                    bool head = !File.Exists(f);
                    using (StreamWriter w = new StreamWriter(f, true, new UTF8Encoding(true)))
                    {
                        if (head) w.WriteLine("Început;Sfârșit;Durată (s);Tip");
                        w.WriteLine(cur.Start.ToString("dd.MM.yyyy HH:mm:ss") + ";" + end.ToString("dd.MM.yyyy HH:mm:ss") + ";" + (int)Math.Round(cur.Seconds) + ";" + StateName(cur.Type));
                    }
                }
                catch (Exception) { }
            }
            cur = null;
        }

        // ------------------------------------------------------------ instantaneu pentru interfata / raport
        public class Snapshot
        {
            public List<Sample> Samples;
            public List<Incident> Incidents;
            public List<NetEvent> Events;
            public DateTime Start, End;
            public int Total, Ok, Roam;
            public string Ssids = "";
            public double Availability { get { return Total == 0 ? 100 : 100.0 * Ok / Total; } }
            public int Big(int thr) { int n = 0; foreach (Incident i in Incidents) if (i.Seconds >= thr) n++; return n; }
            public double Longest { get { double m = 0; foreach (Incident i in Incidents) m = Math.Max(m, i.Seconds); return m; } }
            public double Offline { get { double m = 0; foreach (Incident i in Incidents) m += i.Seconds; return m; } }
        }

        public Snapshot Snap(bool copySamples)
        {
            lock (sync)
            {
                Snapshot s = new Snapshot();
                s.Samples = copySamples ? new List<Sample>(samples) : samples;
                s.Incidents = new List<Incident>(incidents);
                if (cur != null)
                {
                    Incident c = new Incident(); c.Start = cur.Start; c.End = LastTime; c.Type = cur.Type; c.Ongoing = true;
                    s.Incidents.Add(c);
                }
                s.Events = new List<NetEvent>(events);
                s.Start = Started; s.End = LastTime;
                s.Total = samples.Count; s.Ok = nOk; s.Roam = roam;
                s.Ssids = string.Join(", ", new List<string>(ssids.Keys).ToArray());
                return s;
            }
        }

        public object SyncRoot { get { return sync; } }

        // date simulate pentru capturi de ecran / prezentare (--demo)
        public void LoadDemo(double hours)
        {
            Random r = new Random(4);
            Started = DateTime.Now.AddHours(-hours);
            int total = (int)(hours * 3600);
            double[,] outs = new double[,] { { 0.20, 26, 3 }, { 0.45, 8, 2 }, { 0.62, 2, 3 }, { 0.74, 95, 1 }, { 0.86, 6, 3 } };
            IntervalSec = 2;
            bool roamed = false;
            for (int t = 0; t < total; t += 2)
            {
                double f = (double)t / total;
                int sig = (int)(72 + 10 * Math.Sin(t / 2500.0) + r.Next(-3, 4));
                if (f > 0.58 && f < 0.66) sig -= 28;
                int st = 0;
                for (int k = 0; k < outs.GetLength(0); k++)
                    if (f >= outs[k, 0] && t < outs[k, 0] * total + outs[k, 1]) st = (int)outs[k, 2];
                DateTime now = Started.AddSeconds(t);
                Sample s = new Sample();
                s.T = t; s.State = st; s.Signal = st == 3 ? -1 : sig; s.Rssi = st == 3 ? 0 : -100 + sig / 2;
                s.GwMs = st >= 2 ? -1 : 2 + r.Next(0, 6);
                s.NetMs = st != 0 ? -1 : 17 + r.Next(0, 12) + (r.Next(0, 40) == 0 ? r.Next(20, 70) : 0);
                samples.Add(s);
                if (st != 0) { if (cur == null) { cur = new Incident(); cur.Start = now; cur.Type = st; } cur.End = now; }
                else { if (cur != null) { cur.End = now; incidents.Add(cur); cur = null; } nOk++; }
                if (!roamed && f >= 0.58) { roamed = true; roam++; AddEvent(now, "S-a schimbat punctul de acces (AP): a4:2b:b0:11:22:33 → a4:2b:b0:11:22:34, canal 6"); }
            }
            ssids["InfoPC_5G"] = true;
            LastTime = DateTime.Now;
            WifiInfo w = new WifiInfo();
            w.Found = true; w.Connected = true; w.Ssid = "InfoPC_5G"; w.Bssid = "a4:2b:b0:11:22:34"; w.Signal = 74; w.Rssi = -61;
            w.Channel = 36; w.Band = "5 GHz"; w.Phy = "Wi-Fi 6"; w.RxMbps = 866; w.TxMbps = 866; w.Adapter = "Intel(R) Wi-Fi 6 AX201 160MHz";
            Wifi = w; Gateway = "192.168.1.1"; GwMs = 3; NetMs = 19; State = 0; AdapterName = w.Adapter;
            Finished = true;
        }

        // ------------------------------------------------------------ retea
        static NetworkInterface PickNic(WifiInfo w)
        {
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch (Exception) { return null; }
            NetworkInterface wifiUp = null, wifiAny = null, wiredUp = null;
            foreach (NetworkInterface n in all)
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    bool virt = n.Description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || n.Description.IndexOf("Direct", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (n.OperationalStatus == OperationalStatus.Up && wifiUp == null) wifiUp = n;
                    if (!virt && wifiAny == null) wifiAny = n;
                }
                else if (n.OperationalStatus == OperationalStatus.Up && IsRealWired(n) && GatewayOf(n).Length > 0 && wiredUp == null) wiredUp = n;
            }
            if (wifiUp != null) return wifiUp;
            if (wifiAny != null || w.Found) return wifiAny;
            if (wiredUp != null) return wiredUp;   // calculator fara Wi-Fi: testam conexiunea pe cablu
            foreach (NetworkInterface n in all)
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && GatewayOf(n).Length > 0) return n;
            return null;
        }

        static bool IsRealWired(NetworkInterface n)
        {
            if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet && n.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet) return false;
            string d = n.Description;
            foreach (string x in new string[] { "Virtual", "VPN", "VMware", "VirtualBox", "TAP-", "Bluetooth", "Loopback", "WAN Miniport", "Npcap" })
                if (d.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        static bool HasWired(NetworkInterface used)
        {
            if (used == null || used.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return false;
            try
            {
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == OperationalStatus.Up && IsRealWired(n) && GatewayOf(n).Length > 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        public static string GatewayOf(NetworkInterface n)
        {
            if (n == null) return "";
            try
            {
                foreach (GatewayIPAddressInformation g in n.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && g.Address.ToString() != "0.0.0.0") return g.Address.ToString();
            }
            catch (Exception) { }
            return "";
        }

        static Dictionary<string, int> Pings(List<string> targets, int timeout)
        {
            Dictionary<string, int> res = new Dictionary<string, int>();
            List<string> list = new List<string>();
            foreach (string t in targets) if (!string.IsNullOrEmpty(t) && !list.Contains(t)) list.Add(t);
            int[] ms = new int[list.Count];
            CountdownEvent done = new CountdownEvent(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                ms[i] = -1;
                int k = i;
                string target = list[i];
                Thread th = new Thread(delegate()
                {
                    try
                    {
                        Trace("ping " + target + " incep");
                        using (Ping p = new Ping())
                        {
                            Trace("ping " + target + " obiect creat");
                            PingReply r = p.Send(target, timeout);
                            Trace("ping " + target + " raspuns " + (r == null ? "null" : r.Status.ToString()));
                            if (r != null && r.Status == IPStatus.Success) ms[k] = (int)r.RoundtripTime;
                        }
                    }
                    catch (Exception) { }
                    finally { try { done.Signal(); } catch (Exception) { } }
                });
                th.IsBackground = true;
                Trace("pornesc firul pentru " + target);
                th.Start();
            }
            Trace("astept " + list.Count + " ping-uri");
            if (list.Count > 0) done.Wait(timeout + 800);
            Trace("gata asteptarea");
            for (int i = 0; i < list.Count; i++) res[list[i]] = ms[i];
            return res;
        }
    }

    // ------------------------------------------------------------ API-ul WLAN din Windows (wlanapi.dll)
    public static class Wlan
    {
        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr res, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr res, out int size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

        const int OpCurrentConnection = 7, OpChannel = 8, OpRssi = 0x10000102;
        static IntPtr handle = IntPtr.Zero;
        static readonly object sync = new object();

        public static WifiInfo Query()
        {
            WifiInfo w = new WifiInfo();
            lock (sync)
            {
                try
                {
                    if (handle == IntPtr.Zero)
                    {
                        uint neg;
                        if (WlanOpenHandle(2, IntPtr.Zero, out neg, out handle) != 0) { handle = IntPtr.Zero; return w; }
                    }
                    IntPtr list;
                    if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return w;
                    try
                    {
                        int n = Marshal.ReadInt32(list, 0);
                        // WLAN_INTERFACE_INFO: GUID (16) + descriere WCHAR[256] (512) + stare (4) = 532 octeti
                        Guid best = Guid.Empty; string bestDesc = ""; bool bestConn = false;
                        for (int i = 0; i < n; i++)
                        {
                            IntPtr item = new IntPtr(list.ToInt64() + 8 + i * 532);
                            Guid g = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                            string desc = Marshal.PtrToStringUni(new IntPtr(item.ToInt64() + 16));
                            int state = Marshal.ReadInt32(item, 16 + 512);
                            bool conn = state == 1; // wlan_interface_state_connected
                            if (best == Guid.Empty || (conn && !bestConn)) { best = g; bestDesc = desc ?? ""; bestConn = conn; }
                        }
                        if (best == Guid.Empty) return w;
                        w.Found = true;
                        w.Adapter = bestDesc;
                        w.Connected = bestConn;
                        if (!bestConn) return w;
                        int size; IntPtr data;
                        int rc = WlanQueryInterface(handle, ref best, OpCurrentConnection, IntPtr.Zero, out size, out data, IntPtr.Zero);
                        if (rc == 5) { w.AccessDenied = true; return w; }
                        if (rc == 0 && data != IntPtr.Zero)
                        {
                            try
                            {
                                // WLAN_CONNECTION_ATTRIBUTES: stare(4) mod(4) profil(512) | SSID: lungime(4)+32 | tip BSS(4) | BSSID(6)+2 | phy(4) | index(4) | semnal(4) | rx(4) | tx(4)
                                int ssidLen = Marshal.ReadInt32(data, 520);
                                if (ssidLen > 0 && ssidLen <= 32)
                                {
                                    byte[] b = new byte[ssidLen];
                                    Marshal.Copy(new IntPtr(data.ToInt64() + 524), b, 0, ssidLen);
                                    w.Ssid = Encoding.UTF8.GetString(b);
                                }
                                byte[] mac = new byte[6];
                                Marshal.Copy(new IntPtr(data.ToInt64() + 560), mac, 0, 6);
                                w.Bssid = BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant();
                                if (w.Bssid == "00:00:00:00:00:00") w.Bssid = "";
                                w.Phy = PhyName(Marshal.ReadInt32(data, 568));
                                w.Signal = Marshal.ReadInt32(data, 576);
                                w.RxMbps = Marshal.ReadInt32(data, 580) / 1000;
                                w.TxMbps = Marshal.ReadInt32(data, 584) / 1000;
                            }
                            finally { WlanFreeMemory(data); }
                        }
                        if (WlanQueryInterface(handle, ref best, OpChannel, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0 && data != IntPtr.Zero)
                        {
                            w.Channel = Marshal.ReadInt32(data); WlanFreeMemory(data);
                            w.Band = w.Channel >= 1 && w.Channel <= 14 ? "2,4 GHz" : w.Channel > 14 && w.Channel <= 177 ? "5 GHz" : "";
                        }
                        if (WlanQueryInterface(handle, ref best, OpRssi, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0 && data != IntPtr.Zero)
                        {
                            w.Rssi = Marshal.ReadInt32(data); WlanFreeMemory(data);
                        }
                    }
                    finally { WlanFreeMemory(list); }
                }
                catch (Exception) { }
            }
            return w;
        }

        static string PhyName(int t)
        {
            switch (t)
            {
                case 4: return "802.11a"; case 5: return "802.11b"; case 6: return "802.11g";
                case 7: return "Wi-Fi 4 (n)"; case 8: return "Wi-Fi 5 (ac)"; case 10: return "Wi-Fi 6 (ax)"; case 11: return "Wi-Fi 7 (be)";
            }
            return "";
        }
    }
}
