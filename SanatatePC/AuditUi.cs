// Sanatate PC - Info PC Electric
// Fereastra "Audit complet": tehnicianul alege durata si testele, apoi urmareste cum ruleaza fiecare.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace InfoPcDisk
{
    public partial class MainApp
    {
        string goAfterLoad;

        void ShowAudit()
        {
            if (!Pro)
            {
                if (MessageBox.Show(win, "Auditul complet (teste pe durata aleasă, cu raport) e în versiunea Pro.\n\nVrei să activezi Pro acum?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    ShowActivation();
                return;
            }
            if (loading || sections.Count == 0) { MessageBox.Show(win, "Așteaptă să se termine citirea componentelor.", "Sănătate PC"); return; }

            Window d = LoadWindow("Audit.xaml");
            d.Owner = win;
            FitToScreen(d);
            WrapPanel minPanel = (WrapPanel)d.FindName("MinPanel");
            CheckBox cStress = (CheckBox)d.FindName("ChkStress"), cRam = (CheckBox)d.FindName("ChkRam"), cDisk = (CheckBox)d.FindName("ChkDisk"), cErr = (CheckBox)d.FindName("ChkErr");
            TextBlock state = (TextBlock)d.FindName("StateTitle"), phase = (TextBlock)d.FindName("PhaseText"), clock = (TextBlock)d.FindName("ClockText"),
                clockSub = (TextBlock)d.FindName("ClockSub"), hint = (TextBlock)d.FindName("SetupHint"), foot = (TextBlock)d.FindName("FootText");
            ProgressBar prog = (ProgressBar)d.FindName("Prog");
            ItemsControl stepList = (ItemsControl)d.FindName("StepList");
            Button start = (Button)d.FindName("StartBtn"), close = (Button)d.FindName("CloseBtn");
            FrameworkElement setup = (FrameworkElement)d.FindName("SetupBox");

            // valorile implicite din Setari; se pot schimba pentru fiecare client
            AuditOptions def = settings.Audit;
            bool any = false;
            foreach (object o in minPanel.Children)
            {
                RadioButton rb = o as RadioButton;
                if (rb != null && (string)rb.Tag == def.Minutes.ToString()) { rb.IsChecked = true; any = true; }
            }
            if (!any)
            {
                RadioButton extra = new RadioButton();
                extra.Style = (Style)d.FindResource("Chip"); extra.GroupName = "m";
                extra.Content = def.Minutes + " min"; extra.Tag = def.Minutes.ToString(); extra.IsChecked = true;
                minPanel.Children.Add(extra);
            }
            cStress.IsChecked = def.Stress; cRam.IsChecked = def.Ram; cDisk.IsChecked = def.Disk; cErr.IsChecked = def.Errors;

            Func<AuditOptions> read = delegate
            {
                AuditOptions o = new AuditOptions();
                foreach (object x in minPanel.Children)
                {
                    RadioButton rb = x as RadioButton;
                    if (rb != null && rb.IsChecked == true) o.Minutes = int.Parse((string)rb.Tag);
                }
                o.Stress = cStress.IsChecked == true; o.Ram = cRam.IsChecked == true; o.Disk = cDisk.IsChecked == true; o.Errors = cErr.IsChecked == true;
                return o;
            };
            Action preview = delegate
            {
                AuditOptions o = read();
                start.IsEnabled = o.Count > 0;
                AuditRunner pr = new AuditRunner(o, null);
                stepList.ItemsSource = pr.Steps;
                clock.Text = o.Minutes + ":00";
                clockSub.Text = "durata aleasă";
                if (o.Count == 0) hint.Text = "Bifează cel puțin un test.";
                else if (!o.Stress && !o.Ram && !o.Disk) hint.Text = "Doar urmărirea erorilor: aplicația observă jurnalul de sistem pe toată durata, fără să încarce calculatorul.";
                else hint.Text = "Timpul se împarte între testele bifate. Dacă un test termină mai devreme (de ex. un SSD mic citit complet), timpul rămas trece la următorul.";
                if (o.Disk && !Collect.IsAdmin) hint.Text += "  Citirea discurilor cere ca aplicația să ruleze ca administrator.";
            };
            foreach (object o in minPanel.Children) { RadioButton rb = o as RadioButton; if (rb != null) rb.Checked += delegate { preview(); }; }
            RoutedEventHandler ch = delegate { preview(); };
            cStress.Checked += ch; cStress.Unchecked += ch; cRam.Checked += ch; cRam.Unchecked += ch;
            cDisk.Checked += ch; cDisk.Unchecked += ch; cErr.Checked += ch; cErr.Unchecked += ch;
            preview();

            AuditRunner run = null;
            bool done = false;
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.Tick += delegate
            {
                if (run == null) return;
                double total = (run.Deadline - run.Start).TotalSeconds, el = (DateTime.Now - run.Start).TotalSeconds;
                phase.Text = run.Phase;
                if (!run.Finished)
                {
                    double left = Math.Max(0, total - el);
                    clock.Text = ((int)left / 60) + ":" + ((int)left % 60).ToString("00");
                    clockSub.Text = "rămas · trecut " + AuditResult.Duration(el);
                    prog.Value = Math.Min(99, el * 100 / Math.Max(1, total));
                    return;
                }
                timer.Stop();
                done = true;
                AuditResult r = run.Result;
                prog.Value = r.Stopped ? prog.Value : 100;
                clock.Text = AuditResult.Duration(r.Seconds);
                clockSub.Text = "durata reală";
                int lvl = r.Level;
                state.Text = r.Stopped ? "Audit oprit" : lvl == 2 ? "Audit terminat — probleme găsite" : lvl == 1 ? "Audit terminat — de urmărit" : "Audit terminat — totul în regulă";
                state.Foreground = (Brush)new BrushConverter().ConvertFromString(r.Stopped ? "#E6EAF2" : Lvl.Fg(lvl < 0 ? 0 : lvl));
                phase.Text = "Rezultatele apar acum în aplicație (secțiunea „Audit complet” și „Ce trebuie făcut”) și în raportul PDF.";
                foot.Text = "Salvează calculatorul în proiect sau fă raportul PDF ca să păstrezi rezultatele.";
                start.Content = "Vezi rezultatele";
                start.Background = (Brush)new BrushConverter().ConvertFromString("#2563EB");
                start.IsEnabled = true;
                close.IsEnabled = true;
            };

            start.Click += delegate
            {
                if (done) { d.Close(); return; }
                if (run != null && !run.Finished)
                {
                    if (MessageBox.Show(d, "Opresc auditul acum? Rezultatele de până acum se păstrează.", "Audit complet", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    run.Stop();
                    start.IsEnabled = false;
                    phase.Text = "Se oprește…";
                    return;
                }
                AuditOptions o = read();
                if (o.Count == 0) return;
                live.Stop();
                run = new AuditRunner(o, disks);
                stepList.ItemsSource = run.Steps;
                setup.IsEnabled = false;
                state.Text = "Audit în desfășurare…";
                start.Content = "Oprește";
                start.Background = (Brush)new BrushConverter().ConvertFromString("#B91C1C");
                close.IsEnabled = false;
                run.StartAsync();
                timer.Start();
            };
            close.Click += delegate { d.Close(); };
            d.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (run != null && !run.Finished)
                {
                    if (MessageBox.Show(d, "Auditul rulează. Îl opresc?", "Audit complet", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
                    run.Stop();
                    for (int i = 0; i < 200 && !run.Finished; i++) System.Threading.Thread.Sleep(50);
                    done = true;
                }
                timer.Stop();
            };
            d.ShowDialog();
            if (done || (run != null && run.Finished))
            {
                goAfterLoad = "audit";
                Load();
            }
        }
    }
}
