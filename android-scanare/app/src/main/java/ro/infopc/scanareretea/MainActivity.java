package ro.infopc.scanareretea;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

public class MainActivity extends Activity {
    static final int BG = Color.parseColor("#0E131F"), CARD = Color.parseColor("#151C2C"), LINE = Color.parseColor("#24304A"),
            TEXT = Color.parseColor("#E6EAF2"), MUTED = Color.parseColor("#8A96B0"), GREEN = Color.parseColor("#4ADE80"),
            BLUE = Color.parseColor("#2563EB"), RED = Color.parseColor("#B91C1C");

    final Handler ui = new Handler(Looper.getMainLooper());
    final List<Scanner.Host> all = new ArrayList<>();
    final List<Scanner.Host> shown = new ArrayList<>();
    TextView myIp, myDetails, publicIp, status, count;
    EditText fromBox, toBox, filterBox;
    Button scanBtn;
    ProgressBar bar;
    HostAdapter adapter;
    NetInfo net;
    Scanner scanner;
    boolean running;

    int dp(float v) { return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()); }

    TextView text(String s, float sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    GradientDrawable round(int color, int stroke, float r) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color); g.setCornerRadius(dp(r));
        if (stroke != 0) g.setStroke(dp(1), stroke);
        return g;
    }

    EditText box(String hint) {
        EditText e = new EditText(this);
        e.setHint(hint); e.setTextColor(TEXT); e.setHintTextColor(MUTED); e.setTextSize(15);
        e.setSingleLine(true);
        e.setBackground(round(BG, LINE, 8));
        e.setPadding(dp(10), dp(8), dp(10), dp(8));
        return e;
    }

    Button button(String s, int color) {
        Button b = new Button(this);
        b.setText(s); b.setTextColor(Color.WHITE); b.setAllCaps(false); b.setTextSize(15);
        b.setBackground(round(color, 0, 10));
        return b;
    }

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        scanner = new Scanner(this);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BG);
        root.setPadding(dp(14), dp(14), dp(14), dp(8));

        // ---- antet cu IP-ul meu
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.VERTICAL);
        head.setBackground(round(CARD, Color.parseColor("#1F5F3F"), 14));
        head.setPadding(dp(16), dp(12), dp(16), dp(12));
        head.addView(text("IP-UL MEU", 12, MUTED, true));
        myIp = text("…", 38, GREEN, true);
        myIp.setOnClickListener(v -> copy(net != null ? net.ip : "", "IP copiat"));
        head.addView(myIp);
        myDetails = text("", 13, Color.parseColor("#C9D2E3"), false);
        head.addView(myDetails);
        publicIp = text("IP public: se verifică…", 13, MUTED, false);
        head.addView(publicIp);
        root.addView(head);

        // ---- interval + butoane
        LinearLayout range = new LinearLayout(this);
        range.setOrientation(LinearLayout.HORIZONTAL);
        range.setGravity(Gravity.CENTER_VERTICAL);
        range.setPadding(0, dp(10), 0, 0);
        fromBox = box("de la"); toBox = box("până la");
        fromBox.setInputType(InputType.TYPE_CLASS_TEXT); toBox.setInputType(InputType.TYPE_CLASS_TEXT);
        range.addView(fromBox, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        TextView dash = text("  –  ", 15, MUTED, false);
        range.addView(dash);
        range.addView(toBox, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(range);

        LinearLayout btns = new LinearLayout(this);
        btns.setOrientation(LinearLayout.HORIZONTAL);
        btns.setPadding(0, dp(8), 0, 0);
        scanBtn = button("▶  Scanează", BLUE);
        scanBtn.setOnClickListener(v -> { if (running) { scanner.cancel = true; status.setText("Se oprește…"); } else startScan(); });
        btns.addView(scanBtn, new LinearLayout.LayoutParams(0, dp(48), 2));
        Button refresh = button("Reîncarcă IP", LINE);
        refresh.setOnClickListener(v -> loadNet());
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(0, dp(48), 1.3f);
        rp.leftMargin = dp(8);
        btns.addView(refresh, rp);
        root.addView(btns);

        bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        bar.setMax(100);
        LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(10));
        bp.topMargin = dp(8);
        root.addView(bar, bp);
        status = text("Apasă „Scanează” ca să cauți aparatele din rețea.", 13, MUTED, false);
        root.addView(status);
        count = text("", 13, TEXT, true);
        root.addView(count);

        filterBox = box("Caută (IP, tip, nume…)");
        LinearLayout.LayoutParams fp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        fp.topMargin = dp(6);
        root.addView(filterBox, fp);
        filterBox.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            public void onTextChanged(CharSequence s, int a, int b, int c) { refreshList(); }
            public void afterTextChanged(Editable e) { }
        });

        ListView list = new ListView(this);
        list.setDivider(null);
        adapter = new HostAdapter();
        list.setAdapter(adapter);
        list.setOnItemClickListener((p, v, pos, id) -> open(shown.get(pos)));
        list.setOnItemLongClickListener((p, v, pos, id) -> { copy(shown.get(pos).ip, "IP copiat: " + shown.get(pos).ip); return true; });
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1);
        lp.topMargin = dp(6);
        root.addView(list, lp);

        TextView note = text("Atinge un aparat pentru pagina lui web, ține apăsat pentru a copia IP-ul."
                + (Build.VERSION.SDK_INT >= 29 ? " Pe Android 10+ sistemul ascunde adresele MAC, așa că tipul se ghicește după porturi, nume și pagina web." : ""), 11, Color.parseColor("#6B7891"), false);
        root.addView(note);

        setContentView(root);
        loadNet();
    }

    void loadNet() {
        net = NetInfo.read(this);
        if (net == null) {
            myIp.setText("Fără rețea"); myIp.setTextColor(Color.parseColor("#F87171"));
            myDetails.setText("Conectează telefonul la WiFi.");
            publicIp.setText("");
            return;
        }
        myIp.setText(net.ip); myIp.setTextColor(GREEN);
        myDetails.setText(net.kind + (net.iface.isEmpty() ? "" : " · " + net.iface) + "   |   Mască " + net.mask + " (/" + net.prefix + ")\nGateway " + (net.gateway.isEmpty() ? "—" : net.gateway)
                + "   |   DNS " + (net.dns.isEmpty() ? "—" : net.dns) + "\n" + net.deviceName);
        String[] r = net.defaultRange();
        fromBox.setText(r[0]); toBox.setText(r[1]);
        if (!net.local) status.setText("Ești pe date mobile — conectează-te la WiFi-ul rețelei pe care vrei s-o scanezi.");
        publicIp.setText("IP public: se verifică…");
        new Thread(() -> {
            String pub = "";
            for (String u : new String[]{"https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com"}) {
                try {
                    HttpURLConnection c = (HttpURLConnection) new URL(u).openConnection();
                    c.setConnectTimeout(4000); c.setReadTimeout(4000);
                    c.setRequestProperty("User-Agent", "curl/8");
                    try (BufferedReader br = new BufferedReader(new InputStreamReader(c.getInputStream()))) { pub = br.readLine(); }
                    if (pub != null && pub.trim().matches("\\d+\\.\\d+\\.\\d+\\.\\d+")) { pub = pub.trim(); break; }
                    pub = "";
                } catch (Exception ignored) { pub = ""; }
            }
            final String p = pub;
            ui.post(() -> publicIp.setText(p.isEmpty() ? "IP public: fără internet / necunoscut" : "IP public (internet): " + p));
        }).start();
    }

    void startScan() {
        if (net == null) loadNet();
        if (net == null) return;
        final String from = fromBox.getText().toString().trim(), to = toBox.getText().toString().trim();
        try { Scanner.toLong(from); Scanner.toLong(to); } catch (Exception e) { status.setText("Intervalul de IP-uri nu e valid."); return; }
        all.clear(); refreshList();
        running = true;
        scanBtn.setText("■  Stop"); scanBtn.setBackground(round(RED, 0, 10));
        final long t0 = System.currentTimeMillis();
        new Thread(() -> scanner.scan(net, from, to, new Scanner.Listener() {
            public void progress(int d, int t, String s) { ui.post(() -> { bar.setMax(t); bar.setProgress(d); status.setText(s); }); }
            public void found(Scanner.Host h) { ui.post(() -> { if (!all.contains(h)) all.add(h); refreshList(); }); }
            public void updated(Scanner.Host h) { ui.post(() -> refreshList()); }
            public void finished(boolean cancelled) {
                ui.post(() -> {
                    running = false;
                    scanBtn.setText("▶  Scanează"); scanBtn.setBackground(round(BLUE, 0, 10));
                    status.setText((cancelled ? "Scanare oprită" : "Scanare terminată") + " în " + (System.currentTimeMillis() - t0) / 1000 + " s.");
                    refreshList();
                });
            }
        })).start();
    }

    void refreshList() {
        String f = filterBox == null ? "" : filterBox.getText().toString().trim().toLowerCase(Locale.ROOT);
        shown.clear();
        for (Scanner.Host h : all) {
            if (f.isEmpty() || (h.ip + " " + h.type + " " + h.name + " " + h.vendor + " " + h.mac + " " + h.portsText + " " + h.details).toLowerCase(Locale.ROOT).contains(f))
                shown.add(h);
        }
        Collections.sort(shown, Comparator.comparingLong(h -> h.ipSort));
        adapter.notifyDataSetChanged();
        Map<String, Integer> groups = new LinkedHashMap<>();
        for (Scanner.Host h : all) if (!h.isMe) groups.put(h.type, groups.containsKey(h.type) ? groups.get(h.type) + 1 : 1);
        List<Map.Entry<String, Integer>> g = new ArrayList<>(groups.entrySet());
        Collections.sort(g, (x, y) -> y.getValue() - x.getValue());
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < Math.min(3, g.size()); i++) { if (sb.length() > 0) sb.append(", "); sb.append(g.get(i).getValue()).append(" ").append(g.get(i).getKey().toLowerCase(Locale.ROOT)); }
        count.setText(all.isEmpty() ? "" : all.size() + " aparate" + (sb.length() > 0 ? "  ·  " + sb : ""));
    }

    void open(Scanner.Host h) {
        String url = "http://" + h.ip + "/";
        if (!h.ports.contains(80)) {
            if (h.ports.contains(443)) url = "https://" + h.ip + "/";
            else if (h.ports.contains(8080)) url = "http://" + h.ip + ":8080/";
            else if (h.ports.contains(8443)) url = "https://" + h.ip + ":8443/";
            else if (h.ports.contains(8000)) url = "http://" + h.ip + ":8000/";
        }
        try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url))); } catch (Exception e) { Toast.makeText(this, "Nu pot deschide " + url, Toast.LENGTH_SHORT).show(); }
    }

    void copy(String s, String msg) {
        if (s == null || s.isEmpty()) return;
        ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        if (cm != null) cm.setPrimaryClip(ClipData.newPlainText("IP", s));
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show();
    }

    class HostAdapter extends BaseAdapter {
        public int getCount() { return shown.size(); }
        public Object getItem(int p) { return shown.get(p); }
        public long getItemId(int p) { return p; }

        public View getView(int pos, View cv, ViewGroup parent) {
            Scanner.Host h = shown.get(pos);
            LinearLayout row = new LinearLayout(MainActivity.this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            int border = h.isMe ? Color.parseColor("#1F5F3F") : (h.isGateway ? Color.parseColor("#1E3A8A") : LINE);
            row.setBackground(round(h.isMe ? Color.parseColor("#123524") : (h.isGateway ? Color.parseColor("#13254A") : CARD), border, 12));
            row.setPadding(dp(12), dp(10), dp(12), dp(10));
            ViewGroup.MarginLayoutParams mp = new ViewGroup.MarginLayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            TextView icon = text(h.icon, 26, TEXT, false);
            icon.setPadding(0, 0, dp(12), 0);
            row.addView(icon);
            LinearLayout col = new LinearLayout(MainActivity.this);
            col.setOrientation(LinearLayout.VERTICAL);
            LinearLayout top = new LinearLayout(MainActivity.this);
            top.setOrientation(LinearLayout.HORIZONTAL);
            TextView ip = text(h.ip, 16, TEXT, true);
            top.addView(ip, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            top.addView(text(h.type, 13, GREEN, true));
            col.addView(top);
            if (!h.name.isEmpty()) col.addView(text(h.name, 13, Color.parseColor("#C9D2E3"), false));
            String vm = (h.vendor.isEmpty() ? "" : h.vendor) + (h.mac.isEmpty() ? "" : (h.vendor.isEmpty() ? "" : " · ") + h.mac);
            if (!vm.isEmpty()) col.addView(text(vm, 12, MUTED, false));
            String pl = (h.portsText.isEmpty() ? "" : h.portsText) + (h.latency.isEmpty() ? "" : (h.portsText.isEmpty() ? "" : "   ·   ") + h.latency);
            if (!pl.isEmpty()) col.addView(text(pl, 12, MUTED, false));
            if (!h.details.isEmpty()) col.addView(text(h.details, 12, Color.parseColor("#93C5FD"), false));
            row.addView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            LinearLayout wrap = new LinearLayout(MainActivity.this);
            wrap.setPadding(0, dp(3), 0, dp(3));
            wrap.addView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            return wrap;
        }
    }
}
