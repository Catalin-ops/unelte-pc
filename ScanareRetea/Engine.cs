// Scanare Retea - Info PC Electric
// Motorul de scanare: gaseste aparatele din reteaua locala si incearca sa le identifice.
// Scris pentru compilatorul C# 5 inclus in .NET Framework 4.x (fara $"", ?. etc.).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScanareRetea
{
    public class LocalInfo
    {
        public string Adapter = "";
        public string Ip = "";
        public string Mask = "";
        public int Prefix = 24;
        public string Gateway = "";
        public string Mac = "";
        public string Dns = "";
        public string HostName = "";
        public string Kind = "";
    }

    public class Host
    {
        public string Ip { get; set; }
        public uint IpSort { get; set; }
        public string Mac { get; set; }
        public string Vendor { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Icon { get; set; }
        public string PortsText { get; set; }
        public string Details { get; set; }
        public string Latency { get; set; }
        public bool IsMe { get; set; }
        public bool IsGateway { get; set; }
        public List<int> Ports = new List<int>();
        public string Title = "";
        public string Server = "";
        public string Realm = "";
        public bool RandomMac;

        public Host() { Mac = ""; Vendor = ""; Name = ""; Type = ""; Icon = ""; PortsText = ""; Details = ""; Latency = ""; }
    }

    public static class Engine
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int len);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_IPNETROW
        {
            public int dwIndex;
            public int dwPhysAddrLen;
            public byte mac0, mac1, mac2, mac3, mac4, mac5, mac6, mac7;
            public int dwAddr;
            public int dwType;
        }

        // porturile verificate pe fiecare aparat gasit
        public static readonly int[] PortList = {
            21, 22, 23, 53, 80, 135, 139, 443, 445, 515, 554, 631, 1883, 3389, 5000, 5001,
            5900, 7000, 8000, 8008, 8009, 8080, 8291, 8443, 9100, 9198, 34567, 37777, 62078
        };

        public static readonly Dictionary<int, string> PortNames = new Dictionary<int, string> {
            {21,"FTP"},{22,"SSH"},{23,"Telnet"},{53,"DNS"},{80,"Web"},{135,"RPC"},{139,"NetBIOS"},
            {443,"HTTPS"},{445,"Partajare"},{515,"LPD"},{554,"RTSP"},{631,"IPP"},{1883,"MQTT"},
            {3389,"RDP"},{5000,"5000"},{5001,"5001"},{5900,"VNC"},{7000,"AirPlay"},{8000,"8000"},
            {8008,"8008"},{8009,"Cast"},{8080,"Web 8080"},{8291,"Winbox"},{8443,"HTTPS 8443"},
            {9100,"Tipărire"},{9198,"Casă marcat"},{34567,"DVR"},{37777,"Dahua"},{62078,"iPhone"}
        };

        // ---------------------------------------------------------------- informatii locale
        public static LocalInfo GetLocal()
        {
            LocalInfo best = null; int bestScore = -1;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties p;
                try { p = ni.GetIPProperties(); } catch { continue; }
                string gw = "";
                foreach (GatewayIPAddressInformation g in p.GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) { gw = g.Address.ToString(); break; }
                foreach (UnicastIPAddressInformation ua in p.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                    string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    int score = 0;
                    if (gw != "") score += 100;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 20;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 15;
                    if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("vpn") || desc.Contains("wsl")) score -= 60;
                    if (score <= bestScore) continue;
                    LocalInfo li = new LocalInfo();
                    li.Adapter = ni.Name;
                    li.Kind = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "WiFi" : (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Cablu" : ni.NetworkInterfaceType.ToString());
                    li.Ip = ip;
                    IPAddress mask = ua.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any)) mask = IPAddress.Parse("255.255.255.0");
                    li.Mask = mask.ToString();
                    li.Prefix = MaskToPrefix(mask);
                    li.Gateway = gw;
                    li.Mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes());
                    li.Dns = string.Join(", ", p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToArray());
                    li.HostName = Environment.MachineName;
                    best = li; bestScore = score;
                }
            }
            return best;
        }

        static int MaskToPrefix(IPAddress mask)
        {
            int n = 0;
            foreach (byte b in mask.GetAddressBytes()) for (int i = 7; i >= 0; i--) if ((b & (1 << i)) != 0) n++;
            return n;
        }

        public static uint ToUint(string ip)
        {
            byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static string FromUint(uint v)
        {
            return ((v >> 24) & 255) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255);
        }

        public static string FormatMac(byte[] b)
        {
            if (b == null || b.Length < 6) return "";
            bool allZero = true; foreach (byte x in b) if (x != 0) allZero = false;
            if (allZero) return "";
            return string.Join(":", b.Take(6).Select(x => x.ToString("X2")).ToArray());
        }

        // intervalul implicit: subretea proprie (limitata la /24 in jurul IP-ului daca e mai mare)
        public static void DefaultRange(LocalInfo li, out string from, out string to)
        {
            uint ip = ToUint(li.Ip);
            int prefix = li.Prefix < 22 ? 24 : li.Prefix;
            if (prefix > 30) prefix = 30;
            uint mask = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
            uint net = ip & mask, bc = net | ~mask;
            from = FromUint(net + 1); to = FromUint(bc - 1);
        }

        // ---------------------------------------------------------------- producator dupa MAC
        static Dictionary<string, string> oui;
        static readonly object ouiLock = new object();

        static void LoadOui()
        {
            lock (ouiLock)
            {
                if (oui != null) return;
                var d = new Dictionary<string, string>();
                try
                {
                    Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("manuf.txt");
                    if (s != null)
                        using (var r = new StreamReader(s, Encoding.UTF8))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                if (line.Length < 8 || line[0] == '#') continue;
                                string[] f = line.Split('\t');
                                if (f.Length < 2) continue;
                                string key = f[0].Trim();
                                string name = (f.Length >= 3 && f[2].Trim() != "") ? f[2].Trim() : f[1].Trim();
                                int bits = 24;
                                int slash = key.IndexOf('/');
                                if (slash > 0) { int.TryParse(key.Substring(slash + 1), out bits); key = key.Substring(0, slash); }
                                string hex = key.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
                                int nhex = bits / 4;
                                if (hex.Length < nhex || nhex < 6) continue;
                                d[hex.Substring(0, nhex)] = name;
                            }
                        }
                }
                catch { }
                foreach (var kv in Builtin) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
                oui = d;
            }
        }

        // cateva producatori des intalniti, folositi daca lista completa lipseste
        static readonly Dictionary<string, string> Builtin = new Dictionary<string, string> {
            {"C86C1E","Display Systems (Incotex)"},{"403F8C","TP-Link"},{"18D6C7","TP-Link"},{"8C902D","TP-Link"},
            {"6C63F8","Ubiquiti"},{"245A4C","Ubiquiti"},{"F09FC2","Ubiquiti"},{"788A20","Ubiquiti"},{"9C934E","Xerox"},
            {"609BB4","Huawei"},{"606682","Ateko (TV/media)"},{"68EDA4","Seavo"},{"FC4DD4","Universal Global Scientific"},
            {"A0A3B8","Wiscloud"},{"48210B","Pegatron"},{"3C5AB4","Google"},{"B827EB","Raspberry Pi"},{"DCA632","Raspberry Pi"},
            {"BC2411","Hikvision"},{"C056E3","Hikvision"},{"4C11BF","Dahua"},{"E0508B","Dahua"},{"240AC4","Espressif"},
            {"A4CF12","Espressif"},{"001132","Synology"},{"245EBE","QNAP"},{"000C29","VMware"},{"080027","VirtualBox"}
        };

        public static string Vendor(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            LoadOui();
            string hex = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
            string v;
            if (hex.Length >= 9 && oui.TryGetValue(hex.Substring(0, 9), out v)) return v;
            if (hex.Length >= 7 && oui.TryGetValue(hex.Substring(0, 7), out v)) return v;
            if (hex.Length >= 6 && oui.TryGetValue(hex.Substring(0, 6), out v)) return v;
            return "";
        }

        public static bool IsRandomMac(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length < 2) return false;
            int b = Convert.ToInt32(mac.Substring(0, 2), 16);
            return (b & 0x02) != 0;
        }

        // ---------------------------------------------------------------- descoperire
        static string ArpMac(string ip)
        {
            try
            {
                byte[] mac = new byte[6]; int len = 6;
                uint dst = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
                if (SendARP(dst, 0, mac, ref len) == 0 && len >= 6) return FormatMac(mac);
            }
            catch { }
            return "";
        }

        public static Dictionary<string, string> ArpTable()
        {
            var res = new Dictionary<string, string>();
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return res;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buf, ref size, false) != 0) return res;
                int count = Marshal.ReadInt32(buf);
                IntPtr row = new IntPtr(buf.ToInt64() + 4);
                int rs = Marshal.SizeOf(typeof(MIB_IPNETROW));
                for (int i = 0; i < count; i++)
                {
                    var r = (MIB_IPNETROW)Marshal.PtrToStructure(new IntPtr(row.ToInt64() + i * rs), typeof(MIB_IPNETROW));
                    if (r.dwType == 2 || r.dwPhysAddrLen < 6) continue; // invalid
                    string ip = new IPAddress(BitConverter.GetBytes(r.dwAddr)).ToString();
                    string mac = FormatMac(new byte[] { r.mac0, r.mac1, r.mac2, r.mac3, r.mac4, r.mac5 });
                    if (mac == "" || mac == "FF:FF:FF:FF:FF:FF") continue;
                    if (ip.EndsWith(".255") || ip.StartsWith("224.") || ip.StartsWith("239.")) continue;
                    res[ip] = mac;
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        static long PingMs(string ip, int timeout)
        {
            try
            {
                using (var p = new Ping())
                {
                    PingReply r = p.Send(ip, timeout);
                    if (r != null && r.Status == IPStatus.Success) return r.RoundtripTime;
                }
            }
            catch { }
            return -1;
        }

        public static bool PortOpen(string ip, int port, int timeout)
        {
            Socket s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult ar = s.BeginConnect(ip, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(timeout, false);
                if (ok && s.Connected) { try { s.EndConnect(ar); } catch { return false; } return true; }
                return false;
            }
            catch { return false; }
            finally { if (s != null) try { s.Close(); } catch { } }
        }

        static string ReverseDns(string ip, int timeout)
        {
            try
            {
                IAsyncResult ar = Dns.BeginGetHostEntry(ip, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeout, false)) return "";
                IPHostEntry e = Dns.EndGetHostEntry(ar);
                if (e == null || string.IsNullOrEmpty(e.HostName) || e.HostName == ip) return "";
                return e.HostName;
            }
            catch { return ""; }
        }

        // interogare NetBIOS (NBSTAT) pe UDP 137 - numele calculatorului Windows / imprimantei
        public static string NetBiosName(string ip, int timeout)
        {
            try
            {
                byte[] q = new byte[50];
                q[0] = 0x81; q[1] = 0x37; q[5] = 1;
                q[12] = 0x20; q[13] = (byte)'C'; q[14] = (byte)'K';
                for (int i = 15; i < 45; i++) q[i] = (byte)'A';
                q[45] = 0; q[47] = 0x21; q[49] = 0x01;
                using (var u = new UdpClient())
                {
                    u.Client.ReceiveTimeout = timeout;
                    u.Send(q, q.Length, new IPEndPoint(IPAddress.Parse(ip), 137));
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] r = u.Receive(ref ep);
                    if (r.Length < 57) return "";
                    int n = r[56];
                    for (int i = 0; i < n; i++)
                    {
                        int o = 57 + i * 18;
                        if (o + 18 > r.Length) break;
                        string name = Encoding.ASCII.GetString(r, o, 15).Trim();
                        byte suffix = r[o + 15];
                        bool group = (r[o + 16] & 0x80) != 0;
                        if (suffix == 0 && !group && name.Length > 0) return name;
                    }
                }
            }
            catch { }
            return "";
        }

        static bool certHooked;
        static void HttpProbe(Host h)
        {
            if (!certHooked)
            {
                certHooked = true;
                try
                {
                    ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192);
                }
                catch { }
            }
            string[] urls;
            if (h.Ports.Contains(80)) urls = new[] { "http://" + h.Ip + "/" };
            else if (h.Ports.Contains(8080)) urls = new[] { "http://" + h.Ip + ":8080/" };
            else if (h.Ports.Contains(443)) urls = new[] { "https://" + h.Ip + "/" };
            else if (h.Ports.Contains(8443)) urls = new[] { "https://" + h.Ip + ":8443/" };
            else if (h.Ports.Contains(8000)) urls = new[] { "http://" + h.Ip + ":8000/" };
            else return;
            foreach (string url in urls)
            {
                HttpWebResponse resp = null;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 2500; req.ReadWriteTimeout = 2500;
                    req.AllowAutoRedirect = true; req.MaximumAutomaticRedirections = 3;
                    req.UserAgent = "Mozilla/5.0 ScanareRetea";
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException we) { resp = we.Response as HttpWebResponse; }
                    if (resp == null) continue;
                    h.Server = resp.Headers["Server"] ?? "";
                    string auth = resp.Headers["WWW-Authenticate"] ?? "";
                    Match rm = Regex.Match(auth, "realm=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                    if (rm.Success) h.Realm = rm.Groups[1].Value;
                    using (Stream s = resp.GetResponseStream())
                    {
                        byte[] buf = new byte[65536]; int total = 0, n;
                        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
                        string html = Encoding.UTF8.GetString(buf, 0, total);
                        Match m = Regex.Match(html, "<title[^>]*>\\s*(.*?)\\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (m.Success) h.Title = WebUtilityDecode(m.Groups[1].Value).Trim();
                        if (h.Title.Length > 80) h.Title = h.Title.Substring(0, 80);
                    }
                    return;
                }
                catch { }
                finally { if (resp != null) try { resp.Close(); } catch { } }
            }
        }

        static string WebUtilityDecode(string s)
        {
            try { return WebUtility.HtmlDecode(Regex.Replace(s, "\\s+", " ")); } catch { return s; }
        }

        // ---------------------------------------------------------------- scanare
        public static volatile bool Cancel;

        public static void Scan(LocalInfo li, string fromIp, string toIp, Action<int, int, string> progress, Action<Host> found, Action<Host> updated)
        {
            Cancel = false;
            int wt, ct; ThreadPool.GetMinThreads(out wt, out ct);
            if (wt < 160) ThreadPool.SetMinThreads(160, Math.Max(ct, 160));

            uint a = ToUint(fromIp), b = ToUint(toIp);
            if (b < a) { uint t = a; a = b; b = t; }
            if (b - a > 4095) b = a + 4095;
            var ips = new List<string>();
            for (uint v = a; v <= b; v++) ips.Add(FromUint(v));

            var alive = new Dictionary<string, Host>();
            var lockObj = new object();
            int done = 0, total = ips.Count;
            bool sameNet = li != null;

            // etapa 1: ping + ARP in paralel
            var po = new ParallelOptions { MaxDegreeOfParallelism = 96 };
            Parallel.ForEach(ips, po, (ip, state) =>
            {
                if (Cancel) { state.Stop(); return; }
                long ms = PingMs(ip, 600);
                if (ms < 0) ms = PingMs(ip, 900);
                string mac = sameNet ? ArpMac(ip) : "";
                bool me = li != null && ip == li.Ip;
                if (ms >= 0 || mac != "" || me)
                {
                    Host h = NewHost(ip, li);
                    if (me) h.Mac = li.Mac; else h.Mac = mac;
                    h.Latency = me ? "-" : (ms >= 0 ? ms + " ms" : "fără ping");
                    lock (lockObj) alive[ip] = h;
                    h.Vendor = Vendor(h.Mac);
                    h.RandomMac = IsRandomMac(h.Mac);
                    Classify(h);
                    found(h);
                }
                int d = Interlocked.Increment(ref done);
                if (d % 4 == 0 || d == total) progress(d, total * 2, "Caut aparate... " + d + "/" + total);
            });

            // aparate vazute doar in tabela ARP a Windows-ului
            if (!Cancel)
                foreach (var kv in ArpTable())
                {
                    if (alive.ContainsKey(kv.Key)) continue;
                    uint v; try { v = ToUint(kv.Key); } catch { continue; }
                    if (v < a || v > b) continue;
                    Host h = NewHost(kv.Key, li);
                    h.Mac = kv.Value; h.Latency = "fără ping";
                    h.Vendor = Vendor(h.Mac); h.RandomMac = IsRandomMac(h.Mac);
                    Classify(h);
                    alive[kv.Key] = h; found(h);
                }

            // etapa 2: identificare (porturi, nume, pagina web)
            var list = alive.Values.ToList();
            int d2 = 0, t2 = list.Count;
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 24 }, (h, state) =>
            {
                if (Cancel) { state.Stop(); return; }
                var open = new List<int>();
                var openLock = new object();
                Parallel.ForEach(PortList, new ParallelOptions { MaxDegreeOfParallelism = PortList.Length }, p =>
                {
                    if (PortOpen(h.Ip, p, 450)) lock (openLock) open.Add(p);
                });
                open.Sort();
                h.Ports = open;
                if (!h.IsMe)
                {
                    string nb = NetBiosName(h.Ip, 800);
                    string dns = ReverseDns(h.Ip, 1500);
                    h.Name = nb != "" ? nb : dns;
                    if (nb != "" && dns != "" && !dns.StartsWith(nb, StringComparison.OrdinalIgnoreCase)) h.Name = nb + " (" + dns + ")";
                    HttpProbe(h);
                }
                else h.Name = li.HostName + " (acest calculator)";
                Classify(h);
                updated(h);
                int d = Interlocked.Increment(ref d2);
                progress(total + (int)((long)d * total / Math.Max(1, t2)), total * 2, "Identific aparatele... " + d + "/" + t2);
            });
            progress(total * 2, total * 2, Cancel ? "Oprit." : "Gata.");
        }

        static Host NewHost(string ip, LocalInfo li)
        {
            Host h = new Host();
            h.Ip = ip; h.IpSort = ToUint(ip);
            h.IsMe = li != null && ip == li.Ip;
            h.IsGateway = li != null && ip == li.Gateway;
            return h;
        }

        // ---------------------------------------------------------------- tipul aparatului
        static bool Any(string s, params string[] keys)
        {
            foreach (string k in keys) if (s.Contains(k)) return true;
            return false;
        }

        public static void Classify(Host h)
        {
            string v = (h.Vendor ?? "").ToLowerInvariant();
            string t = ((h.Title ?? "") + " " + (h.Server ?? "") + " " + (h.Realm ?? "")).ToLowerInvariant();
            string n = (h.Name ?? "").ToLowerInvariant();
            List<int> p = h.Ports;
            string type, icon;

            if (h.IsMe) { type = "Acest calculator"; icon = "💻"; }
            else if (h.IsGateway) { type = "Router / gateway"; icon = "🌐"; }
            else if (p.Contains(9198) || v.Contains("display systems") || Any(t, "incotex") || Any(v, "datecs", "tremol", "daisy", "elcom"))
            { type = "Casă de marcat"; icon = "🧾"; }
            else if (p.Contains(9100) || p.Contains(515) || p.Contains(631) || Any(v, "xerox", "hewlett", "hp inc", "epson", "brother", "canon", "kyocera", "ricoh", "lexmark", "konica", "sharp corp", "oki data", "zebra", "pantum")
                     || Any(t, "printer", "laserjet", "officejet", "workcentre", "versalink", "imprimant", "print server", "printserv") || Any(n, "printserv", "npi", "brn", "epson"))
            { type = "Imprimantă"; icon = "🖨"; }
            else if (p.Contains(554) || p.Contains(37777) || p.Contains(34567) || Any(v, "hikvision", "dahua", "uniview", "ezviz", "imou", "axis comm", "hanwha", "reolink", "xiongmai", "tiandy", "milesight")
                     || Any(t, "nvr", "dvr", "ipcam", "camera", "netsurveillance", "web service", "hikvision", "dahua"))
            { type = "Cameră / DVR / NVR"; icon = "📷"; }
            else if (p.Contains(8291) || Any(v, "ubiquiti", "tp-link", "tplink", "mikrotik", "routerboard", "cisco", "netgear", "zyxel", "d-link", "tenda", "aruba", "ruckus", "cambium", "totolink", "mercusys", "edimax", "asustek", "linksys", "draytek", "fortinet", "grandstream")
                     || Any(t, "router", "access point", "archer", "unifi", "omada", "switch", "tl-wr", "tl-sg", "deco", "mikrotik", "routeros", "huawei"))
            { type = "Echipament rețea"; icon = "📶"; }
            else if (Any(n, "tv", "webos", "bravia", "roku", "chromecast", "firetv", "androidtv") || p.Contains(8008) || p.Contains(8009) || Any(v, "lg electronics", "ateko", "tcl", "hisense", "vestel", "roku", "amazon tech"))
            { type = "Televizor / media"; icon = "📺"; }
            else if (v.Contains("apple"))
            {
                if (p.Contains(62078)) { type = "iPhone / iPad"; icon = "📱"; }
                else if (p.Contains(445) || p.Contains(22) || p.Contains(5900)) { type = "Mac"; icon = "💻"; }
                else { type = "Dispozitiv Apple"; icon = "📱"; }
            }
            else if (p.Contains(3389) || p.Contains(135) || ((p.Contains(445) || p.Contains(139)) && !p.Contains(22)) || Any(n, "desktop-", "laptop-", "pc-"))
            { type = "Calculator Windows"; icon = "🖥"; }
            else if (Any(v, "synology", "qnap", "western digital", "buffalo", "asustor") || ((p.Contains(5000) || p.Contains(5001)) && p.Contains(22)))
            { type = "NAS / stocare"; icon = "🗄"; }
            else if (Any(v, "espressif", "tuya", "shelly", "itead", "broadlink", "sonoff", "lumi", "meross") || p.Contains(1883))
            { type = "Smart home / IoT"; icon = "💡"; }
            else if (Any(v, "raspberry")) { type = "Raspberry Pi / Linux"; icon = "🐧"; }
            else if (h.RandomMac || Any(v, "samsung", "xiaomi", "huawei device", "oppo", "vivo", "oneplus", "motorola", "realme", "honor", "nokia", "google", "zte", "lenovo mobile") || Any(n, "android", "iphone", "galaxy", "redmi"))
            { type = "Telefon / tabletă"; icon = "📱"; }
            else if (p.Contains(22)) { type = "Linux / server"; icon = "🐧"; }
            else if (p.Contains(80) || p.Contains(443) || p.Contains(8080)) { type = "Aparat cu interfață web"; icon = "🔧"; }
            else { type = "Necunoscut"; icon = "❔"; }

            h.Type = type; h.Icon = icon;
            h.PortsText = string.Join(", ", p.Select(x => PortNames.ContainsKey(x) ? PortNames[x] : x.ToString()).ToArray());
            var det = new List<string>();
            if (h.Title != "") det.Add("„" + h.Title + "”");
            if (h.Realm != "" && h.Realm != h.Title) det.Add(h.Realm);
            if (h.Server != "") det.Add(h.Server);
            if (h.RandomMac && !h.IsMe) det.Add("MAC aleator (telefon/laptop cu confidențialitate)");
            h.Details = string.Join(" · ", det.ToArray());
        }

        // test fara interfata, folosit in compilarea automata
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            LocalInfo li = GetLocal();
            if (li == null) return "Nicio placa de retea activa.";
            sb.AppendLine("IP: " + li.Ip + "/" + li.Prefix + " gw " + li.Gateway + " mac " + li.Mac + " (" + li.Adapter + ")");
            sb.AppendLine("OUI 9C:93:4E -> " + Vendor("9C:93:4E:00:00:00") + " | C8:6C:1E -> " + Vendor("C8:6C:1E:16:12:FF"));
            string from, to; DefaultRange(li, out from, out to);
            uint a = ToUint(li.Ip);
            string f2 = FromUint(Math.Max(ToUint(from), a > 8 ? a - 8 : a)), t2 = FromUint(Math.Min(ToUint(to), a + 8));
            var found = new List<Host>();
            Scan(li, f2, t2, (x, y, s) => { }, h => { lock (found) found.Add(h); }, h => { });
            foreach (Host h in found.OrderBy(x => x.IpSort))
                sb.AppendLine(h.Ip + " | " + h.Type + " | " + h.Mac + " | " + h.Vendor + " | " + h.Name + " | " + h.PortsText + " | " + h.Details);
            var t = new Host(); t.Ip = "10.0.0.5"; t.Vendor = "Xerox Corporation"; t.Ports.Add(80); Classify(t);
            if (t.Type != "Imprimantă") throw new Exception("Clasificare gresita: " + t.Type);
            t = new Host(); t.Ip = "10.0.0.6"; t.Ports.Add(9198); Classify(t);
            if (t.Type != "Casă de marcat") throw new Exception("Clasificare gresita: " + t.Type);
            return sb.ToString();
        }
    }
}
