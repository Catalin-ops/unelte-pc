// Sanatate PC - Info PC Electric
// Fereastra "Test memorie RAM": test in aplicatie (fara repornire) sau test Windows la repornire.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace InfoPcDisk
{
    public partial class MainApp
    {
        void ShowRamTest()
        {
            Window d = LoadWindow("TestRam.xaml");
            d.Owner = win;
            CultureInfo ro = new CultureInfo("ro-RO");
            Slider slider = (Slider)d.FindName("SizeSlider");
            TextBlock sizeText = (TextBlock)d.FindName("SizeText"), state = (TextBlock)d.FindName("StateTitle"), phase = (TextBlock)d.FindName("PhaseText"),
                pct = (TextBlock)d.FindName("PctText"), memT = (TextBlock)d.FindName("MemText"), speedT = (TextBlock)d.FindName("SpeedText"),
                etaT = (TextBlock)d.FindName("EtaText"), errT = (TextBlock)d.FindName("ErrText"), logT = (TextBlock)d.FindName("LogText");
            ProgressBar prog = (ProgressBar)d.FindName("Prog");
            Button start = (Button)d.FindName("StartBtn");
            FrameworkElement setup = (FrameworkElement)d.FindName("SetupBox");
            ScrollViewer logScroll = (ScrollViewer)d.FindName("LogScroll");

            long total, avail;
            RamTester.MemoryInfo(out total, out avail);
            long sugg = RamTester.SuggestedBytes();
            double maxGb = Math.Max(0.25, Math.Floor(sugg / (double)(1L << 30) * 4) / 4);
            ((TextBlock)d.FindName("MemInfo")).Text = "Memorie totală " + (total / (double)(1L << 30)).ToString("0.#", ro) + " GB · liberă acum " +
                (avail / (double)(1L << 30)).ToString("0.#", ro) + " GB · se pot testa până la " + maxGb.ToString("0.##", ro) + " GB fără să încetinească Windows.";
            slider.Maximum = maxGb;
            slider.Value = maxGb;
            sizeText.Text = maxGb.ToString("0.##", ro) + " GB";
            slider.ValueChanged += delegate { sizeText.Text = slider.Value.ToString("0.##", ro) + " GB"; };

            if (!Pro)
            {
                start.IsEnabled = false;
                state.Text = "Testul din aplicație e în versiunea Pro";
                phase.Text = "Poți folosi gratuit testul complet la repornire (butonul din stânga jos).";
            }

            RamTester t = null;
            bool completedOne = false;
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(300);
            int logShown = 0;
            timer.Tick += delegate
            {
                if (t == null) return;
                double p = t.Total > 0 ? Math.Min(100, t.Done * 100.0 / t.Total) : 0;
                prog.Value = p;
                pct.Text = Math.Floor(p) + "%";
                phase.Text = t.Phase;
                memT.Text = (t.AllocatedBytes / (double)(1L << 30)).ToString("0.##", ro) + " GB";
                double el = (DateTime.Now - t.Start).TotalSeconds;
                if (el > 1 && t.Done > 0)
                {
                    double sp = t.Done / el;
                    speedT.Text = (sp / (1L << 30)).ToString("0.0", ro) + " GB/s";
                    double eta = (t.Total - t.Done) / sp;
                    etaT.Text = eta >= 60 ? ((int)(eta / 60)) + " min " + ((int)eta % 60) + " s" : ((int)eta) + " s";
                }
                errT.Text = t.Errors.ToString();
                errT.Foreground = (Brush)new BrushConverter().ConvertFromString(t.Errors > 0 ? "#F87171" : "#4ADE80");
                string[] lines = t.LogLines();
                if (lines.Length != logShown) { logT.Text = string.Join("\n", lines); logShown = lines.Length; logScroll.ScrollToEnd(); }
                if (!t.Finished) return;
                timer.Stop();
                setup.IsEnabled = true;
                start.Content = "Pornește din nou";
                if (t.Error != null) { state.Text = "Testul nu a putut rula"; phase.Text = t.Error; return; }
                RamTestResult r = new RamTestResult();
                r.Date = DateTime.Now; r.TestedGB = t.AllocatedBytes / (double)(1L << 30); r.Passes = Math.Max(1, t.CurrentPass); r.Errors = t.Errors; r.Completed = !t.Cancel;
                if (r.Completed || r.Errors > 0) { r.Save(); completedOne = true; }
                prog.Value = r.Completed ? 100 : prog.Value;
                if (t.Errors > 0)
                {
                    state.Text = "Am găsit " + t.Errors + (t.Errors == 1 ? " eroare" : " erori") + " în memorie";
                    state.Foreground = (Brush)new BrushConverter().ConvertFromString("#F87171");
                    phase.Text = "Memoria RAM are o problemă. Testează plăcuțele pe rând (câte una montată) ca să o găsești pe cea defectă.";
                }
                else if (t.Cancel)
                {
                    state.Text = "Test oprit";
                    phase.Text = "Până la oprire nu a apărut nicio eroare.";
                }
                else
                {
                    state.Text = "Nicio eroare găsită";
                    state.Foreground = (Brush)new BrushConverter().ConvertFromString("#4ADE80");
                    phase.Text = r.TestedGB.ToString("0.##", ro) + " GB testați, " + r.Passes + (r.Passes == 1 ? " trecere" : " treceri") + ". Memoria liberă funcționează corect.";
                }
            };

            start.Click += delegate
            {
                if (t != null && !t.Finished) { t.Cancel = true; start.IsEnabled = false; phase.Text = "Se oprește…"; return; }
                t = new RamTester();
                t.TargetBytes = (long)(slider.Value * (1L << 30));
                t.Passes = ((RadioButton)d.FindName("P10")).IsChecked == true ? 10 : ((RadioButton)d.FindName("P3")).IsChecked == true ? 3 : 1;
                state.Text = "Testez memoria…";
                state.Foreground = (Brush)new BrushConverter().ConvertFromString("#E6EAF2");
                logT.Text = ""; logShown = 0;
                setup.IsEnabled = false;
                start.Content = "Oprește";
                start.IsEnabled = true;
                t.StartAsync();
                timer.Start();
            };
            timer.Tick += delegate { if (t != null && t.Finished) start.IsEnabled = true; };
            ((Button)d.FindName("BootBtn")).Click += delegate { StartWindowsMemTest(d); };
            ((Button)d.FindName("CloseBtn")).Click += delegate { d.Close(); };
            d.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (t != null && !t.Finished)
                {
                    if (MessageBox.Show(d, "Testul rulează. Îl opresc?", "Test memorie RAM", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
                    t.Cancel = true;
                    for (int i = 0; i < 100 && !t.Finished; i++) System.Threading.Thread.Sleep(50);
                }
                timer.Stop();
            };
            d.ShowDialog();
            if (completedOne) Load();
        }
    }
}
