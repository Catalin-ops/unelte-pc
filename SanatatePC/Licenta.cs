// Sanatate PC - Info PC Electric
// Licenta offline: cheie semnata ECDSA P-256 cu numele clientului si data expirarii.
// Functioneaza pe orice PC, fara internet; fisierul licenta.spc poate sta langa .exe (stick) sau in AppData.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InfoPcDisk
{
    public class License
    {
        public string Name = "", Email = "";
        public DateTime Issued, Expires;
        public bool Lifetime;
        public int Tier;
        public bool Valid;          // semnatura corecta
        public bool Expired;
        public string Error = "";
        public string Raw = "";
        public string SourcePath = "";

        public bool IsPro { get { return Valid && !Expired && Tier >= 1; } }

        public string ExpiresText
        {
            get { return Lifetime ? "fără expirare" : Expires.ToString("dd.MM.yyyy"); }
        }

        public int DaysLeft
        {
            get { return Lifetime ? int.MaxValue : (int)Math.Floor((Expires - DateTime.Today).TotalDays); }
        }
    }

    public static class Licensing
    {
        // Cheia PUBLICA (verificare). Cheia privata ramane doar la emitent, in afara codului sursa.
        const string PublicKeyB64 = "RUNTMSAAAACILgm57+3G2OkOy2+T5+r80fZJRNT3YxEMsPQrvswynIgE8VE8p0XfFDvGdnvTnDx7v5rYon4nmsCb1K3XP82b";
        public const string FileName = "licenta.spc";
        public const string Prefix = "SPC1.";
        static readonly DateTime Epoch = new DateTime(2024, 1, 1);

        public static string ExeDir
        {
            get { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
        }

        public static string AppDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InfoPC", "SanatatePC"); }
        }

        // aplicatia e instalata (Program Files / Microsoft Store), nu portabila?
        public static bool IsInstalled
        {
            get
            {
                string d = ExeDir ?? "";
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                return d.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (pf.Length > 0 && d.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) ||
                    (pf86.Length > 0 && d.StartsWith(pf86, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static bool CanWriteExeDir
        {
            get
            {
                if (IsInstalled) return false;
                try
                {
                    string t = Path.Combine(ExeDir, ".licenta_test");
                    File.WriteAllText(t, "x");
                    File.Delete(t);
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        public static bool ExeOnRemovable
        {
            get
            {
                try { return new DriveInfo(Path.GetPathRoot(ExeDir)).DriveType == DriveType.Removable; }
                catch (Exception) { return false; }
            }
        }

        static byte[] B64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        public static License Parse(string text)
        {
            License l = new License();
            try
            {
                text = (text ?? "").Trim();
                // accepta si cheia lipita cu spatii/randuri noi
                StringBuilder sb = new StringBuilder();
                foreach (char c in text) if (!char.IsWhiteSpace(c)) sb.Append(c);
                text = sb.ToString();
                l.Raw = text;
                if (!text.StartsWith(Prefix)) { l.Error = "Cheia nu are formatul corect."; return l; }
                byte[] all = B64Url(text.Substring(Prefix.Length));
                if (all.Length < 64 + 8) { l.Error = "Cheia e incompletă."; return l; }
                int plen = all.Length - 64;
                byte[] payload = new byte[plen], sig = new byte[64];
                Buffer.BlockCopy(all, 0, payload, 0, plen);
                Buffer.BlockCopy(all, plen, sig, 0, 64);

                using (CngKey key = CngKey.Import(Convert.FromBase64String(PublicKeyB64), CngKeyBlobFormat.EccPublicBlob))
                using (ECDsaCng dsa = new ECDsaCng(key))
                {
                    dsa.HashAlgorithm = CngAlgorithm.Sha256;
                    if (!dsa.VerifyData(payload, sig)) { l.Error = "Cheia nu este validă (semnătură greșită)."; return l; }
                }

                if (payload[0] != 1) { l.Error = "Versiune de cheie necunoscută."; return l; }
                l.Tier = payload[1];
                int issue = BitConverter.ToUInt16(payload, 2), exp = BitConverter.ToUInt16(payload, 4);
                l.Issued = Epoch.AddDays(issue);
                l.Lifetime = exp == 0xFFFF;
                l.Expires = l.Lifetime ? DateTime.MaxValue : Epoch.AddDays(exp);
                int p = 6;
                int nl = payload[p++];
                l.Name = Encoding.UTF8.GetString(payload, p, nl); p += nl;
                int el = payload[p++];
                l.Email = Encoding.UTF8.GetString(payload, p, el);
                l.Valid = true;
                l.Expired = !l.Lifetime && DateTime.Today > l.Expires;
                if (l.Expired) l.Error = "Licența a expirat pe " + l.Expires.ToString("dd.MM.yyyy") + ".";
            }
            catch (Exception) { l.Valid = false; l.Error = "Cheia nu este validă."; }
            return l;
        }

        // cauta licenta: intai langa .exe (stick), apoi in AppData
        public static License Load()
        {
            try
            {
                string vis = Path.Combine(ExeDir, FileName);
                if (File.Exists(vis) && (File.GetAttributes(vis) & FileAttributes.Hidden) == 0 && !IsInstalled)
                    File.SetAttributes(vis, FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
            }
            catch (Exception) { }
            string[] places = new string[] { Path.Combine(ExeDir, FileName), Path.Combine(AppDataDir, FileName) };
            License best = null;
            foreach (string f in places)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    License l = Parse(File.ReadAllText(f));
                    l.SourcePath = f;
                    if (l.IsPro) return l;
                    if (best == null || (l.Valid && !best.Valid)) best = l;
                }
                catch (Exception) { }
            }
            // nicio licenta Pro pe acest PC: o caut pe stick-urile conectate si o preiau (aplicatie instalata, stick cu licenta)
            License found = FindOnRemovable();
            if (found != null)
            {
                try
                {
                    Directory.CreateDirectory(AppDataDir);
                    string f = Path.Combine(AppDataDir, FileName);
                    WriteHidden(f, found.Raw);
                    ImportedFrom = found.SourcePath;
                    found.SourcePath = f;
                }
                catch (Exception) { }
                return found;
            }
            return best;
        }

        // calea de unde a fost preluata licenta la pornire (pentru mesajul catre utilizator)
        public static string ImportedFrom;

        // cauta licenta.spc valida (Pro) pe stick-uri: in radacina si in folderele de pe primele doua niveluri
        public static License FindOnRemovable()
        {
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Removable || !d.IsReady) continue;
                        List<string> files = new List<string>();
                        CollectFiles(d.RootDirectory.FullName, 0, files);
                        foreach (string f in files)
                        {
                            License l = Parse(File.ReadAllText(f));
                            l.SourcePath = f;
                            if (l.IsPro) return l;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return null;
        }

        static void CollectFiles(string dir, int depth, List<string> files)
        {
            try
            {
                string f = Path.Combine(dir, FileName);
                if (File.Exists(f)) files.Add(f);
                if (depth >= 2 || files.Count > 20) return;
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(sub);
                    if (n.StartsWith("$") || n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    CollectFiles(sub, depth + 1, files);
                }
            }
            catch (Exception) { }
        }

        // salveaza licenta; intoarce calea sau arunca exceptie
        // fisierul de licenta e ascuns (Ascuns + Sistem), ca sa nu fie vazut sau sters din greseala
        static void WriteHidden(string f, string text)
        {
            if (File.Exists(f)) File.SetAttributes(f, FileAttributes.Normal);
            File.WriteAllText(f, text + Environment.NewLine, new UTF8Encoding(false));
            try { File.SetAttributes(f, FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly); } catch (Exception) { }
        }

        // salveaza licenta pe acest PC (AppData) si, daca se cere si se poate, langa aplicatie (stick)
        public static string Save(License l, bool nextToExe)
        {
            Directory.CreateDirectory(AppDataDir);
            string f = Path.Combine(AppDataDir, FileName);
            WriteHidden(f, l.Raw);
            l.SourcePath = f;
            if (nextToExe)
            {
                string g = Path.Combine(ExeDir, FileName);
                WriteHidden(g, l.Raw);
                l.SourcePath = g;
            }
            return l.SourcePath;
        }

        // la pornire: daca licenta valida e doar pe PC, o copiem si langa aplicatia portabila (stick)
        public static bool CopyToExeDirIfMissing(License l)
        {
            try
            {
                if (l == null || !l.IsPro) return false;
                string g = Path.Combine(ExeDir, FileName);
                if (File.Exists(g) || !CanWriteExeDir) return false;
                WriteHidden(g, l.Raw);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static void Remove()
        {
            foreach (string f in new string[] { Path.Combine(ExeDir, FileName), Path.Combine(AppDataDir, FileName) })
                try { if (File.Exists(f)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } } catch (Exception) { }
        }

        // ------------------------------------------------------------ generare (folosit de GeneratorLicente)
        public static string Create(string privateKeyB64, string name, string email, DateTime issued, DateTime? expires, int tier)
        {
            byte[] n = Encoding.UTF8.GetBytes(name ?? ""), e = Encoding.UTF8.GetBytes(email ?? "");
            if (n.Length > 120 || e.Length > 120) throw new ArgumentException("Numele sau emailul e prea lung.");
            using (MemoryStream ms = new MemoryStream())
            {
                ms.WriteByte(1);
                ms.WriteByte((byte)tier);
                ms.Write(BitConverter.GetBytes((ushort)(issued.Date - Epoch).TotalDays), 0, 2);
                ushort ex = expires.HasValue ? (ushort)(expires.Value.Date - Epoch).TotalDays : (ushort)0xFFFF;
                ms.Write(BitConverter.GetBytes(ex), 0, 2);
                ms.WriteByte((byte)n.Length); ms.Write(n, 0, n.Length);
                ms.WriteByte((byte)e.Length); ms.Write(e, 0, e.Length);
                byte[] payload = ms.ToArray();
                byte[] sig;
                using (CngKey key = CngKey.Import(Convert.FromBase64String(privateKeyB64.Trim()), CngKeyBlobFormat.EccPrivateBlob))
                using (ECDsaCng dsa = new ECDsaCng(key))
                {
                    dsa.HashAlgorithm = CngAlgorithm.Sha256;
                    sig = dsa.SignData(payload);
                }
                byte[] all = new byte[payload.Length + sig.Length];
                Buffer.BlockCopy(payload, 0, all, 0, payload.Length);
                Buffer.BlockCopy(sig, 0, all, payload.Length, sig.Length);
                return Prefix + Convert.ToBase64String(all).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }
    }
}
