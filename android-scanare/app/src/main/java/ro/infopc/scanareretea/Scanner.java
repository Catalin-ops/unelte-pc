package ro.infopc.scanareretea;

import android.content.Context;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** Motorul de scanare: gaseste aparatele din retea si incearca sa le identifice. */
public class Scanner {

    public static class Host {
        public String ip;
        public long ipSort;
        public String mac = "", vendor = "", name = "", type = "", icon = "", portsText = "", details = "", latency = "";
        public String title = "", server = "", realm = "";
        public boolean isMe, isGateway, randomMac;
        public List<Integer> ports = new ArrayList<>();
    }

    public interface Listener {
        void progress(int done, int total, String text);
        void found(Host h);
        void updated(Host h);
        void finished(boolean cancelled);
    }

    static final int[] PORTS = {21, 22, 23, 53, 80, 135, 139, 443, 445, 515, 554, 631, 1883, 3389, 5000, 5001,
            5900, 7000, 8000, 8008, 8009, 8080, 8291, 8443, 9100, 9198, 34567, 37777, 62078};
    static final int[] QUICK = {80, 443, 445, 139, 22, 554, 9100, 8080, 62078, 9198, 7000, 8009};
    static final Map<Integer, String> PORT_NAMES = new HashMap<>();
    static {
        Object[][] p = {{21,"FTP"},{22,"SSH"},{23,"Telnet"},{53,"DNS"},{80,"Web"},{135,"RPC"},{139,"NetBIOS"},{443,"HTTPS"},
                {445,"Partajare"},{515,"LPD"},{554,"RTSP"},{631,"IPP"},{1883,"MQTT"},{3389,"RDP"},{5900,"VNC"},{7000,"AirPlay"},
                {8009,"Cast"},{8080,"Web 8080"},{8291,"Winbox"},{8443,"HTTPS 8443"},{9100,"Tipărire"},{9198,"Casă marcat"},
                {34567,"DVR"},{37777,"Dahua"},{62078,"iPhone"}};
        for (Object[] o : p) PORT_NAMES.put((Integer) o[0], (String) o[1]);
    }

    public volatile boolean cancel;
    private final Context ctx;
    private Map<String, String> oui;

    public Scanner(Context ctx) { this.ctx = ctx.getApplicationContext(); }

    // ------------------------------------------------------------------ utilitare IP
    public static long toLong(String ip) {
        String[] p = ip.split("\\.");
        return (Long.parseLong(p[0]) << 24) | (Long.parseLong(p[1]) << 16) | (Long.parseLong(p[2]) << 8) | Long.parseLong(p[3]);
    }

