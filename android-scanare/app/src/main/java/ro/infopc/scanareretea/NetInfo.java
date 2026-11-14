package ro.infopc.scanareretea;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.RouteInfo;
import android.os.Build;

import java.net.Inet4Address;
import java.net.InetAddress;

/** Informatii despre reteaua la care e conectat telefonul. */
public class NetInfo {
    public String ip = "", gateway = "", mask = "", dns = "", iface = "", kind = "", deviceName = "";
    public int prefix = 24;
    public boolean local; // WiFi sau cablu (nu date mobile)

    public static NetInfo read(Context ctx) {
        NetInfo n = new NetInfo();
        n.deviceName = Build.MANUFACTURER + " " + Build.MODEL;
        ConnectivityManager cm = (ConnectivityManager) ctx.getSystemService(Context.CONNECTIVITY_SERVICE);
        if (cm == null) return null;
        Network best = null;
        Network act = cm.getActiveNetwork();
        // prefera WiFi / Ethernet chiar daca reteaua activa e cea mobila
        for (Network net : cm.getAllNetworks()) {
            NetworkCapabilities c = cm.getNetworkCapabilities(net);
            if (c == null) continue;
            if (c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) { best = net; break; }
        }
        if (best == null) best = act;
        if (best == null) return null;
        NetworkCapabilities caps = cm.getNetworkCapabilities(best);
        LinkProperties lp = cm.getLinkProperties(best);
        if (lp == null) return null;
        if (caps != null) {
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) { n.kind = "WiFi"; n.local = true; }
            else if (caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) { n.kind = "Cablu"; n.local = true; }
            else if (caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) n.kind = "Date mobile";
            else n.kind = "Altă rețea";
        }
        n.iface = lp.getInterfaceName() == null ? "" : lp.getInterfaceName();
        for (LinkAddress la : lp.getLinkAddresses()) {
            InetAddress a = la.getAddress();
            if (a instanceof Inet4Address) {
                n.ip = a.getHostAddress();
                n.prefix = la.getPrefixLength();
                long m = n.prefix == 0 ? 0 : (0xFFFFFFFFL << (32 - n.prefix)) & 0xFFFFFFFFL;
                n.mask = Scanner.fromLong(m);
                break;
            }
        }
        for (RouteInfo r : lp.getRoutes()) {
            if (r.isDefaultRoute() && r.getGateway() instanceof Inet4Address) { n.gateway = r.getGateway().getHostAddress(); break; }
        }
        StringBuilder sb = new StringBuilder();
        for (InetAddress d : lp.getDnsServers()) if (d instanceof Inet4Address) { if (sb.length() > 0) sb.append(", "); sb.append(d.getHostAddress()); }
        n.dns = sb.toString();
        return n.ip.isEmpty() ? null : n;
    }

    public String[] defaultRange() {
        long ip = Scanner.toLong(this.ip);
        int p = prefix < 22 ? 24 : Math.min(prefix, 30);
        long mask = (0xFFFFFFFFL << (32 - p)) & 0xFFFFFFFFL;
        long net = ip & mask, bc = net | (~mask & 0xFFFFFFFFL);
        return new String[]{Scanner.fromLong(net + 1), Scanner.fromLong(bc - 1)};
    }
}
