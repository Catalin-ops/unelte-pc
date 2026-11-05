// Sanatate PC - Info PC Electric
// Fereastra Proiecte: institutii cu mai multe calculatoare, salvate pe stick, si raportul comun.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InfoPcDisk
{
    public partial class MainApp
    {
        void ShowProjects()
        {
            if (!Pro)
            {
                if (MessageBox.Show(win, "Proiectele (mai multe calculatoare dintr-o instituție și raportul comun) sunt în versiunea Pro.\n\nVrei să activezi Pro acum?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    ShowActivation();
                return;
            }
            if (loading || sections.Count == 0) { MessageBox.Show(win, "Așteaptă să se termine citirea componentelor.", "Sănătate PC"); return; }

            Window d = LoadWindow("Proiecte.xaml");
            d.Owner = win;
            ListBox projList = (ListBox)d.FindName("ProjList"), pcList = (ListBox)d.FindName("PcList");
            TextBox newName = (TextBox)d.FindName("NewName"), label = (TextBox)d.FindName("PcLabel"), room = (TextBox)d.FindName("PcRoom"),
                user = (TextBox)d.FindName("PcUser"), notes = (TextBox)d.FindName("PcNotes");
            TextBlock title = (TextBlock)d.FindName("ProjTitle"), sub = (TextBlock)d.FindName("ProjSub"), info = (TextBlock)d.FindName("AddInfo");
            Border addBox = (Border)d.FindName("AddBox");
            Button delBtn = (Button)d.FindName("DelBtn"), reportBtn = (Button)d.FindName("ReportBtn");
            string baseDir = Projects.BaseDir(lic);
            ((TextBlock)d.FindName("WhereText")).Text = "Se salvează în: " + baseDir;
            label.Text = Environment.MachineName;

            Action<string> reload = null;
            reload = delegate(string selectName)
            {
                List<Project> all = Projects.List(baseDir);
                projList.ItemsSource = all;
                foreach (Project p in all) if (p.Name == selectName) { projList.SelectedItem = p; break; }
                if (projList.SelectedItem == null && all.Count == 1) projList.SelectedIndex = 0;
            };
            SelectionChangedEventHandler onProj = delegate
            {
                Project p = projList.SelectedItem as Project;
                addBox.IsEnabled = p != null;
                reportBtn.IsEnabled = p != null && p.Pcs.Count > 0;
                delBtn.IsEnabled = false;
                info.Text = "";
                if (p == null) { title.Text = "Alege sau creează un proiect"; sub.Text = ""; pcList.ItemsSource = null; return; }
                title.Text = p.Name;
                sub.Text = p.Sub;
                pcList.ItemsSource = p.Pcs;
                bool already = false;
                foreach (PcRecord r in p.Pcs) if (r.MachineName == Environment.MachineName) { already = true; label.Text = r.Label; room.Text = r.Room ?? ""; user.Text = r.User ?? ""; notes.Text = r.Notes ?? ""; }
                if (already) info.Text = "Acest PC e deja în proiect; salvarea îl actualizează.";
            };
            projList.SelectionChanged += onProj;
            pcList.SelectionChanged += delegate { delBtn.IsEnabled = pcList.SelectedItem != null; };

            ((Button)d.FindName("NewBtn")).Click += delegate
            {
                string n = newName.Text.Trim();
                if (n.Length < 2) { MessageBox.Show(d, "Scrie numele instituției.", "Sănătate PC"); return; }
                try
                {
                    Projects.Create(baseDir, n);
                    newName.Text = "";
                    reload(n);
                }
                catch (Exception ex) { MessageBox.Show(d, "Nu pot crea proiectul: " + ex.Message, "Sănătate PC"); }
            };
            newName.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) ((Button)d.FindName("NewBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };

            ((Button)d.FindName("AddBtn")).Click += delegate
            {
                Project p = projList.SelectedItem as Project;
                if (p == null) return;
                string l = label.Text.Trim();
                if (l.Length == 0) { MessageBox.Show(d, "Scrie un nume pentru calculator (ex. Secretariat PC1).", "Sănătate PC"); return; }
                string f = Projects.PcFile(p, l);
                if (File.Exists(f))
                {
                    PcRecord old = null;
                    foreach (PcRecord r in p.Pcs) if (string.Equals(r.File, f, StringComparison.OrdinalIgnoreCase)) old = r;
                    if (old != null && old.MachineName != Environment.MachineName &&
                        MessageBox.Show(d, "În proiect există deja „" + l + "” (alt calculator: " + old.MachineName + "). Îl înlocuiesc?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return;
                }
                try
                {
                    PcRecord rec = PcRecord.FromCurrent(sections, disks, currentRecs, l, room.Text.Trim(), user.Text.Trim(), notes.Text.Trim());
                    Projects.Save(p, rec);
                    reload(p.Name);
                    info.Text = "Salvat: " + l + " (" + rec.Recs.Count + (rec.Recs.Count == 1 ? " lucrare" : " lucrări") + ")";
                }
                catch (Exception ex) { MessageBox.Show(d, "Nu am putut salva: " + ex.Message, "Sănătate PC"); }
            };

            delBtn.Click += delegate
            {
                PcRecord r = pcList.SelectedItem as PcRecord;
                Project p = projList.SelectedItem as Project;
                if (r == null || p == null) return;
                if (MessageBox.Show(d, "Șterg „" + r.Label + "” din proiect?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try { File.Delete(r.File); } catch (Exception ex) { MessageBox.Show(d, ex.Message, "Sănătate PC"); }
                reload(p.Name);
            };

            reportBtn.Click += delegate
            {
                Project p = projList.SelectedItem as Project;
                if (p == null || p.Pcs.Count == 0) return;
                Microsoft.Win32.SaveFileDialog sf = new Microsoft.Win32.SaveFileDialog();
                sf.Filter = "Document PDF (*.pdf)|*.pdf";
                sf.FileName = "Raport_" + Projects.SafeName(p.Name) + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".pdf";
                sf.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (sf.ShowDialog(d) != true) return;
                d.Cursor = Cursors.Wait;
                try
                {
                    System.Windows.Documents.FlowDocument doc = Report.BuildProject(p, lic, settings);
                    Report.SavePdf(doc, sf.FileName, (settings.Company.Length > 0 ? settings.Company + " · " : "") + p.Name);
                }
                catch (Exception ex)
                {
                    d.Cursor = null;
                    MessageBox.Show(d, "Nu am putut crea raportul: " + ex.Message, "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                d.Cursor = null;
                try { System.Diagnostics.Process.Start(sf.FileName); } catch (Exception) { }
            };

            ((Button)d.FindName("FolderBtn")).Click += delegate
            {
                try { Directory.CreateDirectory(baseDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + baseDir + "\""); } catch (Exception) { }
            };
            ((Button)d.FindName("CloseBtn")).Click += delegate { d.Close(); };

            reload(null);
            d.ShowDialog();
        }
    }
}
