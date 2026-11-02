// Recuperare Fisiere - Info PC Electric
// Interfata WPF. Motorul (Motor.cs) deschide discul sursa DOAR pentru citire.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

[assembly: AssemblyTitle("Recuperare Fișiere")]
[assembly: AssemblyDescription("Recuperare fișiere șterse sau pierdute")]
[assembly: AssemblyCompany("Info PC Electric S.R.L.")]
[assembly: AssemblyProduct("Recuperare Fișiere")]
[assembly: AssemblyCopyright("© 2026 Info PC Electric")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace InfoPcRecover
{
    public class SourceItem
    {
        public string Kind { get; set; }      // Volume / Disk
        public string Letter { get; set; }
        public string Path { get; set; }
        public long DiskNumber = -1;
        public string FileSystem { get; set; }
        public string Media { get; set; }
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Badge { get; set; }
        public string BadgeBg { get; set; }
        public string BadgeFg { get; set; }
    }

    public class RecApp
    {
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        const string AppTitle = "Recuperare Fișiere";
        Window win;
        Dictionary<string, object> ui = new Dictionary<string, object>();
        Engine eng;
        string mode = "";
        string dest;
        ObservableCollection<FoundFile> results = new ObservableCollection<FoundFile>();
        DispatcherTimer timer;
        static readonly CultureInfo RO = new CultureInfo("ro-RO");

        [STAThread]
        public static void Main()
        {
            Application app = new Application();
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show("A apărut o eroare:\n\n" + e.Exception.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            RecApp r = new RecApp();
            r.Build();
            app.Run(r.win);
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

        T U<T>(string name) where T : class
        {
            object o;
            if (!ui.TryGetValue(name, out o)) { o = win.FindName(name); ui[name] = o; }
            return o as T;
        }

        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        static MessageBoxResult Msg(string text, MessageBoxImage icon, MessageBoxButton buttons)
        {
            return MessageBox.Show(text, AppTitle, buttons, icon);
        }

        static void Msg(string text, MessageBoxImage icon) { Msg(text, icon, MessageBoxButton.OK); }

        // ------------------------------------------------------------ construire
        void Build()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                win = (Window)XamlReader.Load(s);
            FitToScreen(win);
            try { win.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch (Exception) { }

            U<ListBox>("LiveList").ItemsSource = results;
            U<DataGrid>("ResGrid").ItemsSource = results;

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(350);
            timer.Tick += delegate { Tick(); };

            U<Button>("RefreshBtn").Click += delegate { RefreshSources(); };
            U<ListBox>("DriveList").SelectionChanged += delegate { UpdateWarnings(); };
            U<RadioButton>("ModeQuick").Checked += delegate { UpdateWarnings(); };
            U<RadioButton>("ModeDeep").Checked += delegate { UpdateWarnings(); };
            U<Button>("BrowseBtn").Click += delegate { Browse(); };
            U<Button>("StartBtn").Click += delegate { StartScan(U<RadioButton>("ModeQuick").IsChecked == true ? "quick" : "deep"); };
            U<Button>("StopBtn").Click += delegate
            {
                if (eng == null) return;
                if (Msg("Opresc căutarea? Fișierele găsite până acum rămân salvate.", MessageBoxImage.Question, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    eng.CancelRequested = true;
                    U<TextBlock>("PhaseText").Text = "Se oprește…";
                }
            };
            RoutedEventHandler open = delegate
            {
                if (dest != null && Directory.Exists(dest)) Process.Start("explorer.exe", "\"" + dest + "\"");
            };
            U<Button>("OpenBtn").Click += open;
            U<Button>("OpenDuringBtn").Click += open;
            U<Button>("SelAllBtn").Click += delegate { foreach (FoundFile f in results) f.Selected = true; U<DataGrid>("ResGrid").Items.Refresh(); };
            U<Button>("SelNoneBtn").Click += delegate { foreach (FoundFile f in results) f.Selected = false; U<DataGrid>("ResGrid").Items.Refresh(); };
            U<Button>("SelGoodBtn").Click += delegate { foreach (FoundFile f in results) f.Selected = f.Status == "Bun"; U<DataGrid>("ResGrid").Items.Refresh(); };
            U<Button>("RecoverBtn").Click += delegate { StartRecover(); };
            U<Button>("NewBtn").Click += delegate { ShowPage("setup"); RefreshSources(); };
            U<Button>("DeepBtn").Click += delegate { U<RadioButton>("ModeDeep").IsChecked = true; StartScan("deep"); };
            U<DataGrid>("ResGrid").MouseDoubleClick += delegate
            {
                FoundFile f = U<DataGrid>("ResGrid").SelectedItem as FoundFile;
                if (f != null && f.SavedPath != null && File.Exists(f.SavedPath)) Process.Start("explorer.exe", "/select,\"" + f.SavedPath + "\"");
            };
            win.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (eng != null && !eng.Finished)
                {
                    if (Msg("O căutare e în desfășurare. Închid aplicația?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
                    eng.CancelRequested = true;
                }
                timer.Stop();
                AllowSleep();
                if (eng != null) try { eng.Close(); } catch (Exception) { }
            };
            win.ContentRendered += delegate { RefreshSources(); };
        }

        static void KeepAwake() { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); }
        static void AllowSleep() { SetThreadExecutionState(ES_CONTINUOUS); }

        // ------------------------------------------------------------ surse
        static string Fmt(double b)
        {
            if (b >= 1099511627776.0) return (b / 1099511627776.0).ToString("0.##", RO) + " TB";
            if (b >= 1073741824) return (b / 1073741824.0).ToString("0.#", RO) + " GB";
            if (b >= 1048576) return (b / 1048576.0).ToString("0.#", RO) + " MB";
            return (b / 1024.0).ToString("0", RO) + " KB";
        }

        static List<ManagementObject> Wmi(string ns, string q)
        {
            List<ManagementObject> l = new List<ManagementObject>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(q)))
                    foreach (ManagementBaseObject o in s.Get()) l.Add((ManagementObject)o);
            }
            catch (Exception) { }
            return l;
        }

        static string Str(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch (Exception) { return ""; }
        }

        static long Lng(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v == null ? -1 : Convert.ToInt64(v); } catch (Exception) { return -1; }
        }

        static string[] Badge(string media, string bus)
        {
            if (media == "HDD") return new string[] { "HDD", "#0B2A1F", "#4ADE80" };
            if (media == "SSD") return new string[] { "SSD", "#2A1E06", "#FBBF24" };
            if (bus == "USB") return new string[] { "USB", "#172554", "#93C5FD" };
            return new string[] { "Disc", "#1E293B", "#94A3B8" };
        }

        List<SourceItem> GetSources()
        {
            const string ns = @"root\Microsoft\Windows\Storage";
            Dictionary<string, string> media = new Dictionary<string, string>(), bus = new Dictionary<string, string>();
            foreach (ManagementObject pd in Wmi(ns, "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
            {
                string id = Str(pd, "DeviceId");
                long m = Lng(pd, "MediaType"), b = Lng(pd, "BusType");
                media[id] = m == 3 ? "HDD" : m == 4 ? "SSD" : "";
                bus[id] = b == 7 ? "USB" : b == 17 ? "NVMe" : b == 11 ? "SATA" : "";
            }
            Dictionary<string, long> diskOf = new Dictionary<string, long>();
            foreach (ManagementObject p in Wmi(ns, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            {
                try
                {
                    object dl = p["DriveLetter"];
                    if (dl == null) continue;
                    char c = Convert.ToChar(dl);
                    if (char.IsLetter(c)) diskOf[c.ToString().ToUpperInvariant()] = Lng(p, "DiskNumber");
                }
                catch (Exception) { }
            }

            List<SourceItem> list = new List<SourceItem>();
            foreach (ManagementObject v in Wmi(@"root\cimv2", "SELECT DeviceID, DriveType, FileSystem, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 2 OR DriveType = 3"))
            {
                string letter = Str(v, "DeviceID").TrimEnd(':');
                if (letter.Length != 1) continue;
                long dn;
                if (!diskOf.TryGetValue(letter.ToUpperInvariant(), out dn)) dn = -1;
                string md = "", bs = "";
                if (dn >= 0) { media.TryGetValue(dn.ToString(), out md); bus.TryGetValue(dn.ToString(), out bs); }
                string[] bd = Badge(md, bs);
                string fs = Str(v, "FileSystem");
                if (fs.Length == 0) fs = "RAW / necunoscut";
                string label = Str(v, "VolumeName");
                if (label.Length == 0) label = "Fără nume";
                long size = Lng(v, "Size"), free = Lng(v, "FreeSpace");
                SourceItem it = new SourceItem();
                it.Kind = "Volume"; it.Letter = letter; it.Path = @"\\.\" + letter + ":"; it.DiskNumber = dn;
                it.FileSystem = fs; it.Media = md;
                it.Icon = letter + ":"; it.Title = label + " (" + letter + ":)";
                it.Detail = "Partiție · " + fs + (size > 0 ? " · " + Fmt(size) : "") + (free >= 0 && size > 0 ? " · liber " + Fmt(free) : "") + (dn >= 0 ? " · disc #" + dn : "");
                it.Badge = bd[0]; it.BadgeBg = bd[1]; it.BadgeFg = bd[2];
                list.Add(it);
            }
            List<ManagementObject> disks = Wmi(ns, "SELECT Number, FriendlyName, Size FROM MSFT_Disk");
            disks.Sort(delegate(ManagementObject a, ManagementObject b) { return Lng(a, "Number").CompareTo(Lng(b, "Number")); });
            foreach (ManagementObject d in disks)
            {
                long n = Lng(d, "Number");
                string md, bs;
                media.TryGetValue(n.ToString(), out md);
                bus.TryGetValue(n.ToString(), out bs);
                string[] bd = Badge(md ?? "", bs ?? "");
                SourceItem it = new SourceItem();
                it.Kind = "Disk"; it.Letter = ""; it.Path = @"\\.\PhysicalDrive" + n; it.DiskNumber = n;
                it.FileSystem = ""; it.Media = md ?? "";
                it.Icon = "#" + n; it.Title = "Disc întreg #" + n + " — " + Str(d, "FriendlyName");
                it.Detail = "Tot discul, inclusiv partiții pierdute sau RAW · " + Fmt(Lng(d, "Size")) + " · doar căutare profundă";
                it.Badge = bd[0]; it.BadgeBg = bd[1]; it.BadgeFg = bd[2];
                list.Add(it);
            }
            return list;
        }

        void RefreshSources()
        {
            win.Cursor = System.Windows.Input.Cursors.Wait;
            try { U<ListBox>("DriveList").ItemsSource = GetSources(); }
            finally { win.Cursor = null; }
            UpdateWarnings();
        }

        static string DestLetter(string path)
        {
            try { string r = System.IO.Path.GetPathRoot(path); return r.Length >= 2 && r[1] == ':' ? r.Substring(0, 1).ToUpperInvariant() : ""; }
            catch (Exception) { return ""; }
        }

        long DestDisk(string path)
        {
            string l = DestLetter(path);
            if (l.Length == 0) return -1;
            foreach (ManagementObject p in Wmi(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            {
                try
                {
                    object dl = p["DriveLetter"];
                    if (dl != null && Convert.ToChar(dl).ToString().ToUpperInvariant() == l) return Lng(p, "DiskNumber");
                }
                catch (Exception) { }
            }
            return -1;
        }

        void UpdateWarnings()
        {
            SourceItem src = U<ListBox>("DriveList").SelectedItem as SourceItem;
            List<string> msgs = new List<string>();
            bool quickOk = src != null && src.Kind == "Volume" && src.FileSystem == "NTFS";
            U<RadioButton>("ModeQuick").IsEnabled = quickOk || src == null;
            if (src != null && !quickOk && U<RadioButton>("ModeQuick").IsChecked == true) U<RadioButton>("ModeDeep").IsChecked = true;
            U<CheckBox>("OptFree").IsEnabled = src == null || src.Kind == "Volume";
            if (src != null)
            {
                if (src.Media == "SSD")
                    msgs.Add("Discul ales este SSD. Pe SSD, Windows golește de obicei automat zonele șterse (TRIM), deci șansele sunt mici. Dacă fișierele originale erau pe HDD, scanează HDD-ul.");
                if (src.Kind == "Volume" && src.FileSystem != "NTFS")
                    msgs.Add("Partiția este " + src.FileSystem + ", așa că se poate folosi doar căutarea profundă.");
                if (dest != null)
                {
                    long dd = DestDisk(dest);
                    bool sameVol = src.Kind == "Volume" && DestLetter(dest) == src.Letter.ToUpperInvariant();
                    if (sameVol || (src.Kind == "Disk" && dd >= 0 && dd == src.DiskNumber))
                        msgs.Add("Folderul de salvare este pe discul pe care îl scanezi. Alege alt disc, altfel poți suprascrie chiar fișierele pe care le cauți.");
                    else if (src.Kind == "Volume" && dd >= 0 && dd == src.DiskNumber)
                        msgs.Add("Folderul de salvare e pe altă partiție a aceluiași disc fizic. Merge, dar e mai sigur pe un disc complet separat.");
                }
            }
            if (msgs.Count > 0)
            {
                U<TextBlock>("WarnText").Text = string.Join("\n\n", msgs.ToArray());
                U<Border>("WarnBox").Visibility = Visibility.Visible;
            }
            else U<Border>("WarnBox").Visibility = Visibility.Collapsed;
        }

        void Browse()
        {
            using (System.Windows.Forms.FolderBrowserDialog d = new System.Windows.Forms.FolderBrowserDialog())
            {
                d.Description = "Alege folderul unde se salvează fișierele recuperate (pe ALT disc decât cel scanat)";
                d.ShowNewFolderButton = true;
                if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    dest = d.SelectedPath;
                    U<TextBlock>("DestText").Text = dest;
                    U<TextBlock>("DestText").Foreground = B("#E6EAF2");
                    UpdateWarnings();
                }
            }
        }

        void ShowPage(string p)
        {
            U<FrameworkElement>("PageSetup").Visibility = p == "setup" ? Visibility.Visible : Visibility.Collapsed;
            U<FrameworkElement>("PageScan").Visibility = p == "scan" ? Visibility.Visible : Visibility.Collapsed;
            U<FrameworkElement>("PageResults").Visibility = p == "results" ? Visibility.Visible : Visibility.Collapsed;
        }

        static string FmtTime(double sec)
        {
            if (sec < 0 || double.IsInfinity(sec) || double.IsNaN(sec)) return "—";
            TimeSpan t = TimeSpan.FromSeconds(Math.Round(sec));
            if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes.ToString("00") + " min";
            if (t.TotalMinutes >= 1) return t.Minutes + " min " + t.Seconds.ToString("00") + " s";
            return t.Seconds + " s";
        }

        // ------------------------------------------------------------ scanare
        bool Want(string n) { return U<ToggleButton>(n).IsChecked == true; }

        void StartScan(string m)
        {
            SourceItem src = U<ListBox>("DriveList").SelectedItem as SourceItem;
            if (src == null) { Msg("Alege mai întâi discul de pe care s-au pierdut fișierele.", MessageBoxImage.Warning); return; }
            if (!(Want("ChkPdf") || Want("ChkWord") || Want("ChkJpg") || Want("ChkExcel") || Want("ChkPpt"))) { Msg("Bifează cel puțin un tip de fișier.", MessageBoxImage.Warning); return; }
            if (dest == null) { Msg("Alege folderul unde se salvează fișierele recuperate (pe alt disc).", MessageBoxImage.Warning); return; }
            long dd = DestDisk(dest);
            bool sameVol = src.Kind == "Volume" && DestLetter(dest) == src.Letter.ToUpperInvariant();
            if (sameVol || (src.Kind == "Disk" && dd >= 0 && dd == src.DiskNumber))
            {
                Msg("Nu pot salva pe același disc pe care îl scanez — aș putea suprascrie exact fișierele pe care le cauți. Alege un folder pe SSD, pe un stick sau pe un disc extern.", MessageBoxImage.Error);
                return;
            }
            if (m == "quick" && !(src.Kind == "Volume" && src.FileSystem == "NTFS")) m = "deep";

            if (eng != null) try { eng.Close(); } catch (Exception) { }
            Engine e = new Engine();
            e.SourcePath = src.Path;
            e.DestDir = dest;
            e.WantPdf = Want("ChkPdf"); e.WantWord = Want("ChkWord"); e.WantJpg = Want("ChkJpg");
            e.WantExcel = Want("ChkExcel"); e.WantPpt = Want("ChkPpt");
            e.SkipSmallJpg = U<CheckBox>("OptSmall").IsChecked == true;
            e.FreeSpaceOnly = U<CheckBox>("OptFree").IsChecked == true && src.Kind == "Volume";
            eng = e;
            mode = m;
            results.Clear();
            U<ListBox>("LiveList").ItemsSource = results;
            U<TextBlock>("ScanTitle").Text = m == "quick" ? "Caut fișierele șterse pe " + src.Title : "Căutare profundă pe " + src.Title;
            U<Button>("OpenDuringBtn").Visibility = m == "deep" ? Visibility.Visible : Visibility.Collapsed;
            ResetStats();
            ShowPage("scan");
            KeepAwake();
            if (m == "quick") e.StartQuick(); else e.StartDeep();
            timer.Start();
        }

        void ResetStats()
        {
            U<ProgressBar>("ProgBar").Value = 0;
            U<TextBlock>("PctText").Text = "0%";
            U<TextBlock>("SpeedText").Text = "—";
            U<TextBlock>("EtaText").Text = "—";
            U<TextBlock>("DoneText").Text = "—";
            U<TextBlock>("FoundText").Text = "0";
            U<TextBlock>("CountsText").Text = "";
            U<TextBlock>("PhaseText").Text = "Pornesc…";
        }

        void StartRecover()
        {
            List<FoundFile> sel = new List<FoundFile>();
            foreach (FoundFile f in results) if (f.Selected && f.SavedPath == null) sel.Add(f);
            if (sel.Count == 0) { Msg("Bifează fișierele pe care vrei să le recuperezi.", MessageBoxImage.Warning); return; }
            mode = "recover";
            U<TextBlock>("ScanTitle").Text = "Recuperez " + sel.Count + " fișiere";
            U<Button>("OpenDuringBtn").Visibility = Visibility.Visible;
            ResetStats();
            U<ListBox>("LiveList").ItemsSource = sel;
            ShowPage("scan");
            KeepAwake();
            eng.StartRecover(sel.ToArray());
            timer.Start();
        }

        void Tick()
        {
            Engine e = eng;
            if (e == null) { timer.Stop(); return; }
            FoundFile item;
            int added = 0;
            while (added < 400 && e.NewItems.TryDequeue(out item)) { results.Add(item); added++; }
            if (added > 0 && mode != "recover" && results.Count > 0) U<ListBox>("LiveList").ScrollIntoView(results[results.Count - 1]);

            double total = e.Total, done = e.Done;
            double pct = total > 0 ? Math.Min(100, done * 100 / total) : 0;
            U<ProgressBar>("ProgBar").Value = pct;
            U<TextBlock>("PctText").Text = Math.Floor(pct) + "%";
            U<TextBlock>("PhaseText").Text = e.Phase;
            double el = (DateTime.Now - e.StartTime).TotalSeconds;
            bool isBytes = mode != "quick" || (e.Phase ?? "").Contains("MFT");
            if (isBytes && el > 1 && done > 0)
            {
                double speed = done / el;
                U<TextBlock>("SpeedText").Text = Fmt(speed) + "/s";
                U<TextBlock>("EtaText").Text = FmtTime((total - done) / speed);
                U<TextBlock>("DoneText").Text = Fmt(done) + " din " + Fmt(total);
            }
            else if (!isBytes)
            {
                U<TextBlock>("SpeedText").Text = "—";
                U<TextBlock>("EtaText").Text = "aproape gata";
                U<TextBlock>("DoneText").Text = (long)done + " din " + (long)total + " fișiere";
            }
            if (mode == "recover") U<TextBlock>("FoundText").Text = e.Recovered.ToString();
            else
            {
                int cnt = e.CountPdf + e.CountWord + e.CountJpg + e.CountExcel + e.CountPpt;
                U<TextBlock>("FoundText").Text = cnt.ToString();
                List<string> parts = new List<string>();
                if (e.WantPdf) parts.Add("PDF: " + e.CountPdf);
                if (e.WantWord) parts.Add("Word: " + e.CountWord);
                if (e.WantJpg) parts.Add("Poze: " + e.CountJpg);
                if (e.WantExcel) parts.Add("Excel: " + e.CountExcel);
                if (e.WantPpt) parts.Add("PowerPoint: " + e.CountPpt);
                if (e.Duplicates > 0) parts.Add("duplicate sărite: " + e.Duplicates);
                if (e.BadAreas > 0) parts.Add("zone necitibile: " + e.BadAreas);
                U<TextBlock>("CountsText").Text = string.Join("    ·    ", parts.ToArray());
            }

            if (e.Finished && e.NewItems.IsEmpty)
            {
                timer.Stop();
                AllowSleep();
                if (e.Error != null)
                {
                    Msg("A apărut o problemă:\n\n" + e.Error, MessageBoxImage.Error);
                    if (results.Count == 0 && mode != "recover") { ShowPage("setup"); return; }
                }
                ShowResults();
            }
        }

        void ShowResults()
        {
            Engine e = eng;
            U<ListBox>("LiveList").ItemsSource = results;
            DataGrid grid = U<DataGrid>("ResGrid");
            grid.Items.Refresh();
            List<string> extra = new List<string>();
            if (e.CancelRequested) extra.Add("Căutarea a fost oprită înainte de final.");
            if (e.BadAreas > 0) extra.Add("Am întâlnit " + e.BadAreas + " zone care nu s-au putut citi (sectoare defecte) — dacă sunt multe, discul are probleme fizice.");
            string ex = extra.Count > 0 ? " " + string.Join(" ", extra.ToArray()) : "";

            TextBlock icon = U<TextBlock>("ResIcon");
            icon.Text = "\uE73E"; icon.Foreground = B("#4ADE80");
            DataGridColumn selCol = grid.Columns[0];
            if (mode == "quick")
            {
                int n = results.Count, good = 0;
                foreach (FoundFile f in results) if (f.Status == "Bun") good++;
                if (n == 0)
                {
                    icon.Text = "\uE946"; icon.Foreground = B("#FBBF24");
                    U<TextBlock>("ResTitle").Text = "Nu am găsit fișiere șterse cu nume pe această partiție";
                    U<TextBlock>("ResSub").Text = "Încearcă „Căutarea profundă”: scanează sector cu sector și găsește și fișierele fără nume." + ex;
                }
                else
                {
                    U<TextBlock>("ResTitle").Text = "Am găsit " + n + " fișiere șterse — " + good + " în stare bună";
                    U<TextBlock>("ResSub").Text = "Cele bune sunt deja bifate. Apasă „Recuperează selectate”. Cele marcate „Probabil suprascris” pot ieși deteriorate." + ex;
                }
                selCol.Visibility = Visibility.Visible;
                U<FrameworkElement>("SelPanel").Visibility = Visibility.Visible;
                U<Button>("RecoverBtn").Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                U<Button>("DeepBtn").Visibility = Visibility.Visible;
                U<Button>("OpenBtn").Visibility = Visibility.Collapsed;
            }
            else if (mode == "recover")
            {
                U<TextBlock>("ResTitle").Text = "Am recuperat " + e.Recovered + " fișiere" + (e.Failed > 0 ? " (" + e.Failed + " cu erori)" : "");
                U<TextBlock>("ResSub").Text = "Le găsești în " + dest + "\\Recuperate_cu_nume, în aceleași foldere ca pe disc. Dublu-clic pe un rând îl arată în Explorer." + ex;
                selCol.Visibility = Visibility.Collapsed;
                U<FrameworkElement>("SelPanel").Visibility = Visibility.Collapsed;
                U<Button>("RecoverBtn").Visibility = Visibility.Collapsed;
                U<Button>("DeepBtn").Visibility = Visibility.Visible;
                U<Button>("OpenBtn").Visibility = Visibility.Visible;
            }
            else
            {
                int n = results.Count;
                if (n == 0) { icon.Text = "\uE946"; icon.Foreground = B("#FBBF24"); }
                U<TextBlock>("ResTitle").Text = n > 0 ? "Am recuperat " + n + " fișiere" : "Căutarea profundă nu a găsit fișiere";
                string dup = e.Duplicates > 0 ? " Am sărit peste " + e.Duplicates + " duplicate." : "";
                U<TextBlock>("ResSub").Text = "Sunt salvate în " + dest + "\\Recuperare_profunda, pe tipuri. Numele vin din data pozei sau titlul documentului, unde există." + dup + ex;
                selCol.Visibility = Visibility.Collapsed;
                U<FrameworkElement>("SelPanel").Visibility = Visibility.Collapsed;
                U<Button>("RecoverBtn").Visibility = Visibility.Collapsed;
                U<Button>("DeepBtn").Visibility = Visibility.Collapsed;
                U<Button>("OpenBtn").Visibility = Visibility.Visible;
            }
            ShowPage("results");
        }
    }
}
