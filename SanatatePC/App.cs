// Sanatate PC - Info PC Electric
// Fereastra principala: sumar vizual, componente, discuri, monitorizare live, licenta Free/Pro si raport PDF.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Sănătate PC")]
[assembly: AssemblyDescription("Starea componentelor calculatorului")]
[assembly: AssemblyCompany("Info PC Electric S.R.L.")]
[assembly: AssemblyProduct("Sănătate PC")]
[assembly: AssemblyCopyright("© 2026 Info PC Electric")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyInformationalVersion("1.3.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace InfoPcDisk
{
    public partial class MainApp
    {
        // adresa la care se cer ofertele / licentele Pro
        const string SalesEmail = "catalin@infopc.ro";

        Window win;
        ListBox nav, cardList, diskList, tileList;
        FrameworkElement loadingPanel, genericPage, diskPage, detail, summaryPage, livePage;
        TextBlock loadingText, pageTitle, pageDesc;
        Button actionBtn, refreshBtn, saveBtn, proBtn, settingsBtn;
        System.Windows.Shapes.Path ringArc, sumRing;
        List<Section> sections = new List<Section>();
        List<DiskReport> disks = new List<DiskReport>();
        List<Rec> currentRecs = new List<Rec>();
        License lic;
        Settings settings;
        LiveMonitor live;
        bool loading;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        bool Pro { get { return lic != null && lic.IsPro; } }

        [STAThread]
        public static void Main()
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show("A apărut o eroare:\n\n" + e.Exception.Message, "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            MainApp m = new MainApp();
            m.Build();
            app.Run(m.win);
        }

        static void FitToScreen(Window w)
        {
            Rect wa = SystemParameters.WorkArea;
            if (w.Width > wa.Width - 20 || w.Height > wa.Height - 20)
            {
                w.MinWidth = Math.Min(w.MinWidth, wa.Width);
                w.MinHeight = Math.Min(w.MinHeight, wa.Height);
                w.Width = Math.Min(w.Width, wa.Width);
                w.Height = Math.Min(w.Height, wa.Height);
                w.WindowState = WindowState.Maximized;
            }
        }

        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        static Window LoadWindow(string res)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                return (Window)XamlReader.Load(s);
        }

        T F<T>(string name) where T : class { return win.FindName(name) as T; }

        void Build()
        {
            win = LoadWindow("MainWindow.xaml");
            if (Arg("captura") == null) FitToScreen(win);
            nav = F<ListBox>("NavList");
            cardList = F<ListBox>("CardList");
            diskList = F<ListBox>("DiskList");
            tileList = F<ListBox>("TileList");
            loadingPanel = F<FrameworkElement>("LoadingPanel");
            genericPage = F<FrameworkElement>("GenericPage");
            diskPage = F<FrameworkElement>("DiskPage");
            summaryPage = F<FrameworkElement>("SummaryPage");
            livePage = F<FrameworkElement>("LivePage");
            detail = F<FrameworkElement>("Detail");
            loadingText = F<TextBlock>("LoadingText");
            pageTitle = F<TextBlock>("PageTitle");
            pageDesc = F<TextBlock>("PageDesc");
            actionBtn = F<Button>("ActionBtn");
            refreshBtn = F<Button>("RefreshBtn");
            saveBtn = F<Button>("SaveBtn");
            proBtn = F<Button>("ProBtn");
            settingsBtn = F<Button>("SettingsBtn");
            ringArc = F<System.Windows.Shapes.Path>("RingArc");
            sumRing = F<System.Windows.Shapes.Path>("SumRing");
            try { win.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); } catch (Exception) { }
            live = new LiveMonitor(F<UniformGrid>("LiveGrid"));

            lic = Licensing.Load();
            Licensing.CopyToExeDirIfMissing(lic);
            settings = Settings.Load(lic);
            UpdateLicenseUi();

            nav.SelectionChanged += delegate { ShowSection(nav.SelectedItem as Section); };
            cardList.SelectionChanged += delegate
            {
                Card c = cardList.SelectedItem as Card;
                if (c == null) return;
                cardList.SelectedIndex = -1;
                if (c.Locked) { ShowActivation(); return; }
                GoTo(c.Target);
            };
            tileList.SelectionChanged += delegate
            {
                Section s = tileList.SelectedItem as Section;
                if (s == null) return;
                tileList.SelectedIndex = -1;
                GoTo(s.Key);
            };
            diskList.SelectionChanged += delegate
            {
                DiskReport r = diskList.SelectedItem as DiskReport;
                detail.DataContext = r;
                UpdateRing(r);
                ScrollViewer sv = detail as ScrollViewer;
                if (sv != null) sv.ScrollToTop();
            };
            MouseButtonEventHandler act = delegate { ShowActivation(); };
            F<Border>("LicBadge").MouseLeftButtonUp += act;
            F<Border>("FreeBanner").MouseLeftButtonUp += act;
            F<Border>("DiskLock").MouseLeftButtonUp += act;
            F<Border>("LiveLock").MouseLeftButtonUp += act;
            proBtn.Click += delegate { ShowActivation(); };
            refreshBtn.Click += delegate { Load(); };
            saveBtn.Click += delegate { SaveReport(); };
            settingsBtn.Click += delegate { ShowSettings(); };
            F<Button>("ProjectsBtn").Click += delegate { ShowProjects(); };
            actionBtn.Click += delegate { RunAction(); };
            bool started = false;
            win.ContentRendered += delegate
            {
                if (started) return;
                started = true;
                Load();
                if (lic != null && lic.Valid && lic.Expired)
                    MessageBox.Show(win, "Licența Pro pentru " + lic.Name + " a expirat pe " + lic.ExpiresText + ".\n\nAplicația merge acum în versiunea Free. Pentru reînnoire, apasă „Activează Pro” → „Cere ofertă”.",
                        "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            win.Closing += delegate { live.Stop(); };
        }

        // pornire directa pe o pagina: SanatatePC.exe --pagina=live
        static string Arg(string name)
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase)) return a.Substring(name.Length + 3);
            return null;
        }

        // captura de ecran a ferestrei la o rezolutie data (pentru Microsoft Store): --captura=fisier.png --marime=1920x1080
        void CaptureAndExit()
        {
            string file = Arg("captura");
            if (file == null) return;
            int w = 1920, h = 1080;
            string m = Arg("marime");
            if (m != null) { string[] p = m.Split('x'); int.TryParse(p[0], out w); if (p.Length > 1) int.TryParse(p[1], out h); }
            System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(livePage.Visibility == Visibility.Visible ? 12 : 3);
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    FrameworkElement root = (FrameworkElement)win.Content;
                    win.Content = null;   // randare in afara ferestrei, la marimea ceruta (poate depasi ecranul)
                    Grid host = new Grid();
                    host.Background = win.Background;
                    host.Resources = win.Resources;
                    TextElement.SetFontFamily(host, win.FontFamily);
                    TextElement.SetForeground(host, win.Foreground);
                    TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
                    host.Children.Add(root);
                    root = host;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        root.Measure(new Size(w, h));
                        root.Arrange(new Rect(0, 0, w, h));
                        root.UpdateLayout();
                        // lasa listele (DataGrid) sa-si genereze randurile inainte de randare
                        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate { }));
                    }
                    RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    DrawingVisual bg = new DrawingVisual();
                    using (DrawingContext dc = bg.RenderOpen()) dc.DrawRectangle(win.Background, null, new Rect(0, 0, w, h));
                    rtb.Render(bg);
                    rtb.Render(root);
                    PngBitmapEncoder enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (FileStream fs = File.Create(file)) enc.Save(fs);
                }
                catch (Exception) { }
                live.Stop();
                win.Close();
            };
            t.Start();
        }

        static string StartPage()
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--pagina=", StringComparison.OrdinalIgnoreCase)) return a.Substring(9);
            return null;
        }

        void GoTo(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            foreach (Section s in sections) if (s.Key == key) { nav.SelectedItem = s; return; }
        }

        // ------------------------------------------------------------ licenta
        void UpdateLicenseUi()
        {
            TextBlock t = F<TextBlock>("LicTitle"), sub = F<TextBlock>("LicSub");
            Border badge = F<Border>("LicBadge");
            if (Pro)
            {
                int days = lic.DaysLeft;
                bool soon = days <= 30;
                t.Text = "PRO · " + lic.Name;
                t.Foreground = B("#4ADE80");
                sub.Text = lic.Lifetime ? "licență fără expirare" : soon ? "expiră în " + Math.Max(0, days) + " zile (" + lic.ExpiresText + ")" : "valabilă până la " + lic.ExpiresText;
                sub.Foreground = B(soon ? "#FBBF24" : "#86EFAC");
                badge.Background = B("#0B2A1F");
                proBtn.Visibility = soon && !lic.Lifetime ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)((StackPanel)proBtn.Content).Children[1]).Text = "Reînnoiește";
                win.Title = "Sănătate PC Pro — licențiat pentru " + lic.Name;
            }
            else
            {
                bool expired = lic != null && lic.Valid && lic.Expired;
                t.Text = expired ? "Licența a expirat" : "Versiunea Free";
                t.Foreground = B(expired ? "#F87171" : "#94A3B8");
                sub.Text = expired ? "pe " + lic.ExpiresText + " — apasă pentru reînnoire" : "Activează Pro pentru toate detaliile";
                sub.Foreground = B("#8A96B0");
                badge.Background = B("#1E293B");
                proBtn.Visibility = Visibility.Visible;
                ((TextBlock)((StackPanel)proBtn.Content).Children[1]).Text = "Activează Pro";
                win.Title = "Sănătate PC (Free) — Info PC Electric";
            }
            F<FrameworkElement>("FreeBanner").Visibility = Pro ? Visibility.Collapsed : Visibility.Visible;
            if (!Pro && lic != null && lic.Valid && lic.Expired)
            {
                F<TextBlock>("FreeTitle").Text = "Licența Pro a expirat pe " + lic.ExpiresText;
                F<TextBlock>("FreeText").Text = "Reînnoiește abonamentul ca să ai din nou toate detaliile, monitorizarea live și raportul PDF.";
            }
            F<FrameworkElement>("LiveLock").Visibility = Pro ? Visibility.Collapsed : Visibility.Visible;
            Visibility dv = Pro ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("DiskTiles").Visibility = dv;
            F<FrameworkElement>("DiskReasons").Visibility = dv;
            F<FrameworkElement>("DiskInfo").Visibility = dv;
            F<FrameworkElement>("DiskSmart").Visibility = dv;
            F<FrameworkElement>("DiskLock").Visibility = Pro ? Visibility.Collapsed : Visibility.Visible;
        }

        void ShowActivation()
        {
            Window d = LoadWindow("Activare.xaml");
            d.Owner = win;
            TextBox key = (TextBox)d.FindName("KeyBox");
            CheckBox stick = (CheckBox)d.FindName("OnStick");
            TextBlock err = (TextBlock)d.FindName("ErrText");
            Button remove = (Button)d.FindName("RemoveBtn");
            bool canStick = Licensing.CanWriteExeDir;
            stick.IsChecked = canStick;
            stick.IsEnabled = canStick;
            if (!canStick) ((TextBlock)d.FindName("StickHint")).Text = Licensing.IsInstalled
                ? "Aplicația e instalată, așa că licența rămâne pe acest calculator. Pentru stick, copiază SanatatePC.exe pe stick și activează de acolo."
                : "Nu pot scrie în folderul aplicației; licența rămâne pe acest calculator.";
            if (lic != null && lic.Valid)
            {
                ((FrameworkElement)d.FindName("CurBox")).Visibility = Visibility.Visible;
                ((TextBlock)d.FindName("CurTitle")).Text = (lic.IsPro ? "Pro activ — " : "Licență expirată — ") + lic.Name;
                ((TextBlock)d.FindName("CurSub")).Text = (lic.Email.Length > 0 ? lic.Email + " · " : "") + (lic.IsPro ? "valabilă până la " + lic.ExpiresText : "a expirat pe " + lic.ExpiresText) + "\nFișier: " + lic.SourcePath;
                if (!lic.IsPro) { ((Border)d.FindName("CurBox")).Background = B("#2A0E0E"); ((TextBlock)d.FindName("CurTitle")).Foreground = B("#F87171"); }
                remove.Visibility = Visibility.Visible;
            }
            Action<string> fail = delegate(string m) { err.Text = m; err.Visibility = Visibility.Visible; };
            ((Button)d.FindName("FileBtn")).Click += delegate
            {
                Microsoft.Win32.OpenFileDialog o = new Microsoft.Win32.OpenFileDialog();
                o.Filter = "Licență Sănătate PC (*.spc)|*.spc|Toate fișierele|*.*";
                if (o.ShowDialog(d) == true) { try { key.Text = File.ReadAllText(o.FileName).Trim(); } catch (Exception ex) { fail(ex.Message); } }
            };
            ((Button)d.FindName("OkBtn")).Click += delegate
            {
                License l = Licensing.Parse(key.Text);
                if (!l.Valid) { fail(l.Error); return; }
                if (l.Expired) { fail(l.Error + " Cere o cheie nouă pentru reînnoire."); return; }
                try { Licensing.Save(l, stick.IsChecked == true); }
                catch (Exception ex)
                {
                    if (stick.IsChecked == true) fail("Nu pot scrie lângă aplicație (" + ex.Message + "). Debifează opțiunea pentru stick ca să o salvez doar pe acest PC.");
                    else fail("Nu pot salva licența: " + ex.Message);
                    return;
                }
                lic = l;
                settings = Settings.Load(lic);
                d.DialogResult = true;
            };
            remove.Click += delegate
            {
                if (MessageBox.Show(d, "Elimin licența de pe acest calculator / stick?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Licensing.Remove();
                lic = null;
                d.DialogResult = true;
            };
            ((Button)d.FindName("CancelBtn")).Click += delegate { d.DialogResult = false; };
            ((Button)d.FindName("BuyBtn")).Click += delegate
            {
                string subj = "Licenta Sanatate PC Pro";
                string body = "Buna ziua,\n\nDoresc o oferta pentru abonamentul anual Sanatate PC Pro.\n\nNume / firma:\nTelefon:\n\nMultumesc!";
                try { System.Diagnostics.Process.Start("mailto:" + SalesEmail + "?subject=" + Uri.EscapeDataString(subj) + "&body=" + Uri.EscapeDataString(body)); }
                catch (Exception) { Clipboard.SetText(SalesEmail); MessageBox.Show(d, "Scrie-ne la " + SalesEmail + " (adresa a fost copiată).", "Sănătate PC"); }
            };
            bool? ok = d.ShowDialog();
            if (ok == true)
            {
                UpdateLicenseUi();
                Load();
                if (Pro) MessageBox.Show(win, "Sănătate PC Pro e activ pentru " + lic.Name + ".\nValabil până la " + lic.ExpiresText + ".", "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        void ShowSettings()
        {
            Window d = LoadWindow("Setari.xaml");
            d.Owner = win;
            Settings st = Settings.Load(lic);
            TextBox company = (TextBox)d.FindName("CompanyBox"), addr = (TextBox)d.FindName("AddressBox"), phone = (TextBox)d.FindName("PhoneBox"),
                email = (TextBox)d.FindName("EmailBox"), web = (TextBox)d.FindName("WebBox"), tech = (TextBox)d.FindName("TechBox");
            Image img = (Image)d.FindName("LogoImg");
            company.Text = st.Company; addr.Text = st.Address; phone.Text = st.Phone; email.Text = st.Email; web.Text = st.Website; tech.Text = st.Technician;
            string pendingLogo = null;
            bool clearLogo = false;
            Action<string> preview = delegate(string p)
            {
                try
                {
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) { img.Source = null; return; }
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(p); bi.EndInit();
                    img.Source = bi;
                }
                catch (Exception) { img.Source = null; }
            };
            preview(st.LogoPath);
            ((Button)d.FindName("LogoBtn")).Click += delegate
            {
                Microsoft.Win32.OpenFileDialog o = new Microsoft.Win32.OpenFileDialog();
                o.Filter = "Imagini (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg";
                if (o.ShowDialog(d) == true) { pendingLogo = o.FileName; clearLogo = false; preview(pendingLogo); }
            };
            ((Button)d.FindName("LogoClear")).Click += delegate { pendingLogo = null; clearLogo = true; preview(null); };
            ((Button)d.FindName("CancelBtn")).Click += delegate { d.DialogResult = false; };
            ((Button)d.FindName("OkBtn")).Click += delegate
            {
                try
                {
                    st.Company = company.Text.Trim(); st.Address = addr.Text.Trim(); st.Phone = phone.Text.Trim();
                    st.Email = email.Text.Trim(); st.Website = web.Text.Trim(); st.Technician = tech.Text.Trim();
                    if (clearLogo) st.LogoFile = "";
                    if (pendingLogo != null) st.SetLogo(pendingLogo);
                    st.Save();
                    settings = st;
                    d.DialogResult = true;
                }
                catch (Exception ex) { MessageBox.Show(d, "Nu am putut salva setările: " + ex.Message, "Sănătate PC"); }
            };
            d.ShowDialog();
        }

        // ------------------------------------------------------------ incarcare
        void Status(string text)
        {
            win.Dispatcher.BeginInvoke(new Action(delegate { loadingText.Text = text; }));
        }

        void HideAllPages()
        {
            summaryPage.Visibility = Visibility.Collapsed;
            livePage.Visibility = Visibility.Collapsed;
            genericPage.Visibility = Visibility.Collapsed;
            diskPage.Visibility = Visibility.Collapsed;
            loadingPanel.Visibility = Visibility.Collapsed;
        }

        void Load()
        {
            if (loading) return;
            loading = true;
            live.Stop();
            refreshBtn.IsEnabled = false;
            saveBtn.IsEnabled = false;
            HideAllPages();
            loadingPanel.Visibility = Visibility.Visible;
            string prevKey = nav.SelectedItem is Section ? ((Section)nav.SelectedItem).Key : null;
            bool pro = Pro;

            Thread t = new Thread(delegate()
            {
                List<Section> secs = new List<Section>();
                List<DiskReport> dl = new List<DiskReport>();
                List<Rec> recs = new List<Rec>();
                try
                {
                    Recs.Reset();
                    Status("Citesc jurnalul de stabilitate…");
                    Stability st = Collect.ReadStability();
                    Status("Citesc senzorii de temperatură…");
                    Sensors sn = Collect.ReadSensors();
                    Status("Citesc sistemul…");
                    Section sys = Safe(delegate { return Collect.SystemInfo(st); }, "sistem", "Sistem și Windows", "");
                    Status("Citesc procesorul…");
                    Section cpu = Safe(delegate { return Collect.Cpu(sn, st); }, "cpu", "Procesor", "");
                    Status("Citesc placa de bază…");
                    Section board = Safe(delegate { return Collect.Board(sn); }, "placa", "Placă de bază", "");
                    Status("Citesc memoria RAM…");
                    Section ram = Safe(delegate { return Collect.Ram(st); }, "ram", "Memorie RAM", "");
                    Status("Citesc placa video…");
                    Section gpu = Safe(delegate { return Collect.Gpu(sn, st); }, "gpu", "Placă video", "");
                    Status("Citesc discurile și datele SMART…");
                    dl = ReadDisks();
                    Section disk = DiskSection(dl, st);
                    Status("Citesc monitoarele…");
                    Section mon = Safe(delegate { return Collect.Monitors(); }, "monitor", "Monitoare", "");
                    Section bat = null;
                    try { bat = Collect.Battery(); } catch (Exception) { }
                    Status("Citesc rețeaua…");
                    Section net = Safe(delegate { return Collect.Network(); }, "retea", "Rețea", "");
                    Section stab = Safe(delegate { return Collect.StabilitySection(st); }, "stabilitate", "Stabilitate (30 zile)", "");

                    secs.Add(cpu); secs.Add(board); secs.Add(ram); secs.Add(gpu); secs.Add(disk);
                    secs.Add(mon);
                    if (bat != null) secs.Add(bat);
                    secs.Add(net); secs.Add(sys); secs.Add(stab);
                    EnsureRecs(secs);
                    recs = Recs.Take();
                    Section todo = Recs.BuildSection(recs);
                    secs.Insert(0, todo);
                    if (!pro) foreach (Section s in secs) FreeSummary(s);
                    secs.Insert(0, BuildSummary(secs, sys, pro));
                    Section lv = new Section("live", "Monitorizare live", "");
                    lv.Summary = "Încărcare și temperaturi în timp real";
                    secs.Insert(2, lv);
                    if (!pro)
                        foreach (Section s in secs)
                        {
                            if (s.Key == "todo")
                            {
                                int urg = 0; foreach (Rec r in recs) if (r.Priority == 1) urg++;
                                s.Cards.Clear();
                                Card lc = s.AddCard(recs.Count == 0 ? "Nu e nimic de făcut" : recs.Count + (recs.Count == 1 ? " lucrare recomandată" : " lucrări recomandate"), urg > 0 ? urg + " urgente" : "pentru acest calculator");
                                lc.SetLevel(recs.Count == 0 ? 0 : s.Level, null);
                                if (recs.Count > 0) lc.Locked = true;
                                continue;
                            }
                            if (s.Key != "sumar") foreach (Card c in s.Cards) c.Locked = true;
                        }
                }
                catch (Exception ex)
                {
                    Section err = new Section("sumar", "Sumar", "");
                    Card c = err.AddCard("Eroare la citire", ex.Message);
                    c.SetLevel(2, null);
                    err.Finish();
                    secs.Add(err);
                }
                win.Dispatcher.BeginInvoke(new Action(delegate
                {
                    sections = secs;
                    disks = dl;
                    currentRecs = recs;
                    nav.ItemsSource = null;
                    nav.ItemsSource = sections;
                    foreach (DiskReport r in disks) r.ListTemp = Pro ? r.TempText : "";
                    diskList.ItemsSource = disks;
                    if (disks.Count > 0) diskList.SelectedIndex = 0;
                    FillSummary();
                    loading = false;
                    refreshBtn.IsEnabled = true;
                    saveBtn.IsEnabled = true;
                    Section sel = sections[0];
                    if (prevKey == null) prevKey = StartPage();
                    if (prevKey != null) foreach (Section s in sections) if (s.Key == prevKey) sel = s;
                    nav.SelectedItem = sel;
                    ShowSection(sel);
                    CaptureAndExit();
                }));
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        // orice componenta cu Atentie / Problema are cel putin o lucrare in "Ce trebuie facut"
        static void EnsureRecs(List<Section> secs)
        {
            List<Rec> have = Recs.Take();
            Dictionary<string, string[]> comp = new Dictionary<string, string[]>();
            comp["cpu"] = new string[] { "Procesor", "Hardware" };
            comp["placa"] = new string[] { "Placă de bază" };
            comp["ram"] = new string[] { "Memorie RAM" };
            comp["gpu"] = new string[] { "Placă video" };
            comp["discuri"] = new string[] { "Disc" };
            comp["monitor"] = new string[] { "Monitor" };
            comp["baterie"] = new string[] { "Baterie" };
            comp["retea"] = new string[] { "Rețea" };
            comp["sistem"] = new string[] { "Windows" };
            comp["stabilitate"] = new string[] { "Stabilitate", "Alimentare", "Hardware", "Disc" };
            foreach (Section s in secs)
            {
                string[] names;
                if (s.Level < 1 || !comp.TryGetValue(s.Key, out names)) continue;
                bool found = false;
                foreach (Rec r in have) if (Array.IndexOf(names, r.Component) >= 0) found = true;
                if (found) continue;
                string note = "";
                foreach (Card c in s.Cards) if (c.Level >= 1 && c.NoteList.Count > 0) { note = c.NoteList[0]; break; }
                Recs.Add(s.Level == 2 ? 1 : 2, names[0], "Verificare " + s.Name.ToLower(), note, null);
            }
        }

        // in Free, pe pagina principala apare doar modelul / verdictul, fara valorile din Pro
        static void FreeSummary(Section s)
        {
            string first = (s.Summary ?? "").Split(new string[] { " · " }, StringSplitOptions.None)[0];
            switch (s.Key)
            {
                case "cpu": case "ram": s.Summary = first; break;
                case "baterie": s.Summary = s.Cards.Count > 0 ? s.Cards[0].Title : "Baterie"; break;
                case "stabilitate": s.Summary = s.Level >= 1 ? "Au apărut probleme — detaliile sunt în Pro" : "Fără probleme în ultimele 30 de zile"; break;
            }
        }

        Section BuildSummary(List<Section> secs, Section sys, bool pro)
        {
            Section sum = new Section("sumar", "Sumar", "");
            sum.Description = "Starea fiecărei componente pe scurt.";
            int worst = -1, warn = 0, bad = 0;
            foreach (Section s in secs)
            {
                if (s.Key == "todo") continue;
                if (s.Level > worst) worst = s.Level;
                if (s.Level == 1) warn++;
                if (s.Level == 2) bad++;
            }
            int score = 100 - 10 * warn - 25 * bad;
            if (bad > 0) score = Math.Min(score, 45);
            else if (warn > 0) score = Math.Max(50, Math.Min(score, 80));
            score = Math.Max(5, Math.Min(100, score));
            string title = worst == 2 ? "Am găsit probleme care cer atenție" : worst == 1 ? "Calculatorul merge, dar are câteva lucruri de urmărit" : "Calculatorul este în stare bună";
            Card head = sum.AddCard(title, sys != null && sys.Cards.Count > 0 ? sys.Cards[0].Title + " · " + sys.Summary : "");
            head.SetLevel(worst < 0 ? 0 : worst, null);
            head.Badge = score.ToString();
            foreach (Section s in secs)
            {
                if (s.Level < 1 || s.Key == "todo") continue;
                string first = null;
                if (pro) foreach (Card x in s.Cards) { if (x.Level >= 1 && x.NoteList.Count > 0) { first = x.NoteList[0]; break; } }
                head.Note(s.Name + (s.Level == 2 ? " — problemă" : " — de urmărit") + (first != null ? ": " + first : ""));
            }
            if (head.NoteList.Count == 0) head.Note("Nicio componentă nu are probleme detectate. Ține totuși o copie de rezervă a datelor importante.");
            if (!Collect.IsAdmin)
                head.Note(Collect.IsPackaged
                    ? "Versiunea din Microsoft Store rulează fără drepturi de administrator: la HDD-uri și SSD-uri SATA vezi starea dată de Windows, fără tabelul SMART complet. Versiunea portabilă de pe infopc.ro citește tot."
                    : "Aplicația nu rulează ca administrator, așa că unele date (SMART complet, TPM) nu pot fi citite. Pornește-o cu clic dreapta → Executare ca administrator.");
            foreach (Section s in secs)
            {
                Card c = sum.AddCard(s.Name, s.Summary);
                c.Target = s.Key;
                c.SetLevel(s.Level, s.Status);
            }
            sum.Finish();
            sum.SetLevel(worst);
            return sum;
        }

        void FillSummary()
        {
            Section sum = sections.Count > 0 ? sections[0] : null;
            if (sum == null || sum.Key != "sumar" || sum.Cards.Count == 0) return;
            Card h = sum.Cards[0];
            int score;
            int.TryParse(h.Badge, out score);
            F<TextBlock>("SumScore").Text = h.Badge ?? "—";
            F<TextBlock>("SumScore").Foreground = B(h.StatusColor);
            F<TextBlock>("SumTitle").Text = h.Title;
            F<TextBlock>("SumSub").Text = h.Subtitle;
            F<TextBlock>("SumIssues").Text = h.Notes;
            F<Border>("SumPill").Background = B(h.StatusBg);
            F<TextBlock>("SumPillText").Text = h.Level == 2 ? "Necesită atenție" : h.Level == 1 ? "De urmărit" : "Stare bună";
            F<TextBlock>("SumPillText").Foreground = B(h.StatusColor);
            sumRing.Stroke = B(h.StatusColor);
            double deg = Math.Max(0.5, Math.Min(359.9, score * 3.6));
            double rad = deg * Math.PI / 180, r = 71, c = 85;
            double x = c + r * Math.Sin(rad), y = c - r * Math.Cos(rad);
            sumRing.Data = Geometry.Parse(string.Format(Inv, "M {0},{1} A {2},{2} 0 {3} 1 {4:0.##},{5:0.##}", c, c - r, r, deg > 180 ? 1 : 0, x, y));
            List<Section> tiles = new List<Section>();
            foreach (Section s in sections) if (s.Key != "sumar") tiles.Add(s);
            tileList.ItemsSource = tiles;
        }

        delegate Section SecFn();

        static Section Safe(SecFn f, string key, string name, string icon)
        {
            try { return f(); }
            catch (Exception ex)
            {
                Section s = new Section(key, name, icon);
                Card c = s.AddCard(name, "Nu am putut citi informațiile");
                c.Note(ex.Message);
                s.Finish();
                return s;
            }
        }

        // ------------------------------------------------------------ discuri
        static string BusName(long b)
        {
            switch (b)
            {
                case 1: return "SCSI";
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 17: return "NVMe";
            }
            return b > 0 ? "Altă interfață" : "";
        }

        static List<DiskReport> ReadDisks()
        {
            List<DiskReport> list = new List<DiskReport>();
            const string ns = @"root\Microsoft\Windows\Storage";
            Dictionary<long, string> letters = new Dictionary<long, string>();
            foreach (ManagementObject p in Collect.Wmi(ns, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            {
                object dlo = Collect.P(p, "DriveLetter");
                char c = '\0';
                try { if (dlo != null) c = Convert.ToChar(dlo); } catch (Exception) { }
                if (c == '\0' || c == ' ') continue;
                long dn = Collect.L(p, "DiskNumber");
                string cur;
                letters[dn] = letters.TryGetValue(dn, out cur) ? cur + " " + c + ":" : c + ":";
            }
            List<ManagementObject> pds = Collect.Wmi(ns, "SELECT * FROM MSFT_PhysicalDisk");
            pds.Sort(delegate(ManagementObject a, ManagementObject b)
            {
                int x, y;
                int.TryParse(Collect.S(a, "DeviceId"), out x);
                int.TryParse(Collect.S(b, "DeviceId"), out y);
                return x.CompareTo(y);
            });
            foreach (ManagementObject pd in pds)
            {
                try
                {
                    int n;
                    if (!int.TryParse(Collect.S(pd, "DeviceId"), out n)) continue;
                    long media = Collect.L(pd, "MediaType");
                    string mediaS = media == 3 ? "HDD" : media == 4 ? "SSD" : "";
                    string bus = BusName(Collect.L(pd, "BusType"));
                    long hs = Collect.L(pd, "HealthStatus");
                    string health = hs == 0 ? "Healthy" : hs == 1 ? "Warning" : hs == 2 ? "Unhealthy" : "Unknown";
                    long temp = -1, tmax = -1, wear = -1, hours = -1, cycles = -1, re = -1, we = -1;
                    try
                    {
                        foreach (ManagementBaseObject rc in pd.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            temp = Collect.L(rc, "Temperature"); tmax = Collect.L(rc, "TemperatureMax");
                            wear = Collect.L(rc, "Wear"); hours = Collect.L(rc, "PowerOnHours");
                            cycles = Collect.L(rc, "StartStopCycleCount");
                            re = Collect.L(rc, "ReadErrorsUncorrected"); we = Collect.L(rc, "WriteErrorsUncorrected");
                        }
                    }
                    catch (Exception) { }
                    string lt;
                    letters.TryGetValue(n, out lt);
                    list.Add(Health.Analyze(n, Collect.S(pd, "FriendlyName"), Collect.S(pd, "SerialNumber"), Collect.S(pd, "FirmwareVersion"),
                        mediaS, bus, Collect.L(pd, "Size"), health, lt ?? "", (int)temp, (int)tmax, (int)wear, hours, cycles, re, we));
                }
                catch (Exception) { }
            }
            return list;
        }

        static Section DiskSection(List<DiskReport> dl, Stability st)
        {
            Section s = new Section("discuri", "Discuri (HDD / SSD)", "");
            int worst = -1, issues = 0;
            List<string> names = new List<string>();
            foreach (DiskReport r in dl)
            {
                if (r.Level > worst) worst = r.Level;
                if (r.Level >= 1) issues++;
                names.Add(r.TypeBadge + " " + r.SizeText);
                Card c = new Card(r.Title, r.Subtitle);
                c.SetLevel(r.Level, r.HealthText);
                if (r.Level >= 1) foreach (string x in r.Reasons) c.Note(x);
                s.Cards.Add(c);
                bool usb = r.Interface == "USB";
                bool sysDisk = (" " + r.Letters + " ").IndexOf(" C: ", StringComparison.OrdinalIgnoreCase) >= 0;
                string ssd = Recs.SsdFor(r.SizeBytes, r.IsNvme);
                if (r.Level == 2)
                    Recs.Add(1, "Disc", "Backup imediat și înlocuire disc " + r.Title + " (" + r.SizeText + ")", r.Reasons.Count > 0 ? r.Reasons[0] : r.Verdict, usb ? null : ssd);
                else if (r.Level == 1)
                    Recs.Add(2, "Disc", "Backup și înlocuire planificată disc " + r.Title + " (" + r.SizeText + ")", r.Reasons.Count > 0 ? r.Reasons[0] : r.Verdict, usb ? null : ssd);
                else if (sysDisk && !r.IsSsd && r.TypeBadge == "HDD")
                    Recs.Add(2, "Disc", "Înlocuire HDD de sistem cu SSD", "Windows rulează de pe HDD (" + r.Title + "). Cu SSD, pornirea și programele merg de câteva ori mai repede.", Recs.SsdFor(Math.Min(r.SizeBytes, 512000000000L), false));
            }
            if (st.DiskBadBlock > 0 && worst < 2) worst = 2;
            s.Summary = dl.Count == 0 ? "Nu am găsit discuri" : string.Join(" + ", names.ToArray()) + (issues > 0 ? " · " + issues + " cu probleme" : "");
            s.SetLevel(worst);
            return s;
        }

        void UpdateRing(DiskReport r)
        {
            if (r == null || r.Level < 0) { ringArc.Data = null; return; }
            double deg = Math.Max(0.5, Math.Min(359.9, r.Score * 3.6));
            double rad = deg * Math.PI / 180;
            double x = 75 + 62 * Math.Sin(rad), y = 75 - 62 * Math.Cos(rad);
            string d = string.Format(Inv, "M 75,13 A 62,62 0 {0} 1 {1:0.##},{2:0.##}", deg > 180 ? 1 : 0, x, y);
            ringArc.Data = Geometry.Parse(d);
        }

        void RunAction()
        {
            if ((actionBtn.Tag as string) == "ram") ShowRamTest();
        }

        void StartWindowsMemTest(Window owner)
        {
            MessageBoxResult r = MessageBox.Show(owner, "Se deschide Diagnosticul de memorie Windows.\n\nAlege „Repornește acum și verifică problemele”. Testul durează 10–20 de minute, verifică toată memoria (și cea folosită de Windows), iar rezultatul apare aici după repornire.\n\nSalvează-ți întâi lucrul deschis.",
                "Test memorie RAM", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (r != MessageBoxResult.OK) return;
            try { System.Diagnostics.Process.Start(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "MdSched.exe")); }
            catch (Exception ex) { MessageBox.Show(owner, "Nu am putut porni testul: " + ex.Message, "Sănătate PC"); }
        }

        // ------------------------------------------------------------ navigare
        void ShowSection(Section s)
        {
            if (s == null || loading) return;
            HideAllPages();
            if (s.Key != "live") live.Stop();
            if (s.Key == "sumar") { summaryPage.Visibility = Visibility.Visible; return; }
            if (s.Key == "live")
            {
                livePage.Visibility = Visibility.Visible;
                if (Pro) live.Start();
                return;
            }
            if (s.Key == "discuri")
            {
                diskPage.Visibility = Visibility.Visible;
                if (diskList.SelectedIndex < 0 && disks.Count > 0) diskList.SelectedIndex = 0;
                return;
            }
            genericPage.Visibility = Visibility.Visible;
            pageTitle.Text = s.Name;
            pageDesc.Text = s.Description + (Pro ? "" : "  În versiunea Free vezi modelul și starea; detaliile sunt în Pro.");
            actionBtn.Content = s.ActionText;
            actionBtn.Tag = s.Key;
            actionBtn.Visibility = string.IsNullOrEmpty(s.ActionText) ? Visibility.Collapsed : Visibility.Visible;
            cardList.ItemsSource = s.Cards;
            if (cardList.Items.Count > 0) cardList.ScrollIntoView(cardList.Items[0]);
        }

        // ------------------------------------------------------------ raport PDF
        void SaveReport()
        {
            if (sections.Count == 0) return;
            if (!Pro)
            {
                if (MessageBox.Show(win, "Raportul PDF pentru client e disponibil în versiunea Pro.\n\nVrei să activezi Pro acum?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    ShowActivation();
                return;
            }
            Window d = LoadWindow("RaportDialog.xaml");
            d.Owner = win;
            TextBox client = (TextBox)d.FindName("ClientBox"), notes = (TextBox)d.FindName("NotesBox");
            if (settings.Company.Length == 0) ((FrameworkElement)d.FindName("NoCompany")).Visibility = Visibility.Visible;
            ((Button)d.FindName("CancelBtn")).Click += delegate { d.DialogResult = false; };
            ((Button)d.FindName("OkBtn")).Click += delegate { d.DialogResult = true; };
            if (d.ShowDialog() != true) return;

            Microsoft.Win32.SaveFileDialog sf = new Microsoft.Win32.SaveFileDialog();
            sf.Filter = "Document PDF (*.pdf)|*.pdf";
            string who = client.Text.Trim().Length > 0 ? client.Text.Trim() : Environment.MachineName;
            foreach (char ch in Path.GetInvalidFileNameChars()) who = who.Replace(ch, '_');
            sf.FileName = "Raport_" + who + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".pdf";
            sf.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (sf.ShowDialog(win) != true) return;
            win.Cursor = Cursors.Wait;
            try
            {
                System.Windows.Documents.FlowDocument doc = Report.Build(sections, disks, lic, settings, client.Text.Trim(), notes.Text.Trim());
                string foot = (settings.Company.Length > 0 ? settings.Company + " · " : "") + "Sănătate PC Pro";
                Report.SavePdf(doc, sf.FileName, foot);
            }
            catch (Exception ex)
            {
                win.Cursor = null;
                MessageBox.Show(win, "Nu am putut crea raportul: " + ex.Message, "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            win.Cursor = null;
            try { System.Diagnostics.Process.Start(sf.FileName); } catch (Exception) { }
        }
    }
}
