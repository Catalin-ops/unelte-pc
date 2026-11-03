// Sanatate PC - Info PC Electric
// Monitorizare live: incarcare procesor, RAM, placa video, disc si temperaturi, cu grafice.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace InfoPcDisk
{
    public class LiveChart
    {
        public string Key, Title, Unit;
        public double Max;
        public Queue<double> Points = new Queue<double>();
        public TextBlock ValueText, SubText;
        public Polyline Line;
        public Polygon Fill;
        public Canvas Canvas;
        public double Peak = -1;
    }

    public class LiveMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMSTAT
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);

        const int History = 60;
        static readonly CultureInfo RO = new CultureInfo("ro-RO");
        public List<LiveChart> Charts = new List<LiveChart>();
        Dictionary<string, LiveChart> byKey = new Dictionary<string, LiveChart>();
        System.Windows.Threading.DispatcherTimer timer;
        Thread worker;
        volatile bool running;
        readonly object sync = new object();
        Dictionary<string, double> latest = new Dictionary<string, double>();
        Dictionary<string, string> latestSub = new Dictionary<string, string>();

        public LiveMonitor(UniformGrid grid)
        {
            Add(grid, "cpu", "Procesor — încărcare", "%", 100);
            Add(grid, "ram", "Memorie RAM — folosită", "%", 100);
            Add(grid, "gpu", "Placă video — încărcare", "%", 100);
            Add(grid, "disk", "Discuri — activitate", "%", 100);
            Add(grid, "cputemp", "Temperatură procesor", "°C", 100);
            Add(grid, "gputemp", "Temperatură placă video", "°C", 100);
            timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1000);
            timer.Tick += delegate { Draw(); };
        }

        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        void Add(UniformGrid grid, string key, string title, string unit, double max)
        {
            LiveChart c = new LiveChart();
            c.Key = key; c.Title = title; c.Unit = unit; c.Max = max;
            Border card = new Border();
            card.Background = B("#151C2C"); card.BorderBrush = B("#24304A"); card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(16); card.Padding = new Thickness(18, 16, 18, 14); card.Margin = new Thickness(0, 0, 12, 12);
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DockPanel head = new DockPanel();
            TextBlock t = new TextBlock(); t.Text = title; t.FontSize = 12.5; t.Foreground = B("#8A96B0"); t.FontWeight = FontWeights.SemiBold;
            c.ValueText = new TextBlock(); c.ValueText.Text = "—"; c.ValueText.FontSize = 26; c.ValueText.FontWeight = FontWeights.Bold; c.ValueText.Foreground = B("#E6EAF2");
            StackPanel sp = new StackPanel(); sp.Children.Add(t); sp.Children.Add(c.ValueText);
            head.Children.Add(sp);
            Grid.SetRow(head, 0);
            c.Canvas = new Canvas(); c.Canvas.Height = 90; c.Canvas.Margin = new Thickness(0, 8, 0, 6); c.Canvas.ClipToBounds = true;
            c.Canvas.Background = B("#0F1524");
            c.Fill = new Polygon(); c.Fill.Fill = B("#1A3B82F6");
            c.Line = new Polyline(); c.Line.Stroke = B("#60A5FA"); c.Line.StrokeThickness = 2; c.Line.StrokeLineJoin = PenLineJoin.Round;
            c.Canvas.Children.Add(c.Fill); c.Canvas.Children.Add(c.Line);
            Grid.SetRow(c.Canvas, 1);
            c.SubText = new TextBlock(); c.SubText.FontSize = 11.5; c.SubText.Foreground = B("#8A96B0"); c.SubText.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(c.SubText, 2);
            g.Children.Add(head); g.Children.Add(c.Canvas); g.Children.Add(c.SubText);
            card.Child = g;
            grid.Children.Add(card);
            Charts.Add(c);
            byKey[key] = c;
        }

        public void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(Sample);
            worker.IsBackground = true;
            worker.Start();
            timer.Start();
        }

        public void Stop()
        {
            running = false;
            timer.Stop();
        }

        void Put(string k, double v, string sub)
        {
            lock (sync) { latest[k] = v; latestSub[k] = sub; }
        }

        static PerformanceCounter TryCounter(string cat, string name, string inst)
        {
            try { PerformanceCounter p = new PerformanceCounter(cat, name, inst, true); p.NextValue(); return p; }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ esantionare (fir separat)
        void Sample()
        {
            PerformanceCounter cpu = TryCounter("Processor Information", "% Processor Utility", "_Total") ?? TryCounter("Processor", "% Processor Time", "_Total");
            PerformanceCounter disk = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
            List<PerformanceCounter> gpu = new List<PerformanceCounter>();
            DateTime gpuRefresh = DateTime.MinValue, slowNext = DateTime.MinValue;
            bool hasNv = false, nvChecked = false;
            string smi = null;
            while (running)
            {
                try
                {
                    if (cpu != null) { double v = Math.Min(100, Math.Max(0, cpu.NextValue())); Put("cpu", v, Environment.ProcessorCount + " fire logice"); }
                    else Put("cpu", -1, "indisponibil pe acest sistem");

                    MEMSTAT m = new MEMSTAT(); m.dwLength = (uint)Marshal.SizeOf(typeof(MEMSTAT));
                    if (GlobalMemoryStatusEx(ref m))
                    {
                        double used = m.ullTotalPhys - m.ullAvailPhys;
                        Put("ram", used * 100.0 / m.ullTotalPhys, Health.FormatBytes((long)used, false) + " din " + Health.FormatBytes((long)m.ullTotalPhys, false));
                    }

                    if (disk != null) { double v = 100 - Math.Min(100, Math.Max(0, disk.NextValue())); Put("disk", v, "toate discurile, procent din timp ocupat"); }
                    else Put("disk", -1, "indisponibil pe acest sistem");

                    if (DateTime.Now >= gpuRefresh)
                    {
                        foreach (PerformanceCounter p in gpu) p.Dispose();
                        gpu.Clear();
                        try
                        {
                            PerformanceCounterCategory cat = new PerformanceCounterCategory("GPU Engine");
                            foreach (string inst in cat.GetInstanceNames())
                                if (inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                                {
                                    PerformanceCounter p = TryCounter("GPU Engine", "Utilization Percentage", inst);
                                    if (p != null) gpu.Add(p);
                                }
                        }
                        catch (Exception) { }
                        gpuRefresh = DateTime.Now.AddSeconds(10);
                    }
                    if (gpu.Count > 0)
                    {
                        double sum = 0;
                        foreach (PerformanceCounter p in gpu) { try { sum += p.NextValue(); } catch (Exception) { } }
                        Put("gpu", Math.Min(100, sum), "motor 3D, toate aplicațiile");
                    }
                    else Put("gpu", -1, "indisponibil pe acest sistem");

                    if (DateTime.Now >= slowNext)
                    {
                        Sensors sn = Collect.ReadSensors();
                        if (sn.CpuTemp > 0) Put("cputemp", sn.CpuTemp, "senzor: " + (sn.Source.Length > 0 ? sn.Source : "ACPI"));
                        else if (sn.BoardTemp > 0) Put("cputemp", sn.BoardTemp, "zonă termică ACPI (aproximativ)");
                        else Put("cputemp", -1, "pornește LibreHardwareMonitor pentru temperatura exactă");

                        if (!nvChecked)
                        {
                            nvChecked = true;
                            string[] paths = new string[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" };
                            foreach (string p in paths) if (File.Exists(p)) { smi = p; hasNv = true; break; }
                        }
                        int gt = -1;
                        if (hasNv)
                        {
                            try
                            {
                                ProcessStartInfo psi = new ProcessStartInfo(smi, "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
                                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
                                using (Process pr = Process.Start(psi))
                                {
                                    string o = pr.StandardOutput.ReadToEnd();
                                    pr.WaitForExit(4000);
                                    int.TryParse(o.Split('\n')[0].Trim(), out gt);
                                }
                            }
                            catch (Exception) { }
                        }
                        if (gt <= 0 && sn.GpuTemp > 0) gt = sn.GpuTemp;
                        if (gt > 0) Put("gputemp", gt, hasNv ? "senzor NVIDIA" : "senzor: " + sn.Source);
                        else Put("gputemp", -1, "indisponibilă pentru această placă video");
                        slowNext = DateTime.Now.AddSeconds(3);
                    }
                }
                catch (Exception) { }
                Thread.Sleep(1000);
            }
            if (cpu != null) cpu.Dispose();
            if (disk != null) disk.Dispose();
            foreach (PerformanceCounter p in gpu) p.Dispose();
        }

        // ------------------------------------------------------------ desenare (UI)
        static string Tint(string key, double v)
        {
            if (key.EndsWith("temp")) return v >= 90 ? "#F87171" : v >= 80 ? "#FBBF24" : "#4ADE80";
            return v >= 90 ? "#F87171" : v >= 75 ? "#FBBF24" : "#60A5FA";
        }

        void Draw()
        {
            Dictionary<string, double> vals;
            Dictionary<string, string> subs;
            lock (sync) { vals = new Dictionary<string, double>(latest); subs = new Dictionary<string, string>(latestSub); }
            foreach (LiveChart c in Charts)
            {
                double v;
                if (!vals.TryGetValue(c.Key, out v)) continue;
                string sub;
                subs.TryGetValue(c.Key, out sub);
                if (v < 0)
                {
                    c.ValueText.Text = "—";
                    c.SubText.Text = sub ?? "";
                    continue;
                }
                c.Points.Enqueue(v);
                while (c.Points.Count > History) c.Points.Dequeue();
                if (v > c.Peak) c.Peak = v;
                string col = Tint(c.Key, v);
                c.ValueText.Text = v.ToString("0", RO) + " " + c.Unit;
                c.ValueText.Foreground = B(col);
                c.SubText.Text = (sub ?? "") + "   ·   maxim în sesiune: " + c.Peak.ToString("0", RO) + " " + c.Unit;

                double w = c.Canvas.ActualWidth, h = c.Canvas.ActualHeight;
                if (w <= 0 || h <= 0) continue;
                double max = c.Key.EndsWith("temp") ? Math.Max(100, c.Peak + 5) : 100;
                PointCollection pts = new PointCollection(), fill = new PointCollection();
                int i = 0, n = c.Points.Count;
                double step = w / (History - 1);
                double x0 = w - (n - 1) * step;
                foreach (double p in c.Points)
                {
                    double x = x0 + i * step, y = h - 2 - (h - 6) * Math.Min(p, max) / max;
                    pts.Add(new Point(x, y));
                    i++;
                }
                if (pts.Count > 0)
                {
                    fill.Add(new Point(pts[0].X, h));
                    foreach (Point p in pts) fill.Add(p);
                    fill.Add(new Point(pts[pts.Count - 1].X, h));
                }
                c.Line.Points = pts;
                c.Line.Stroke = B(col);
                c.Fill.Points = fill;
                System.Windows.Media.Color fc = (System.Windows.Media.Color)ColorConverter.ConvertFromString(col);
                c.Fill.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, fc.R, fc.G, fc.B));
            }
        }
    }
}
