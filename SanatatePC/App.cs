// Sanatate PC - Info PC Electric
// Fereastra principala: meniu pe componente, sumar, pagina de discuri si raport.
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
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

[assembly: AssemblyTitle("Sănătate PC")]
[assembly: AssemblyDescription("Starea componentelor calculatorului")]
[assembly: AssemblyCompany("Info PC Electric S.R.L.")]
[assembly: AssemblyProduct("Sănătate PC")]
[assembly: AssemblyCopyright("© 2026 Info PC Electric")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace InfoPcDisk
{
    public class MainApp
    {
        Window win;
        ListBox nav, cardList, diskList;
        FrameworkElement loadingPanel, genericPage, diskPage, detail;
        TextBlock loadingText, pageTitle, pageDesc;
        Button actionBtn, refreshBtn, saveBtn;
        System.Windows.Shapes.Path ringArc;
        List<Section> sections = new List<Section>();
        List<DiskReport> disks = new List<DiskReport>();
        bool loading;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

        T F<T>(string name) where T : class { return win.FindName(name) as T; }

        void Build()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                win = (Window)XamlReader.Load(s);
            FitToScreen(win);
            nav = F<ListBox>("NavList");
            cardList = F<ListBox>("CardList");
            diskList = F<ListBox>("DiskList");
            loadingPanel = F<FrameworkElement>("LoadingPanel");
            genericPage = F<FrameworkElement>("GenericPage");
            diskPage = F<FrameworkElement>("DiskPage");
            detail = F<FrameworkElement>("Detail");
            loadingText = F<TextBlock>("LoadingText");
            pageTitle = F<TextBlock>("PageTitle");
            pageDesc = F<TextBlock>("PageDesc");
            actionBtn = F<Button>("ActionBtn");
            refreshBtn = F<Button>("RefreshBtn");
            saveBtn = F<Button>("SaveBtn");
            ringArc = F<System.Windows.Shapes.Path>("RingArc");
            try { win.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch (Exception) { }

            nav.SelectionChanged += delegate { ShowSection(nav.SelectedItem as Section); };
            cardList.SelectionChanged += delegate
            {
                Card c = cardList.SelectedItem as Card;
                if (c == null) return;
                cardList.SelectedIndex = -1;
                if (string.IsNullOrEmpty(c.Target)) return;
                foreach (Section s in sections) if (s.Key == c.Target) { nav.SelectedItem = s; break; }
            };
            diskList.SelectionChanged += delegate
            {
                DiskReport r = diskList.SelectedItem as DiskReport;
                detail.DataContext = r;
                UpdateRing(r);
                ScrollViewer sv = detail as ScrollViewer;
                if (sv != null) sv.ScrollToTop();
            };
            refreshBtn.Click += delegate { Load(); };
            saveBtn.Click += delegate { SaveReport(); };
            actionBtn.Click += delegate { RunAction(); };
            win.ContentRendered += delegate { Load(); };
        }

        // ------------------------------------------------------------ incarcare
        void Status(string text)
        {
            win.Dispatcher.BeginInvoke(new Action(delegate { loadingText.Text = text; }));
        }

        void Load()
        {
            if (loading) return;
            loading = true;
            refreshBtn.IsEnabled = false;
            saveBtn.IsEnabled = false;
            loadingPanel.Visibility = Visibility.Visible;
            genericPage.Visibility = Visibility.Collapsed;
            diskPage.Visibility = Visibility.Collapsed;
            string prevKey = nav.SelectedItem is Section ? ((Section)nav.SelectedItem).Key : null;

            Thread t = new Thread(delegate()
            {
                List<Section> secs = new List<Section>();
                List<DiskReport> dl = new List<DiskReport>();
                try
                {
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
                    secs.Insert(0, BuildSummary(secs, sys));
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
                    nav.ItemsSource = null;
                    nav.ItemsSource = sections;
                    diskList.ItemsSource = disks;
                    if (disks.Count > 0) diskList.SelectedIndex = 0;
                    loading = false;
                    refreshBtn.IsEnabled = true;
                    saveBtn.IsEnabled = true;
                    Section sel = sections[0];
                    if (prevKey != null) foreach (Section s in sections) if (s.Key == prevKey) sel = s;
                    nav.SelectedItem = sel;
                    ShowSection(sel);
                }));
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
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

        Section BuildSummary(List<Section> secs, Section sys)
        {
            Section sum = new Section("sumar", "Sumar", "");
            sum.Description = "Starea fiecărei componente pe scurt. Apasă pe un card pentru detalii.";
            int worst = -1;
            foreach (Section s in secs) if (s.Level > worst) worst = s.Level;
            string title = worst == 2 ? "Am găsit probleme care cer atenție" : worst == 1 ? "Calculatorul merge, dar are câteva lucruri de urmărit" : "Calculatorul este în stare bună";
            Card head = sum.AddCard(title, sys != null && sys.Cards.Count > 0 ? sys.Cards[0].Title + " · " + sys.Summary : "");
            head.SetLevel(worst < 0 ? 0 : worst, null);
            foreach (Section s in secs)
            {
                if (s.Level >= 1) head.Note(s.Name + ": " + (s.Level == 2 ? "problemă" : "de urmărit"));
            }
            if (head.NoteList.Count == 0) head.Note("Nicio componentă nu are probleme detectate. Ține totuși o copie de rezervă a datelor importante.");
            foreach (Section s in secs)
            {
                Card c = sum.AddCard(s.Name, s.Summary);
                c.Target = s.Key;
                c.SetLevel(s.Level, s.Status);
                int n = 0;
                foreach (Card x in s.Cards)
                {
                    if (x.Level < 1) continue;
                    foreach (string note in x.NoteList) { if (n++ < 2) c.Note(note); }
                }
            }
            sum.Finish();
            sum.SetLevel(worst);
            return sum;
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

        // ------------------------------------------------------------ navigare
        void ShowSection(Section s)
        {
            if (s == null || loading) return;
            loadingPanel.Visibility = Visibility.Collapsed;
            if (s.Key == "discuri")
            {
                genericPage.Visibility = Visibility.Collapsed;
                diskPage.Visibility = Visibility.Visible;
                if (diskList.SelectedIndex < 0 && disks.Count > 0) diskList.SelectedIndex = 0;
                return;
            }
            diskPage.Visibility = Visibility.Collapsed;
            genericPage.Visibility = Visibility.Visible;
            pageTitle.Text = s.Name;
            pageDesc.Text = s.Description;
            actionBtn.Content = s.ActionText;
            actionBtn.Tag = s.Key;
            actionBtn.Visibility = string.IsNullOrEmpty(s.ActionText) ? Visibility.Collapsed : Visibility.Visible;
            cardList.ItemsSource = s.Cards;
            if (cardList.Items.Count > 0) cardList.ScrollIntoView(cardList.Items[0]);
        }

        void RunAction()
        {
            if ((actionBtn.Tag as string) == "ram")
            {
                MessageBoxResult r = MessageBox.Show("Se deschide Diagnosticul de memorie Windows.\n\nAlege „Repornește acum și verifică problemele”. Testul durează 10–20 de minute, iar rezultatul apare aici după repornire.\n\nSalvează-ți întâi lucrul deschis.",
                    "Test memorie RAM", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (r != MessageBoxResult.OK) return;
                try { System.Diagnostics.Process.Start(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "MdSched.exe")); }
                catch (Exception ex) { MessageBox.Show("Nu am putut porni testul: " + ex.Message, "Sănătate PC"); }
            }
        }

        // ------------------------------------------------------------ raport
        string BuildReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RAPORT SĂNĂTATE PC — Info PC Electric");
            sb.AppendLine("Calculator: " + Environment.MachineName + "    Data: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm"));
            sb.AppendLine(new string('=', 76));
            foreach (Section s in sections)
            {
                if (s.Key == "discuri") continue;
                sb.AppendLine();
                sb.AppendLine(s.Name.ToUpperInvariant() + "   [" + s.Status + "]");
                foreach (Card c in s.Cards)
                {
                    sb.AppendLine("  " + c.Title + (c.Subtitle.Length > 0 ? " — " + c.Subtitle : "") + "   [" + c.Status + "]");
                    foreach (KV kv in c.Props) sb.AppendLine("      " + kv.Key + ": " + kv.Value);
                    foreach (string n in c.NoteList) sb.AppendLine("      ! " + n);
                }
                sb.AppendLine(new string('-', 76));
            }
            sb.AppendLine();
            sb.AppendLine("DISCURI");
            foreach (DiskReport r in disks)
            {
                sb.AppendLine();
                sb.AppendLine("  " + r.Title + "  [" + r.TypeBadge + "]   Stare: " + r.HealthText + "   Scor: " + r.ScoreText + "/100");
                sb.AppendLine("      Verdict: " + r.Verdict);
                sb.AppendLine("      Capacitate: " + r.SizeText + "   Partiții: " + r.Letters + "   Interfață: " + r.Interface);
                sb.AppendLine("      Serie: " + r.Serial + "   Firmware: " + r.Firmware);
                sb.AppendLine("      Temperatură: " + r.TempText + "   Viață rămasă: " + r.LifeText + "   Ore: " + r.HoursText + "   Porniri: " + r.CyclesText + "   Scris: " + r.WrittenText);
                sb.AppendLine("      Sectoare defecte / erori: " + r.ErrorsText + " (" + r.ErrorsSub + ")");
                foreach (string x in r.Reasons) sb.AppendLine("      ! " + x);
                if (r.Attributes.Count > 0)
                {
                    sb.AppendLine("      Atribute:");
                    foreach (SmartAttr a in r.Attributes)
                        sb.AppendLine(string.Format("        {0,-3} {1,-38} {2,4} {3,4} {4,4}  {5,-26} {6}", a.Id, a.Name, a.Current, a.Worst, a.Threshold, a.RawText, a.Status));
                }
            }
            return sb.ToString();
        }

        void SaveReport()
        {
            if (sections.Count == 0) return;
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog();
            d.Filter = "Fișier text (*.txt)|*.txt";
            d.FileName = "Raport_sanatate_PC_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt";
            d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (d.ShowDialog(win) == true)
            {
                File.WriteAllText(d.FileName, BuildReport(), new UTF8Encoding(true));
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + d.FileName + "\""); } catch (Exception) { }
            }
        }
    }
}
