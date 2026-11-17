// Wi-Fi Stabil - graficul de evolutie: banda de stare, semnalul si latenta, cu valori la trecerea mouse-ului.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace InfoPcWifi
{
    public class Chart : FrameworkElement
    {
        public Monitor Source;
        double hoverX = -1;

        static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)));
        static readonly Brush Empty = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)));
        static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x96, 0xB0)));
        static readonly Pen Grid = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x4A)), 1));
        static readonly Pen SigPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), 1.6));
        static readonly Pen NetPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), 1.6));
        static readonly Pen GwPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)), 1.1));
        static readonly Pen Cursor1 = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xE6, 0xEA, 0xF2)), 1));
        static readonly Brush[] StateBr = new Brush[] {
            Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))), Freeze(new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08))),
            Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16))), Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))) };
        static readonly Typeface Face = new Typeface("Segoe UI");

        static Brush Freeze(Brush b) { b.Freeze(); return b; }
        static Pen FreezePen(Pen p) { p.Freeze(); return p; }

        public Chart()
        {
            MinHeight = 260;
            MouseMove += delegate(object s, MouseEventArgs e) { hoverX = e.GetPosition(this).X; InvalidateVisual(); };
            MouseLeave += delegate { hoverX = -1; InvalidateVisual(); };
        }

        class Col { public int St = -1, N, Bad; public int Sig = -1, Net = -1, Gw = -1; public bool Fill; }

        FormattedText Text(string s, double size, Brush b)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (Source == null || W < 50) return;
            Monitor.Snapshot snap = Source.Snap(true);
            List<Sample> S = snap.Samples;
            double left = 44;
            int cols = Math.Max(1, (int)(W - left));
            double TT = Math.Max(60, (snap.End - snap.Start).TotalSeconds);
            Col[] agg = new Col[cols];
            int maxLat = 0;
            List<int> lats = new List<int>();
            foreach (Sample s in S)
            {
                int c = (int)(s.T / TT * cols);
                if (c >= cols) c = cols - 1; if (c < 0) c = 0;
                Col a = agg[c];
                if (a == null) { a = new Col(); a.St = 0; agg[c] = a; }
                a.N++;
                if (s.State > a.St) a.St = s.State;
                if (s.State != 0) a.Bad++;
                if (s.Signal >= 0) a.Sig = a.Sig < 0 ? s.Signal : Math.Min(a.Sig, s.Signal);
                if (s.NetMs >= 0) { a.Net = Math.Max(a.Net, s.NetMs); lats.Add(s.NetMs); }
                if (s.GwMs >= 0) a.Gw = Math.Max(a.Gw, s.GwMs);
            }
            // completeaza coloanele dintre masuratori apropiate
            double colSec = TT / cols;
            int maxFill = (int)Math.Ceiling(Source.IntervalSec * 2.5 / colSec);
            Col last = null; int run = 0;
            for (int i = 0; i < cols; i++)
            {
                if (agg[i] != null && !agg[i].Fill) { last = agg[i]; run = 0; continue; }
                if (last != null && ++run <= maxFill && i * colSec <= (snap.End - snap.Start).TotalSeconds)
                {
                    Col f = new Col(); f.St = last.St; f.Sig = last.Sig; f.Net = last.Net; f.Gw = last.Gw; f.N = last.N; f.Bad = last.Bad; f.Fill = true;
                    agg[i] = f;
                }
            }
            lats.Sort();
            int p98 = lats.Count > 0 ? lats[Math.Min(lats.Count - 1, (int)(lats.Count * 0.98))] : 50;
            foreach (int v in new int[] { 50, 100, 150, 200, 300, 500, 1000, 2000 }) { maxLat = v; if (v >= p98 * 1.25) break; }

            // zone
            double bandY = 18, bandH = 22;
            double sigY = bandY + bandH + 26, sigH = Math.Max(60, (H - sigY - 26 - 22) * 0.45);
            double latY = sigY + sigH + 26, latH = Math.Max(60, H - latY - 22);

            dc.DrawText(Text("Stare conexiune", 11, Muted), new Point(left, 0));
            dc.DrawRectangle(Empty, null, new Rect(left, bandY, cols, bandH));
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < cols; i++)
                {
                    Col a = agg[i];
                    if (a == null) continue;
                    if (pass == 0 && a.St == 0) dc.DrawRectangle(StateBr[0], null, new Rect(left + i, bandY, 1, bandH));
                    if (pass == 1 && a.St > 0) dc.DrawRectangle(StateBr[a.St], null, new Rect(left + i - 1, bandY, 3, bandH));
                }

            dc.DrawText(Text("Semnal Wi-Fi (%)", 11, Muted), new Point(left, sigY - 18));
            GridLines(dc, left, cols, sigY, sigH, new int[] { 0, 50, 100 }, 100, "%");
            Line(dc, agg, left, sigY, sigH, 100, SigPen, delegate(Col c) { return c.Sig; });

            dc.DrawText(Text("Latență (ms) — internet / router", 11, Muted), new Point(left, latY - 18));
            GridLines(dc, left, cols, latY, latH, new int[] { 0, maxLat / 2, maxLat }, maxLat, "");
            Line(dc, agg, left, latY, latH, maxLat, GwPen, delegate(Col c) { return c.Gw; });
            Line(dc, agg, left, latY, latH, maxLat, NetPen, delegate(Col c) { return c.Net; });

            // axa timpului
            int[] steps = new int[] { 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200 };
            int maxTicks = Math.Max(2, cols / 90);
            int step = 43200;
            foreach (int st in steps) if (TT / st <= maxTicks) { step = st; break; }
            DateTime t0 = snap.Start;
            double first = Math.Ceiling((t0.TimeOfDay.TotalSeconds) / step) * step - t0.TimeOfDay.TotalSeconds;
            for (double t = first; t <= TT; t += step)
            {
                double x = left + t / TT * cols;
                string lab = t0.AddSeconds(t).ToString(step < 60 ? "HH:mm:ss" : "HH:mm");
                FormattedText ft = Text(lab, 10.5, Muted);
                dc.DrawRectangle(Muted, null, new Rect(x, H - 20, 1, 4));
                dc.DrawText(ft, new Point(Math.Min(W - ft.Width, Math.Max(left, x - ft.Width / 2)), H - 15));
            }

            if (S.Count == 0)
            {
                FormattedText ft = Text("Graficul apare după pornirea testului", 13, Muted);
                dc.DrawText(ft, new Point(left + (cols - ft.Width) / 2, sigY + sigH / 2 - 10));
                return;
            }

            // valori sub mouse
            if (hoverX >= left && hoverX < left + cols)
            {
                int i = (int)(hoverX - left);
                dc.DrawLine(Cursor1, new Point(hoverX, bandY), new Point(hoverX, latY + latH));
                Col a = agg[i];
                DateTime when = t0.AddSeconds((i + 0.5) / cols * TT);
                string txt = when.ToString("HH:mm:ss") + "\n";
                if (a == null) txt += "fără măsurători";
                else
                {
                    txt += Monitor.StateName(a.St) + (a.N > 1 && a.Bad > 0 ? " (" + a.Bad + " din " + a.N + ")" : "") + "\n";
                    txt += "Semnal: " + (a.Sig < 0 ? "—" : a.Sig + "%") + "\nInternet: " + (a.Net < 0 ? "—" : a.Net + " ms") + "  ·  Router: " + (a.Gw < 0 ? "—" : a.Gw + " ms");
                }
                FormattedText ft = Text(txt, 12, Brushes.White);
                double bw = ft.Width + 18, bh = ft.Height + 12;
                double bx = hoverX + 12 + bw > W ? hoverX - 12 - bw : hoverX + 12;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xF0, 0x0B, 0x11, 0x20)), new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 1), new Rect(bx, sigY, bw, bh), 8, 8);
                dc.DrawText(ft, new Point(bx + 9, sigY + 6));
            }
        }

        void GridLines(DrawingContext dc, double left, int cols, double y, double h, int[] ticks, int max, string unit)
        {
            foreach (int v in ticks)
            {
                double yy = Math.Round(y + h - (double)v / max * h) + 0.5;
                dc.DrawLine(Grid, new Point(left, yy), new Point(left + cols, yy));
                FormattedText ft = Text(v + unit, 10.5, Muted);
                dc.DrawText(ft, new Point(left - 6 - ft.Width, yy - ft.Height / 2));
            }
        }

        delegate int Pick(Col c);

        void Line(DrawingContext dc, Col[] agg, double left, double y, double h, int max, Pen pen, Pick pick)
        {
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                bool on = false;
                for (int i = 0; i < agg.Length; i++)
                {
                    int v = agg[i] == null ? -1 : pick(agg[i]);
                    if (v < 0) { on = false; continue; }
                    Point p = new Point(left + i + 0.5, y + h - Math.Min(v, max) / (double)max * h);
                    if (on) c.LineTo(p, true, false); else { c.BeginFigure(p, false, false); on = true; }
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }
}
