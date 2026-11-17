// Wi-Fi Stabil - Info PC Electric
// Fereastra principala: testul de stabilitate (pornire/oprire, durata), starea live, graficul, intreruperile si licenta Free/Pro.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InfoPcDisk;

[assembly: AssemblyTitle("Wi-Fi Stabil")]
[assembly: AssemblyDescription("Testează dacă rețeaua Wi-Fi e stabilă sau cade")]
[assembly: AssemblyCompany("Info PC Electric S.R.L.")]
[assembly: AssemblyProduct("Wi-Fi Stabil")]
[assembly: AssemblyCopyright("© 2026 Info PC Electric")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace InfoPcWifi
{
    public class MainApp
    {
        const string SalesEmail = "catalin@infopc.ro";
        const int FreeMinutes = 60;

        Window win;
        Chart chart;
        Monitor mon;
        License lic;
        DispatcherTimer timer;
        bool demo, finishedShown;
        string testDir;

        bool Pro { get { return lic != null && lic.IsPro; } }

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        static void Awake(bool on) { try { SetThreadExecutionState(on ? 0x80000001u : 0x80000000u); } catch (Exception) { } }

        [STAThread]
        public static void Main()
        {
            Licensing.UseWifiStabil();
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show("A apărut o eroare:\n\n" + e.Exception.Message, "Wi-Fi Stabil", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            MainApp m = new MainApp();
            m.Build();
            app.Run(m.win);
        }

        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        static Window LoadWindow(string res)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                return (Window)XamlReader.Load(s);
        }

        T F<T>(string name) where T : class { return win.FindName(name) as T; }
        TextBlock T(string name) { return F<TextBlock>(name); }

        static string Arg(string name)
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)) return "";
                if (a.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase)) return a.Substring(name.Length + 3);
            }
            return null;
        }

        public static string TestsDir
        {
            get { return Path.Combine(Licensing.AppDataDir, "Teste"); }
        }

        void Build()
        {
            win = LoadWindow("MainWindow.xaml");
            Rect wa = SystemParameters.WorkArea;
            if (Arg("captura") == null && (win.Width > wa.Width - 20 || win.Height > wa.Height - 20))
            {
                win.MinWidth = Math.Min(win.MinWidth, wa.Width); win.MinHeight = Math.Min(win.MinHeight, wa.Height);
                win.Width = Math.Min(win.Width, wa.Width); win.Height = Math.Min(win.Height, wa.Height);
                win.WindowState = WindowState.Maximized;
            }
            try { win.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); } catch (Exception) { }

            chart = new Chart();
            F<Border>("ChartHost").Child = chart;
            mon = new Monitor();
            chart.Source = mon;

            lic = Licensing.Load();
            Licensing.CopyToExeDirIfMissing(lic);
            UpdateLicenseUi();

            MouseButtonEventHandler act = delegate { ShowActivation(); };
            F<Border>("LicBadge").MouseLeftButtonUp += act;
            F<Border>("FreeBanner").MouseLeftButtonUp += act;
            F<Button>("ProBtn").Click += delegate { ShowActivation(); };
            F<Button>("StartBtn").Click += delegate { if (mon.Running) StopTest(); else StartTest(); };
            F<Button>("ReportBtn").Click += delegate { SaveReport(); };
            F<Button>("CsvBtn").Click += delegate { SaveCsv(); };
            F<Button>("FolderBtn").Click += delegate
            {
                string d = testDir != null && Directory.Exists(testDir) ? testDir : TestsDir;
                try { Directory.CreateDirectory(d); System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\""); } catch (Exception) { }
            };
            F<Border>("WarnBox").MouseLeftButtonUp += delegate
            {
                if (F<Border>("WarnBox").Tag as string == "locatie")
                    try { System.Diagnostics.Process.Start("ms-settings:privacy-location"); } catch (Exception) { }
            };
            foreach (object o in F<WrapPanel>("DurPanel").Children)
            {
                RadioButton rb = o as RadioButton;
                if (rb == null) continue;
                rb.Checked += delegate(object s, RoutedEventArgs e)
                {
                    RadioButton r = (RadioButton)s;
                    if (!Pro && Minutes(r) != 15 && Minutes(r) != 60)
                    {
                        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(delegate { SelectMinutes(60); ShowActivation(); }));
                    }
                };
            }

            win.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (mon.Running && Arg("captura") == null)
                {
                    if (MessageBox.Show(win, "Testul încă rulează. Îl opresc și închid aplicația?", "Wi-Fi Stabil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
                    mon.Stop("inchidere");
                    FinishFiles();
                }
                Awake(false);
            };

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate { Refresh(); };
            timer.Start();

            if (Arg("demo") != null)
            {
                demo = true;
                mon.LoadDemo(6);
                win.Title = "Wi-Fi Stabil — demonstrație";
            }
            WifiInfo w = demo ? mon.Wifi : Wlan.Query();
            if (!demo) mon.Wifi = w;
            if (!w.Found && !demo) ShowWarn("Nu am găsit o placă Wi-Fi pe acest calculator. Testul verifică atunci conexiunea pe cablu.", null);
            Refresh();
            if (Arg("captura") != null) win.ContentRendered += delegate { CaptureAndExit(); };
        }

        static int Minutes(RadioButton r) { int m; int.TryParse(r.Tag as string, out m); return m; }

        void SelectMinutes(int m)
        {
            foreach (object o in F<WrapPanel>("DurPanel").Children)
            {
                RadioButton r = o as RadioButton;
                if (r != null && Minutes(r) == m) r.IsChecked = true;
            }
        }

        int SelectedMinutes()
        {
            foreach (object o in F<WrapPanel>("DurPanel").Children)
            {
                RadioButton r = o as RadioButton;
                if (r != null && r.IsChecked == true) return Minutes(r);
            }
            return 60;
        }

        // ------------------------------------------------------------ test
        void StartTest()
        {
            int min = SelectedMinutes();
            if (!Pro && (min == 0 || min > FreeMinutes)) { min = FreeMinutes; SelectMinutes(FreeMinutes); }
            mon = new Monitor();
            mon.MaxMinutes = min;
            chart.Source = mon;
            demo = false;
            finishedShown = false;
            testDir = null;
            if (Pro)
            {
                testDir = Path.Combine(TestsDir, "Test_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                mon.SaveDir = testDir;
            }
            mon.IncidentStarted += delegate
            {
                win.Dispatcher.BeginInvoke(new Action(delegate { if (F<CheckBox>("BeepBox").IsChecked == true) System.Media.SystemSounds.Hand.Play(); }));
            };
            mon.Start();
            Awake(true);
            F<Border>("VerdictBox").Visibility = Visibility.Collapsed;
            F<WrapPanel>("DurPanel").IsEnabled = false;
            T("StartLabel").Text = "Oprește testul";
            T("StartIcon").Text = "";
            F<Button>("StartBtn").Background = B("#DC2626");
            Refresh();
        }

        void StopTest()
        {
            mon.Stop("manual");
            for (int i = 0; i < 40 && !mon.Finished; i++) System.Threading.Thread.Sleep(50);
            Refresh();
        }

        void OnFinished()
        {
            finishedShown = true;
            Awake(false);
            F<WrapPanel>("DurPanel").IsEnabled = true;
            T("StartLabel").Text = "Pornește un test nou";
            T("StartIcon").Text = "";
            F<Button>("StartBtn").Background = B("#16A34A");
            ShowVerdict();
            FinishFiles();
            if (!demo && mon.StopReason == "durata" && Arg("captura") == null)
            {
                win.Activate();
                string extra = !Pro && mon.MaxMinutes >= FreeMinutes ? "\n\nÎn versiunea Free testul durează cel mult o oră. Cu Pro îl poți lăsa toată noaptea." : "";
                MessageBox.Show(win, "Testul s-a terminat.\n\n" + T("VerdictTitle").Text + "\n" + T("VerdictText").Text + extra, "Wi-Fi Stabil", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        void FinishFiles()
        {
            if (testDir == null || !Pro) return;
            try { Report.SaveHtml(Path.Combine(testDir, "raport.html"), mon, mon.Snap(true), lic.Name, true); } catch (Exception) { }
        }

        // ------------------------------------------------------------ afisare
        void ShowWarn(string text, string tag)
        {
            Border b = F<Border>("WarnBox");
            if (text == null) { b.Visibility = Visibility.Collapsed; return; }
            T("WarnText").Text = text;
            b.Tag = tag;
            b.Cursor = tag != null ? Cursors.Hand : null;
            b.Visibility = Visibility.Visible;
        }

        void Refresh()
        {
            Monitor.Snapshot s = mon.Snap(false);
            bool running = mon.Running;
            bool done = mon.Finished;
            if (done && !finishedShown && (s.Total > 0)) { OnFinished(); }
            WifiInfo w = mon.Wifi;
            int big = s.Big(mon.ThresholdSec), small = s.Incidents.Count - big;

            // cercul de stare
            string col, title, sub, icon;
            Incident ongoing = s.Incidents.Count > 0 && s.Incidents[s.Incidents.Count - 1].Ongoing ? s.Incidents[s.Incidents.Count - 1] : null;
            if (running)
            {
                if (ongoing != null)
                {
                    col = Monitor.StateColor(ongoing.Type);
                    title = ongoing.Type == 3 && mon.AdapterKind == "Cablu" ? "Cablu deconectat" : Monitor.StateName(ongoing.Type);
                    sub = "de " + Monitor.Dur(ongoing.Seconds);
                    icon = ongoing.Type == 3 ? "" : "";
                }
                else if (s.Total == 0) { col = "#3B82F6"; title = "Pornesc…"; sub = "prima măsurătoare"; icon = ""; }
                else { col = "#22C55E"; title = "Conectat"; sub = "totul merge"; icon = ""; }
            }
            else if (s.Total > 0)
            {
                int lvl = Level(s);
                col = lvl == 0 ? "#22C55E" : lvl == 1 ? "#EAB308" : "#EF4444";
                title = demo ? "Test de 6 ore" : "Test încheiat";
                sub = lvl == 0 ? "conexiune stabilă" : lvl == 1 ? "întreruperi ocazionale" : "conexiune instabilă";
                icon = lvl == 0 ? "" : "";
            }
            else { col = "#475569"; title = "Pregătit"; sub = "alege durata și pornește testul"; icon = ""; }
            F<System.Windows.Shapes.Ellipse>("StateRing").Stroke = B(col);
            F<System.Windows.Shapes.Ellipse>("StateGlow").Fill = B(col);
            F<System.Windows.Shapes.Ellipse>("StateGlow").Opacity = 0.14;
            T("StateIcon").Foreground = B(col);
            T("StateIcon").Text = icon;
            T("StateText").Text = title;
            T("StateSub").Text = sub;

            double el = s.Total > 0 ? (s.End - s.Start).TotalSeconds : 0;
            T("ElapsedText").Text = s.Total > 0 ? Monitor.Dur(el) : "—";
            if (running && mon.MaxMinutes > 0) { T("RemainLabel").Text = "MAI RĂMÂN"; T("RemainText").Text = Monitor.Dur(Math.Max(0, mon.MaxMinutes * 60 - el)); }
            else if (running) { T("RemainLabel").Text = "OPRIRE"; T("RemainText").Text = "manuală"; }
            else if (s.Total > 0) { T("RemainLabel").Text = "TERMINAT LA"; T("RemainText").Text = s.End.ToString("HH:mm"); }
            else { T("RemainLabel").Text = "DURATĂ"; T("RemainText").Text = SelectedMinutes() == 0 ? "nelimitată" : Monitor.Dur(SelectedMinutes() * 60); }

            // carduri
            bool any = s.Total > 0;
            T("AvailText").Text = any ? s.Availability.ToString("0.00", CultureInfo.GetCultureInfo("ro-RO")) + " %" : "—";
            T("AvailText").Foreground = B(!any ? "#E6EAF2" : s.Availability >= 99.5 ? "#4ADE80" : s.Availability >= 98 ? "#FACC15" : "#F87171");
            T("IncText").Text = any ? big.ToString() : "—";
            T("IncText").Foreground = B(!any ? "#E6EAF2" : big == 0 ? "#4ADE80" : big <= 2 ? "#FACC15" : "#F87171");
            T("IncSub").Text = small > 0 ? "+ " + small + " pierderi scurte (sub " + mon.ThresholdSec + "s)" : "de " + mon.ThresholdSec + " secunde sau mai mult";
            T("LongText").Text = any && s.Incidents.Count > 0 ? Monitor.Dur(s.Longest) : any ? "0s" : "—";
            T("LongSub").Text = any && s.Offline > 0 ? "în total " + Monitor.Dur(s.Offline) + " offline" : "fără conexiune";
            bool hasSig = w.Connected && w.Signal >= 0;
            T("SigText").Text = hasSig ? w.Signal + " %" : "—";
            T("SigText").Foreground = B(!hasSig ? "#E6EAF2" : w.Signal >= 60 ? "#4ADE80" : w.Signal >= 40 ? "#FACC15" : "#F87171");
            T("SigSub").Text = hasSig && w.Rssi != 0 ? w.Rssi + " dBm" + (w.Signal >= 70 ? " · foarte bun" : w.Signal >= 50 ? " · bun" : w.Signal >= 35 ? " · slab" : " · foarte slab") : w.AccessDenied ? "necesită permisiunea Locație" : "";
            T("NetText").Text = (running || demo) && mon.NetMs >= 0 ? mon.NetMs + " ms" : running ? "—" : "—";
            T("NetText").Foreground = B(running && mon.NetMs < 0 && s.Total > 0 ? "#F87171" : mon.NetMs > 150 ? "#FACC15" : "#E6EAF2");
            T("NetSub").Text = string.Join(" · ", mon.Targets);
            T("RouterText").Text = (running || demo) && mon.GwMs >= 0 ? mon.GwMs + " ms" : "—";
            T("RouterSub").Text = mon.Gateway.Length > 0 ? mon.Gateway : "";

            // retea
            T("SsidText").Text = w.Ssid.Length > 0 ? w.Ssid : w.Connected ? "(ascuns de Windows)" : w.Found ? "neconectat" : mon.AdapterKind == "Cablu" ? "conexiune pe cablu" : "—";
            T("BssidText").Text = w.Bssid.Length > 0 ? w.Bssid : "—";
            T("ChanText").Text = w.Channel > 0 ? w.Channel + (w.Band.Length > 0 ? " · " + w.Band : "") : "—";
            T("PhyText").Text = w.Phy.Length > 0 ? w.Phy : "—";
            T("RateText").Text = w.RxMbps > 0 ? w.RxMbps + " / " + w.TxMbps + " Mbps" : "—";
            T("GwText").Text = mon.Gateway.Length > 0 ? mon.Gateway : "—";
            string ad = mon.AdapterName.Length > 0 ? mon.AdapterName : w.Adapter;
            T("AdapterText").Text = ad.Length > 0 ? ad : "—";

            // avertismente
            if (w.AccessDenied) ShowWarn("Windows nu dă aplicației numele rețelei și semnalul. Clic aici → Setări › Confidențialitate și securitate › Locație: pornește Locația și „Permite aplicațiilor desktop să acceseze locația”. Testul de întreruperi merge oricum.", "locatie");
            else if (mon.WiredAlso) ShowWarn("Este conectat și un cablu de rețea. Scoate-l pe durata testului, altfel se poate măsura cablul în loc de Wi-Fi.", null);
            else if (w.Found || demo) ShowWarn(null, null);

            // liste
            List<Incident> inc = new List<Incident>();
            foreach (Incident i in s.Incidents) if (i.Seconds >= mon.ThresholdSec || i.Ongoing) inc.Insert(0, i);
            F<ItemsControl>("IncList").ItemsSource = inc;
            T("IncEmpty").Visibility = inc.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            T("IncEmpty").Text = !any ? "Întreruperile apar aici, cu ora, durata și cauza." : small > 0 ? "Nicio întrerupere reală. " + small + " pierderi scurte de pachete (normale în Wi-Fi)." : "Nicio întrerupere până acum.";
            List<NetEvent> ev = new List<NetEvent>(s.Events);
            ev.Reverse();
            F<ItemsControl>("EvList").ItemsSource = ev;
            T("EvEmpty").Visibility = ev.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            chart.InvalidateVisual();
        }

        static int Level(Monitor.Snapshot s)
        {
            int big = s.Big(3);
            if (big == 0 && s.Availability >= 99.5) return 0;
            if (big <= 2 && s.Availability >= 99) return 1;
            return 2;
        }

        void ShowVerdict()
        {
            Monitor.Snapshot s = mon.Snap(true);
            if (s.Total == 0) return;
            int lvl = Level(s), big = s.Big(mon.ThresholdSec);
            int[] cnt = new int[4];
            foreach (Incident i in s.Incidents) if (i.Seconds >= mon.ThresholdSec) cnt[i.Type]++;
            int dom = 1; for (int k = 1; k < 4; k++) if (cnt[k] > cnt[dom]) dom = k;
            string t, x;
            double dur = (s.End - s.Start).TotalSeconds;
            if (lvl == 0)
            {
                t = "Conexiune stabilă";
                x = "Nicio întrerupere reală în " + Monitor.Dur(dur) + ".";
            }
            else
            {
                t = lvl == 1 ? "Întreruperi ocazionale" : "Conexiune instabilă";
                x = big + " întreruperi în " + Monitor.Dur(dur) + ", cea mai lungă de " + Monitor.Dur(s.Longest) + ". ";
                if (dom == 3) x += "Majoritatea sunt deconectări de la Wi-Fi: semnal slab, interferențe pe canal, driverul plăcii sau economisirea de energie a plăcii.";
                else if (dom == 2) x += "Laptopul rămâne pe Wi-Fi, dar routerul nu mai răspunde: routerul se blochează, se repornește sau e suprasolicitat.";
                else x += "Wi-Fi-ul și routerul merg, dar internetul cade: problema e la furnizor sau la modem.";
            }
            int sigMin = 101, n = 0; double sum = 0;
            foreach (Sample p in s.Samples) if (p.Signal >= 0) { sum += p.Signal; n++; sigMin = Math.Min(sigMin, p.Signal); }
            if (n > 0 && (sum / n < 50 || sigMin < 30)) x += " Semnalul e slab (medie " + (int)Math.Round(sum / n) + "%, minim " + sigMin + "%).";
            T("VerdictTitle").Text = t;
            T("VerdictTitle").Foreground = B(lvl == 0 ? "#4ADE80" : lvl == 1 ? "#FACC15" : "#F87171");
            T("VerdictText").Text = x;
            F<Border>("VerdictBox").Background = B(lvl == 0 ? "#0B2A1F" : lvl == 1 ? "#2A2210" : "#2A0E0E");
            F<Border>("VerdictBox").Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------ raport / export (Pro)
        bool NeedPro(string what)
        {
            if (Pro) return false;
            if (MessageBox.Show(win, what + " e disponibil în versiunea Pro.\n\nVrei să activezi Pro acum?", "Wi-Fi Stabil", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                ShowActivation();
            return true;
        }

        string BaseName()
        {
            Monitor.Snapshot s = mon.Snap(false);
            string n = s.Ssids.Length > 0 ? s.Ssids.Split(',')[0].Trim() : Environment.MachineName;
            foreach (char ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
            return "WiFi_" + n + "_" + (s.Total > 0 ? s.Start : DateTime.Now).ToString("yyyy-MM-dd_HH-mm");
        }

        void SaveReport()
        {
            if (mon.Snap(false).Total == 0) { MessageBox.Show(win, "Pornește întâi un test.", "Wi-Fi Stabil"); return; }
            if (NeedPro("Raportul cu grafice pentru client")) return;
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog();
            d.Filter = "Pagină web (*.html)|*.html";
            d.FileName = "Raport_" + BaseName() + ".html";
            d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (d.ShowDialog(win) != true) return;
            try
            {
                Report.SaveHtml(d.FileName, mon, mon.Snap(true), lic.Name, !mon.Running);
                System.Diagnostics.Process.Start(d.FileName);
            }
            catch (Exception ex) { MessageBox.Show(win, "Nu am putut salva raportul: " + ex.Message, "Wi-Fi Stabil", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void SaveCsv()
        {
            if (mon.Snap(false).Total == 0) { MessageBox.Show(win, "Pornește întâi un test.", "Wi-Fi Stabil"); return; }
            if (NeedPro("Exportul în Excel")) return;
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog();
            d.Filter = "Fișier Excel CSV (*.csv)|*.csv";
            d.FileName = BaseName() + ".csv";
            d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (d.ShowDialog(win) != true) return;
            try { Report.SaveCsv(d.FileName, mon.Snap(true), mon.IntervalSec); }
            catch (Exception ex) { MessageBox.Show(win, "Nu am putut salva fișierul: " + ex.Message, "Wi-Fi Stabil", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // ------------------------------------------------------------ licenta
        void UpdateLicenseUi()
        {
            TextBlock t = T("LicTitle"), sub = T("LicSub");
            Border badge = F<Border>("LicBadge");
            Button proBtn = F<Button>("ProBtn");
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
                win.Title = "Wi-Fi Stabil Pro — licențiat pentru " + lic.Name;
            }
            else
            {
                bool expired = lic != null && lic.Valid && lic.Expired;
                t.Text = expired ? "Licența a expirat" : "Versiunea Free";
                t.Foreground = B(expired ? "#F87171" : "#94A3B8");
                sub.Text = expired ? "pe " + lic.ExpiresText + " — apasă pentru reînnoire" : "teste de până la 1 oră";
                sub.Foreground = B("#8A96B0");
                badge.Background = B("#1E293B");
                proBtn.Visibility = Visibility.Visible;
                ((TextBlock)((StackPanel)proBtn.Content).Children[1]).Text = "Activează Pro";
                win.Title = "Wi-Fi Stabil (Free) — Info PC Electric";
            }
            F<FrameworkElement>("FreeBanner").Visibility = Pro ? Visibility.Collapsed : Visibility.Visible;
            foreach (object o in F<WrapPanel>("DurPanel").Children)
            {
                RadioButton r = o as RadioButton;
                if (r == null) continue;
                int m = Minutes(r);
                string baseText = m == 15 ? "15 min" : m == 60 ? "1 oră" : m == 240 ? "4 ore" : m == 480 ? "8 ore" : "Până opresc eu";
                r.Content = Pro || m == 15 || m == 60 ? baseText : baseText + " · Pro";
            }
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
                ? "Aplicația e instalată, așa că licența rămâne pe acest calculator. Pentru stick, copiază WiFiStabil.exe pe stick și activează de acolo."
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
                o.Filter = "Licență Wi-Fi Stabil (*.wfs)|*.wfs|Toate fișierele|*.*";
                if (o.ShowDialog(d) == true) { try { key.Text = File.ReadAllText(o.FileName).Trim(); } catch (Exception ex) { fail(ex.Message); } }
            };
            ((Button)d.FindName("ScanBtn")).Click += delegate
            {
                License f = Licensing.FindOnRemovable();
                if (f == null) { fail("Nu am găsit o licență Pro validă pe stick-urile conectate. Pune stick-ul cu licența și încearcă din nou, sau lipește cheia de mai sus."); return; }
                key.Text = f.Raw;
                err.Visibility = Visibility.Collapsed;
                ((TextBlock)d.FindName("ScanInfo")).Text = "Găsită pe " + f.SourcePath + " — " + f.Name + ", valabilă până la " + f.ExpiresText + ". Apasă „Activează”.";
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
                d.DialogResult = true;
            };
            remove.Click += delegate
            {
                if (MessageBox.Show(d, "Elimin licența de pe acest calculator / stick?", "Wi-Fi Stabil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Licensing.Remove();
                lic = null;
                d.DialogResult = true;
            };
            ((Button)d.FindName("CancelBtn")).Click += delegate { d.DialogResult = false; };
            ((Button)d.FindName("BuyBtn")).Click += delegate
            {
                string subj = "Licenta Wi-Fi Stabil Pro";
                string body = "Buna ziua,\n\nDoresc o oferta pentru abonamentul anual Wi-Fi Stabil Pro.\n\nNume / firma:\nTelefon:\n\nMultumesc!";
                try { System.Diagnostics.Process.Start("mailto:" + SalesEmail + "?subject=" + Uri.EscapeDataString(subj) + "&body=" + Uri.EscapeDataString(body)); }
                catch (Exception) { Clipboard.SetText(SalesEmail); MessageBox.Show(d, "Scrie-ne la " + SalesEmail + " (adresa a fost copiată).", "Wi-Fi Stabil"); }
            };
            if (d.ShowDialog() == true)
            {
                UpdateLicenseUi();
                if (Pro) MessageBox.Show(win, "Wi-Fi Stabil Pro e activ pentru " + lic.Name + ".\nValabil până la " + lic.ExpiresText + ".\n\nPoți porni teste nelimitate și salva rapoarte.", "Wi-Fi Stabil", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ------------------------------------------------------------ captura (CI / Microsoft Store): --captura=fisier.png --marime=1920x1080 [--demo]
        void CaptureAndExit()
        {
            string file = Arg("captura");
            int w = 1920, h = 1080;
            string m = Arg("marime");
            if (!string.IsNullOrEmpty(m)) { string[] p = m.Split('x'); int.TryParse(p[0], out w); if (p.Length > 1) int.TryParse(p[1], out h); }
            int wait = 3;
            if (Arg("porneste") != null) { StartTest(); wait = 14; }
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(wait);
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    Refresh();
                    if (demo) ShowVerdict();
                    FrameworkElement root = (FrameworkElement)win.Content;
                    win.Content = null;
                    Grid host = new Grid();
                    host.Background = win.Background;
                    host.Resources = win.Resources;
                    TextElement.SetFontFamily(host, win.FontFamily);
                    TextElement.SetForeground(host, win.Foreground);
                    TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
                    host.Children.Add(root);
                    for (int pass = 0; pass < 3; pass++)
                    {
                        host.Measure(new Size(w, h));
                        host.Arrange(new Rect(0, 0, w, h));
                        host.UpdateLayout();
                        chart.InvalidateVisual();
                        win.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
                    }
                    RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(host);
                    PngBitmapEncoder enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (FileStream fs = File.Create(file)) enc.Save(fs);
                    if (mon.LastError.Length > 0 || (Arg("porneste") != null && mon.Snap(false).Total == 0))
                        File.WriteAllText(file + ".diag.txt", "Masuratori: " + mon.Snap(false).Total + "\r\nRunning: " + mon.Running + "\r\n" + mon.LastError);
                    if (Arg("raport") != null)
                        Report.SaveHtml(Arg("raport"), mon, mon.Snap(true), "", true);
                }
                catch (Exception ex) { try { File.WriteAllText(file + ".eroare.txt", ex.ToString()); } catch (Exception) { } }
                mon.Stop("captura");
                win.Close();
            };
            t.Start();
        }
    }
}
