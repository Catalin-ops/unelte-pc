// Sanatate PC - Info PC Electric
// Setarile firmei (pentru raport) si generarea raportului PDF cu logo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InfoPcDisk
{
    public class Settings
    {
        public string Company = "", Phone = "", Email = "", Website = "", Address = "", Technician = "";
        public string LogoFile = "";   // nume fisier in acelasi folder cu setarile
        public string Dir = "";

        public string LogoPath { get { return LogoFile.Length > 0 ? Path.Combine(Dir, LogoFile) : ""; } }

        public static Settings Load(License lic)
        {
            Settings s = new Settings();
            // setarile stau in acelasi loc cu licenta (stick sau AppData)
            s.Dir = lic != null && lic.SourcePath.Length > 0 ? Path.GetDirectoryName(lic.SourcePath) : Licensing.AppDataDir;
            string f = Path.Combine(s.Dir, "setari.ini");
            if (!File.Exists(f)) return s;
            foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "Firma": s.Company = v; break;
                    case "Telefon": s.Phone = v; break;
                    case "Email": s.Email = v; break;
                    case "Website": s.Website = v; break;
                    case "Adresa": s.Address = v; break;
                    case "Tehnician": s.Technician = v; break;
                    case "Logo": s.LogoFile = v; break;
                }
            }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Firma=" + Company);
            sb.AppendLine("Telefon=" + Phone);
            sb.AppendLine("Email=" + Email);
            sb.AppendLine("Website=" + Website);
            sb.AppendLine("Adresa=" + Address);
            sb.AppendLine("Tehnician=" + Technician);
            sb.AppendLine("Logo=" + LogoFile);
            File.WriteAllText(Path.Combine(Dir, "setari.ini"), sb.ToString(), new UTF8Encoding(false));
        }

        public void SetLogo(string source)
        {
            Directory.CreateDirectory(Dir);
            string ext = Path.GetExtension(source).ToLowerInvariant();
            string name = "logo" + ext;
            string target = Path.Combine(Dir, name);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, true);
            LogoFile = name;
        }
    }

    public static class Report
    {
        static readonly CultureInfo RO = new CultureInfo("ro-RO");
        const double PageW = 793.7, PageH = 1122.5; // A4 la 96 dpi

        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        static Paragraph P(string text, double size, string color, bool bold)
        {
            Paragraph p = new Paragraph(new Run(text ?? ""));
            p.FontSize = size;
            p.Foreground = B(color);
            if (bold) p.FontWeight = FontWeights.Bold;
            p.Margin = new Thickness(0, 0, 0, 4);
            return p;
        }

        static string StatusColor(int level)
        {
            return level == 0 ? "#15803D" : level == 1 ? "#B45309" : level == 2 ? "#B91C1C" : "#475569";
        }

        static TableCell Cell(Block b, string bg, double padX)
        {
            TableCell c = new TableCell(b);
            if (bg != null) c.Background = B(bg);
            c.Padding = new Thickness(padX, 5, padX, 5);
            c.BorderBrush = B("#E2E8F0");
            c.BorderThickness = new Thickness(0, 0, 0, 1);
            return c;
        }

        static Block StatusPill(string text, int level)
        {
            Paragraph p = new Paragraph(new Run(" " + text + " "));
            p.FontSize = 10.5; p.FontWeight = FontWeights.Bold; p.Foreground = B(StatusColor(level));
            p.Margin = new Thickness(0); p.TextAlignment = TextAlignment.Right;
            return p;
        }

        public static FlowDocument Build(List<Section> sections, List<DiskReport> disks, License lic, Settings st, string client, string notes)
        {
            FlowDocument d = new FlowDocument();
            d.PageWidth = PageW; d.PageHeight = PageH;
            d.PagePadding = new Thickness(48, 40, 48, 56);
            d.ColumnWidth = PageW - 96; d.IsColumnWidthFlexible = true;
            d.FontFamily = new FontFamily("Segoe UI");
            d.FontSize = 11.5;
            d.Foreground = B("#0F172A");
            d.Background = Brushes.White;

            // ---- antet: logo + firma
            Table head = new Table();
            head.CellSpacing = 0;
            head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            TableRowGroup hg = new TableRowGroup();
            TableRow hr = new TableRow();
            Block left;
            BitmapImage logo = null;
            try
            {
                if (st.LogoPath.Length > 0 && File.Exists(st.LogoPath))
                {
                    logo = new BitmapImage();
                    logo.BeginInit();
                    logo.CacheOption = BitmapCacheOption.OnLoad;
                    logo.UriSource = new Uri(st.LogoPath);
                    logo.EndInit();
                }
            }
            catch (Exception) { logo = null; }
            if (logo != null)
            {
                System.Windows.Controls.Image img = new System.Windows.Controls.Image();
                img.Source = logo;
                img.MaxHeight = 64; img.MaxWidth = 220;
                img.Stretch = Stretch.Uniform;
                img.HorizontalAlignment = HorizontalAlignment.Left;
                left = new BlockUIContainer(img);
            }
            else left = P(st.Company.Length > 0 ? st.Company : "Raport tehnic", 18, "#1E3A8A", true);
            hr.Cells.Add(new TableCell(left));
            Paragraph comp = new Paragraph();
            comp.TextAlignment = TextAlignment.Right;
            comp.FontSize = 10.5;
            comp.Foreground = B("#334155");
            comp.Margin = new Thickness(0);
            List<string> lines = new List<string>();
            if (st.Company.Length > 0) lines.Add(st.Company);
            if (st.Address.Length > 0) lines.Add(st.Address);
            if (st.Phone.Length > 0) lines.Add("Tel: " + st.Phone);
            if (st.Email.Length > 0) lines.Add(st.Email);
            if (st.Website.Length > 0) lines.Add(st.Website);
            for (int i = 0; i < lines.Count; i++)
            {
                Run r = new Run(lines[i]);
                if (i == 0) r.FontWeight = FontWeights.Bold;
                comp.Inlines.Add(r);
                if (i < lines.Count - 1) comp.Inlines.Add(new LineBreak());
            }
            hr.Cells.Add(new TableCell(comp));
            hg.Rows.Add(hr);
            head.RowGroups.Add(hg);
            d.Blocks.Add(head);

            Paragraph rule = new Paragraph();
            rule.BorderBrush = B("#2563EB"); rule.BorderThickness = new Thickness(0, 0, 0, 2.5);
            rule.Margin = new Thickness(0, 10, 0, 14); rule.FontSize = 2;
            d.Blocks.Add(rule);

            d.Blocks.Add(P("Raport tehnic — starea calculatorului", 20, "#0F172A", true));
            Section sys = sections.Find(delegate(Section s) { return s.Key == "sistem"; });
            string pc = sys != null && sys.Cards.Count > 0 ? sys.Cards[0].Title : Environment.MachineName;
            Table meta = new Table();
            meta.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(4, GridUnitType.Star) });
            TableRowGroup mg = new TableRowGroup();
            Action<string, string> metaRow = delegate(string k, string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                TableRow r = new TableRow();
                r.Cells.Add(Cell(P(k, 10.5, "#64748B", false), null, 0));
                r.Cells.Add(Cell(P(v, 11, "#0F172A", true), null, 0));
                mg.Rows.Add(r);
            };
            metaRow("Client", client);
            metaRow("Calculator", pc + " (" + Environment.MachineName + ")");
            if (sys != null) metaRow("Sistem", sys.Summary);
            metaRow("Data verificării", DateTime.Now.ToString("dd.MM.yyyy, HH:mm", RO));
            metaRow("Tehnician", st.Technician);
            meta.RowGroups.Add(mg);
            d.Blocks.Add(meta);

            // ---- verdict general
            Section sum = sections.Find(delegate(Section s) { return s.Key == "sumar"; });
            if (sum != null && sum.Cards.Count > 0)
            {
                Card h = sum.Cards[0];
                Paragraph v = new Paragraph();
                v.Background = B(h.Level == 2 ? "#FEF2F2" : h.Level == 1 ? "#FFFBEB" : "#F0FDF4");
                v.BorderBrush = B(StatusColor(h.Level)); v.BorderThickness = new Thickness(4, 0, 0, 0);
                v.Padding = new Thickness(14, 10, 14, 10); v.Margin = new Thickness(0, 16, 0, 6);
                Run title = new Run(h.Title); title.FontSize = 15; title.FontWeight = FontWeights.Bold; title.Foreground = B(StatusColor(h.Level));
                v.Inlines.Add(title);
                foreach (string n in h.NoteList) { v.Inlines.Add(new LineBreak()); v.Inlines.Add(new Run("• " + n)); }
                d.Blocks.Add(v);

                // tabel cu toate componentele
                Table t = new Table();
                t.CellSpacing = 0;
                t.Columns.Add(new TableColumn { Width = new GridLength(2, GridUnitType.Star) });
                t.Columns.Add(new TableColumn { Width = new GridLength(5, GridUnitType.Star) });
                t.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
                TableRowGroup g = new TableRowGroup();
                for (int i = 1; i < sum.Cards.Count; i++)
                {
                    Card c = sum.Cards[i];
                    TableRow r = new TableRow();
                    r.Cells.Add(Cell(P(c.Title, 11, "#0F172A", true), i % 2 == 0 ? "#F8FAFC" : null, 6));
                    r.Cells.Add(Cell(P(c.Subtitle, 10.5, "#334155", false), i % 2 == 0 ? "#F8FAFC" : null, 6));
                    r.Cells.Add(Cell(StatusPill(c.Status, c.Level), i % 2 == 0 ? "#F8FAFC" : null, 6));
                    g.Rows.Add(r);
                }
                t.RowGroups.Add(g);
                d.Blocks.Add(t);
            }

            if (!string.IsNullOrEmpty(notes))
            {
                d.Blocks.Add(H2("Observațiile tehnicianului"));
                d.Blocks.Add(P(notes, 11.5, "#0F172A", false));
            }

            // ---- detalii pe componente
            foreach (Section s in sections)
            {
                if (s.Key == "sumar" || s.Key == "discuri" || s.Key == "live") continue;
                d.Blocks.Add(H2(s.Name));
                foreach (Card c in s.Cards) AddCard(d, c.Title, c.Subtitle, c.Status, c.Level, c.Props, c.NoteList);
                if (s.Key == "gpu") AddDisks(d, disks);
            }
            if (!sections.Exists(delegate(Section s) { return s.Key == "gpu"; })) AddDisks(d, disks);

            Paragraph foot = P("Raport generat cu Sănătate PC" + (lic != null && lic.IsPro ? " · licențiat pentru " + lic.Name : "") +
                ". Valorile reflectă starea calculatorului la data verificării.", 9.5, "#64748B", false);
            foot.Margin = new Thickness(0, 18, 0, 0);
            d.Blocks.Add(foot);
            return d;
        }

        static Paragraph H2(string text)
        {
            Paragraph p = P(text, 14.5, "#1E3A8A", true);
            p.Margin = new Thickness(0, 18, 0, 6);
            p.BorderBrush = B("#CBD5E1"); p.BorderThickness = new Thickness(0, 0, 0, 1);
            p.Padding = new Thickness(0, 0, 0, 3);
            p.KeepWithNext = true;
            return p;
        }

        static void AddCard(FlowDocument d, string title, string subtitle, string status, int level, List<KV> props, List<string> notes)
        {
            Table t = new Table();
            t.CellSpacing = 0;
            t.Margin = new Thickness(0, 6, 0, 4);
            t.Columns.Add(new TableColumn { Width = new GridLength(2, GridUnitType.Star) });
            t.Columns.Add(new TableColumn { Width = new GridLength(3, GridUnitType.Star) });
            TableRowGroup g = new TableRowGroup();
            TableRow hr = new TableRow();
            Paragraph tp = P(title, 12, "#0F172A", true);
            if (!string.IsNullOrEmpty(subtitle)) { tp.Inlines.Add(new Run("   " + subtitle) { FontWeight = FontWeights.Normal, FontSize = 10.5, Foreground = B("#64748B") }); }
            hr.Cells.Add(new TableCell(tp) { Padding = new Thickness(8, 6, 8, 4), Background = B("#F1F5F9") });
            hr.Cells.Add(new TableCell(StatusPill(status, level)) { Padding = new Thickness(8, 6, 8, 4), Background = B("#F1F5F9") });
            g.Rows.Add(hr);
            foreach (KV kv in props)
            {
                TableRow r = new TableRow();
                r.Cells.Add(Cell(P(kv.Key, 10.5, "#64748B", false), null, 8));
                r.Cells.Add(Cell(P(kv.Value, 11, "#0F172A", false), null, 8));
                g.Rows.Add(r);
            }
            t.RowGroups.Add(g);
            d.Blocks.Add(t);
            foreach (string n in notes)
            {
                Paragraph p = P("! " + n, 10.5, level >= 2 ? "#B91C1C" : level == 1 ? "#92400E" : "#334155", false);
                p.Margin = new Thickness(8, 2, 0, 2);
                d.Blocks.Add(p);
            }
        }

        static void AddDisks(FlowDocument d, List<DiskReport> disks)
        {
            if (disks == null || disks.Count == 0) return;
            d.Blocks.Add(H2("Discuri (HDD / SSD)"));
            foreach (DiskReport r in disks)
            {
                List<KV> props = new List<KV>();
                Action<string, string> a = delegate(string k, string v) { KV kv = new KV(); kv.Key = k; kv.Value = v; props.Add(kv); };
                a("Scor sănătate", r.ScoreText + " / 100");
                a("Tip / interfață", r.TypeBadge + " · " + r.Interface);
                a("Capacitate / partiții", r.SizeText + (r.Letters.Length > 0 ? " · " + r.Letters : ""));
                a("Număr serie / firmware", r.Serial + " / " + r.Firmware);
                a("Temperatură", r.TempText + " (" + r.TempMaxText + ")");
                if (r.IsSsd) a("Viață rămasă", r.LifeText);
                a("Ore de funcționare", r.HoursText + " (" + r.HoursSub + ")");
                a("Porniri", r.CyclesText);
                a("Date scrise", r.WrittenText);
                a("Sectoare defecte / erori", r.ErrorsText + " (" + r.ErrorsSub + ")");
                a("Verdict", r.Verdict);
                List<string> notes = new List<string>();
                foreach (string x in r.Reasons) if (r.Level >= 1) notes.Add(x);
                AddCard(d, r.Title, r.Subtitle, r.HealthText, r.Level, props, notes);
            }
        }


        // ------------------------------------------------------------ raport pe institutie (proiect)
        static Block Header(Settings st)
        {
            Table head = new Table();
            head.CellSpacing = 0;
            head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            TableRowGroup hg = new TableRowGroup();
            TableRow hr = new TableRow();
            Block left = null;
            try
            {
                if (st.LogoPath.Length > 0 && File.Exists(st.LogoPath))
                {
                    BitmapImage logo = new BitmapImage();
                    logo.BeginInit(); logo.CacheOption = BitmapCacheOption.OnLoad; logo.UriSource = new Uri(st.LogoPath); logo.EndInit();
                    System.Windows.Controls.Image img = new System.Windows.Controls.Image();
                    img.Source = logo; img.MaxHeight = 64; img.MaxWidth = 220; img.Stretch = Stretch.Uniform; img.HorizontalAlignment = HorizontalAlignment.Left;
                    left = new BlockUIContainer(img);
                }
            }
            catch (Exception) { left = null; }
            if (left == null) left = P(st.Company.Length > 0 ? st.Company : "Raport tehnic", 18, "#1E3A8A", true);
            hr.Cells.Add(new TableCell(left));
            Paragraph comp = new Paragraph();
            comp.TextAlignment = TextAlignment.Right; comp.FontSize = 10.5; comp.Foreground = B("#334155"); comp.Margin = new Thickness(0);
            List<string> lines = new List<string>();
            if (st.Company.Length > 0) lines.Add(st.Company);
            if (st.Address.Length > 0) lines.Add(st.Address);
            if (st.Phone.Length > 0) lines.Add("Tel: " + st.Phone);
            if (st.Email.Length > 0) lines.Add(st.Email);
            if (st.Website.Length > 0) lines.Add(st.Website);
            for (int i = 0; i < lines.Count; i++)
            {
                Run r = new Run(lines[i]);
                if (i == 0) r.FontWeight = FontWeights.Bold;
                comp.Inlines.Add(r);
                if (i < lines.Count - 1) comp.Inlines.Add(new LineBreak());
            }
            hr.Cells.Add(new TableCell(comp));
            hg.Rows.Add(hr);
            head.RowGroups.Add(hg);
            return head;
        }

        static FlowDocument NewDoc()
        {
            FlowDocument d = new FlowDocument();
            d.PageWidth = PageW; d.PageHeight = PageH;
            d.PagePadding = new Thickness(48, 40, 48, 56);
            d.ColumnWidth = PageW - 96; d.IsColumnWidthFlexible = true;
            d.FontFamily = new FontFamily("Segoe UI");
            d.FontSize = 11.5;
            d.Foreground = B("#0F172A");
            d.Background = Brushes.White;
            return d;
        }

        static Table Grid(double[] stars, string[] headers, List<string[]> rows, List<int> levels)
        {
            Table t = new Table();
            t.CellSpacing = 0;
            t.Margin = new Thickness(0, 6, 0, 6);
            foreach (double w in stars) t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
            TableRowGroup g = new TableRowGroup();
            TableRow h = new TableRow();
            foreach (string x in headers) h.Cells.Add(Cell(P(x, 10, "#475569", true), "#F1F5F9", 6));
            g.Rows.Add(h);
            for (int i = 0; i < rows.Count; i++)
            {
                TableRow r = new TableRow();
                string bg = i % 2 == 1 ? "#F8FAFC" : null;
                for (int k = 0; k < rows[i].Length; k++)
                {
                    bool last = k == rows[i].Length - 1 && levels != null;
                    Block b = last ? StatusPill(rows[i][k], levels[i]) : P(rows[i][k], 10.5, k == 0 ? "#0F172A" : "#334155", k == 0);
                    r.Cells.Add(Cell(b, bg, 6));
                }
                g.Rows.Add(r);
            }
            t.RowGroups.Add(g);
            return t;
        }

        public static FlowDocument BuildProject(Project pr, License lic, Settings st)
        {
            FlowDocument d = NewDoc();
            d.Blocks.Add(Header(st));
            Paragraph rule = new Paragraph();
            rule.BorderBrush = B("#2563EB"); rule.BorderThickness = new Thickness(0, 0, 0, 2.5);
            rule.Margin = new Thickness(0, 10, 0, 14); rule.FontSize = 2;
            d.Blocks.Add(rule);
            d.Blocks.Add(P("Raport tehnic — " + pr.Name, 20, "#0F172A", true));

            int good = 0, warn = 0, bad = 0, urgent = 0, recommended = 0;
            DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
            foreach (PcRecord p in pr.Pcs)
            {
                if (p.Level == 2) bad++; else if (p.Level == 1) warn++; else good++;
                foreach (Rec r in p.Recs) { if (r.Priority == 1) urgent++; else if (r.Priority == 2) recommended++; }
                if (p.Date < first) first = p.Date;
                if (p.Date > last) last = p.Date;
            }
            string period = first.Date == last.Date ? first.ToString("dd.MM.yyyy", RO) : first.ToString("dd.MM.yyyy", RO) + " – " + last.ToString("dd.MM.yyyy", RO);
            d.Blocks.Add(P("Verificare efectuată: " + period + (st.Technician.Length > 0 ? " · Tehnician: " + st.Technician : ""), 11, "#475569", false));

            // ---- sumar
            Paragraph sum = new Paragraph();
            int worst = bad > 0 ? 2 : warn > 0 ? 1 : 0;
            sum.Background = B(worst == 2 ? "#FEF2F2" : worst == 1 ? "#FFFBEB" : "#F0FDF4");
            sum.BorderBrush = B(StatusColor(worst)); sum.BorderThickness = new Thickness(4, 0, 0, 0);
            sum.Padding = new Thickness(14, 10, 14, 10); sum.Margin = new Thickness(0, 14, 0, 6);
            Run t1 = new Run(pr.Pcs.Count + (pr.Pcs.Count == 1 ? " calculator verificat" : " calculatoare verificate")); t1.FontSize = 15; t1.FontWeight = FontWeights.Bold;
            sum.Inlines.Add(t1);
            sum.Inlines.Add(new LineBreak());
            sum.Inlines.Add(new Run(good + " în stare bună · " + warn + " de urmărit · " + bad + " cu probleme"));
            sum.Inlines.Add(new LineBreak());
            sum.Inlines.Add(new Run(urgent + " lucrări urgente · " + recommended + " recomandate"));
            d.Blocks.Add(sum);

            // ---- ce trebuie facut, pe prioritati
            d.Blocks.Add(H2("Ce trebuie făcut"));
            bool any = false;
            for (int pri = 1; pri <= 3; pri++)
            {
                List<string[]> rows = new List<string[]>();
                foreach (PcRecord p in pr.Pcs)
                    foreach (Rec r in p.Recs)
                        if (r.Priority == pri) rows.Add(new string[] { p.Title, r.Action + (r.Detail.Length > 0 ? " — " + r.Detail : "") });
                if (rows.Count == 0) continue;
                any = true;
                string name = pri == 1 ? "Urgent" : pri == 2 ? "Recomandat" : "Opțional";
                Paragraph ph = P(name + " (" + rows.Count + ")", 12.5, StatusColor(pri == 1 ? 2 : pri == 2 ? 1 : -1), true);
                ph.Margin = new Thickness(0, 10, 0, 2); ph.KeepWithNext = true;
                d.Blocks.Add(ph);
                d.Blocks.Add(Grid(new double[] { 2, 5 }, new string[] { "Calculator", "Lucrare" }, rows, null));
            }
            if (!any) d.Blocks.Add(P("Nu e nevoie de nicio lucrare: toate calculatoarele sunt în stare bună.", 11.5, "#15803D", false));

            // ---- necesar piese (pentru oferta)
            Dictionary<string, int> parts = new Dictionary<string, int>();
            Dictionary<string, List<string>> where = new Dictionary<string, List<string>>();
            foreach (PcRecord p in pr.Pcs)
                foreach (Rec r in p.Recs)
                {
                    if (string.IsNullOrEmpty(r.Part) || r.Priority == 3) continue;
                    int c; parts.TryGetValue(r.Part, out c); parts[r.Part] = c + 1;
                    if (!where.ContainsKey(r.Part)) where[r.Part] = new List<string>();
                    where[r.Part].Add(p.Label);
                }
            if (parts.Count > 0)
            {
                d.Blocks.Add(H2("Necesar estimativ de piese"));
                List<string[]> rows = new List<string[]>();
                List<string> keys = new List<string>(parts.Keys);
                keys.Sort();
                foreach (string k in keys) rows.Add(new string[] { k, parts[k].ToString(), string.Join(", ", where[k].ToArray()) });
                d.Blocks.Add(Grid(new double[] { 3, 1, 4 }, new string[] { "Piesă", "Buc.", "Pentru" }, rows, null));
                d.Blocks.Add(P("Lista e orientativă: capacitățile și tipurile se confirmă la comandă (de ex. compatibilitatea plăcii de bază).", 9.5, "#64748B", false));
            }

            // ---- inventar
            d.Blocks.Add(H2("Inventarul calculatoarelor"));
            List<string[]> inv = new List<string[]>();
            List<int> lv = new List<int>();
            foreach (PcRecord p in pr.Pcs)
            {
                inv.Add(new string[] { p.Title + (string.IsNullOrEmpty(p.User) ? "" : "\n" + p.User), (p.Model ?? "") + "\n" + (p.Cpu ?? ""), p.Ram ?? "", p.Disks ?? "", p.Windows ?? "", Lvl.Text(p.Level < 0 ? 0 : p.Level) });
                lv.Add(p.Level < 0 ? 0 : p.Level);
            }
            Table it = Grid(new double[] { 2, 3, 2, 2.4, 2, 1.2 }, new string[] { "Calculator", "Model / procesor", "RAM", "Discuri", "Windows", "Stare" }, inv, lv);
            foreach (TableRowGroup g in it.RowGroups) foreach (TableRow r in g.Rows) foreach (TableCell c in r.Cells) foreach (Block b in c.Blocks) { Paragraph pp = b as Paragraph; if (pp != null) pp.FontSize = 9.5; }
            d.Blocks.Add(it);

            // ---- detalii pe fiecare calculator
            d.Blocks.Add(H2("Detalii pe calculatoare"));
            foreach (PcRecord p in pr.Pcs)
            {
                List<KV> props = new List<KV>();
                Action<string, string> a = delegate(string k, string v) { if (string.IsNullOrEmpty(v)) return; KV kv = new KV(); kv.Key = k; kv.Value = v; props.Add(kv); };
                a("Utilizator", p.User);
                a("Model", p.Model);
                a("Procesor", p.Cpu);
                a("Memorie RAM", p.Ram);
                a("Placă video", p.Gpu);
                a("Windows", p.Windows);
                foreach (SavedLine dl in p.DiskLines) a("Disc: " + dl.Name, dl.Status + " · " + dl.Summary);
                a("Scor general", p.Score + " / 100");
                a("Verificat la", p.Date.ToString("dd.MM.yyyy HH:mm", RO) + " (" + p.MachineName + ")");
                a("Observații tehnician", p.Notes);
                List<string> notes = new List<string>();
                foreach (Rec r in p.Recs) notes.Add("[" + r.PriorityText + "] " + r.Action + (r.Part.Length > 0 ? " — piesă: " + r.Part : ""));
                AddCard(d, p.Title, "", Lvl.Text(p.Level < 0 ? 0 : p.Level), p.Level < 0 ? 0 : p.Level, props, notes);
            }

            Paragraph foot = P("Raport generat cu Sănătate PC" + (lic != null && lic.IsPro ? " · licențiat pentru " + lic.Name : "") +
                ". Valorile reflectă starea fiecărui calculator la data verificării lui.", 9.5, "#64748B", false);
            foot.Margin = new Thickness(0, 18, 0, 0);
            d.Blocks.Add(foot);
            return d;
        }

        // ------------------------------------------------------------ PDF (pagini randate ca imagini, A4)
        public static int SavePdf(FlowDocument doc, string path, string footerLeft)
        {
            IDocumentPaginatorSource src = doc;
            DocumentPaginator pg = src.DocumentPaginator;
            pg.PageSize = new Size(PageW, PageH);
            pg.ComputePageCount();
            int n = pg.PageCount;
            List<byte[]> jpgs = new List<byte[]>();
            List<int[]> dims = new List<int[]>();
            const double scale = 2.0; // ~192 dpi
            for (int i = 0; i < n; i++)
            {
                DocumentPage page = pg.GetPage(i);
                DrawingVisual footer = new DrawingVisual();
                using (DrawingContext dc = footer.RenderOpen())
                {
                    Typeface tf = new Typeface("Segoe UI");
                    FormattedText l = new FormattedText(footerLeft, RO, FlowDirection.LeftToRight, tf, 9, B("#94A3B8"));
                    FormattedText r = new FormattedText("Pagina " + (i + 1) + " din " + n, RO, FlowDirection.LeftToRight, tf, 9, B("#94A3B8"));
                    dc.DrawText(l, new Point(48, PageH - 34));
                    dc.DrawText(r, new Point(PageW - 48 - r.Width, PageH - 34));
                }
                int w = (int)Math.Round(PageW * scale), h = (int)Math.Round(PageH * scale);
                RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                DrawingVisual bg = new DrawingVisual();
                using (DrawingContext dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageW, PageH));
                rtb.Render(bg);
                rtb.Render(page.Visual);
                rtb.Render(footer);
                JpegBitmapEncoder enc = new JpegBitmapEncoder();
                enc.QualityLevel = 88;
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (MemoryStream ms = new MemoryStream()) { enc.Save(ms); jpgs.Add(ms.ToArray()); }
                dims.Add(new int[] { w, h });
            }
            WritePdf(path, jpgs, dims);
            return n;
        }

        static void WritePdf(string path, List<byte[]> jpgs, List<int[]> dims)
        {
            int n = jpgs.Count;
            // obiecte: 1 catalog, 2 pages, apoi pentru fiecare pagina: page, content, image; la final info
            List<long> offsets = new List<long>();
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                Encoding a = Encoding.ASCII;
                Action<string> w = delegate(string s) { byte[] b = a.GetBytes(s); fs.Write(b, 0, b.Length); };
                w("%PDF-1.4\n%âãÏÓ\n");
                int total = 2 + n * 3 + 1;
                for (int i = 0; i <= total; i++) offsets.Add(0);
                offsets[1] = fs.Position;
                w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                offsets[2] = fs.Position;
                StringBuilder kids = new StringBuilder();
                for (int i = 0; i < n; i++) kids.Append((3 + i * 3) + " 0 R ");
                w("2 0 obj\n<< /Type /Pages /Count " + n + " /Kids [" + kids + "] >>\nendobj\n");
                for (int i = 0; i < n; i++)
                {
                    int po = 3 + i * 3, co = po + 1, io = po + 2;
                    string mb = "0 0 595.28 841.89";
                    offsets[po] = fs.Position;
                    w(po + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [" + mb + "] /Resources << /XObject << /Im" + i + " " + io + " 0 R >> >> /Contents " + co + " 0 R >>\nendobj\n");
                    string content = "q 595.28 0 0 841.89 0 0 cm /Im" + i + " Do Q";
                    offsets[co] = fs.Position;
                    w(co + " 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
                    offsets[io] = fs.Position;
                    w(io + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + dims[i][0] + " /Height " + dims[i][1] +
                      " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpgs[i].Length + " >>\nstream\n");
                    fs.Write(jpgs[i], 0, jpgs[i].Length);
                    w("\nendstream\nendobj\n");
                }
                int info = total;
                offsets[info] = fs.Position;
                w(info + " 0 obj\n<< /Title (Raport tehnic - starea calculatorului) /Producer (Sanatate PC - Info PC Electric) /CreationDate (D:" + DateTime.Now.ToString("yyyyMMddHHmmss") + ") >>\nendobj\n");
                long xref = fs.Position;
                w("xref\n0 " + (total + 1) + "\n0000000000 65535 f \n");
                for (int i = 1; i <= total; i++) w(offsets[i].ToString("0000000000") + " 00000 n \n");
                w("trailer\n<< /Size " + (total + 1) + " /Root 1 0 R /Info " + info + " 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }
    }
}
