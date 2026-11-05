// Sanatate PC - Info PC Electric
// Colecteaza informatii despre componente prin WMI, registru si jurnalele Windows. Doar citire.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace InfoPcDisk
{
    public class Sensors
    {
        public int CpuTemp = -1, CpuTempMax = -1, GpuTemp = -1, BoardTemp = -1;
        public string Source = "";
    }

    public class Stability
    {
        public int Bsod, PowerLoss, WheaFatal, WheaCorrected, DiskBadBlock, DiskRetry, NtfsCorrupt;
        public List<string> BsodLines = new List<string>();
        public List<string> BsodHints = new List<string>();
        public int MemTestResult = -1; // -1 nu a rulat, 0 fara erori, 1 cu erori
        public DateTime MemTestDate;
        public bool LogsReadable = true;
    }

    public static class Collect
    {
        static readonly CultureInfo RO = new CultureInfo("ro-RO");

        // ------------------------------------------------------------ utilitare WMI
        public static List<ManagementObject> Wmi(string ns, string query)
        {
            List<ManagementObject> l = new List<ManagementObject>();
            try
            {
                ManagementScope scope = new ManagementScope(ns);
                scope.Options.Timeout = TimeSpan.FromSeconds(15);
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery(query)))
                {
                    s.Options.Timeout = TimeSpan.FromSeconds(15);
                    foreach (ManagementBaseObject o in s.Get()) l.Add((ManagementObject)o);
                }
            }
            catch (Exception) { }
            return l;
        }

        public static List<ManagementObject> Cim(string query) { return Wmi(@"root\cimv2", query); }

        public static object P(ManagementBaseObject o, string name)
        {
            try { return o[name]; } catch (Exception) { return null; }
        }

        public static string S(ManagementBaseObject o, string name)
        {
            object v = P(o, name);
            if (v == null) return "";
            string s = v.ToString().Trim();
            return s;
        }

        public static long L(ManagementBaseObject o, string name)
        {
            object v = P(o, name);
            if (v == null) return -1;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return -1; }
        }

        public static DateTime D(ManagementBaseObject o, string name)
        {
            string s = S(o, name);
            if (s.Length < 14) return DateTime.MinValue;
            try { return ManagementDateTimeConverter.ToDateTime(s); } catch (Exception) { return DateTime.MinValue; }
        }

        public static string Size(long b, bool dec)
        {
            return Health.FormatBytes(b, dec);
        }

        public static string Date(DateTime d)
        {
            return d == DateTime.MinValue ? "—" : d.ToString("dd.MM.yyyy", RO);
        }

        static string Ago(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            double days = (DateTime.Now - d).TotalDays;
            if (days < 1) return "azi";
            if (days < 60) return "acum " + (int)days + " zile";
            if (days < 730) return "acum " + (int)(days / 30.4) + " luni";
            return "acum " + (days / 365.25).ToString("0.#", RO) + " ani";
        }

        static bool Junk(string s)
        {
            return string.IsNullOrEmpty(s) || Regex.IsMatch(s.Trim(), "^(None|Unknown|Undefined|Not Specified|To be filled.*|Default string|0+|N/A|Serial.*|SerNum.*)$", RegexOptions.IgnoreCase);
        }

        static string Clean(string s)
        {
            if (s == null) return "";
            s = Regex.Replace(s, @"\((R|TM|tm|r)\)|®|™", "");
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim();
        }

        static string RunHidden(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return null; }
                    return o;
                }
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ senzori de temperatura
        public static Sensors ReadSensors()
        {
            Sensors s = new Sensors();
            // LibreHardwareMonitor / OpenHardwareMonitor expun senzorii prin WMI cand ruleaza
            string[] nss = new string[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };
            foreach (string ns in nss)
            {
                List<ManagementObject> hw = Wmi(ns, "SELECT Identifier, HardwareType FROM Hardware");
                if (hw.Count == 0) continue;
                Dictionary<string, string> type = new Dictionary<string, string>();
                foreach (ManagementObject h in hw) type[S(h, "Identifier")] = S(h, "HardwareType");
                foreach (ManagementObject t in Wmi(ns, "SELECT Name, Value, Max, Parent FROM Sensor WHERE SensorType='Temperature'"))
                {
                    string ht;
                    if (!type.TryGetValue(S(t, "Parent"), out ht)) continue;
                    int v = (int)Math.Round(Convert.ToDouble(P(t, "Value") ?? 0.0, CultureInfo.InvariantCulture));
                    int mx = (int)Math.Round(Convert.ToDouble(P(t, "Max") ?? 0.0, CultureInfo.InvariantCulture));
                    string name = S(t, "Name");
                    if (v <= 0 || v > 125) continue;
                    if (ht.IndexOf("Cpu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bool main = Regex.IsMatch(name, "Package|Tctl|Tdie|Core Average|CPU", RegexOptions.IgnoreCase);
                        if (main || s.CpuTemp < 0) { if (v > s.CpuTemp || main) s.CpuTemp = Math.Max(main ? v : s.CpuTemp, v); }
                        if (mx > s.CpuTempMax && mx < 125) s.CpuTempMax = mx;
                    }
                    else if (ht.IndexOf("Gpu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (Regex.IsMatch(name, "Core|GPU", RegexOptions.IgnoreCase) && !Regex.IsMatch(name, "Hot|Memory|Junction", RegexOptions.IgnoreCase))
                            s.GpuTemp = Math.Max(s.GpuTemp, v);
                    }
                    else if (ht.IndexOf("Motherboard", StringComparison.OrdinalIgnoreCase) >= 0 || ht.IndexOf("SuperIO", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        s.BoardTemp = Math.Max(s.BoardTemp, v);
                    }
                }
                s.Source = ns.EndsWith("LibreHardwareMonitor") ? "LibreHardwareMonitor" : "OpenHardwareMonitor";
                break;
            }
            if (s.CpuTemp < 0)
            {
                foreach (ManagementObject z in Wmi(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    long k = L(z, "CurrentTemperature");
                    if (k <= 0) continue;
                    double c = k / 10.0 - 273.15;
                    if (c > 15 && c < 110 && Math.Abs(c - 27.85) > 0.2) s.BoardTemp = Math.Max(s.BoardTemp, (int)Math.Round(c));
                }
            }
            return s;
        }

        // ------------------------------------------------------------ sistem / Windows
        public static Section SystemInfo(Stability st)
        {
            Section sec = new Section("sistem", "Sistem și Windows", "\uE770");
            sec.Description = "Modelul calculatorului, versiunea de Windows, spațiul liber și actualizările.";
            List<ManagementObject> cs = Cim("SELECT Manufacturer, Model, SystemType, TotalPhysicalMemory, Name, UserName, PCSystemType FROM Win32_ComputerSystem");
            List<ManagementObject> os = Cim("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");

            string manu = cs.Count > 0 ? Clean(S(cs[0], "Manufacturer")) : "";
            string model = cs.Count > 0 ? Clean(S(cs[0], "Model")) : "";
            bool generic = Regex.IsMatch(manu + " " + model, "To be filled|System manufacturer|System Product|Default string|O.E.M", RegexOptions.IgnoreCase);
            Card pc = sec.AddCard(generic ? "Calculator asamblat" : (manu + " " + model).Trim(), cs.Count > 0 ? "Nume în rețea: " + S(cs[0], "Name") : "");
            pc.Badge = cs.Count > 0 && L(cs[0], "PCSystemType") == 2 ? "Laptop" : "Desktop";
            if (!generic) { pc.Add("Producător", manu); pc.Add("Model", model); }
            if (cs.Count > 0) { pc.Add("Utilizator", S(cs[0], "UserName")); pc.Add("Arhitectură", S(cs[0], "SystemType")); }
            pc.SetLevel(-1, "Info");

            Card win = sec.AddCard("Windows", "");
            string winCaption = "";
            if (os.Count > 0)
            {
                string disp = "", ubr = "";
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        if (k != null)
                        {
                            object o = k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId");
                            if (o != null) disp = o.ToString();
                            object u = k.GetValue("UBR");
                            if (u != null) ubr = "." + u.ToString();
                        }
                    }
                }
                catch (Exception) { }
                string cap = Clean(S(os[0], "Caption")).Replace("Microsoft ", "");
                winCaption = cap;
                win.Title = cap;
                win.Subtitle = (disp.Length > 0 ? "Versiunea " + disp + " · " : "") + "build " + S(os[0], "BuildNumber") + ubr + " · " + S(os[0], "OSArchitecture");
                DateTime inst = D(os[0], "InstallDate"), boot = D(os[0], "LastBootUpTime");
                win.Add("Instalat la", Date(inst) + (inst != DateTime.MinValue ? " (" + Ago(inst) + ")" : ""));
                if (boot != DateTime.MinValue)
                {
                    TimeSpan up = DateTime.Now - boot;
                    win.Add("Pornit de", (up.TotalDays >= 1 ? (int)up.TotalDays + " zile " : "") + up.Hours + " h " + up.Minutes + " min");
                    if (up.TotalDays > 14) win.Note("Calculatorul n-a mai fost repornit de " + (int)up.TotalDays + " zile. O repornire termină actualizările și eliberează memoria.");
                }
            }
            win.SetLevel(0, null);

            // ultima actualizare instalata
            DateTime last = DateTime.MinValue;
            foreach (ManagementObject q in Cim("SELECT InstalledOn FROM Win32_QuickFixEngineering"))
            {
                string v = S(q, "InstalledOn");
                DateTime d;
                if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ||
                    DateTime.TryParseExact(v, new string[] { "M/d/yyyy", "d/M/yyyy", "yyyyMMdd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    if (d > last && d <= DateTime.Now.AddDays(1)) last = d;
            }
            win.Add("Ultima actualizare", last == DateTime.MinValue ? "—" : Date(last) + " (" + Ago(last) + ")");
            if (last != DateTime.MinValue && (DateTime.Now - last).TotalDays > 90)
            {
                win.Raise(1, "Windows nu a mai instalat actualizări de peste 3 luni. Verifică Windows Update.");
                Recs.Add(2, "Windows", "Instalare actualizări Windows", "Ultima actualizare: " + Date(last) + ".", null);
            }

            // spatiu liber pe discul de sistem
            try
            {
                string sd = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                DriveInfo di = new DriveInfo(sd);
                long free = di.AvailableFreeSpace, tot = di.TotalSize;
                double pct = tot > 0 ? free * 100.0 / tot : 100;
                string col = pct < 5 || free < (5L << 30) ? "#F87171" : pct < 12 || free < (15L << 30) ? "#FBBF24" : null;
                win.Add("Spațiu liber pe " + sd, Size(free, false) + " din " + Size(tot, false) + " (" + pct.ToString("0", RO) + "%)", col);
                if (pct < 5 || free < (5L << 30))
                {
                    win.Raise(2, "Discul de sistem e aproape plin. Windows și programele încetinesc sau dau erori. Eliberează spațiu.");
                    Recs.Add(1, "Windows", "Eliberare spațiu pe " + sd, "Liber doar " + Size(free, false) + " (" + pct.ToString("0", RO) + "%). Curățare fișiere temporare, Descărcări, sau disc mai mare.", null);
                }
                else if (pct < 12 || free < (15L << 30))
                {
                    win.Raise(1, "Spațiul liber pe discul de sistem e mic. Ideal ar fi peste 15% liber.");
                    Recs.Add(2, "Windows", "Eliberare spațiu pe " + sd, "Liber " + Size(free, false) + " (" + pct.ToString("0", RO) + "%).", null);
                }
            }
            catch (Exception) { }

            // TPM + Secure Boot (cerinte Windows 11)
            string tpm = "Lipsă sau dezactivat";
            foreach (ManagementObject t in Wmi(@"root\CIMV2\Security\MicrosoftTpm", "SELECT SpecVersion, IsEnabled_InitialValue FROM Win32_Tpm"))
            {
                string spec = S(t, "SpecVersion");
                string ver = spec.Split(',')[0].Trim();
                tpm = (ver.Length > 0 ? "TPM " + ver : "TPM") + (S(t, "IsEnabled_InitialValue") == "False" ? " (dezactivat)" : " activ");
            }
            string sb = "Necunoscut";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    if (k == null) sb = "Indisponibil (BIOS clasic / Legacy)";
                    else
                    {
                        object v = k.GetValue("UEFISecureBootEnabled");
                        sb = v != null && Convert.ToInt32(v) == 1 ? "Activ (UEFI)" : "Inactiv (UEFI)";
                    }
                }
            }
            catch (Exception) { }
            Card sec2 = sec.AddCard("Securitate", "Cerințe pentru Windows 11 și protecția datelor");
            sec2.Add("Cip de securitate", tpm, tpm.StartsWith("TPM 2") && tpm.EndsWith("activ") ? "#4ADE80" : "#FBBF24");
            sec2.Add("Secure Boot", sb, sb.StartsWith("Activ") ? "#4ADE80" : "#FBBF24");
            sec2.SetLevel(-1, "Info");
            if (!tpm.StartsWith("TPM 2")) sec2.Note("Fără TPM 2.0 activ, Windows 11 nu se poate instala oficial. Se activează de obicei din BIOS (fTPM / PTT).");
            bool tpmOk = tpm.StartsWith("TPM 2") && tpm.EndsWith("activ");
            if (Regex.IsMatch(winCaption, @"Windows (XP|Vista|7|8)\b"))
            {
                win.Raise(2, "Sistemul de operare nu mai primește actualizări de securitate.");
                Recs.Add(1, "Windows", "Înlocuire sistem de operare (" + winCaption + ")", "Fără actualizări de securitate. Instalare Windows 11 sau, dacă hardware-ul nu permite, înlocuirea calculatorului.", "Licență Windows 11 Pro");
            }
            else if (winCaption.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                win.Raise(1, "Windows 10 nu mai primește actualizări gratuite din 14 octombrie 2025.");
                if (tpmOk) Recs.Add(2, "Windows", "Upgrade gratuit la Windows 11", "Windows 10 nu mai primește actualizări din 14.10.2025. TPM 2.0 e activ; verifică și compatibilitatea procesorului.", null);
                else Recs.Add(2, "Windows", "Activare TPM 2.0 din BIOS, apoi upgrade la Windows 11", "Windows 10 nu mai primește actualizări din 14.10.2025, iar TPM 2.0 nu e activ (" + tpm + "). Dacă placa nu are TPM 2.0, calculatorul trebuie înlocuit sau trecut pe ESU.", null);
            }

            sec.Summary = win.Title;
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ procesor
        public static Section Cpu(Sensors sn, Stability st)
        {
            Section sec = new Section("cpu", "Procesor", "\uE9F5");
            sec.Description = "Modelul procesorului, nucleele, frecvența, încărcarea și temperatura.";
            List<ManagementObject> l = Cim("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, LoadPercentage, Status, VirtualizationFirmwareEnabled FROM Win32_Processor");
            int i = 0;
            foreach (ManagementObject p in l)
            {
                string name = Clean(S(p, "Name")).Replace(" CPU", "");
                name = Regex.Replace(name, @"\s+\d+-Core Processor$", "");
                Card c = sec.AddCard(name, Clean(S(p, "Manufacturer")) + (l.Count > 1 ? " · procesorul " + (++i) : ""));
                c.Badge = S(p, "SocketDesignation");
                long cores = L(p, "NumberOfCores"), thr = L(p, "NumberOfLogicalProcessors");
                c.Add("Nuclee / fire", cores + " nuclee / " + thr + " fire");
                long mx = L(p, "MaxClockSpeed"), cur = L(p, "CurrentClockSpeed");
                c.Add("Frecvență de bază", mx > 0 ? (mx / 1000.0).ToString("0.00", RO) + " GHz" : "—");
                c.Add("Frecvență acum", cur > 0 ? (cur / 1000.0).ToString("0.00", RO) + " GHz" : "—");
                long l2 = L(p, "L2CacheSize"), l3 = L(p, "L3CacheSize");
                c.Add("Memorie cache", (l2 > 0 ? "L2 " + Size(l2 * 1024, false) : "") + (l3 > 0 ? " · L3 " + Size(l3 * 1024, false) : ""));
                long load = L(p, "LoadPercentage");
                c.Add("Încărcare acum", load >= 0 ? load + "%" : "—", load >= 90 ? "#FBBF24" : null);
                object virt = P(p, "VirtualizationFirmwareEnabled");
                if (virt != null) c.Add("Virtualizare (BIOS)", Convert.ToBoolean(virt) ? "Activată" : "Dezactivată");

                c.SetLevel(S(p, "Status") == "OK" || S(p, "Status") == "" ? 0 : 1, null);
                if (sn.CpuTemp > 0)
                {
                    string col = sn.CpuTemp >= 95 ? "#F87171" : sn.CpuTemp >= 85 ? "#FBBF24" : "#4ADE80";
                    c.Add("Temperatură", sn.CpuTemp + " °C" + (sn.CpuTempMax > sn.CpuTemp ? " (max " + sn.CpuTempMax + " °C)" : ""), col);
                    if (sn.CpuTemp >= 95)
                    {
                        c.Raise(2, "Procesorul e foarte fierbinte (" + sn.CpuTemp + " °C). Curăță radiatorul de praf și schimbă pasta termoconductoare.");
                        Recs.Add(1, "Procesor", "Curățare de praf și pastă termoconductoare nouă", "Procesorul ajunge la " + sn.CpuTemp + " °C.", "Pastă termoconductoare");
                    }
                    else if (sn.CpuTemp >= 85)
                    {
                        c.Raise(1, "Procesorul e cald (" + sn.CpuTemp + " °C). Dacă nu rulează ceva greu acum, verifică răcirea.");
                        Recs.Add(2, "Procesor", "Verificare răcire și curățare de praf", "Procesorul ajunge la " + sn.CpuTemp + " °C.", null);
                    }
                }
                else
                {
                    c.Add("Temperatură", "indisponibilă fără senzor");
                    c.Note("Windows nu oferă temperatura exactă a procesorului. Dacă pornești LibreHardwareMonitor (gratuit) și reîmprospătezi, aplicația o preia automat.");
                }
                if (st.WheaFatal > 0)
                {
                    c.Raise(2, "Windows a înregistrat erori hardware fatale (WHEA) în ultimele 30 de zile. Cauze frecvente: overclock, procesor sau sursă de alimentare.");
                    Recs.Add(1, "Hardware", "Diagnosticare erori hardware grave (WHEA)", st.WheaFatal + " erori în 30 de zile. Verifică overclock-ul, sursa, procesorul și memoria.", null);
                }
                else if (st.WheaCorrected > 20) c.Raise(1, "Au apărut multe erori hardware corectate (WHEA). Pot indica instabilitate la procesor, RAM sau placa de bază.");
                sec.Summary = name + " · " + cores + " nuclee / " + thr + " fire";
            }
            if (l.Count == 0) sec.AddCard("Procesor", "Nu am putut citi informațiile");
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ placa de baza / BIOS
        public static Section Board(Sensors sn)
        {
            Section sec = new Section("placa", "Placă de bază", "\uE950");
            sec.Description = "Producătorul și modelul plăcii de bază și versiunea de BIOS.";
            List<ManagementObject> bb = Cim("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
            List<ManagementObject> bios = Cim("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS");
            string manu = bb.Count > 0 ? Clean(S(bb[0], "Manufacturer")) : "";
            string prod = bb.Count > 0 ? Clean(S(bb[0], "Product")) : "";
            Card c = sec.AddCard(prod.Length > 0 ? prod : "Placă de bază", manu);
            if (bb.Count > 0)
            {
                c.Add("Producător", manu);
                c.Add("Model", prod);
                string ver = S(bb[0], "Version");
                if (ver.Length > 0 && !Regex.IsMatch(ver, "Default|To be filled|x\\.x", RegexOptions.IgnoreCase)) c.Add("Revizie", ver);
                string sn2 = S(bb[0], "SerialNumber");
                if (sn2.Length > 3 && !Regex.IsMatch(sn2, "Default|To be filled|0123456789", RegexOptions.IgnoreCase)) c.Add("Număr serie", sn2);
            }
            c.SetLevel(0, null);
            if (sn.BoardTemp > 0) c.Add("Temperatură", sn.BoardTemp + " °C", sn.BoardTemp >= 80 ? "#FBBF24" : null);
            if (bios.Count > 0)
            {
                DateTime rd = D(bios[0], "ReleaseDate");
                Card b = sec.AddCard("BIOS / UEFI", Clean(S(bios[0], "Manufacturer")));
                b.Add("Versiune", S(bios[0], "SMBIOSBIOSVersion"));
                b.Add("Data lansării", Date(rd) + (rd != DateTime.MinValue ? " (" + Ago(rd) + ")" : ""));
                b.SetLevel(-1, "Info");
                if (rd != DateTime.MinValue && (DateTime.Now - rd).TotalDays > 365 * 4)
                    b.Note("BIOS-ul are peste 4 ani. Merită verificată o versiune nouă pe site-ul producătorului plăcii: aduce compatibilitate și remedieri de securitate.");
            }
            sec.Summary = (manu + " " + prod).Trim();
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ memorie RAM
        static string RamType(long smbios, long speed)
        {
            switch (smbios)
            {
                case 20: return "DDR";
                case 21: case 22: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
            }
            if (speed >= 4800) return "DDR5";
            if (speed >= 2133) return "DDR4";
            if (speed >= 800) return "DDR3";
            return "";
        }

        static string RamMaker(string manu, string part)
        {
            string m = (manu ?? "").Trim();
            string u = m.ToUpperInvariant();
            string[,] codes = new string[,] {
                { "80CE", "Samsung" }, { "80AD", "SK Hynix" }, { "802C", "Micron" }, { "859B", "Crucial" }, { "0198", "Kingston" },
                { "029E", "Corsair" }, { "04CD", "G.Skill" }, { "04CB", "ADATA" }, { "04EF", "TeamGroup" }, { "014F", "Transcend" },
                { "0443", "Ramaxel" }, { "830B", "Nanya" }, { "8551", "Qimonda" }, { "0783", "Transcend" }, { "8A76", "Lexar" },
                { "05CB", "Patriot" }, { "0A9B", "SK Hynix" }, { "83F1", "Kingmax" }, { "86F1", "Goodram" }
            };
            for (int i = 0; i < codes.GetLength(0); i++)
                if (u.StartsWith(codes[i, 0]) || u == codes[i, 0].Substring(2)) return codes[i, 1];
            if (m.Length > 1 && !Regex.IsMatch(m, "^(0x)?[0-9A-F]+$|Unknown|Undefined|Manufacturer", RegexOptions.IgnoreCase)) return m;
            string p = (part ?? "").Trim().ToUpperInvariant();
            if (p.StartsWith("M378") || p.StartsWith("M471") || p.StartsWith("M425") || p.StartsWith("M323") || p.StartsWith("M393")) return "Samsung";
            if (p.StartsWith("HMA") || p.StartsWith("HMT") || p.StartsWith("HMC") || p.StartsWith("HMCG")) return "SK Hynix";
            if (p.StartsWith("KHX") || p.StartsWith("KF") || p.StartsWith("KVR") || p.StartsWith("9905") || p.StartsWith("ACR") || p.StartsWith("KCP")) return "Kingston";
            if (p.StartsWith("CMK") || p.StartsWith("CMW") || p.StartsWith("CMS") || p.StartsWith("CMH") || p.StartsWith("CMT")) return "Corsair";
            if (p.StartsWith("F4-") || p.StartsWith("F5-") || p.StartsWith("F3-")) return "G.Skill";
            if (p.StartsWith("CT") || p.StartsWith("BL")) return "Crucial";
            if (p.StartsWith("MTA") || p.StartsWith("MT")) return "Micron";
            if (p.StartsWith("AD4") || p.StartsWith("AX4") || p.StartsWith("AD5")) return "ADATA";
            return "Producător necunoscut";
        }

        public static Section Ram(Stability st)
        {
            Section sec = new Section("ram", "Memorie RAM", "\uE9D2");
            sec.Description = "Plăcuțele de memorie montate, tipul, frecvența și cât din memorie e folosită acum.";
            sec.ActionText = "Testează memoria (la repornire)";
            List<ManagementObject> mods = Cim("SELECT BankLabel, DeviceLocator, Capacity, Manufacturer, PartNumber, SerialNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory");
            List<ManagementObject> arr = Cim("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
            List<ManagementObject> os = Cim("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

            long total = 0, slots = 0, maxCap = 0;
            foreach (ManagementObject a in arr)
            {
                slots += Math.Max(0, L(a, "MemoryDevices"));
                long mc = L(a, "MaxCapacityEx");
                if (mc <= 0) mc = L(a, "MaxCapacity");
                if (mc > 0) maxCap += mc * 1024;
            }
            List<long> caps = new List<long>();
            List<string> parts = new List<string>();
            string type = "";
            long rated = 0, conf = 0;
            foreach (ManagementObject m in mods)
            {
                long cap = L(m, "Capacity");
                if (cap > 0) total += cap;
                caps.Add(cap);
                string part = S(m, "PartNumber");
                parts.Add(part);
                long sp = L(m, "Speed"), cf = L(m, "ConfiguredClockSpeed");
                string t = RamType(L(m, "SMBIOSMemoryType"), sp);
                if (t.Length > 0) type = t;
                if (sp > rated) rated = sp;
                if (cf > 0 && (conf == 0 || cf < conf)) conf = cf;
            }

            Card sum = sec.AddCard(Size(total, false) + (type.Length > 0 ? " " + type : ""), mods.Count + (mods.Count == 1 ? " plăcuță" : " plăcuțe") + (slots > 0 ? " în " + slots + " sloturi" : ""));
            sum.Badge = type;
            if (os.Count > 0)
            {
                long tv = L(os[0], "TotalVisibleMemorySize") * 1024, fr = L(os[0], "FreePhysicalMemory") * 1024;
                if (tv > 0)
                {
                    double used = (tv - fr) * 100.0 / tv;
                    sum.Add("Folosită acum", Size(tv - fr, false) + " din " + Size(tv, false) + " (" + used.ToString("0", RO) + "%)", used >= 90 ? "#FBBF24" : null);
                    if (used >= 90) sum.Note("Memoria e aproape plină acum. Închide programe sau ia în calcul mai mult RAM dacă se întâmplă des.");
                }
            }
            if (rated > 0) sum.Add("Frecvență", (conf > 0 ? conf + " MT/s" : "") + (conf > 0 && rated > conf ? " (plăcuțele suportă " + rated + ")" : (conf == 0 ? rated + " MT/s" : "")));
            if (slots > 0) sum.Add("Sloturi ocupate", mods.Count + " din " + slots);
            if (maxCap > 0) sum.Add("Maxim suportat", Size(maxCap, false));
            if (total > 0 && total < (8L << 30))
            {
                sum.Note("Sub 8 GB de RAM e puțin pentru Windows 10/11 modern. Un upgrade ar face calculatorul mult mai rapid.");
                bool sodimm = false;
                foreach (ManagementObject m in mods) if (L(m, "FormFactor") == 12) sodimm = true;
                bool freeSlot = slots > mods.Count;
                long addGb = Math.Max(4, 8 - total / (1L << 30));
                string part = "Memorie RAM " + (freeSlot ? addGb : 8) + " GB " + (type.Length > 0 ? type + " " : "") + (sodimm ? "SO-DIMM" : "DIMM") + (conf > 0 ? " " + conf + " MT/s" : "");
                Recs.Add(2, "Memorie RAM", "Upgrade memorie RAM la minim 8 GB", "Acum " + Size(total, false) + (freeSlot ? "; există slot liber." : "; toate sloturile sunt ocupate, se înlocuiește o plăcuță."), part);
            }
            if (mods.Count == 1 && slots >= 2) sum.Note("E montată o singură plăcuță: memoria merge pe un singur canal. Două plăcuțe identice (dual channel) aduc performanță vizibil mai bună.");
            bool mixed = false;
            for (int i = 1; i < mods.Count; i++) if (caps[i] != caps[0] || (!Junk(parts[i]) && parts[i].Trim() != parts[0].Trim())) mixed = true;
            if (mixed) sum.Note("Plăcuțele sunt diferite (capacitate sau model). De obicei funcționează, dar perechile identice sunt mai stabile și mai rapide.");
            if (conf > 0 && rated > conf + 100) sum.Note("Memoria rulează sub viteza suportată. Din BIOS poți activa profilul XMP/EXPO, dacă placa de bază îl suportă.");
            sum.SetLevel(0, null);

            if (st.MemTestResult == 1)
            {
                sum.Raise(2, "Testul de memorie Windows din " + Date(st.MemTestDate) + " a găsit ERORI. Plăcuța defectă trebuie înlocuită.");
                Recs.Add(1, "Memorie RAM", "Înlocuire plăcuță RAM defectă", "Testul de memorie din " + Date(st.MemTestDate) + " a găsit erori.", "Memorie RAM " + (type.Length > 0 ? type : ""));
            }
            else if (st.MemTestResult == 0) sum.Add("Ultimul test de memorie", "fără erori (" + Date(st.MemTestDate) + ")", "#4ADE80");
            else sum.Add("Ultimul test de memorie", "nu a fost rulat");
            if (st.BsodHints.Exists(delegate(string h) { return h.IndexOf("RAM", StringComparison.Ordinal) >= 0; }))
                sum.Raise(1, "Unele ecrane albastre recente au coduri care indică frecvent memoria RAM. Rulează testul de memorie.");

            int idx = 0;
            foreach (ManagementObject m in mods)
            {
                idx++;
                string slot = S(m, "DeviceLocator");
                string bank = Junk(S(m, "BankLabel")) ? "" : S(m, "BankLabel");
                string maker = RamMaker(S(m, "Manufacturer"), S(m, "PartNumber"));
                long cap = L(m, "Capacity");
                long sp = L(m, "Speed"), cf = L(m, "ConfiguredClockSpeed");
                string t = RamType(L(m, "SMBIOSMemoryType"), sp);
                long ff = L(m, "FormFactor");
                Card c = sec.AddCard(maker + " " + Size(cap, false) + (t.Length > 0 ? " " + t : ""), "Slot: " + (slot.Length > 0 ? slot : "#" + idx) + (bank.Length > 0 && bank != slot ? " · " + bank : ""));
                c.Badge = ff == 12 ? "SO-DIMM (laptop)" : ff == 8 ? "DIMM" : "";
                c.Add("Producător", maker);
                if (!Junk(S(m, "PartNumber"))) c.Add("Cod model", S(m, "PartNumber"));
                c.Add("Capacitate", Size(cap, false));
                c.Add("Viteză", (sp > 0 ? sp + " MT/s" : "—") + (cf > 0 && cf != sp ? " (rulează la " + cf + ")" : ""));
                string ser = S(m, "SerialNumber");
                if (!Junk(ser)) c.Add("Număr serie", ser);
                if (st.MemTestResult == 1) c.SetLevel(1, "Verifică"); else c.SetLevel(0, "Funcționează");
            }
            sec.Summary = Size(total, false) + (type.Length > 0 ? " " + type : "") + " · " + mods.Count + (mods.Count == 1 ? " plăcuță" : " plăcuțe") + (conf > 0 ? " · " + conf + " MT/s" : "");
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ placa video
        static long VramFromRegistry(string name, string pnp)
        {
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return -1;
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                        try
                        {
                            using (RegistryKey k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string desc = (k.GetValue("DriverDesc") ?? "").ToString();
                                string mid = (k.GetValue("MatchingDeviceId") ?? "").ToString();
                                bool match = string.Equals(desc.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                    (mid.Length > 8 && pnp.StartsWith(mid, StringComparison.OrdinalIgnoreCase));
                                if (!match) continue;
                                object q = k.GetValue("HardwareInformation.qwMemorySize");
                                if (q is long) return (long)q;
                                if (q is byte[] && ((byte[])q).Length >= 8) return BitConverter.ToInt64((byte[])q, 0);
                                object d = k.GetValue("HardwareInformation.MemorySize");
                                if (d is int) return (long)(uint)(int)d;
                                if (d is byte[] && ((byte[])d).Length >= 4) return BitConverter.ToUInt32((byte[])d, 0);
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return -1;
        }

        static Dictionary<string, string> NvidiaSmi()
        {
            string[] paths = new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" };
            foreach (string p in paths)
            {
                if (!File.Exists(p)) continue;
                string o = RunHidden(p, "--query-gpu=name,temperature.gpu,fan.speed,utilization.gpu,memory.used,memory.total,power.draw,clocks.gr --format=csv,noheader,nounits", 6000);
                if (string.IsNullOrEmpty(o)) continue;
                string line = o.Split('\n')[0];
                string[] f = line.Split(',');
                if (f.Length < 8) continue;
                Dictionary<string, string> d = new Dictionary<string, string>();
                string[] keys = new string[] { "name", "temp", "fan", "util", "memUsed", "memTotal", "power", "clock" };
                for (int i = 0; i < keys.Length; i++) d[keys[i]] = f[i].Trim();
                return d;
            }
            return null;
        }

        static int ParseInt(string s)
        {
            double d;
            if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (int)Math.Round(d);
            return -1;
        }

        public static Section Gpu(Sensors sn, Stability st)
        {
            Section sec = new Section("gpu", "Placă video", "\uE7FC");
            sec.Description = "Placa video, memoria video, driverul și temperatura (unde e disponibilă).";
            List<ManagementObject> l = Cim("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, VideoProcessor, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, ConfigManagerErrorCode, Status, PNPDeviceID FROM Win32_VideoController");
            Dictionary<string, string> nv = null;
            bool nvTried = false;
            List<string> names = new List<string>();
            foreach (ManagementObject g in l)
            {
                string name = Clean(S(g, "Name"));
                if (Regex.IsMatch(name, "Remote Display|Virtual|Parsec|Citrix|DisplayLink USB|Mirage|Meta Virtual", RegexOptions.IgnoreCase)) continue;
                bool basic = Regex.IsMatch(name, "Basic Display|Standard VGA|Microsoft Basic", RegexOptions.IgnoreCase);
                bool integrated = Regex.IsMatch(name, @"Intel.*(UHD|HD Graphics|Iris|Arc\(TM\) Graphics$)|^Intel\(R\) Graphics|AMD Radeon\(TM\) Graphics|Radeon.*Vega|Radeon \d{3}M|Radeon.*Graphics$", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(name, @"RX\s?\d|Arc A\d", RegexOptions.IgnoreCase);
                Card c = sec.AddCard(name, Clean(S(g, "AdapterCompatibility")));
                c.Badge = basic ? "Fără driver" : integrated ? "Integrată" : "Dedicată";
                names.Add(name);

                long vram = VramFromRegistry(S(g, "Name"), S(g, "PNPDeviceID"));
                if (vram <= 0) vram = L(g, "AdapterRAM");
                if (vram > 0) c.Add(integrated ? "Memorie video (partajată/dedicată)" : "Memorie video", Size(vram, false));
                long w = L(g, "CurrentHorizontalResolution"), h = L(g, "CurrentVerticalResolution"), hz = L(g, "CurrentRefreshRate");
                if (w > 0) c.Add("Rezoluție", w + " × " + h + (hz > 1 ? " la " + hz + " Hz" : ""));
                DateTime dd = D(g, "DriverDate");
                c.Add("Versiune driver", S(g, "DriverVersion"));
                c.Add("Data driverului", Date(dd) + (dd != DateTime.MinValue ? " (" + Ago(dd) + ")" : ""));

                long err = L(g, "ConfigManagerErrorCode");
                if (basic) c.SetLevel(1, "Fără driver");
                else if (err > 0) c.SetLevel(2, "Eroare driver");
                else c.SetLevel(0, "Funcționează");
                if (basic) { c.Note("Placa video folosește driverul generic Microsoft: imagine lentă, fără accelerare 3D. Instalează driverul de pe site-ul NVIDIA, AMD sau Intel."); Recs.Add(2, "Placă video", "Instalare driver placă video", "Rulează cu driverul generic Microsoft.", null); }
                if (err > 0) { c.Note("Windows raportează o problemă la acest dispozitiv (cod " + err + " în Device Manager). Reinstalează driverul plăcii video."); Recs.Add(1, "Placă video", "Reinstalare driver placă video", "Eroare în Device Manager, cod " + err + ".", null); }
                bool msDriver = S(g, "AdapterCompatibility").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
                if (dd != DateTime.MinValue && (DateTime.Now - dd).TotalDays > 730 && !basic && !msDriver)
                    c.Note("Driverul are peste 2 ani. Un driver nou aduce remedieri și performanță mai bună, mai ales la jocuri.");

                bool isNv = name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                int temp = -1;
                if (isNv && !nvTried) { nv = NvidiaSmi(); nvTried = true; }
                if (isNv && nv != null)
                {
                    temp = ParseInt(nv["temp"]);
                    int util = ParseInt(nv["util"]), fan = ParseInt(nv["fan"]), mu = ParseInt(nv["memUsed"]), mt = ParseInt(nv["memTotal"]), pw = ParseInt(nv["power"]), clk = ParseInt(nv["clock"]);
                    if (util >= 0) c.Add("Încărcare acum", util + "%");
                    if (mu >= 0 && mt > 0) c.Add("Memorie video folosită", mu + " MB din " + mt + " MB");
                    if (fan >= 0) c.Add("Ventilator", fan + "%");
                    if (pw >= 0) c.Add("Consum", pw + " W");
                    if (clk > 0) c.Add("Frecvență nucleu", clk + " MHz");
                }
                else if (!integrated && sn.GpuTemp > 0) temp = sn.GpuTemp;
                if (temp > 0)
                {
                    c.Add("Temperatură", temp + " °C", temp >= 90 ? "#F87171" : temp >= 82 ? "#FBBF24" : "#4ADE80");
                    if (temp >= 90) { c.Raise(2, "Placa video e foarte fierbinte (" + temp + " °C). Curăță ventilatoarele și verifică circulația aerului în carcasă."); Recs.Add(1, "Placă video", "Curățare placă video și verificare ventilatoare", "Ajunge la " + temp + " °C.", null); }
                    else if (temp >= 82) c.Raise(1, "Placa video e caldă (" + temp + " °C). E normal în jocuri, dar nu în repaus.");
                }
                if (st.BsodHints.Exists(delegate(string x) { return x.IndexOf("video", StringComparison.Ordinal) >= 0; }))
                    c.Raise(1, "Unele ecrane albastre recente au coduri legate de placa video sau de driverul ei.");
            }
            if (sec.Cards.Count == 0) sec.AddCard("Placă video", "Nu am putut citi informațiile");
            sec.Summary = string.Join(" + ", names.ToArray());
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ monitoare
        static string U16(object o)
        {
            if (o == null) return "";
            StringBuilder sb = new StringBuilder();
            try
            {
                foreach (object x in (Array)o)
                {
                    int c = Convert.ToInt32(x);
                    if (c == 0) break;
                    sb.Append((char)c);
                }
            }
            catch (Exception) { }
            return sb.ToString().Trim();
        }

        static string PnpMaker(string code)
        {
            switch ((code ?? "").ToUpperInvariant())
            {
                case "SAM": case "SEC": return "Samsung";
                case "DEL": return "Dell";
                case "GSM": case "LGD": return "LG";
                case "AUO": return "AU Optronics";
                case "BOE": return "BOE";
                case "ACR": return "Acer";
                case "AOC": return "AOC";
                case "HWP": case "HPN": return "HP";
                case "LEN": return "Lenovo";
                case "PHL": return "Philips";
                case "BNQ": return "BenQ";
                case "AUS": case "ACI": return "ASUS";
                case "IVM": return "iiyama";
                case "CMN": return "Innolux";
                case "SHP": return "Sharp";
                case "MSI": return "MSI";
                case "VSC": return "ViewSonic";
                case "HKC": return "HKC";
                case "SNY": return "Sony";
                case "APP": return "Apple";
                case "LGP": return "LG Philips";
                case "NEC": return "NEC";
                case "GBT": return "Gigabyte";
                case "XMI": return "Xiaomi";
                case "HSD": return "HannStar";
                case "IVO": return "InfoVision";
                case "CSO": return "CSOT";
            }
            return code ?? "";
        }

        public static Section Monitors()
        {
            Section sec = new Section("monitor", "Monitoare", "\uE7F4");
            sec.Description = "Ecranele conectate: producător, model, diagonală și anul fabricației.";
            List<ManagementObject> ids = Wmi(@"root\wmi", "SELECT InstanceName, ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture, WeekOfManufacture FROM WmiMonitorID");
            List<ManagementObject> bp = Wmi(@"root\wmi", "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams");
            List<string> names = new List<string>();
            foreach (ManagementObject m in ids)
            {
                string inst = S(m, "InstanceName");
                string maker = PnpMaker(U16(P(m, "ManufacturerName")));
                string model = U16(P(m, "UserFriendlyName"));
                bool internalPanel = model.Length == 0 && inst.IndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase) >= 0;
                Card c = sec.AddCard((model.Length > 0 ? model : "Ecran " + (internalPanel ? "integrat (laptop)" : "")).Trim(), maker);
                c.Add("Producător", maker);
                foreach (ManagementObject b in bp)
                {
                    if (S(b, "InstanceName") != inst) continue;
                    long hcm = L(b, "MaxHorizontalImageSize"), vcm = L(b, "MaxVerticalImageSize");
                    if (hcm > 0 && vcm > 0)
                    {
                        double inch = Math.Sqrt(hcm * hcm + vcm * vcm) / 2.54;
                        c.Add("Diagonală", inch.ToString("0.#", RO) + "\" (" + hcm + " × " + vcm + " cm)");
                    }
                }
                long y = L(m, "YearOfManufacture");
                if (y > 1990) c.Add("An fabricație", y.ToString());
                string ser = U16(P(m, "SerialNumberID"));
                if (ser.Length > 1 && ser != "0") c.Add("Număr serie", ser);
                c.SetLevel(0, "Conectat");
                names.Add(model.Length > 0 ? model : maker);
            }
            try
            {
                System.Windows.Forms.Screen[] scr = System.Windows.Forms.Screen.AllScreens;
                Card sc = sec.AddCard("Ecrane active", scr.Length + (scr.Length == 1 ? " ecran" : " ecrane"));
                int i = 0;
                foreach (System.Windows.Forms.Screen s in scr)
                    sc.Add("Ecran " + (++i) + (s.Primary ? " (principal)" : ""), s.Bounds.Width + " × " + s.Bounds.Height);
                sc.SetLevel(-1, "Info");
            }
            catch (Exception) { }
            sec.Summary = names.Count > 0 ? string.Join(", ", names.ToArray()) : "—";
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ baterie (laptop)
        public static Section Battery()
        {
            List<ManagementObject> wb = Cim("SELECT Name, EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime FROM Win32_Battery");
            if (wb.Count == 0) return null;
            Section sec = new Section("baterie", "Baterie", "\uE83F");
            sec.Description = "Uzura bateriei: cât din capacitatea inițială a mai rămas.";
            List<ManagementObject> stat = Wmi(@"root\wmi", "SELECT DesignedCapacity, ManufactureName, DeviceName, SerialNumber, Chemistry FROM BatteryStaticData");
            List<ManagementObject> full = Wmi(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            List<ManagementObject> cyc = Wmi(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount");
            for (int i = 0; i < wb.Count; i++)
            {
                string dev = stat.Count > i ? S(stat[i], "DeviceName") : "";
                string man = stat.Count > i ? S(stat[i], "ManufactureName") : "";
                Card c = sec.AddCard(dev.Length > 0 ? dev : (S(wb[i], "Name").Length > 0 ? S(wb[i], "Name") : "Baterie"), man);
                long charge = L(wb[i], "EstimatedChargeRemaining");
                long bs = L(wb[i], "BatteryStatus");
                c.Add("Încărcare acum", charge >= 0 ? charge + "%" + (bs == 2 || bs >= 6 && bs <= 9 ? " (la încărcător)" : "") : "—");
                long des = stat.Count > i ? L(stat[i], "DesignedCapacity") : -1;
                long fc = full.Count > i ? L(full[i], "FullChargedCapacity") : -1;
                c.SetLevel(-1, "Info");
                if (des > 0 && fc > 0)
                {
                    double health = Math.Min(100, fc * 100.0 / des);
                    c.Add("Capacitate inițială", (des / 1000.0).ToString("0.#", RO) + " Wh");
                    c.Add("Capacitate acum", (fc / 1000.0).ToString("0.#", RO) + " Wh");
                    string col = health < 50 ? "#F87171" : health < 75 ? "#FBBF24" : "#4ADE80";
                    c.Add("Sănătate baterie", health.ToString("0", RO) + "% (uzură " + (100 - health).ToString("0", RO) + "%)", col);
                    if (health < 50) { c.Raise(2, "Bateria mai ține sub jumătate din capacitatea inițială. Merită înlocuită."); Recs.Add(2, "Baterie", "Înlocuire baterie laptop", "Mai are " + health.ToString("0", RO) + "% din capacitate.", "Baterie laptop" + (dev.Length > 0 ? " " + dev : "")); }
                    else if (health < 75) { c.Raise(1, "Bateria a pierdut o parte vizibilă din capacitate. Încă e folosibilă, dar autonomia e mai mică."); Recs.Add(3, "Baterie", "Înlocuire baterie, când autonomia nu mai ajunge", "Mai are " + health.ToString("0", RO) + "% din capacitate.", null); }
                    else c.SetLevel(0, null);
                    sec.Summary = "Sănătate " + health.ToString("0", RO) + "%";
                }
                else c.Note("Capacitatea detaliată nu e disponibilă pentru această baterie.");
                long cc = cyc.Count > i ? L(cyc[i], "CycleCount") : -1;
                if (cc > 0) c.Add("Cicluri de încărcare", cc.ToString("N0", RO));
            }
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ retea
        public static Section Network()
        {
            Section sec = new Section("retea", "Rețea", "\uE839");
            sec.Description = "Plăcile de rețea (cablu și Wi-Fi), viteza conexiunii și adresele.";
            List<ManagementObject> ad = Cim("SELECT Index, Name, NetConnectionID, NetConnectionStatus, Speed, MACAddress, Manufacturer FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            List<ManagementObject> cfg = Cim("SELECT Index, IPAddress, DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            string wifiSignal = null;
            int connected = 0;
            foreach (ManagementObject a in ad)
            {
                string name = Clean(S(a, "Name"));
                string conn = S(a, "NetConnectionID");
                if (conn.Length == 0 || Regex.IsMatch(name, "Bluetooth|Virtual|Hyper-V|VMware|VirtualBox|TAP-|WAN Miniport|Kernel Debug", RegexOptions.IgnoreCase)) continue;
                bool wifi = Regex.IsMatch(name + conn, "Wi-?Fi|Wireless|WLAN|802\\.11|Wi-Fi", RegexOptions.IgnoreCase);
                long status = L(a, "NetConnectionStatus");
                bool up = status == 2;
                Card c = sec.AddCard(name, conn);
                c.Badge = wifi ? "Wi-Fi" : "Cablu";
                c.Add("Stare", up ? "Conectat" : status == 7 ? "Deconectat" : status == 0 ? "Oprit" : "Altă stare (" + status + ")", up ? "#4ADE80" : null);
                long sp = L(a, "Speed");
                if (up && sp > 0 && sp < 400000000000L) c.Add("Viteză legătură", sp >= 1000000000 ? (sp / 1e9).ToString("0.#", RO) + " Gbps" : (sp / 1e6).ToString("0", RO) + " Mbps",
                    !wifi && sp == 100000000 ? "#FBBF24" : null);
                c.Add("Adresă MAC", S(a, "MACAddress"));
                long idx = L(a, "Index");
                foreach (ManagementObject k in cfg)
                {
                    if (L(k, "Index") != idx) continue;
                    string[] ips = P(k, "IPAddress") as string[];
                    if (ips != null) foreach (string ip in ips) if (ip.IndexOf('.') > 0) { c.Add("Adresă IP", ip); break; }
                    string[] gw = P(k, "DefaultIPGateway") as string[];
                    if (gw != null && gw.Length > 0) c.Add("Router (gateway)", gw[0]);
                }
                if (wifi && up)
                {
                    if (wifiSignal == null)
                    {
                        string o = RunHidden("netsh.exe", "wlan show interfaces", 5000) ?? "";
                        Match m = Regex.Match(o, @"(Signal|Semnal)\s*:\s*(\d+)\s*%", RegexOptions.IgnoreCase);
                        wifiSignal = m.Success ? m.Groups[2].Value : "";
                    }
                    if (wifiSignal.Length > 0)
                    {
                        int sig = int.Parse(wifiSignal);
                        c.Add("Semnal Wi-Fi", sig + "%", sig < 40 ? "#FBBF24" : "#4ADE80");
                        if (sig < 40) c.Note("Semnalul Wi-Fi e slab. Apropie calculatorul de router sau folosește un repetor/cablu.");
                    }
                }
                if (!wifi && up && sp == 100000000) { c.Note("Conexiunea pe cablu merge doar la 100 Mbps. De obicei e de vină cablul sau mufa (trebuie 1 Gbps)."); Recs.Add(3, "Rețea", "Verificare cablu de rețea / mufă (merge la 100 Mbps)", "Placa " + name + " ar trebui să meargă la 1 Gbps.", "Patch cord UTP Cat6"); }
                c.SetLevel(up ? 0 : -1, up ? "Conectat" : "Neconectat");
                if (up) connected++;
            }
            if (sec.Cards.Count == 0) sec.AddCard("Rețea", "Nu am găsit plăci de rețea fizice");
            sec.Summary = connected + (connected == 1 ? " conexiune activă" : " conexiuni active") + " din " + sec.Cards.Count;
            sec.Finish();
            return sec;
        }

        // ------------------------------------------------------------ stabilitate (jurnale Windows)
        static int CountEvents(string xpath, int max, List<EventRecord> keep)
        {
            int n = 0;
            EventLogQuery q = new EventLogQuery("System", PathType.LogName, xpath);
            q.ReverseDirection = true;
            using (EventLogReader r = new EventLogReader(q))
            {
                EventRecord e;
                while (n < max && (e = r.ReadEvent()) != null)
                {
                    n++;
                    if (keep != null && keep.Count < 5) keep.Add(e); else e.Dispose();
                }
            }
            return n;
        }

        static string BsodHint(uint code)
        {
            switch (code)
            {
                case 0x1A: return "MEMORY_MANAGEMENT — des legat de memoria RAM";
                case 0x50: return "PAGE_FAULT_IN_NONPAGED_AREA — RAM sau driver";
                case 0x124: return "WHEA_UNCORRECTABLE_ERROR — eroare hardware (procesor, overclock, sursă)";
                case 0x116: return "VIDEO_TDR_FAILURE — placa video sau driverul ei";
                case 0x117: return "VIDEO_TDR_TIMEOUT — placa video sau driverul ei";
                case 0x119: return "VIDEO_SCHEDULER_INTERNAL_ERROR — placa video sau driverul ei";
                case 0x7A: return "KERNEL_DATA_INPAGE_ERROR — disc sau cablu de date";
                case 0x77: return "KERNEL_STACK_INPAGE_ERROR — disc sau RAM";
                case 0xF4: return "CRITICAL_OBJECT_TERMINATION — disc sau SSD";
                case 0xEF: return "CRITICAL_PROCESS_DIED — fișiere de sistem sau disc";
                case 0x133: return "DPC_WATCHDOG_VIOLATION — driver sau firmware SSD";
                case 0xD1: return "DRIVER_IRQL_NOT_LESS_OR_EQUAL — driver defect";
                case 0xA: return "IRQL_NOT_LESS_OR_EQUAL — driver sau RAM";
                case 0x3B: return "SYSTEM_SERVICE_EXCEPTION — driver sau fișiere de sistem";
                case 0x7E: return "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED — driver";
                case 0x139: return "KERNEL_SECURITY_CHECK_FAILURE — driver sau RAM";
                case 0x9F: return "DRIVER_POWER_STATE_FAILURE — driver (de obicei la sleep)";
                case 0x101: return "CLOCK_WATCHDOG_TIMEOUT — procesor / overclock";
                case 0xC000021A: return "eroare critică a sistemului de operare";
            }
            return "";
        }

        public static Stability ReadStability()
        {
            Stability st = new Stability();
            string last30 = "TimeCreated[timediff(@SystemTime) <= 2592000000]";
            try
            {
                List<EventRecord> bs = new List<EventRecord>();
                st.Bsod = CountEvents("*[System[(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and " + last30 + "]]", 200, bs);
                HashSet<string> seen = new HashSet<string>();
                foreach (EventRecord e in bs)
                {
                    string all = "";
                    try { foreach (EventProperty p in e.Properties) all += " " + (p.Value ?? ""); } catch (Exception) { }
                    Match m = Regex.Match(all, @"0x([0-9a-fA-F]{8})");
                    if (!m.Success) { try { m = Regex.Match(e.FormatDescription() ?? "", @"0x([0-9a-fA-F]{8})"); } catch (Exception) { } }
                    string when = e.TimeCreated.HasValue ? e.TimeCreated.Value.ToString("dd.MM.yyyy HH:mm", RO) : "";
                    if (m.Success)
                    {
                        uint code = Convert.ToUInt32(m.Groups[1].Value, 16);
                        string hint = BsodHint(code);
                        st.BsodLines.Add(when + " — cod 0x" + code.ToString("X") + (hint.Length > 0 ? " (" + hint + ")" : ""));
                        if (hint.Length > 0 && seen.Add(hint))
                        {
                            if (hint.IndexOf("RAM", StringComparison.Ordinal) >= 0) st.BsodHints.Add("RAM");
                            if (hint.IndexOf("placa video", StringComparison.Ordinal) >= 0) st.BsodHints.Add("video");
                            if (hint.IndexOf("disc", StringComparison.Ordinal) >= 0 || hint.IndexOf("SSD", StringComparison.Ordinal) >= 0) st.BsodHints.Add("disc");
                        }
                    }
                    else st.BsodLines.Add(when);
                    e.Dispose();
                }
                st.PowerLoss = CountEvents("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and " + last30 + "]]", 200, null);
                st.WheaFatal = CountEvents("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and " + last30 + "]]", 500, null);
                st.WheaCorrected = CountEvents("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and Level=3 and " + last30 + "]]", 500, null);
                st.DiskBadBlock = CountEvents("*[System[Provider[@Name='disk'] and EventID=7 and " + last30 + "]]", 500, null);
                st.DiskRetry = CountEvents("*[System[Provider[@Name='disk'] and (EventID=153 or EventID=51 or EventID=11) and " + last30 + "]]", 500, null);
                st.NtfsCorrupt = CountEvents("*[System[(Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and EventID=55 and " + last30 + "]]", 500, null);

                List<EventRecord> mt = new List<EventRecord>();
                CountEvents("*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']]]", 1, mt);
                if (mt.Count > 0)
                {
                    byte lv = mt[0].Level.HasValue ? mt[0].Level.Value : (byte)4;
                    st.MemTestResult = lv <= 3 ? 1 : 0;
                    st.MemTestDate = mt[0].TimeCreated.HasValue ? mt[0].TimeCreated.Value : DateTime.MinValue;
                    mt[0].Dispose();
                }
            }
            catch (Exception) { st.LogsReadable = false; }
            return st;
        }

        public static Section StabilitySection(Stability st)
        {
            Section sec = new Section("stabilitate", "Stabilitate (30 zile)", "\uE9D9");
            sec.Description = "Ce a înregistrat Windows în ultimele 30 de zile: ecrane albastre, opriri bruște și erori hardware.";
            Card c = sec.AddCard("Jurnalul Windows", "Ultimele 30 de zile");
            if (!st.LogsReadable)
            {
                c.SetLevel(-1, "Indisponibil");
                c.Note("Nu am putut citi jurnalul de sistem.");
                sec.Finish();
                return sec;
            }
            c.SetLevel(0, null);
            c.Add("Ecrane albastre (BSOD)", st.Bsod.ToString(), st.Bsod >= 3 ? "#F87171" : st.Bsod > 0 ? "#FBBF24" : "#4ADE80");
            c.Add("Opriri bruște", st.PowerLoss.ToString(), st.PowerLoss >= 3 ? "#FBBF24" : null);
            c.Add("Erori hardware grave (WHEA)", st.WheaFatal.ToString(), st.WheaFatal > 0 ? "#F87171" : "#4ADE80");
            c.Add("Erori hardware corectate", st.WheaCorrected.ToString(), st.WheaCorrected > 20 ? "#FBBF24" : null);
            c.Add("Blocuri defecte pe disc", st.DiskBadBlock.ToString(), st.DiskBadBlock > 0 ? "#F87171" : "#4ADE80");
            c.Add("Reîncercări de citire pe disc", st.DiskRetry.ToString(), st.DiskRetry > 5 ? "#FBBF24" : null);
            c.Add("Erori sistem de fișiere", st.NtfsCorrupt.ToString(), st.NtfsCorrupt > 0 ? "#FBBF24" : null);

            if (st.Bsod >= 3) { c.Raise(2, st.Bsod + " ecrane albastre într-o lună: sistemul e instabil."); Recs.Add(1, "Stabilitate", "Diagnosticare ecrane albastre", st.Bsod + " în ultimele 30 de zile" + (st.BsodLines.Count > 0 ? "; ultimul: " + st.BsodLines[0] : "") + ".", null); }
            else if (st.Bsod > 0) c.Raise(1, st.Bsod + (st.Bsod == 1 ? " ecran albastru" : " ecrane albastre") + " în ultima lună.");
            if (st.PowerLoss >= 3) Recs.Add(2, "Alimentare", "Verificare sursă de alimentare / UPS", st.PowerLoss + " opriri bruște în 30 de zile.", "UPS");
            if (st.PowerLoss >= 3) c.Raise(1, st.PowerLoss + " opriri bruște, fără închidere corectă. Dacă nu au fost pene de curent sau opriri din buton, verifică sursa de alimentare și temperaturile.");
            else if (st.PowerLoss > 0) c.Note(st.PowerLoss + (st.PowerLoss == 1 ? " oprire bruscă" : " opriri bruște") + " (pană de curent, oprire din buton sau blocare).");
            if (st.WheaFatal > 0) c.Raise(2, "Erori hardware grave raportate de procesor sau de placa de bază. Cauze frecvente: overclock, sursă slabă, procesor sau RAM.");
            else if (st.WheaCorrected > 20) c.Raise(1, "Multe erori hardware corectate. De obicei vin de la PCIe (placa video, SSD NVMe) sau de la un overclock instabil.");
            if (st.DiskBadBlock > 0) c.Raise(2, "Windows a găsit blocuri defecte pe un disc (" + st.DiskBadBlock + " ori). Vezi secțiunea Discuri și fă backup.");
            else if (st.DiskRetry > 5) c.Raise(1, "Multe reîncercări de citire pe disc. Pot veni de la un disc obosit sau de la cablul SATA.");
            if (st.NtfsCorrupt > 0) c.Raise(1, "Sistemul de fișiere a raportat corupții. Rulează „chkdsk /scan” pe partiția afectată.");

            if (st.BsodLines.Count > 0)
            {
                Card b = sec.AddCard("Ultimele ecrane albastre", "Codul de eroare și cauza probabilă");
                int i = 0;
                foreach (string l in st.BsodLines) b.Add("BSOD " + (++i), l);
                b.SetLevel(st.Bsod >= 3 ? 2 : 1, null);
            }
            sec.Summary = st.Bsod + " BSOD · " + st.PowerLoss + " opriri bruște · " + (st.WheaFatal + st.WheaCorrected) + " erori hardware";
            sec.Finish();
            return sec;
        }
    }
}
