// Generator Licente - Info PC Electric. Emite chei Pro pentru Sanatate PC (cheia privata e citita din fisier, nu e in cod).
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using InfoPcDisk;

[assembly: AssemblyTitle("Generator Licențe")]
[assembly: AssemblyCompany("Info PC Electric S.R.L.")]
[assembly: AssemblyProduct("Generator Licențe Sănătate PC")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace InfoPcKeygen
{
    public class GenApp
    {
        Window win;
        string priv;
        string lastKey, lastName;

        [STAThread]
        public static void Main()
        {
            Application app = new Application();
            app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "Generator Licențe", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            GenApp g = new GenApp();
            g.Build();
            app.Run(g.win);
        }

        T F<T>(string n) where T : class { return win.FindName(n) as T; }

        void Build()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                win = (Window)XamlReader.Load(s);
            string auto = Path.Combine(Licensing.ExeDir, "cheie_privata_SanatatePC.txt");
            if (File.Exists(auto)) SetKey(File.ReadAllText(auto), auto);
            F<Button>("LoadKeyBtn").Click += delegate
            {
                Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog();
                d.Filter = "Cheie privată (*.txt)|*.txt|Toate fișierele|*.*";
                if (d.ShowDialog(win) == true) SetKey(File.ReadAllText(d.FileName), d.FileName);
            };
            F<Button>("GenBtn").Click += delegate { Generate(); };
            F<Button>("CopyBtn").Click += delegate { if (lastKey != null) { Clipboard.SetText(lastKey); MessageBox.Show("Cheia a fost copiată.", "Generator Licențe"); } };
            F<Button>("SaveBtn").Click += delegate
            {
                if (lastKey == null) return;
                Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog();
                d.FileName = Licensing.FileName;
                d.Filter = "Licență (*.spc)|*.spc";
                if (d.ShowDialog(win) == true) File.WriteAllText(d.FileName, lastKey + Environment.NewLine, new UTF8Encoding(false));
            };
        }

        void SetKey(string text, string path)
        {
            try
            {
                // test: semneaza si verifica o cheie de proba
                string test = Licensing.Create(text, "test", "", DateTime.Today, DateTime.Today.AddDays(1), 1);
                if (!Licensing.Parse(test).Valid) throw new Exception("Cheia privată nu corespunde aplicației Sănătate PC.");
                priv = text.Trim();
                F<TextBlock>("KeyStatus").Text = "Cheia privată e încărcată ✓  (" + Path.GetFileName(path) + ")";
                F<TextBlock>("KeyStatus").Foreground = (Brush)new BrushConverter().ConvertFromString("#4ADE80");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cheia privată nu e validă: " + ex.Message, "Generator Licențe", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void Generate()
        {
            if (priv == null) { MessageBox.Show("Încarcă întâi cheia privată.", "Generator Licențe"); return; }
            string name = F<TextBox>("NameBox").Text.Trim(), email = F<TextBox>("EmailBox").Text.Trim();
            if (name.Length < 2) { MessageBox.Show("Scrie numele clientului.", "Generator Licențe"); return; }
            DateTime? exp = null;
            if (F<RadioButton>("Y1").IsChecked == true) exp = DateTime.Today.AddYears(1);
            else if (F<RadioButton>("M1").IsChecked == true) exp = DateTime.Today.AddMonths(1);
            string key = Licensing.Create(priv, name, email, DateTime.Today, exp, 1);
            License l = Licensing.Parse(key);
            if (!l.IsPro) { MessageBox.Show("Verificarea cheii a eșuat: " + l.Error, "Generator Licențe"); return; }
            lastKey = key; lastName = name;
            F<TextBox>("OutBox").Text = key;
            F<TextBlock>("OutInfo").Text = "Licență Pro pentru " + name + " — valabilă până la " + l.ExpiresText;
            F<Border>("OutCard").Visibility = Visibility.Visible;
            try
            {
                string log = Path.Combine(Licensing.ExeDir, "licente_emise.csv");
                bool head = !File.Exists(log);
                using (StreamWriter w = new StreamWriter(log, true, new UTF8Encoding(true)))
                {
                    if (head) w.WriteLine("Data;Client;Email;Expira;Cheie");
                    w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ";" + name.Replace(";", ",") + ";" + email.Replace(";", ",") + ";" + l.ExpiresText + ";" + key);
                }
            }
            catch (Exception) { }
        }
    }
}
