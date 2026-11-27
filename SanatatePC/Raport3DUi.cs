// Sanatate PC - DigitalSecureIt
// Butonul "Raport 3D" (Pro): creeaza pagina 3D pentru client si o trimite pe WhatsApp, e-mail sau din folder.
using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InfoPcDisk
{
    public partial class MainApp
    {
        void SaveReport3D()
        {
            if (sections.Count == 0) return;
            if (!Pro)
            {
                if (MessageBox.Show(win, "Raportul 3D pentru client e disponibil în versiunea Pro.\n\nVrei să activezi Pro acum?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    ShowActivation();
                return;
            }
            Window d = LoadWindow("RaportDialog.xaml");
            d.Owner = win;
            d.Title = "Raport 3D";
            ((TextBlock)d.FindName("Heading")).Text = "Raport 3D pentru client";
            ((TextBlock)d.FindName("Intro")).Text = "O pagină interactivă cu modelul 3D al calculatorului, starea fiecărei componente, ce trebuie făcut și istoricul verificărilor. Clientul o deschide cu dublu-clic, în orice browser, și fără internet.";
            ((Button)d.FindName("OkBtn")).Content = "Creează raport 3D";
            TextBox client = (TextBox)d.FindName("ClientBox"), notes = (TextBox)d.FindName("NotesBox");
            if (settings.Company.Length == 0) ((FrameworkElement)d.FindName("NoCompany")).Visibility = Visibility.Visible;
            ((Button)d.FindName("CancelBtn")).Click += delegate { d.DialogResult = false; };
            ((Button)d.FindName("OkBtn")).Click += delegate { d.DialogResult = true; };
            if (d.ShowDialog() != true) return;

            string who = client.Text.Trim().Length > 0 ? client.Text.Trim() : Environment.MachineName;
            foreach (char ch in Path.GetInvalidFileNameChars()) who = who.Replace(ch, '_');
            Microsoft.Win32.SaveFileDialog sf = new Microsoft.Win32.SaveFileDialog();
            sf.Filter = "Pagină web (*.html)|*.html";
            sf.FileName = "Raport3D_" + who + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".html";
            sf.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (sf.ShowDialog(win) != true) return;

            win.Cursor = Cursors.Wait;
            try
            {
                string html = Raport3D.Build(sections, disks, currentRecs, settings, client.Text.Trim(), notes.Text.Trim(), true);
                File.WriteAllText(sf.FileName, html, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                win.Cursor = null;
                MessageBox.Show(win, "Nu am putut crea raportul 3D: " + ex.Message, "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            win.Cursor = null;
            ShowSend3D(sf.FileName, client.Text.Trim());
        }

        void ShowSend3D(string file, string client)
        {
            Window d = LoadWindow("Trimite3D.xaml");
            d.Owner = win;
            ((TextBlock)d.FindName("FileText")).Text = Path.GetFileName(file) + " · " + Math.Max(1, new FileInfo(file).Length / 1024) + " KB · " + Path.GetDirectoryName(file);
            if (!Raport3D.HasModel) ((FrameworkElement)d.FindName("ModelWarn")).Visibility = Visibility.Visible;
            Border box = (Border)d.FindName("InfoBox");
            TextBlock info = (TextBlock)d.FindName("InfoText");
            Action<string> say = delegate(string t) { info.Text = t; box.Visibility = Visibility.Visible; };

            ((Button)d.FindName("WhatsBtn")).Click += delegate
            {
                try
                {
                    StringCollection files = new StringCollection();
                    files.Add(file);
                    Clipboard.SetFileDropList(files);
                }
                catch (Exception ex) { say("Nu am putut copia fișierul: " + ex.Message); return; }
                bool opened = false;
                try { System.Diagnostics.Process.Start("whatsapp:"); opened = true; } catch (Exception) { }
                say("Fișierul e copiat. " + (opened ? "În WhatsApp" : "Deschide WhatsApp (aplicația sau web.whatsapp.com),") + " intră în conversația clientului, apasă Ctrl+V și apoi Trimite.");
            };
            ((Button)d.FindName("MailBtn")).Click += delegate
            {
                string subject = "Raport 3D — starea calculatorului" + (client.Length > 0 ? " (" + client + ")" : "");
                string body = "Bună ziua,\r\n\r\nVă trimit raportul cu starea calculatorului verificat. Deschideți fișierul atașat cu dublu-clic: se deschide în browser, cu modelul 3D și toate detaliile.\r\n\r\n" +
                    "Cu stimă,\r\n" + (settings.Technician.Length > 0 ? settings.Technician + "\r\n" : "") + (settings.Company.Length > 0 ? settings.Company + "\r\n" : "") + (settings.Phone.Length > 0 ? settings.Phone + "\r\n" : "");
                Button mb = (Button)d.FindName("MailBtn");
                mb.IsEnabled = false;
                say("Se deschide programul de e-mail…");
                Mapi.Send(subject, body, file, delegate(int rc)
                {
                    d.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        mb.IsEnabled = true;
                        if (rc == 0) { say("Mesajul e pregătit în programul de e-mail, cu raportul atașat."); return; }
                        if (rc == 1) { box.Visibility = Visibility.Collapsed; return; }
                        try { System.Diagnostics.Process.Start("mailto:?subject=" + Uri.EscapeDataString(subject) + "&body=" + Uri.EscapeDataString(body)); } catch (Exception) { }
                        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception) { }
                        say("Nu am găsit un program de e-mail care să atașeze singur fișierul. Am deschis un mesaj nou și folderul cu raportul: trage fișierul în mesaj.");
                    }));
                });
            };
            ((Button)d.FindName("OpenBtn")).Click += delegate
            {
                try { System.Diagnostics.Process.Start(file); } catch (Exception ex) { say("Nu am putut deschide fișierul: " + ex.Message); }
            };
            ((Button)d.FindName("FolderBtn")).Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception ex) { say("Nu am putut deschide folderul: " + ex.Message); }
            };
            ((Button)d.FindName("CloseBtn")).Click += delegate { d.Close(); };
            d.ShowDialog();
        }
    }

    // e-mail cu atasament prin MAPI (Outlook, Thunderbird si alte programe de e-mail instalate)
    static class Mapi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class MapiMessageW
        {
            public int Reserved;
            public string Subject;
            public string NoteText;
            public string MessageType;
            public string DateReceived;
            public string ConversationID;
            public int Flags;
            public IntPtr Originator;
            public int RecipCount;
            public IntPtr Recips;
            public int FileCount;
            public IntPtr Files;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class MapiFileDescW
        {
            public int Reserved;
            public int Flags;
            public int Position;
            public string PathName;
            public string FileName;
            public IntPtr FileType;
        }

        [DllImport("MAPI32.DLL", CharSet = CharSet.Unicode)]
        static extern int MAPISendMailW(IntPtr session, IntPtr hwnd, MapiMessageW message, int flags, int reserved);

        const int MAPI_LOGON_UI = 0x1, MAPI_DIALOG = 0x8;

        // rezultat: 0 = mesaj pregatit, 1 = anulat de utilizator, altceva = nu exista program de e-mail MAPI
        public static void Send(string subject, string body, string file, Action<int> done)
        {
            Thread t = new Thread(delegate()
            {
                int rc = -1;
                IntPtr fp = IntPtr.Zero;
                try
                {
                    MapiFileDescW fd = new MapiFileDescW();
                    fd.Position = -1; fd.PathName = file; fd.FileName = Path.GetFileName(file);
                    fp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MapiFileDescW)));
                    Marshal.StructureToPtr(fd, fp, false);
                    MapiMessageW m = new MapiMessageW();
                    m.Subject = subject; m.NoteText = body; m.FileCount = 1; m.Files = fp;
                    rc = MAPISendMailW(IntPtr.Zero, IntPtr.Zero, m, MAPI_LOGON_UI | MAPI_DIALOG, 0);
                }
                catch (Exception) { rc = -1; }
                finally
                {
                    if (fp != IntPtr.Zero) { Marshal.DestroyStructure(fp, typeof(MapiFileDescW)); Marshal.FreeHGlobal(fp); }
                }
                done(rc);
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }
}