    public static String fromLong(long v) {
        return ((v >> 24) & 255) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255);
    }

    // ------------------------------------------------------------------ producator dupa MAC
    private synchronized void loadOui() {
        if (oui != null) return;
        Map<String, String> d = new HashMap<>();
        try (InputStream in = ctx.getAssets().open("manuf.txt");
             BufferedReader r = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = r.readLine()) != null) {
                if (line.length() < 8 || line.charAt(0) == '#') continue;
                String[] f = line.split("\t");
                if (f.length < 2) continue;
                String key = f[0].trim();
                String name = (f.length >= 3 && !f[2].trim().isEmpty()) ? f[2].trim() : f[1].trim();
                int bits = 24;
                int slash = key.indexOf('/');
                if (slash > 0) { try { bits = Integer.parseInt(key.substring(slash + 1)); } catch (Exception ignored) {} key = key.substring(0, slash); }
                String hex = key.replace(":", "").replace("-", "").replace(".", "").toUpperCase(Locale.ROOT);
                int n = bits / 4;
                if (hex.length() < n || n < 6) continue;
                d.put(hex.substring(0, n), name);
            }
        } catch (Exception ignored) { }
        String[][] builtin = {{"C86C1E","Display Systems (Incotex)"},{"403F8C","TP-Link"},{"18D6C7","TP-Link"},{"8C902D","TP-Link"},
                {"6C63F8","Ubiquiti"},{"9C934E","Xerox"},{"609BB4","Huawei"},{"606682","Ateko (TV/media)"},{"B827EB","Raspberry Pi"},
                {"BC2411","Hikvision"},{"4C11BF","Dahua"},{"240AC4","Espressif"},{"001132","Synology"}};
        for (String[] b : builtin) if (!d.containsKey(b[0])) d.put(b[0], b[1]);
        oui = d;
    }

    public String vendor(String mac) {
        if (mac == null || mac.isEmpty()) return "";
        loadOui();
        String hex = mac.replace(":", "").replace("-", "").toUpperCase(Locale.ROOT);
        String v;
        if (hex.length() >= 9 && (v = oui.get(hex.substring(0, 9))) != null) return v;
        if (hex.length() >= 7 && (v = oui.get(hex.substring(0, 7))) != null) return v;
        if (hex.length() >= 6 && (v = oui.get(hex.substring(0, 6))) != null) return v;
        return "";
    }

    static boolean isRandomMac(String mac) {
        if (mac == null || mac.length() < 2) return false;
        try { return (Integer.parseInt(mac.substring(0, 2), 16) & 0x02) != 0; } catch (Exception e) { return false; }
    }

    // tabela ARP (functioneaza pe Android 9 si mai vechi; pe 10+ e blocata de sistem)
    public static Map<String, String> arpTable() {
        Map<String, String> res = new HashMap<>();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(new java.io.FileInputStream("/proc/net/arp")))) {
            String line; r.readLine();
            while ((line = r.readLine()) != null) {
                String[] f = line.trim().split("\\s+");
                if (f.length >= 4 && f[3].matches("..:..:..:..:..:..") && !f[3].equals("00:00:00:00:00:00")) res.put(f[0], f[3].toUpperCase(Locale.ROOT));
            }
        } catch (Exception ignored) { }
        if (res.isEmpty()) {
            try {
                Process p = Runtime.getRuntime().exec(new String[]{"ip", "neigh", "show"});
                try (BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream()))) {
                    String line;
                    while ((line = r.readLine()) != null) {
                        String[] f = line.trim().split("\\s+");
                        for (int i = 0; i < f.length - 1; i++)
                            if (f[i].equals("lladdr") && f[i + 1].matches("..:..:..:..:..:..")) res.put(f[0], f[i + 1].toUpperCase(Locale.ROOT));
                    }
                }
                p.waitFor(2, TimeUnit.SECONDS);
            } catch (Exception ignored) { }
        }
        return res;
    }

    // ------------------------------------------------------------------ sonde
    static boolean portOpen(String ip, int port, int timeout) {
        try (Socket s = new Socket()) {
            s.connect(new InetSocketAddress(ip, port), timeout);
            return true;
        } catch (Exception e) { return false; }
    }

    static String netBiosName(String ip, int timeout) {
        try (DatagramSocket s = new DatagramSocket()) {
            byte[] q = new byte[50];
            q[0] = (byte) 0x81; q[1] = 0x37; q[5] = 1;
            q[12] = 0x20; q[13] = 'C'; q[14] = 'K';
            for (int i = 15; i < 45; i++) q[i] = 'A';
            q[47] = 0x21; q[49] = 0x01;
            s.setSoTimeout(timeout);
            s.send(new DatagramPacket(q, q.length, InetAddress.getByName(ip), 137));
            byte[] r = new byte[1024];
            DatagramPacket pk = new DatagramPacket(r, r.length);
            s.receive(pk);
            int len = pk.getLength();
            if (len < 57) return "";
            int n = r[56] & 255;
            for (int i = 0; i < n; i++) {
                int o = 57 + i * 18;
                if (o + 18 > len) break;
                String name = new String(r, o, 15, StandardCharsets.US_ASCII).trim();
                int suffix = r[o + 15] & 255;
                boolean group = (r[o + 16] & 0x80) != 0;
                if (suffix == 0 && !group && !name.isEmpty()) return name;
            }
        } catch (Exception ignored) { }
        return "";
    }

    private static boolean sslReady;
    private static void trustAll() {
        if (sslReady) return;
        sslReady = true;
        try {
            TrustManager[] tm = {new X509TrustManager() {
                public void checkClientTrusted(X509Certificate[] c, String a) { }
                public void checkServerTrusted(X509Certificate[] c, String a) { }
                public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            }};
            SSLContext sc = SSLContext.getInstance("TLS");
            sc.init(null, tm, new java.security.SecureRandom());
            HttpsURLConnection.setDefaultSSLSocketFactory(sc.getSocketFactory());
            HttpsURLConnection.setDefaultHostnameVerifier((h, s) -> true);
        } catch (Exception ignored) { }
    }

    static final Pattern TITLE = Pattern.compile("<title[^>]*>\\s*(.*?)\\s*</title>", Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    static final Pattern REALM = Pattern.compile("realm=\"([^\"]*)\"", Pattern.CASE_INSENSITIVE);

    static void httpProbe(Host h) {
        String url;
        if (h.ports.contains(80)) url = "http://" + h.ip + "/";
        else if (h.ports.contains(8080)) url = "http://" + h.ip + ":8080/";
        else if (h.ports.contains(443)) url = "https://" + h.ip + "/";
        else if (h.ports.contains(8443)) url = "https://" + h.ip + ":8443/";
        else if (h.ports.contains(8000)) url = "http://" + h.ip + ":8000/";
        else return;
        trustAll();
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(2500); c.setReadTimeout(2500);
            c.setInstanceFollowRedirects(true);
            c.setRequestProperty("User-Agent", "Mozilla/5.0 ScanareRetea");
            int code = c.getResponseCode();
            String srv = c.getHeaderField("Server");
            if (srv != null) h.server = srv;
            String auth = c.getHeaderField("WWW-Authenticate");
            if (auth != null) { Matcher m = REALM.matcher(auth); if (m.find()) h.realm = m.group(1); }
            InputStream in = code >= 400 ? c.getErrorStream() : c.getInputStream();
            if (in != null) {
                byte[] buf = new byte[65536]; int total = 0, n;
                while (total < buf.length && (n = in.read(buf, total, buf.length - total)) > 0) total += n;
                in.close();
                Matcher m = TITLE.matcher(new String(buf, 0, total, StandardCharsets.UTF_8));
                if (m.find()) {
                    String t = android.text.Html.fromHtml(m.group(1).replaceAll("\\s+", " "), android.text.Html.FROM_HTML_MODE_LEGACY).toString().trim();
                    h.title = t.length() > 80 ? t.substring(0, 80) : t;
                }
            }
        } catch (Exception ignored) {
        } finally { if (c != null) c.disconnect(); }
    }

    // ------------------------------------------------------------------ scanare
    public void scan(NetInfo me, String from, String to, Listener l) {
        cancel = false;
        long a = toLong(from), b = toLong(to);
        if (b < a) { long t = a; a = b; b = t; }
        if (b - a > 1023) b = a + 1023;
        final List<String> ips = new ArrayList<>();
        for (long v = a; v <= b; v++) ips.add(fromLong(v));
        final int total = ips.size();
        final Map<String, Host> alive = Collections.synchronizedMap(new HashMap<>());
        final AtomicInteger done = new AtomicInteger();

        ExecutorService pool = Executors.newFixedThreadPool(64);
        for (final String ip : ips) {
            pool.submit(() -> {
                if (cancel) return;
                boolean isMe = me != null && ip.equals(me.ip);
                long t0 = System.currentTimeMillis();
                boolean ok = isMe;
                String lat = isMe ? "-" : "";
                if (!ok) {
                    try { ok = InetAddress.getByName(ip).isReachable(700); } catch (Exception ignored) { }
                    if (ok) lat = (System.currentTimeMillis() - t0) + " ms";
                }
                if (!ok) for (int p : QUICK) { if (cancel) break; if (portOpen(ip, p, 300)) { ok = true; lat = "fără ping"; break; } }
                if (ok) {
                    Host h = newHost(ip, me);
                    h.latency = lat;
                    alive.put(ip, h);
                    classify(h);
                    l.found(h);
                }
                int d = done.incrementAndGet();
                if (d % 4 == 0 || d == total) l.progress(d, total * 2, "Caut aparate… " + d + "/" + total);
            });
        }
        pool.shutdown();
        try { pool.awaitTermination(10, TimeUnit.MINUTES); } catch (InterruptedException ignored) { }

        // MAC din tabela ARP (doar unde sistemul permite)
        Map<String, String> arp = arpTable();
        Map<String, Integer> macCount = new HashMap<>();
        for (String m : arp.values()) macCount.put(m, macCount.containsKey(m) ? macCount.get(m) + 1 : 1);
        for (Map.Entry<String, String> e : arp.entrySet()) {
            long v; try { v = toLong(e.getKey()); } catch (Exception ex) { continue; }
            if (v < a || v > b) continue;
            Host h = alive.get(e.getKey());
            if (h == null) {
                if (cancel) break;
                if (macCount.get(e.getValue()) > 2) continue; // proxy ARP: acelasi MAC pentru multe IP-uri
                h = newHost(e.getKey(), me); h.latency = "fără ping";
                alive.put(e.getKey(), h);
                h.mac = e.getValue();
                h.vendor = vendor(h.mac); h.randomMac = isRandomMac(h.mac);
                classify(h); l.found(h);
            } else {
                h.mac = e.getValue();
                h.vendor = vendor(h.mac); h.randomMac = isRandomMac(h.mac);
            }
        }

        // etapa 2: identificare
        final List<Host> list = new ArrayList<>(alive.values());
        final AtomicInteger d2 = new AtomicInteger();
        final int t2 = Math.max(1, list.size());
        ExecutorService pool2 = Executors.newFixedThreadPool(16);
        final ExecutorService dnsPool = Executors.newCachedThreadPool();
        for (final Host h : list) {
            pool2.submit(() -> {
                if (cancel) return;
                ExecutorService pp = Executors.newFixedThreadPool(PORTS.length);
                final List<Integer> open = Collections.synchronizedList(new ArrayList<>());
                for (final int p : PORTS) pp.submit(() -> { if (portOpen(h.ip, p, 500)) open.add(p); });
                pp.shutdown();
                try { pp.awaitTermination(10, TimeUnit.SECONDS); } catch (InterruptedException ignored) { }
                List<Integer> sorted = new ArrayList<>(open);
                Collections.sort(sorted);
                h.ports = sorted;
                if (!h.isMe) {
                    String nb = netBiosName(h.ip, 900);
                    String dns = "";
                    try {
                        Future<String> f = dnsPool.submit(() -> {
                            String n = InetAddress.getByName(h.ip).getCanonicalHostName();
                            return n.equals(h.ip) ? "" : n;
                        });
                        dns = f.get(1500, TimeUnit.MILLISECONDS);
                    } catch (Exception ignored) { }
                    h.name = !nb.isEmpty() ? nb : dns;
                    if (!nb.isEmpty() && !dns.isEmpty() && !dns.toLowerCase(Locale.ROOT).startsWith(nb.toLowerCase(Locale.ROOT))) h.name = nb + " (" + dns + ")";
                    httpProbe(h);
                } else h.name = me.deviceName + " (acest telefon)";
                classify(h);
                l.updated(h);
                int d = d2.incrementAndGet();
                l.progress(total + (int) ((long) d * total / t2), total * 2, "Identific aparatele… " + d + "/" + list.size());
            });
        }
        pool2.shutdown();
        try { pool2.awaitTermination(10, TimeUnit.MINUTES); } catch (InterruptedException ignored) { }
        dnsPool.shutdownNow();
        l.progress(total * 2, total * 2, cancel ? "Oprit." : "Gata.");
        l.finished(cancel);
    }

    static Host newHost(String ip, NetInfo me) {
        Host h = new Host();
        h.ip = ip; h.ipSort = toLong(ip);
        h.isMe = me != null && ip.equals(me.ip);
        h.isGateway = me != null && ip.equals(me.gateway);
        return h;
    }

    // ------------------------------------------------------------------ tipul aparatului
    static boolean any(String s, String... keys) { for (String k : keys) if (s.contains(k)) return true; return false; }

    public static void classify(Host h) {
        String v = h.vendor.toLowerCase(Locale.ROOT);
        String t = (h.title + " " + h.server + " " + h.realm).toLowerCase(Locale.ROOT);
        String n = h.name.toLowerCase(Locale.ROOT);
        List<Integer> p = h.ports;
        String type, icon;
        if (h.isMe) { type = "Acest telefon"; icon = "📱"; }
        else if (h.isGateway) { type = "Router / gateway"; icon = "🌐"; }
        else if (p.contains(9198) || v.contains("display systems") || any(t, "incotex") || any(v, "datecs", "tremol", "daisy", "elcom"))
        { type = "Casă de marcat"; icon = "🧾"; }
        else if (p.contains(9100) || p.contains(515) || p.contains(631) || any(v, "xerox", "hewlett", "hp inc", "epson", "brother", "canon", "kyocera", "ricoh", "lexmark", "konica", "sharp corp", "oki data", "zebra", "pantum")
                || any(t, "printer", "laserjet", "officejet", "workcentre", "versalink", "imprimant", "print server", "printserv") || any(n, "printserv", "npi", "brn", "epson"))
        { type = "Imprimantă"; icon = "🖨️"; }
        else if (p.contains(554) || p.contains(37777) || p.contains(34567) || any(v, "hikvision", "dahua", "uniview", "ezviz", "imou", "axis comm", "hanwha", "reolink", "xiongmai", "tiandy", "milesight")
                || any(t, "nvr", "dvr", "ipcam", "camera", "netsurveillance", "web service", "hikvision", "dahua"))
        { type = "Cameră / DVR / NVR"; icon = "📷"; }
        else if (p.contains(8291) || any(v, "ubiquiti", "tp-link", "tplink", "mikrotik", "routerboard", "cisco", "netgear", "zyxel", "d-link", "tenda", "aruba", "ruckus", "cambium", "totolink", "mercusys", "edimax", "asustek", "linksys", "draytek", "fortinet", "grandstream")
                || any(t, "router", "access point", "archer", "unifi", "omada", "switch", "tl-wr", "tl-sg", "deco", "mikrotik", "routeros", "huawei"))
        { type = "Echipament rețea"; icon = "📶"; }
        else if (any(n, "tv", "webos", "bravia", "roku", "chromecast", "firetv", "androidtv") || p.contains(8008) || p.contains(8009) || any(v, "lg electronics", "ateko", "tcl", "hisense", "vestel", "roku", "amazon tech"))
        { type = "Televizor / media"; icon = "📺"; }
        else if (v.contains("apple") || p.contains(62078)) {
            if (p.contains(62078)) { type = "iPhone / iPad"; icon = "📱"; }
            else if (p.contains(445) || p.contains(22) || p.contains(5900)) { type = "Mac"; icon = "💻"; }
            else { type = "Dispozitiv Apple"; icon = "📱"; }
        }
        else if (p.contains(3389) || p.contains(135) || ((p.contains(445) || p.contains(139)) && !p.contains(22)) || any(n, "desktop-", "laptop-", "pc-"))
        { type = "Calculator Windows"; icon = "🖥️"; }
        else if (any(v, "synology", "qnap", "western digital", "buffalo", "asustor") || ((p.contains(5000) || p.contains(5001)) && p.contains(22)))
        { type = "NAS / stocare"; icon = "🗄️"; }
        else if (any(v, "espressif", "tuya", "shelly", "itead", "broadlink", "sonoff", "lumi", "meross") || p.contains(1883))
        { type = "Smart home / IoT"; icon = "💡"; }
        else if (v.contains("raspberry")) { type = "Raspberry Pi / Linux"; icon = "🐧"; }
        else if (h.randomMac || any(v, "samsung", "xiaomi", "huawei device", "oppo", "vivo", "oneplus", "motorola", "realme", "honor", "nokia", "google", "zte") || any(n, "android", "iphone", "galaxy", "redmi"))
        { type = "Telefon / tabletă"; icon = "📱"; }
        else if (p.contains(22)) { type = "Linux / server"; icon = "🐧"; }
        else if (p.contains(80) || p.contains(443) || p.contains(8080)) { type = "Aparat cu interfață web"; icon = "🔧"; }
        else { type = "Necunoscut"; icon = "❔"; }
        h.type = type; h.icon = icon;
        StringBuilder sb = new StringBuilder();
        for (int x : p) { if (sb.length() > 0) sb.append(", "); String nm = PORT_NAMES.get(x); sb.append(nm != null ? nm : String.valueOf(x)); }
        h.portsText = sb.toString();
        List<String> det = new ArrayList<>();
        if (!h.title.isEmpty()) det.add("„" + h.title + "”");
        if (!h.realm.isEmpty() && !h.realm.equals(h.title)) det.add(h.realm);
        if (!h.server.isEmpty()) det.add(h.server);
        if (h.randomMac && !h.isMe) det.add("MAC aleator");
        h.details = android.text.TextUtils.join(" · ", det);
    }
}
