// Sanatate PC - DigitalSecureIt
// Fereastra "Monitorizare" (Pro): activeaza agentul zilnic pe acest calculator.
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InfoPcDisk
{
    public partial class MainApp
    {
        void ShowMonitoring()
        {
            if (!Pro)
            {
                if (MessageBox.Show(win, "Monitorizarea zilnică a calculatoarelor dintr-o instituție e în versiunea Pro.\n\nVrei să activezi Pro acum?", "Sănătate PC", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    ShowActivation();
                return;
            }
            if (Collect.IsPackaged)
            {
                MessageBox.Show(win, "Monitorizarea rulează în fundal ca serviciu Windows, iar asta nu e permis aplicațiilor din Microsoft Store.\n\nFolosește versiunea portabilă sau cea instalată cu installer-ul.", "Sănătate PC", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Window d = LoadWindow("Monitorizare.xaml");
            d.Owner = win;
            TextBox server = (TextBox)d.FindName("ServerBox"), key = (TextBox)d.FindName("KeyBox"), label = (TextBox)d.FindName("LabelBox");
            TextBlock msg = (TextBlock)d.FindName("Msg");
            Button enable = (Button)d.FindName("EnableBtn"), send = (Button)d.FindName("SendBtn"), disable = (Button)d.FindName("DisableBtn");
            MonitorConfig cfg = Monitorizare.Load();
            server.Text = cfg.Server.Length > 0 ? cfg.Server : settings.MonitorServer;
            key.Text = cfg.Key; label.Text = cfg.Label;

            Action refresh = delegate
            {
                bool on = Monitorizare.Load().Enabled && Monitorizare.TaskExists();
                ((Border)d.FindName("StateBox")).Background = B(on ? "#0B2A1F" : "#1E293B");
                ((TextBlock)d.FindName("StateTitle")).Text = on ? "Monitorizarea e activă pe acest calculator" : "Monitorizarea nu e activă";
                ((TextBlock)d.FindName("StateTitle")).Foreground = B(on ? "#4ADE80" : "#E6EAF2");
                string last = Monitorizare.LastLog();
                ((TextBlock)d.FindName("StateSub")).Text = on ? "Trimite starea la fiecare 2 ore și la pornire." + (last.Length > 0 ? "\nUltima acțiune: " + last : "") : "Completează datele de mai jos și apasă „Activează monitorizarea”.";
                disable.IsEnabled = on;
                send.IsEnabled = on;
            };
            Action<string, bool> say = delegate(string t, bool ok)
            {
                msg.Text = t; msg.Foreground = B(ok ? "#4ADE80" : "#F87171"); msg.Visibility = Visibility.Visible;
            };
            Action<bool> busy = delegate(bool b) { enable.IsEnabled = !b; send.IsEnabled = !b; disable.IsEnabled = !b; d.Cursor = b ? System.Windows.Input.Cursors.Wait : null; };
            Action sendNow = delegate
            {
                busy(true);
                say("Citesc componentele și trimit raportul… (durează până la un minut)", true);
                Thread t = new Thread(delegate()
                {
                    string err = Monitorizare.Send(Monitorizare.Load());
                    Monitorizare.Log(err == null ? "raport trimis (manual)" : "eroare: " + err);
                    d.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        busy(false);
                        refresh();
                        say(err == null ? "Raportul a ajuns la server. Calculatorul apare acum în pagina instituției." : "Nu am putut trimite: " + err, err == null);
                    }));
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            };

            enable.Click += delegate
            {
                string err = Monitorizare.Install(server.Text, key.Text, label.Text);
                if (err != null) { say(err, false); return; }
                settings.MonitorServer = Monitorizare.NormalizeServer(server.Text);
                try { settings.Save(); } catch (Exception) { }
                refresh();
                sendNow();
            };
            send.Click += delegate { sendNow(); };
            disable.Click += delegate
            {
                string err = Monitorizare.Uninstall();
                if (err != null) say(err, false); else say("Monitorizarea a fost oprită pe acest calculator.", true);
                refresh();
            };
            ((Button)d.FindName("CancelBtn")).Click += delegate { d.Close(); };
            refresh();
            d.ShowDialog();
        }
    }
}
