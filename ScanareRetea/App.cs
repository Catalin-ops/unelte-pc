// Scanare Retea - Info PC Electric
// Interfata WPF construita din cod (fara XAML). IP-ul propriu e afisat mare, sus.
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

[assembly: AssemblyTitle("Scanare Rețea")]
[assembly: AssemblyDescription("Scanează rețeaua locală și identifică aparatele")]
[assembly: AssemblyCompany("Info PC Electric S.R.L.")]
[assembly: AssemblyProduct("Scanare Rețea")]
[assembly: AssemblyCopyright("© 2026 Info PC Electric")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ScanareRetea
{
    public class MainApp
    {
        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        Window win;
        TextBlock myIp, myDetails, publicIp, status, countText;
        TextBox fromBox, toBox, filterBox;
        Button scanBtn, stopBtn, exportBtn;
        ProgressBar bar;
        DataGrid grid;
        ObservableCollection<Host> hosts = new ObservableCollection<Host>();
        ICollectionView view;
        LocalInfo local;
        Thread worker;
        bool autoScan;

        [STAThread]
        public static void Main(string[] args)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var m = new MainApp();
            m.autoScan = args.Any(a => a == "--scaneaza");
            m.Build();
            app.Run(m.win);
        }

        TextBlock Txt(string s, double size, string color, FontWeight w)
        {
            return new TextBlock { Text = s, FontSize = size, Foreground = B(color), FontWeight = w, TextWrapping = TextWrapping.Wrap };
        }

        Border Card(UIElement child)
        {
            return new Border { Background = B("#151C2C"), BorderBrush = B("#24304A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12), Child = child };
        }

        Button Btn(string text, string bg)
        {
            var b = new Button { Content = text, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(0) };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            bd.SetValue(Border.BackgroundProperty, B(bg));
            bd.SetValue(Border.PaddingProperty, new Thickness(18, 8, 18, 8));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;
            return b;
        }

        TextBox Box(string text, double width)
        {
            return new TextBox { Text = text, Width = width, FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Background = B("#0E131F"), Foreground = B("#E6EAF2"), BorderBrush = B("#2E3B57"), CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center };
        }

        public void Build()
        {
            win = new Window
            {
                Title = "Scanare Rețea — Info PC Electric",
                Width = 1240, Height = 800, MinWidth = 900, MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = B("#0E131F"), FontFamily = new FontFamily("Segoe UI"), Foreground = B("#E6EAF2")
            };
            try { win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(Assembly.GetExecutingAssembly().GetManifestResourceStream("retea.ico")); } catch { }

            var root = new DockPanel { Margin = new Thickness(18) };

            // ---- antet: IP-ul meu
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(Txt("IP-UL MEU", 12, "#8A96B0", FontWeights.SemiBold));
            myIp = Txt("…", 40, "#4ADE80", FontWeights.Bold);
            myIp.Cursor = Cursors.Hand;
            myIp.ToolTip = "Click pentru copiere";
            myIp.MouseLeftButtonUp += delegate { try { Clipboard.SetText(local != null ? local.Ip : ""); status.Text = "IP copiat."; } catch { } };
            left.Children.Add(myIp);
            myDetails = Txt("", 13.5, "#C9D2E3", FontWeights.Normal);
            myDetails.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(myDetails);
            publicIp = Txt("IP public: se verifică…", 13.5, "#8A96B0", FontWeights.Normal);
            publicIp.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(publicIp);
            head.Children.Add(left);
            var brand = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            brand.Children.Add(Txt("Scanare Rețea", 18, "#E6EAF2", FontWeights.SemiBold));
            brand.Children.Add(Txt("Info PC Electric", 12, "#8A96B0", FontWeights.Normal));
            var refresh = Btn("Reîncarcă IP", "#24304A");
            refresh.Margin = new Thickness(0, 10, 0, 0);
            refresh.Click += delegate { LoadLocal(); };
            brand.Children.Add(refresh);
            Grid.SetColumn(brand, 1);
            head.Children.Add(brand);
            var headCard = Card(head);
            headCard.BorderBrush = B("#1F5F3F");
            DockPanel.SetDock(headCard, Dock.Top);
            root.Children.Add(headCard);

            // ---- bara de comenzi
            var bar1 = new DockPanel { LastChildFill = false };
            var rangePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            rangePanel.Children.Add(Txt("De la ", 14, "#8A96B0", FontWeights.Normal));
            fromBox = Box("", 140); rangePanel.Children.Add(fromBox);
            var la = Txt("  până la  ", 14, "#8A96B0", FontWeights.Normal); la.VerticalAlignment = VerticalAlignment.Center;
            rangePanel.Children.Add(la);
            toBox = Box("", 140); rangePanel.Children.Add(toBox);
            ((TextBlock)rangePanel.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            scanBtn = Btn("▶  Scanează", "#2563EB"); scanBtn.Click += delegate { StartScan(); };
            stopBtn = Btn("■  Stop", "#7F1D1D"); stopBtn.IsEnabled = false; stopBtn.Click += delegate { Engine.Cancel = true; status.Text = "Se oprește…"; };
            exportBtn = Btn("Export CSV", "#24304A"); exportBtn.Click += delegate { Export(); };
            rangePanel.Children.Add(scanBtn); rangePanel.Children.Add(stopBtn); rangePanel.Children.Add(exportBtn);
            DockPanel.SetDock(rangePanel, Dock.Left);
            bar1.Children.Add(rangePanel);
            var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var cauta = Txt("Caută ", 14, "#8A96B0", FontWeights.Normal); cauta.VerticalAlignment = VerticalAlignment.Center;
            filterPanel.Children.Add(cauta);
            filterBox = Box("", 220);
            filterBox.TextChanged += delegate { if (view != null) view.Refresh(); };
            filterPanel.Children.Add(filterBox);
            DockPanel.SetDock(filterPanel, Dock.Right);
            bar1.Children.Add(filterPanel);

            var progressPanel = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            countText = Txt("", 13, "#C9D2E3", FontWeights.SemiBold);
            DockPanel.SetDock(countText, Dock.Right);
            progressPanel.Children.Add(countText);
            status = Txt("Apasă „Scanează” ca să cauți aparatele din rețea.", 13, "#8A96B0", FontWeights.Normal);
            DockPanel.SetDock(status, Dock.Top);
            progressPanel.Children.Add(status);
            bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 6, 0, 0), Background = B("#0E131F"), Foreground = B("#2563EB"), BorderThickness = new Thickness(0), Maximum = 100 };
            progressPanel.Children.Add(bar);

            var ctl = new StackPanel();
            ctl.Children.Add(bar1); ctl.Children.Add(progressPanel);
            var ctlCard = Card(ctl);
            DockPanel.SetDock(ctlCard, Dock.Top);
            root.Children.Add(ctlCard);

            // ---- nota de jos
            var note = Txt("Dublu-click pe un aparat deschide pagina lui web. Tipul e ghicit după producătorul plăcii de rețea, porturile deschise, numele din rețea și pagina web.", 12, "#6B7891", FontWeights.Normal);
            note.Margin = new Thickness(4, 8, 0, 0);
            DockPanel.SetDock(note, Dock.Bottom);
            root.Children.Add(note);

            // ---- tabel
            grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = B("#1E2840"),
                Background = B("#151C2C"), RowBackground = B("#151C2C"), AlternatingRowBackground = B("#121827"),
                Foreground = B("#E6EAF2"), BorderBrush = B("#24304A"), BorderThickness = new Thickness(1), FontSize = 13.5,
                RowHeight = 34, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow
            };
            var hs = new Style(typeof(DataGridColumnHeader));
            hs.Setters.Add(new Setter(Control.BackgroundProperty, B("#1A2235")));
            hs.Setters.Add(new Setter(Control.ForegroundProperty, B("#8A96B0")));
            hs.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            hs.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
            hs.Setters.Add(new Setter(Control.BorderBrushProperty, B("#24304A")));
            hs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            grid.ColumnHeaderStyle = hs;
            var cs = new Style(typeof(DataGridCell));
            cs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cs.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            var sel = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Control.BackgroundProperty, B("#1E3A8A")));
            sel.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            cs.Triggers.Add(sel);
            grid.CellStyle = cs;
            var rs = new Style(typeof(DataGridRow));
            var meT = new DataTrigger { Binding = new Binding("IsMe"), Value = true };
            meT.Setters.Add(new Setter(Control.BackgroundProperty, B("#123524")));
            rs.Triggers.Add(meT);
            var gwT = new DataTrigger { Binding = new Binding("IsGateway"), Value = true };
            gwT.Setters.Add(new Setter(Control.BackgroundProperty, B("#13254A")));
            rs.Triggers.Add(gwT);
            grid.RowStyle = rs;

            AddCol("", "Icon", 46, null, 18);
            AddCol("Tip aparat", "Type", 170, null, 0);
            AddCol("IP", "Ip", 125, "IpSort", 0);
            AddCol("Nume în rețea", "Name", 200, null, 0);
            AddCol("Producător", "Vendor", 190, null, 0);
            AddCol("MAC", "Mac", 140, null, 0);
            AddCol("Răspuns", "Latency", 80, null, 0);
            AddCol("Porturi / servicii", "PortsText", 190, null, 0);
            AddCol("Detalii", "Details", 0, null, 0);

            grid.MouseDoubleClick += delegate { OpenSelected(); };
            var menu = new ContextMenu();
            var mi1 = new MenuItem { Header = "Deschide în browser" }; mi1.Click += delegate { OpenSelected(); }; menu.Items.Add(mi1);
            var mi2 = new MenuItem { Header = "Copiază IP" }; mi2.Click += delegate { var h = grid.SelectedItem as Host; if (h != null) try { Clipboard.SetText(h.Ip); } catch { } }; menu.Items.Add(mi2);
            var mi3 = new MenuItem { Header = "Copiază MAC" }; mi3.Click += delegate { var h = grid.SelectedItem as Host; if (h != null && h.Mac != "") try { Clipboard.SetText(h.Mac); } catch { } }; menu.Items.Add(mi3);
            var mi4 = new MenuItem { Header = "Partajare Windows (\\\\IP)" }; mi4.Click += delegate { var h = grid.SelectedItem as Host; if (h != null) try { Process.Start("explorer.exe", "\\\\" + h.Ip); } catch { } }; menu.Items.Add(mi4);
            grid.ContextMenu = menu;

            view = CollectionViewSource.GetDefaultView(hosts);
            view.SortDescriptions.Add(new SortDescription("IpSort", ListSortDirection.Ascending));
            view.Filter = o =>
            {
                string f = filterBox == null ? "" : filterBox.Text.Trim().ToLowerInvariant();
                if (f == "") return true;
                var h = (Host)o;
                return (h.Ip + " " + h.Type + " " + h.Name + " " + h.Vendor + " " + h.Mac + " " + h.PortsText + " " + h.Details).ToLowerInvariant().Contains(f);
            };
            grid.ItemsSource = view;
            root.Children.Add(grid);

            win.Content = root;
            win.Loaded += delegate { LoadLocal(); if (autoScan) StartScan(); };
            win.Closing += delegate { Engine.Cancel = true; };
        }

        void AddCol(string header, string path, double width, string sortPath, double fontSize)
        {
            var c = new DataGridTextColumn { Header = header, Binding = new Binding(path) };
            if (width > 0) c.Width = new DataGridLength(width); else c.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            if (sortPath != null) c.SortMemberPath = sortPath;
            var es = new Style(typeof(TextBlock));
            es.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            es.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (fontSize > 0)
            {
                es.Setters.Add(new Setter(TextBlock.FontSizeProperty, fontSize));
                es.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Emoji")));
                es.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            }
            c.ElementStyle = es;
            grid.Columns.Add(c);
        }

        void LoadLocal()
        {
            local = Engine.GetLocal();
            if (local == null)
            {
                myIp.Text = "Fără rețea";
                myIp.Foreground = B("#F87171");
                myDetails.Text = "Nu am găsit nicio placă de rețea conectată.";
                return;
            }
            myIp.Text = local.Ip;
            myIp.Foreground = B("#4ADE80");
            myDetails.Text = local.Kind + " · " + local.Adapter + "   |   Mască " + local.Mask + " (/" + local.Prefix + ")   |   Gateway " + (local.Gateway == "" ? "—" : local.Gateway)
                + "   |   DNS " + (local.Dns == "" ? "—" : local.Dns) + "\nMAC " + local.Mac + "   |   Nume PC " + local.HostName;
            string f, t; Engine.DefaultRange(local, out f, out t);
            fromBox.Text = f; toBox.Text = t;
            publicIp.Text = "IP public: se verifică…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string pub = "";
                foreach (string url in new[] { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" })
                {
                    try
                    {
                        ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192);
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Timeout = 4000; req.UserAgent = "curl/8";
                        using (var resp = req.GetResponse())
                        using (var r = new StreamReader(resp.GetResponseStream()))
                            pub = r.ReadToEnd().Trim();
                        IPAddress tmp;
                        if (IPAddress.TryParse(pub, out tmp)) break;
                        pub = "";
                    }
                    catch { }
                }
                win.Dispatcher.BeginInvoke(new Action(delegate
                {
                    publicIp.Text = pub == "" ? "IP public: fără internet / necunoscut" : "IP public (internet): " + pub;
                }));
            });
        }

        void StartScan()
        {
            if (worker != null && worker.IsAlive) return;
            if (local == null) LoadLocal();
            IPAddress a, b;
            if (!IPAddress.TryParse(fromBox.Text.Trim(), out a) || !IPAddress.TryParse(toBox.Text.Trim(), out b))
            { status.Text = "Intervalul de IP-uri nu e valid."; return; }
            hosts.Clear();
            scanBtn.IsEnabled = false; stopBtn.IsEnabled = true;
            bar.Value = 0;
            string from = fromBox.Text.Trim(), to = toBox.Text.Trim();
            var started = DateTime.Now;
            worker = new Thread(delegate ()
            {
                try
                {
                    Engine.Scan(local, from, to,
                        (d, t, s) => win.Dispatcher.BeginInvoke(new Action(delegate { bar.Maximum = t; bar.Value = d; status.Text = s; })),
                        h => win.Dispatcher.BeginInvoke(new Action(delegate { hosts.Add(h); UpdateCount(); })),
                        h => win.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            int i = hosts.IndexOf(h);
                            if (i >= 0) { hosts.RemoveAt(i); hosts.Insert(i, h); } // reimprospateaza randul
                            UpdateCount();
                        })));
                }
                catch (Exception ex)
                {
                    win.Dispatcher.BeginInvoke(new Action(delegate { status.Text = "Eroare: " + ex.Message; }));
                }
                win.Dispatcher.BeginInvoke(new Action(delegate
                {
                    scanBtn.IsEnabled = true; stopBtn.IsEnabled = false;
                    status.Text = (Engine.Cancel ? "Scanare oprită" : "Scanare terminată") + " în " + (int)(DateTime.Now - started).TotalSeconds + " s.";
                    UpdateCount();
                    view.Refresh();
                }));
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void UpdateCount()
        {
            int n = hosts.Count;
            var groups = hosts.Where(h => !h.IsMe).GroupBy(h => h.Type).OrderByDescending(g => g.Count()).Take(4).Select(g => g.Count() + " " + g.Key.ToLowerInvariant());
            countText.Text = n + " aparate" + (n > 1 ? "   ·   " + string.Join(", ", groups.ToArray()) : "");
        }

        void OpenSelected()
        {
            var h = grid.SelectedItem as Host;
            if (h == null) return;
            string url = "http://" + h.Ip + "/";
            if (!h.Ports.Contains(80))
            {
                if (h.Ports.Contains(443)) url = "https://" + h.Ip + "/";
                else if (h.Ports.Contains(8080)) url = "http://" + h.Ip + ":8080/";
                else if (h.Ports.Contains(8443)) url = "https://" + h.Ip + ":8443/";
                else if (h.Ports.Contains(8000)) url = "http://" + h.Ip + ":8000/";
            }
            try { Process.Start(url); } catch { }
        }

        void Export()
        {
            if (hosts.Count == 0) { status.Text = "Nu e nimic de exportat — scanează întâi."; return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "scanare_retea_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".csv" };
            if (dlg.ShowDialog(win) != true) return;
            var sb = new StringBuilder();
            sb.AppendLine("IP;Tip;Nume;Producator;MAC;Raspuns;Porturi;Detalii");
            foreach (Host h in hosts.OrderBy(x => x.IpSort))
                sb.AppendLine(string.Join(";", new[] { h.Ip, h.Type, h.Name, h.Vendor, h.Mac, h.Latency, h.PortsText, h.Details }.Select(s => "\"" + (s ?? "").Replace("\"", "'") + "\"").ToArray()));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            status.Text = "Salvat: " + dlg.FileName;
        }
    }
}
