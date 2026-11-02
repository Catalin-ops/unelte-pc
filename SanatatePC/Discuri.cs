// Sanatate Disc - Info PC Electric
// Citeste datele SMART (SATA/ATA) si jurnalul de sanatate NVMe. Doar interogari, nu scrie nimic pe disc.
// Scris in C# 5 (compilat la pornire de Windows PowerShell).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace InfoPcDisk
{
    public class SmartAttr
    {
        public int IdNum;
        public string Id { get; set; }
        public string Name { get; set; }
        public string Current { get; set; }
        public string Worst { get; set; }
        public string Threshold { get; set; }
        public string RawText { get; set; }
        public string Status { get; set; }
        public string StatusColor { get; set; }
        public long Raw;
        public int Cur, Wst, Thr;
    }

    public class DiskReport
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string TypeBadge { get; set; }
        public string Serial { get; set; }
        public string Firmware { get; set; }
        public string Letters { get; set; }
        public string SizeText { get; set; }
        public string Interface { get; set; }

        public string HealthText { get; set; }
        public string HealthColor { get; set; }
        public string HealthBg { get; set; }
        public int Score { get; set; }
        public string ScoreText { get; set; }
        public string Verdict { get; set; }

        public string LifeText { get; set; }
        public string TempText { get; set; }
        public string TempColor { get; set; }
        public string TempMaxText { get; set; }
        public string HoursText { get; set; }
        public string HoursSub { get; set; }
        public string CyclesText { get; set; }
        public string WrittenText { get; set; }
        public string ErrorsText { get; set; }
        public string ErrorsColor { get; set; }
        public string ErrorsSub { get; set; }
        public string SourceText { get; set; }
        public string ReasonsText { get; set; }
        public List<string> Reasons { get; set; }
        public List<SmartAttr> Attributes { get; set; }

        public int LifeLeft = -1, Temp = -1, TempMax = -1;
        public long Hours = -1, Cycles = -1;
        public bool IsSsd, IsNvme;
        public int Level; // 0 bun, 1 atentie, 2 rau, -1 necunoscut
    }

    public static class Health
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);

        const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        static readonly CultureInfo RO = new CultureInfo("ro-RO");

        static SafeFileHandle Open(int disk, bool rw)
        {
            // Accesul "read/write" e cerut de Windows doar pentru comanda de interogare SMART; nu se scrie nimic.
            uint access = rw ? 0xC0000000 : 0x80000000;
            return CreateFileW(@"\\.\PhysicalDrive" + disk, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        static byte[] AtaSmart(SafeFileHandle h, byte feature)
        {
            byte[] inBuf = new byte[33];
            BitConverter.GetBytes(512).CopyTo(inBuf, 0);
            inBuf[4] = feature;   // D0 = citire atribute, D1 = citire praguri
            inBuf[5] = 1;
            inBuf[6] = 1;
            inBuf[7] = 0x4F;
            inBuf[8] = 0xC2;
            inBuf[9] = 0xA0;
            inBuf[10] = 0xB0;     // comanda SMART
            byte[] outBuf = new byte[16 + 512];
            int ret;
            if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBuf, 32, outBuf, outBuf.Length, out ret, IntPtr.Zero)) return null;
            byte[] data = new byte[512];
            Buffer.BlockCopy(outBuf, 16, data, 0, 512);
            for (int i = 2; i < 362; i += 12) if (data[i] != 0) return data;
            return null;
        }

        static byte[] NvmeHealthLog(SafeFileHandle h)
        {
            const int specSize = 40;
            byte[] buf = new byte[8 + specSize + 512];
            BitConverter.GetBytes(50).CopyTo(buf, 0);           // StorageDeviceProtocolSpecificProperty
            BitConverter.GetBytes(0).CopyTo(buf, 4);            // PropertyStandardQuery
            BitConverter.GetBytes(3).CopyTo(buf, 8);            // ProtocolTypeNvme
            BitConverter.GetBytes(2).CopyTo(buf, 12);           // NVMeDataTypeLogPage
            BitConverter.GetBytes(2).CopyTo(buf, 16);           // jurnal SMART / Health
            BitConverter.GetBytes(0).CopyTo(buf, 20);
            BitConverter.GetBytes(specSize).CopyTo(buf, 24);    // ProtocolDataOffset
            BitConverter.GetBytes(512).CopyTo(buf, 28);         // ProtocolDataLength
            int ret;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, buf.Length, buf, buf.Length, out ret, IntPtr.Zero)) return null;
            int off = BitConverter.ToInt32(buf, 8 + 16);
            int len = BitConverter.ToInt32(buf, 8 + 20);
            if (off <= 0 || len < 512 || 8 + off + 512 > buf.Length) return null;
            byte[] log = new byte[512];
            Buffer.BlockCopy(buf, 8 + off, log, 0, 512);
            return log;
        }

        // ------------------------------------------------------------ punct de intrare
        public static DiskReport Analyze(int number, string model, string serial, string firmware, string media, string bus,
            long size, string winHealth, string letters, int winTemp, int winTempMax, int winWear, long winHours,
            long winCycles, long winReadErr, long winWriteErr)
        {
            byte[] attrs = null, thr = null, nvme = null;
            string readError = null;
            bool isNvme = string.Equals(bus, "NVMe", StringComparison.OrdinalIgnoreCase);
            try
            {
                using (SafeFileHandle h = isNvme ? Open(number, false) : Open(number, true))
                {
                    if (h.IsInvalid) readError = "acces refuzat (" + Marshal.GetLastWin32Error() + ")";
                    else if (isNvme) nvme = NvmeHealthLog(h);
                    else
                    {
                        attrs = AtaSmart(h, 0xD0);
                        if (attrs != null) thr = AtaSmart(h, 0xD1);
                    }
                }
                if (isNvme && nvme == null)
                {
                    using (SafeFileHandle h2 = Open(number, true))
                        if (!h2.IsInvalid) nvme = NvmeHealthLog(h2);
                }
            }
            catch (Exception ex) { readError = ex.Message; }

            DiskReport rep = Evaluate(number, model, serial, firmware, media, bus, size, winHealth, letters,
                winTemp, winTempMax, winWear, winHours, winCycles, winReadErr, winWriteErr, attrs, thr, nvme);
            if (attrs == null && nvme == null && readError != null) rep.SourceText += " (" + readError + ")";
            return rep;
        }

        // ------------------------------------------------------------ evaluare (fara acces la disc - testabila)
        public static DiskReport Evaluate(int number, string model, string serial, string firmware, string media, string bus,
            long size, string winHealth, string letters, int winTemp, int winTempMax, int winWear, long winHours,
            long winCycles, long winReadErr, long winWriteErr, byte[] attrs, byte[] thr, byte[] nvme)
        {
            DiskReport r = new DiskReport();
            r.Number = number;
            r.Title = string.IsNullOrEmpty(model) ? ("Disc #" + number) : model.Trim();
            r.Serial = string.IsNullOrEmpty(serial) ? "—" : serial.Trim();
            r.Firmware = string.IsNullOrEmpty(firmware) ? "—" : firmware.Trim();
            r.Letters = letters ?? "";
            r.IsNvme = string.Equals(bus, "NVMe", StringComparison.OrdinalIgnoreCase);
            r.IsSsd = r.IsNvme || string.Equals(media, "SSD", StringComparison.OrdinalIgnoreCase);
            bool isHdd = string.Equals(media, "HDD", StringComparison.OrdinalIgnoreCase);
            r.SizeText = FormatBytes(size, true);
            r.Interface = string.IsNullOrEmpty(bus) ? "—" : bus;
            r.TypeBadge = r.IsNvme ? "SSD NVMe" : r.IsSsd ? "SSD" : isHdd ? "HDD" : (string.Equals(bus, "USB", StringComparison.OrdinalIgnoreCase) ? "USB" : "Disc");
            r.Subtitle = r.TypeBadge + " · " + r.SizeText + (r.Letters.Length > 0 ? " · " + r.Letters : "") + " · disc #" + number;
            r.Attributes = new List<SmartAttr>();
            r.Reasons = new List<string>();

            int bad = 0, warn = 0;
            int score = 100;
            long realloc = -1, pending = -1, uncorr = -1, crc = -1, mediaErr = -1;
            string written = "—";
            bool smartFailed = false;

            // ---- NVMe
            if (nvme != null)
            {
                r.SourceText = "Jurnal de sănătate NVMe citit direct de pe disc";
                int crit = nvme[0];
                int kelvin = BitConverter.ToUInt16(nvme, 1);
                int spare = nvme[3], spareThr = nvme[4], used = nvme[5];
                ulong unitsWritten = BitConverter.ToUInt64(nvme, 48);
                ulong unitsRead = BitConverter.ToUInt64(nvme, 32);
                long pcycles = (long)BitConverter.ToUInt64(nvme, 112);
                long hours = (long)BitConverter.ToUInt64(nvme, 128);
                long unsafeSd = (long)BitConverter.ToUInt64(nvme, 144);
                mediaErr = (long)BitConverter.ToUInt64(nvme, 160);
                long errLog = (long)BitConverter.ToUInt64(nvme, 176);

                if (kelvin > 200 && kelvin < 400) r.Temp = kelvin - 273;
                r.LifeLeft = Math.Max(0, 100 - Math.Min(used, 100));
                r.Hours = hours;
                r.Cycles = pcycles;
                written = FormatBytes((long)Math.Min((double)unitsWritten * 512000.0, long.MaxValue), false);

                AddNvme(r, "Avertizare critică", crit == 0 ? "0 (niciuna)" : "0x" + crit.ToString("X2"), crit == 0 ? 0 : 2);
                AddNvme(r, "Temperatură", r.Temp >= 0 ? r.Temp + " °C" : "—", 0);
                AddNvme(r, "Rezervă disponibilă", spare + "% (prag " + spareThr + "%)", spare < spareThr ? 2 : spare < 50 ? 1 : 0);
                AddNvme(r, "Procent de viață folosit", used + "%", used >= 100 ? 2 : used >= 80 ? 1 : 0);
                AddNvme(r, "Date citite", FormatBytes((long)Math.Min((double)unitsRead * 512000.0, long.MaxValue), false), 0);
                AddNvme(r, "Date scrise", written, 0);
                AddNvme(r, "Cicluri de pornire", pcycles.ToString("N0", RO), 0);
                AddNvme(r, "Ore de funcționare", hours.ToString("N0", RO), 0);
                AddNvme(r, "Opriri bruște (fără închidere corectă)", unsafeSd.ToString("N0", RO), 0);
                AddNvme(r, "Erori de integritate a datelor", mediaErr.ToString("N0", RO), mediaErr > 0 ? 1 : 0);
                AddNvme(r, "Intrări în jurnalul de erori", errLog.ToString("N0", RO), 0);

                if (crit != 0)
                {
                    bad++; score = Math.Min(score, 20);
                    List<string> w = new List<string>();
                    if ((crit & 1) != 0) w.Add("rezerva de blocuri e sub prag");
                    if ((crit & 2) != 0) w.Add("temperatură în afara limitelor");
                    if ((crit & 4) != 0) w.Add("fiabilitate degradată");
                    if ((crit & 8) != 0) w.Add("discul a trecut în mod doar-citire");
                    if ((crit & 16) != 0) w.Add("memoria de rezervă a eșuat");
                    r.Reasons.Add("Discul raportează o avertizare critică: " + (w.Count > 0 ? string.Join(", ", w.ToArray()) : "cod 0x" + crit.ToString("X2")) + ".");
                }
                if (spare < spareThr) { bad++; score = Math.Min(score, 20); r.Reasons.Add("Rezerva de blocuri (" + spare + "%) a scăzut sub pragul de siguranță (" + spareThr + "%)."); }
                if (mediaErr > 0) { warn++; score = Math.Min(score, 70); r.Reasons.Add(mediaErr + " erori de integritate a datelor (blocuri care nu s-au putut citi corect)."); }
            }

            // ---- SATA / ATA
            if (attrs != null)
            {
                r.SourceText = "Date SMART citite direct de pe disc";
                Dictionary<int, int> thrMap = new Dictionary<int, int>();
                if (thr != null)
                    for (int i = 2; i + 12 <= 362; i += 12) if (thr[i] != 0) thrMap[thr[i]] = thr[i + 1];

                Dictionary<int, SmartAttr> byId = new Dictionary<int, SmartAttr>();
                for (int i = 2; i + 12 <= 362; i += 12)
                {
                    int id = attrs[i];
                    if (id == 0) continue;
                    SmartAttr a = new SmartAttr();
                    a.IdNum = id;
                    a.Id = id.ToString("X2");
                    a.Name = AttrName(id);
                    a.Cur = attrs[i + 3];
                    a.Wst = attrs[i + 4];
                    long raw = 0;
                    for (int k = 0; k < 6; k++) raw |= (long)attrs[i + 5 + k] << (8 * k);
                    a.Raw = raw;
                    a.Thr = thrMap.ContainsKey(id) ? thrMap[id] : -1;
                    a.Current = a.Cur.ToString();
                    a.Worst = a.Wst.ToString();
                    a.Threshold = a.Thr >= 0 ? a.Thr.ToString() : "—";
                    a.RawText = RawDisplay(id, raw);
                    int lvl = 0;
                    if (a.Thr > 0 && a.Cur > 0 && a.Cur <= a.Thr) { lvl = 2; smartFailed = true; }
                    else if (IsCritical(id) && raw > 0 && !(r.IsSsd && id == 0xBB && raw < 10)) lvl = 1;
                    a.Status = lvl == 2 ? "Sub prag" : lvl == 1 ? "Atenție" : "OK";
                    a.StatusColor = lvl == 2 ? "#F87171" : lvl == 1 ? "#FBBF24" : "#4ADE80";
                    r.Attributes.Add(a);
                    byId[id] = a;
                }

                SmartAttr t;
                if (byId.TryGetValue(0xC2, out t) && (t.Raw & 0xFF) > 0 && (t.Raw & 0xFF) < 120) r.Temp = (int)(t.Raw & 0xFF);
                else if (byId.TryGetValue(0xBE, out t) && (t.Raw & 0xFF) > 0 && (t.Raw & 0xFF) < 120) r.Temp = (int)(t.Raw & 0xFF);
                if (byId.TryGetValue(0xC2, out t))
                {
                    int hi = (int)((t.Raw >> 32) & 0xFF);
                    if (hi > 0 && hi < 120 && hi >= r.Temp) r.TempMax = hi;
                }
                if (byId.TryGetValue(0x09, out t)) r.Hours = t.Raw & 0xFFFFFFFFL;
                if (byId.TryGetValue(0x0C, out t)) r.Cycles = t.Raw & 0xFFFFFFFFL;
                if (byId.TryGetValue(0x05, out t)) realloc = t.Raw & 0xFFFFFFFFL;
                if (byId.TryGetValue(0xC5, out t)) pending = t.Raw & 0xFFFFFFFFL;
                if (byId.TryGetValue(0xC6, out t)) uncorr = t.Raw & 0xFFFFFFFFL;
                if (byId.TryGetValue(0xC7, out t)) crc = t.Raw & 0xFFFFFFFFL;

                if (r.IsSsd)
                {
                    int[] lifeIds = new int[] { 0xE7, 0xB1, 0xE9, 0xCA, 0xA9 };
                    foreach (int lid in lifeIds)
                        if (byId.TryGetValue(lid, out t) && t.Cur > 0 && t.Cur <= 100) { r.LifeLeft = t.Cur; break; }
                    if (byId.TryGetValue(0xF1, out t) && t.Raw > 0 && (r.Title.IndexOf("Samsung", StringComparison.OrdinalIgnoreCase) >= 0))
                        written = FormatBytes(t.Raw * 512, false);
                }

                if (smartFailed)
                {
                    bad++; score = Math.Min(score, 15);
                    foreach (SmartAttr a in r.Attributes)
                        if (a.Status == "Sub prag") r.Reasons.Add("Atributul „" + a.Name + "” a coborât sub pragul de defectare stabilit de producător.");
                }
                if (realloc > 0)
                {
                    warn++;
                    score = Math.Min(score, 85 - (int)Math.Min(45, realloc / 4));
                    r.Reasons.Add(realloc + " sectoare realocate — zone defecte pe care discul le-a înlocuit cu rezerve." +
                        (realloc > 100 ? " Numărul e mare." : " Urmărește dacă numărul crește."));
                    if (realloc > 500) bad++;
                }
                if (pending > 0)
                {
                    warn++;
                    score = Math.Min(score, pending > 10 ? 30 : 55);
                    r.Reasons.Add(pending + " sectoare instabile, care încă nu s-au putut citi sigur — semn clar de deteriorare a suprafeței.");
                    if (pending > 10) bad++;
                }
                if (uncorr > 0)
                {
                    warn++;
                    score = Math.Min(score, 60);
                    r.Reasons.Add(uncorr + " sectoare necorectabile — date care nu s-au mai putut recupera din acele zone.");
                }
                if (byId.TryGetValue(0x0A, out t) && t.Raw > 0)
                {
                    warn++; score = Math.Min(score, 60);
                    r.Reasons.Add("Motorul a avut nevoie de " + t.Raw + " reîncercări la pornire — problemă mecanică.");
                }
                if (byId.TryGetValue(0xBB, out t) && t.Raw > 0 && !(r.IsSsd && t.Raw < 10))
                {
                    warn++; score = Math.Min(score, 70);
                    r.Reasons.Add(t.Raw + " erori necorectabile raportate de disc.");
                }
                if (crc > 0)
                    r.Reasons.Add(crc + " erori CRC de transfer — de obicei vine de la cablul SATA sau de la mufă, nu de la disc. Schimbă cablul dacă numărul crește.");
            }

            // ---- date de la Windows (completare / rezerva cand SMART nu poate fi citit)
            if (r.Temp < 0 && winTemp > 0 && winTemp < 120) r.Temp = winTemp;
            if (r.TempMax < 0 && winTempMax > 0 && winTempMax < 120) r.TempMax = winTempMax;
            if (r.Hours < 0 && winHours > 0) r.Hours = winHours;
            if (r.Cycles < 0 && winCycles > 0) r.Cycles = winCycles;
            if (r.LifeLeft < 0 && r.IsSsd && winWear > 0 && winWear <= 100) r.LifeLeft = 100 - winWear;
            if (attrs == null && nvme == null)
            {
                r.SourceText = "Date SMART indisponibile direct (frecvent la discuri pe USB sau RAID) — afișez ce raportează Windows";
                if (winReadErr > 0 || winWriteErr > 0)
                {
                    warn++; score = Math.Min(score, 60);
                    r.Reasons.Add("Windows raportează erori necorectate: " + Math.Max(winReadErr, 0) + " la citire, " + Math.Max(winWriteErr, 0) + " la scriere.");
                }
            }

            string wh = winHealth ?? "";
            if (wh.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase))
            {
                bad++; score = Math.Min(score, 15);
                r.Reasons.Add("Windows marchează discul ca nesănătos (predicție de defectare).");
            }
            else if (wh.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            {
                warn++; score = Math.Min(score, 60);
                r.Reasons.Add("Windows afișează un avertisment de sănătate pentru acest disc.");
            }

            // ---- uzura SSD
            if (r.LifeLeft >= 0)
            {
                score = Math.Min(score, Math.Max(r.LifeLeft, 5));
                if (r.LifeLeft < 10) { bad++; r.Reasons.Add("Uzura SSD-ului e aproape de limită: mai are " + r.LifeLeft + "% din viața estimată."); }
                else if (r.LifeLeft < 30) { warn++; r.Reasons.Add("SSD-ul a consumat o mare parte din viață: mai are " + r.LifeLeft + "%."); }
            }

            // ---- temperatura
            int hot = r.IsNvme ? 75 : r.IsSsd ? 65 : 52;
            int veryHot = r.IsNvme ? 85 : r.IsSsd ? 75 : 60;
            if (r.Temp >= veryHot) { warn++; score = Math.Max(0, score - 10); r.Reasons.Add("Temperatură foarte mare: " + r.Temp + " °C. Verifică răcirea/ventilația carcasei."); }
            else if (r.Temp >= hot) { score = Math.Max(0, score - 5); r.Reasons.Add("Temperatură ridicată: " + r.Temp + " °C (ideal sub " + hot + " °C)."); }

            // ---- vechime
            if (r.Hours > 0)
            {
                double years = r.Hours / 8766.0;
                if (!r.IsSsd && r.Hours > 45000) r.Reasons.Add("Discul are " + r.Hours.ToString("N0", RO) + " ore de funcționare (~" + years.ToString("0.#", RO) + " ani) — HDD-urile încep să cedeze mai des după această vârstă.");
            }

            // ---- verdict
            if (bad > 0) r.Level = 2;
            else if (warn > 0) r.Level = 1;
            else if (attrs != null || nvme != null) r.Level = 0;
            else if (wh.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) r.Level = 0;
            else r.Level = -1;

            if (r.Level == 2) score = Math.Min(score, 35);
            else if (r.Level == 1) score = Math.Min(Math.Max(score, 36), 75);
            r.Score = Math.Max(0, Math.Min(100, score));

            switch (r.Level)
            {
                case 0:
                    r.HealthText = "Bun"; r.HealthColor = "#4ADE80"; r.HealthBg = "#0B2A1F";
                    r.Verdict = attrs == null && nvme == null
                        ? "Windows raportează discul ca sănătos. Detaliile SMART nu sunt accesibile, așa că verdictul e mai puțin sigur."
                        : "Discul e sănătos și poate rula liniștit în continuare. Ține totuși o copie de rezervă a datelor importante.";
                    break;
                case 1:
                    r.HealthText = "Atenție"; r.HealthColor = "#FBBF24"; r.HealthBg = "#2A1E06";
                    r.Verdict = "Discul are primele semne de uzură sau de erori. Fă backup acum, pregătește înlocuirea și verifică din nou peste câteva săptămâni: dacă valorile cresc, schimbă-l.";
                    break;
                case 2:
                    r.HealthText = "Rău"; r.HealthColor = "#F87171"; r.HealthBg = "#2A0E0E";
                    r.Verdict = "Discul se poate defecta oricând. Copiază imediat datele importante pe alt disc și înlocuiește-l. Evită scanările lungi pe el.";
                    break;
                default:
                    r.HealthText = "Necunoscut"; r.HealthColor = "#94A3B8"; r.HealthBg = "#1E293B";
                    r.Verdict = "Nu am putut citi datele de sănătate. Se întâmplă des la discuri conectate prin adaptoare USB sau controlere RAID. Încearcă discul pe un port SATA direct.";
                    break;
            }
            r.ScoreText = r.Level < 0 ? "?" : r.Score.ToString();
            if (r.Reasons.Count == 0 && r.Level == 0) r.Reasons.Add("Nicio problemă detectată: fără sectoare defecte, fără erori și fără avertizări.");
            r.ReasonsText = "• " + string.Join("\n• ", r.Reasons.ToArray());

            // ---- texte pentru afisare
            r.LifeText = r.LifeLeft >= 0 ? r.LifeLeft + "%" : (r.IsSsd ? "—" : "n/a (HDD)");
            r.TempText = r.Temp >= 0 ? r.Temp + " °C" : "—";
            r.TempMaxText = r.TempMax >= 0 ? "maxim înregistrat " + r.TempMax + " °C" : "temperatura actuală";
            r.TempColor = r.Temp < 0 ? "#E6EAF2" : r.Temp >= veryHot ? "#F87171" : r.Temp >= hot ? "#FBBF24" : "#4ADE80";
            r.HoursText = r.Hours >= 0 ? r.Hours.ToString("N0", RO) + " h" : "—";
            r.HoursSub = r.Hours >= 0 ? "≈ " + (r.Hours / 8766.0).ToString("0.0", RO) + " ani de funcționare" : "ore de funcționare";
            r.CyclesText = r.Cycles >= 0 ? r.Cycles.ToString("N0", RO) : "—";
            r.WrittenText = written;

            if (r.IsNvme)
            {
                r.ErrorsText = mediaErr >= 0 ? mediaErr.ToString("N0", RO) : "—";
                r.ErrorsSub = "erori de integritate a datelor";
                r.ErrorsColor = mediaErr > 0 ? "#FBBF24" : "#4ADE80";
            }
            else if (attrs != null)
            {
                long bads = Math.Max(realloc, 0) + Math.Max(pending, 0) + Math.Max(uncorr, 0);
                r.ErrorsText = bads.ToString("N0", RO);
                r.ErrorsSub = "sectoare realocate / instabile / necorectabile";
                r.ErrorsColor = pending > 0 || uncorr > 0 ? "#F87171" : bads > 0 ? "#FBBF24" : "#4ADE80";
            }
            else
            {
                r.ErrorsText = "—";
                r.ErrorsSub = "sectoare defecte (indisponibil)";
                r.ErrorsColor = "#E6EAF2";
            }
            return r;
        }

        static void AddNvme(DiskReport r, string name, string val, int lvl)
        {
            SmartAttr a = new SmartAttr();
            a.Id = "";
            a.Name = name;
            a.Current = "";
            a.Worst = "";
            a.Threshold = "";
            a.RawText = val;
            a.Status = lvl == 2 ? "Critic" : lvl == 1 ? "Atenție" : "OK";
            a.StatusColor = lvl == 2 ? "#F87171" : lvl == 1 ? "#FBBF24" : "#4ADE80";
            r.Attributes.Add(a);
        }

        static bool IsCritical(int id)
        {
            return id == 0x05 || id == 0x0A || id == 0xBB || id == 0xC4 || id == 0xC5 || id == 0xC6;
        }

        static string RawDisplay(int id, long raw)
        {
            if (id == 0xC2 || id == 0xBE)
            {
                long cur = raw & 0xFF;
                long min = (raw >> 16) & 0xFF, max = (raw >> 32) & 0xFF;
                if (max > 0 && max < 120 && min <= cur) return cur + " °C (min " + min + ", max " + max + ")";
                return cur + " °C";
            }
            if (id == 0x09) return (raw & 0xFFFFFFFFL).ToString("N0", RO) + " h";
            return raw.ToString("N0", RO);
        }

        public static string FormatBytes(long b, bool decimalUnits)
        {
            if (b < 0) return "—";
            double k = decimalUnits ? 1000.0 : 1024.0;
            string[] u = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = b;
            int i = 0;
            while (v >= k && i < u.Length - 1) { v /= k; i++; }
            return v.ToString(v >= 100 ? "0" : "0.#", RO) + " " + u[i];
        }

        public static string AttrName(int id)
        {
            switch (id)
            {
                case 0x01: return "Rată erori de citire";
                case 0x02: return "Performanță transfer";
                case 0x03: return "Timp pornire motor";
                case 0x04: return "Porniri / opriri motor";
                case 0x05: return "Sectoare realocate";
                case 0x07: return "Rată erori poziționare";
                case 0x08: return "Performanță poziționare";
                case 0x09: return "Ore de funcționare";
                case 0x0A: return "Reîncercări pornire motor";
                case 0x0B: return "Reîncercări calibrare";
                case 0x0C: return "Cicluri de pornire";
                case 0x0D: return "Erori soft de citire";
                case 0x16: return "Nivel heliu";
                case 0x64: return "Ștergeri / programări (total)";
                case 0xA0: return "Sectoare necorectabile (SSD)";
                case 0xA1: return "Blocuri valide de rezervă";
                case 0xA3: return "Blocuri defecte inițiale";
                case 0xA4: return "Total ștergeri (TLC)";
                case 0xA5: return "Ștergeri maxime";
                case 0xA6: return "Ștergeri minime";
                case 0xA7: return "Ștergeri medii";
                case 0xA8: return "Erori PHY SATA";
                case 0xA9: return "Viață rămasă";
                case 0xAA: return "Blocuri de rezervă disponibile";
                case 0xAB: return "Erori de programare";
                case 0xAC: return "Erori de ștergere";
                case 0xAD: return "Nivelare uzură (medie)";
                case 0xAE: return "Opriri bruște";
                case 0xAF: return "Erori program (cel mai rău cip)";
                case 0xB0: return "Erori ștergere (cel mai rău cip)";
                case 0xB1: return "Nivel uzură";
                case 0xB2: return "Blocuri rezervă folosite (cip)";
                case 0xB3: return "Blocuri rezervă folosite";
                case 0xB4: return "Blocuri de rezervă nefolosite";
                case 0xB5: return "Erori de programare";
                case 0xB6: return "Erori de ștergere";
                case 0xB7: return "Reduceri viteză SATA";
                case 0xB8: return "Erori end-to-end";
                case 0xBB: return "Erori necorectabile raportate";
                case 0xBC: return "Expirări comenzi";
                case 0xBD: return "Scrieri la înălțime mare";
                case 0xBE: return "Temperatură flux de aer";
                case 0xBF: return "Șocuri / vibrații";
                case 0xC0: return "Opriri de urgență (retract)";
                case 0xC1: return "Cicluri parcare capete";
                case 0xC2: return "Temperatură";
                case 0xC3: return "ECC hardware recuperat";
                case 0xC4: return "Evenimente de realocare";
                case 0xC5: return "Sectoare instabile (în așteptare)";
                case 0xC6: return "Sectoare necorectabile";
                case 0xC7: return "Erori CRC (cablu/interfață)";
                case 0xC8: return "Rată erori de scriere";
                case 0xC9: return "Erori soft de citire";
                case 0xCA: return "Procent viață folosit / adrese";
                case 0xCC: return "Erori ECC soft corectate";
                case 0xCE: return "Înălțime de zbor capete";
                case 0xD2: return "Erori RAIN recuperate";
                case 0xDC: return "Deplasare platane";
                case 0xDF: return "Reîncercări încărcare capete";
                case 0xE0: return "Frecare încărcare capete";
                case 0xE1: return "Cicluri încărcare capete";
                case 0xE2: return "Timp încărcare capete";
                case 0xE6: return "Amplitudine capete (GMR)";
                case 0xE7: return "Viață rămasă SSD";
                case 0xE8: return "Rezervă disponibilă";
                case 0xE9: return "Indicator uzură / ore";
                case 0xEA: return "Statistici medii ștergere";
                case 0xEB: return "Opriri corecte / blocuri";
                case 0xF0: return "Ore de zbor capete";
                case 0xF1: return "Total date scrise";
                case 0xF2: return "Total date citite";
                case 0xF3: return "Total scris (extins)";
                case 0xF4: return "Total citit (extins)";
                case 0xF5: return "Viață rămasă (Micron)";
                case 0xF6: return "Total scrieri gazdă";
                case 0xF7: return "Pagini scrise de gazdă";
                case 0xF8: return "Pagini scrise intern";
                case 0xF9: return "Total scris NAND";
                case 0xFA: return "Reîncercări de citire";
                case 0xFE: return "Protecție la cădere liberă";
            }
            return "Atribut producător";
        }
    }
}
