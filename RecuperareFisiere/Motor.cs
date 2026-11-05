// Motor de recuperare - Info PC Electric
// Acces STRICT DOAR-CITIRE la discul sursa. Scris in C# 5 (compilat la pornire de Windows PowerShell).
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace InfoPcRecover
{
    public class FoundFile
    {
        public bool Selected { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        public string SizeText { get; set; }
        public string Folder { get; set; }
        public string Status { get; set; }
        public string StatusColor { get; set; }
        public string KindColor { get; set; }
        public string SavedPath { get; set; }
        public string Status2;

        public long RecordNo;
        public List<long[]> Runs;
        public byte[] Resident;
        public long ClusterSize;
    }

    // ---------------------------------------------------------------- citire bruta, doar-citire
    public class RawReader : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
            out long lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        public const int Block = 1 << 20;
        FileStream fs;
        public long Length;
        byte[] cache = new byte[Block];
        long cacheStart = -1;
        int cacheLen = 0;
        public int BadAreas;
        readonly object sync = new object();

        public RawReader(string path)
        {
            if (path.StartsWith(@"\\.\"))
            {
                // GENERIC_READ (0x80000000) = doar citire. Nu cerem niciodata drept de scriere.
                SafeFileHandle h = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "Nu pot deschide discul " + path + ". Porneste aplicatia ca Administrator.");
                long len; uint ret;
                if (!DeviceIoControl(h, 0x0007405C, IntPtr.Zero, 0, out len, 8, out ret, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    throw new System.ComponentModel.Win32Exception(err, "Nu pot afla dimensiunea discului.");
                }
                Length = len;
                fs = new FileStream(h, FileAccess.Read, 4096, false);
            }
            else
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                Length = fs.Length;
            }
        }

        void LoadBlock(long start)
        {
            int want = (int)Math.Min((long)Block, Length - start);
            int got = 0;
            try
            {
                fs.Position = start;
                while (got < want)
                {
                    int n = fs.Read(cache, got, want - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            catch (Exception)
            {
                got = ReadSlow(start, want);
            }
            if (got < want) Array.Clear(cache, got, want - got);
            cacheStart = start;
            cacheLen = want;
        }

        // zona cu sectoare defecte: citim pe bucati mici si inlocuim ce nu se poate citi cu zero
        int ReadSlow(long start, int want)
        {
            const int step = 4096;
            for (int off = 0; off < want; off += step)
            {
                int c = Math.Min(step, want - off);
                try
                {
                    fs.Position = start + off;
                    int g = 0;
                    while (g < c)
                    {
                        int n = fs.Read(cache, off + g, c - g);
                        if (n <= 0) break;
                        g += n;
                    }
                    if (g < c) Array.Clear(cache, off + g, c - g);
                }
                catch (Exception)
                {
                    Array.Clear(cache, off, c);
                    BadAreas++;
                }
            }
            return want;
        }

        public int Read(long pos, byte[] buf, int off, int count)
        {
            lock (sync)
            {
                if (pos < 0 || pos >= Length || count <= 0) return 0;
                if (pos + count > Length) count = (int)(Length - pos);
                int done = 0;
                while (done < count)
                {
                    long p = pos + done;
                    long bs = p - (p % Block);
                    if (bs != cacheStart) LoadBlock(bs);
                    int inBlock = (int)(p - bs);
                    int c = Math.Min(count - done, cacheLen - inBlock);
                    if (c <= 0) break;
                    Buffer.BlockCopy(cache, inBlock, buf, off + done, c);
                    done += c;
                }
                return done;
            }
        }

        public void Dispose()
        {
            if (fs != null) fs.Dispose();
        }
    }

    // cursor secvential peste RawReader
    public class Seq
    {
        RawReader r;
        byte[] buf = new byte[65536];
        long bufStart = -1;
        int bufLen = 0;
        public long Pos;

        public Seq(RawReader reader, long pos) { r = reader; Pos = pos; }

        public int Next()
        {
            if (bufStart < 0 || Pos < bufStart || Pos >= bufStart + bufLen)
            {
                bufStart = Pos;
                bufLen = r.Read(Pos, buf, 0, buf.Length);
                if (bufLen <= 0) { bufStart = -1; bufLen = 0; return -1; }
            }
            int b = buf[Pos - bufStart];
            Pos++;
            return b;
        }
    }

    class RecInfo
    {
        public bool InUse, IsDir, HasData, Unsupported;
        public long BaseRef, Parent = -1, DataSize;
        public string Name;
        public int NameSpace = -1;
        public List<long[]> Runs;
        public byte[] Resident;
    }

    class DirEntry
    {
        public string Name;
        public long Parent;
    }

    // ---------------------------------------------------------------- motorul
    public class Engine
    {
        public string SourcePath;
        public string DestDir;
        public bool WantPdf, WantWord, WantJpg, WantExcel, WantPpt;
        public bool SkipSmallJpg = true;
        public bool FreeSpaceOnly = true;

        public volatile bool CancelRequested;
        public volatile bool Finished = true;
        public volatile string Phase = "";
        public volatile string Error;
        public long Done, Total;
        public DateTime StartTime;
        public int CountPdf, CountWord, CountJpg, CountExcel, CountPpt, Duplicates, Recovered, Failed;
        public int Trimmed, InRecycleBin;
        public Dictionary<string, int> SkippedKinds = new Dictionary<string, int>();
        public ConcurrentQueue<FoundFile> NewItems = new ConcurrentQueue<FoundFile>();

        RawReader rd;
        HashSet<string> hashes = new HashSet<string>();
        int seq = 0;

        // NTFS
        long cluster, mftLcn;
        int recSize;
        byte[] bitmap;

        public int BadAreas { get { return rd == null ? 0 : rd.BadAreas; } }

        public void StartQuick() { Run(QuickScan); }
        public void StartDeep() { Run(DeepScan); }
        public void StartRecover(FoundFile[] items)
        {
            List<FoundFile> l = new List<FoundFile>(items);
            Run(delegate { RecoverList(l); });
        }

        void Run(ThreadStart work)
        {
            Finished = false;
            Error = null;
            CancelRequested = false;
            Done = 0;
            StartTime = DateTime.Now;
            Thread t = new Thread(delegate()
            {
                try
                {
                    if (rd == null) rd = new RawReader(SourcePath);
                    work();
                }
                catch (Exception ex) { Error = ex.Message; }
                finally { Finished = true; }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Close()
        {
            if (rd != null) { rd.Dispose(); rd = null; }
        }

        // ------------------------------------------------------------ utilitare
        public static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024L * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }

        public static string KindFromName(string name)
        {
            int d = name.LastIndexOf('.');
            if (d < 0) return null;
            string e = name.Substring(d + 1).ToLowerInvariant();
            switch (e)
            {
                case "pdf": return "PDF";
                case "doc": case "docx": case "docm": case "dot": case "dotx": case "rtf": case "odt": return "Word";
                case "jpg": case "jpeg": case "jpe": case "jfif": return "Poze";
                case "xls": case "xlsx": case "xlsm": case "ods": case "csv": return "Excel";
                case "ppt": case "pptx": case "pps": case "ppsx": case "odp": return "PowerPoint";
            }
            return null;
        }

        static string KindColorOf(string k)
        {
            switch (k)
            {
                case "PDF": return "#F87171";
                case "Word": return "#60A5FA";
                case "Poze": return "#34D399";
                case "Excel": return "#4ADE80";
                case "PowerPoint": return "#FB923C";
            }
            return "#94A3B8";
        }

        bool Wanted(string kind)
        {
            if (kind == null) return false;
            switch (kind)
            {
                case "PDF": return WantPdf;
                case "Word": return WantWord;
                case "Poze": return WantJpg;
                case "Excel": return WantExcel;
                case "PowerPoint": return WantPpt;
            }
            return false;
        }

        void Count(string kind)
        {
            switch (kind)
            {
                case "PDF": Interlocked.Increment(ref CountPdf); break;
                case "Word": Interlocked.Increment(ref CountWord); break;
                case "Poze": Interlocked.Increment(ref CountJpg); break;
                case "Excel": Interlocked.Increment(ref CountExcel); break;
                case "PowerPoint": Interlocked.Increment(ref CountPpt); break;
            }
        }

        static string SafeName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "fisier";
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in n) sb.Append(Array.IndexOf(bad, c) >= 0 || c < 32 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length == 0) s = "fisier";
            if (s.Length > 100)
            {
                int d = s.LastIndexOf('.');
                string ext = (d > 0 && s.Length - d <= 6) ? s.Substring(d) : "";
                s = s.Substring(0, 100 - ext.Length) + ext;
            }
            return s;
        }

        static string UniquePath(string p)
        {
            if (!File.Exists(p)) return p;
            string dir = Path.GetDirectoryName(p), name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
            for (int i = 2; i < 100000; i++)
            {
                string c = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(c)) return c;
            }
            return Path.Combine(dir, name + "_" + Guid.NewGuid().ToString("N") + ext);
        }

        static bool IsZero(byte[] b, int off, int len)
        {
            for (int i = 0; i < len; i++) if (b[off + i] != 0) return false;
            return true;
        }

        // ============================================================ NTFS
        static bool Fixup(byte[] r)
        {
            int usaOff = BitConverter.ToUInt16(r, 4), usaCnt = BitConverter.ToUInt16(r, 6);
            if (usaCnt < 2 || usaOff + usaCnt * 2 > r.Length) return false;
            int stride = r.Length / (usaCnt - 1);
            if (stride < 256) return false;
            for (int i = 1; i < usaCnt; i++)
            {
                int end = i * stride - 2;
                if (end + 2 > r.Length) break;
                if (r[end] != r[usaOff] || r[end + 1] != r[usaOff + 1]) return false;
                r[end] = r[usaOff + 2 * i];
                r[end + 1] = r[usaOff + 2 * i + 1];
            }
            return true;
        }

        static List<long[]> DecodeRuns(byte[] r, int p, int end)
        {
            List<long[]> runs = new List<long[]>();
            long lcn = 0;
            while (p < end)
            {
                int h = r[p];
                if (h == 0) break;
                int lenB = h & 0xF, offB = h >> 4;
                if (lenB == 0 || lenB > 8 || offB > 8 || p + 1 + lenB + offB > end) break;
                long count = 0;
                for (int i = 0; i < lenB; i++) count |= (long)r[p + 1 + i] << (8 * i);
                if (offB == 0)
                {
                    runs.Add(new long[] { -1, count });
                }
                else
                {
                    long delta = 0;
                    for (int i = 0; i < offB; i++) delta |= (long)r[p + 1 + lenB + i] << (8 * i);
                    if ((r[p + lenB + offB] & 0x80) != 0)
                        for (int i = offB; i < 8; i++) delta |= (long)0xFF << (8 * i);
                    lcn += delta;
                    runs.Add(new long[] { lcn, count });
                }
                p += 1 + lenB + offB;
            }
            return runs;
        }

        static RecInfo ParseRecord(byte[] r)
        {
            if (r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E') return null;
            RecInfo ri = new RecInfo();
            int flags = BitConverter.ToUInt16(r, 0x16);
            ri.InUse = (flags & 1) != 0;
            ri.IsDir = (flags & 2) != 0;
            ri.BaseRef = BitConverter.ToInt64(r, 0x20) & 0xFFFFFFFFFFFFL;
            int off = BitConverter.ToUInt16(r, 0x14);
            int guard = 0;
            while (off + 16 <= r.Length && guard++ < 64)
            {
                uint type = BitConverter.ToUInt32(r, off);
                if (type == 0xFFFFFFFF) break;
                int len = BitConverter.ToInt32(r, off + 4);
                if (len < 16 || off + len > r.Length) break;
                bool nonRes = r[off + 8] != 0;
                int nameLen = r[off + 9];
                if (type == 0x30 && !nonRes)
                {
                    int cOff = off + BitConverter.ToUInt16(r, off + 0x14);
                    if (cOff + 0x42 <= off + len)
                    {
                        long parent = BitConverter.ToInt64(r, cOff) & 0xFFFFFFFFFFFFL;
                        int nl = r[cOff + 0x40];
                        int ns = r[cOff + 0x41];
                        if (cOff + 0x42 + nl * 2 <= off + len)
                        {
                            string nm = Encoding.Unicode.GetString(r, cOff + 0x42, nl * 2);
                            if (ri.Name == null || ri.NameSpace == 2)
                            {
                                ri.Name = nm;
                                ri.Parent = parent;
                                ri.NameSpace = ns;
                            }
                        }
                    }
                }
                else if (type == 0x80 && nameLen == 0)
                {
                    if (!nonRes)
                    {
                        int cl = BitConverter.ToInt32(r, off + 0x10);
                        int co = BitConverter.ToUInt16(r, off + 0x14);
                        if (cl >= 0 && off + co + cl <= r.Length)
                        {
                            ri.HasData = true;
                            ri.Resident = new byte[cl];
                            Buffer.BlockCopy(r, off + co, ri.Resident, 0, cl);
                            ri.DataSize = cl;
                        }
                    }
                    else if (len >= 0x40)
                    {
                        long startVcn = BitConverter.ToInt64(r, off + 0x10);
                        if (startVcn == 0)
                        {
                            ri.HasData = true;
                            int fl = BitConverter.ToUInt16(r, off + 0x0C);
                            ri.Unsupported = (fl & 0x40FF) != 0; // comprimat / criptat
                            ri.DataSize = BitConverter.ToInt64(r, off + 0x30);
                            int ro = BitConverter.ToUInt16(r, off + 0x20);
                            ri.Runs = DecodeRuns(r, off + ro, off + len);
                        }
                    }
                }
                off += len;
            }
            return ri;
        }

        bool OpenNtfs()
        {
            byte[] boot = new byte[512];
            if (rd.Read(0, boot, 0, 512) < 512) return false;
            if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS") return false;
            int bps = BitConverter.ToUInt16(boot, 0x0B);
            int spc = boot[0x0D];
            if (spc > 0x80) spc = 1 << (256 - spc);
            if (bps < 256 || spc < 1) return false;
            cluster = (long)bps * spc;
            mftLcn = BitConverter.ToInt64(boot, 0x30);
            int cpr = (sbyte)boot[0x40];
            recSize = cpr > 0 ? (int)(cpr * cluster) : (1 << -cpr);
            return recSize >= 512 && recSize <= 65536;
        }

        RecInfo ReadSystemRecord(int n)
        {
            byte[] rec = new byte[recSize];
            if (rd.Read(mftLcn * cluster + (long)n * recSize, rec, 0, recSize) < recSize) return null;
            if (!Fixup(rec)) return null;
            return ParseRecord(rec);
        }

        long ReadRuns(List<long[]> runs, long size, Stream outS, byte[] buf, bool countProgress)
        {
            long remaining = size, written = 0;
            foreach (long[] run in runs)
            {
                if (remaining <= 0 || CancelRequested) break;
                long bytes = Math.Min(run[1] * cluster, remaining);
                long p = 0;
                while (p < bytes)
                {
                    int c = (int)Math.Min(buf.Length, bytes - p);
                    int n;
                    if (run[0] < 0) { Array.Clear(buf, 0, c); n = c; }
                    else
                    {
                        n = rd.Read(run[0] * cluster + p, buf, 0, c);
                        if (n < c) { Array.Clear(buf, Math.Max(n, 0), c - Math.Max(n, 0)); n = c; }
                    }
                    outS.Write(buf, 0, n);
                    p += n;
                    written += n;
                    if (countProgress) Interlocked.Add(ref Done, n);
                }
                remaining -= bytes;
            }
            return written;
        }

        void LoadBitmap()
        {
            bitmap = null;
            RecInfo bm = ReadSystemRecord(6);
            if (bm == null || bm.Runs == null || bm.DataSize <= 0 || bm.DataSize > (512L << 20)) return;
            using (MemoryStream ms = new MemoryStream())
            {
                ReadRuns(bm.Runs, bm.DataSize, ms, new byte[1 << 20], false);
                bitmap = ms.ToArray();
            }
        }

        bool ClusterUsed(long c)
        {
            if (bitmap == null) return false;
            long byteIdx = c >> 3;
            if (byteIdx < 0 || byteIdx >= bitmap.Length) return false;
            return (bitmap[byteIdx] & (1 << (int)(c & 7))) != 0;
        }

        bool RangeFullyUsed(long start, long len)
        {
            if (bitmap == null) return false;
            long c0 = start / cluster, c1 = (start + len - 1) / cluster;
            for (long c = c0; c <= c1; c++)
            {
                if ((c & 7) == 0 && c + 7 <= c1)
                {
                    long bi = c >> 3;
                    if (bi < bitmap.Length && bitmap[bi] == 0xFF) { c += 7; continue; }
                }
                if (!ClusterUsed(c)) return false;
            }
            return true;
        }

        static bool SignatureMatches(string kind, string name, byte[] b, int n)
        {
            if (n < 8) return false;
            string ext = name.Substring(name.LastIndexOf('.') + 1).ToLowerInvariant();
            if (ext == "pdf") return b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F';
            if (kind == "Poze") return b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
            if (ext == "rtf") return b[0] == '{' && b[1] == '\\' && b[2] == 'r';
            if (ext == "csv") return true;
            if (ext.EndsWith("x") || ext == "docm" || ext == "xlsm" || ext.StartsWith("od"))
                return b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4;
            return b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0;
        }

        void QuickScan()
        {
            Phase = "Citesc tabela de fisiere (MFT)...";
            if (!OpenNtfs()) throw new Exception("Partitia nu este NTFS sau sectorul de boot e deteriorat. Foloseste Cautarea profunda.");
            RecInfo r0 = ReadSystemRecord(0);
            if (r0 == null || r0.Runs == null || r0.DataSize <= 0)
                throw new Exception("Tabela de fisiere (MFT) e deteriorata. Foloseste Cautarea profunda.");

            Total = r0.DataSize;
            Dictionary<long, DirEntry> dirs = new Dictionary<long, DirEntry>();
            List<FoundFile> cands = new List<FoundFile>();
            List<KeyValuePair<long, RecInfo>> recycle = new List<KeyValuePair<long, RecInfo>>();
            Dictionary<FoundFile, long> parentOf = new Dictionary<FoundFile, long>();
            byte[] chunk = new byte[1 << 20];
            byte[] rec = new byte[recSize];
            long recNo = 0, mftLeft = r0.DataSize;

            foreach (long[] run in r0.Runs)
            {
                if (CancelRequested || mftLeft <= 0) break;
                long runBytes = Math.Min(run[1] * cluster, mftLeft);
                if (run[0] < 0) { recNo += runBytes / recSize; mftLeft -= runBytes; continue; }
                long p = 0;
                while (p < runBytes && !CancelRequested)
                {
                    int c = (int)Math.Min(chunk.Length, runBytes - p);
                    c -= c % recSize;
                    if (c <= 0) break;
                    int n = rd.Read(run[0] * cluster + p, chunk, 0, c);
                    for (int o = 0; o + recSize <= n; o += recSize, recNo++)
                    {
                        Buffer.BlockCopy(chunk, o, rec, 0, recSize);
                        if (!Fixup(rec)) continue;
                        RecInfo ri;
                        try { ri = ParseRecord(rec); } catch (Exception) { continue; }
                        if (ri == null || ri.BaseRef != 0 || ri.Name == null) continue;
                        if (ri.IsDir)
                        {
                            DirEntry de = new DirEntry();
                            de.Name = ri.Name;
                            de.Parent = ri.Parent;
                            dirs[recNo] = de;
                            continue;
                        }
                        if (!ri.HasData || ri.DataSize <= 0) continue;
                        if (ri.InUse)
                        {
                            // Cosul de reciclare: $Rxxxxxx.ext = continutul, $Ixxxxxx.ext = numele original
                            if (ri.Name.Length > 2 && ri.Name[0] == '$' && (ri.Name[1] == 'R' || ri.Name[1] == 'I'))
                            {
                                RecInfo cp = ri;
                                recycle.Add(new KeyValuePair<long, RecInfo>(recNo, cp));
                            }
                            continue;
                        }
                        string kind = KindFromName(ri.Name);
                        if (kind == null) continue;
                        if (!Wanted(kind))
                        {
                            int sk;
                            SkippedKinds.TryGetValue(kind, out sk);
                            SkippedKinds[kind] = sk + 1;
                            continue;
                        }
                        FoundFile f = new FoundFile();
                        f.Name = ri.Name;
                        f.Kind = kind;
                        f.KindColor = KindColorOf(kind);
                        f.Size = ri.DataSize;
                        f.SizeText = FormatSize(ri.DataSize);
                        f.RecordNo = recNo;
                        f.Runs = ri.Runs;
                        f.Resident = ri.Resident;
                        f.ClusterSize = cluster;
                        if (ri.Unsupported) { f.Status = "Comprimat/criptat"; f.StatusColor = "#F59E0B"; f.Runs = null; f.Resident = null; }
                        cands.Add(f);
                        parentOf[f] = ri.Parent;
                    }
                    p += c;
                    Interlocked.Add(ref Done, c);
                }
                mftLeft -= runBytes;
            }

            AddRecycleBin(recycle, dirs, cands, parentOf);

            Phase = "Verific starea fisierelor gasite...";
            Done = 0;
            Total = cands.Count;
            byte[] head = new byte[512];
            int i2 = 0;
            foreach (FoundFile f in cands)
            {
                if (CancelRequested) break;
                if (f.Folder == null) f.Folder = BuildPath(dirs, parentOf[f]);
                if (f.Status == null)
                {
                    int n = 0;
                    if (f.Resident != null) { n = Math.Min(512, f.Resident.Length); Buffer.BlockCopy(f.Resident, 0, head, 0, n); }
                    else if (f.Runs != null && f.Runs.Count > 0 && f.Runs[0][0] >= 0) n = rd.Read(f.Runs[0][0] * cluster, head, 0, 512);
                    bool okSig = SignatureMatches(f.Kind, f.Name, head, n);
                    if (f.Runs == null && f.Resident == null) { f.Status = "Fara date"; f.StatusColor = "#EF4444"; }
                    else if (okSig)
                    {
                        string problem = CheckIntegrity(f);
                        if (problem == null) { f.Status = f.Status2 ?? "Bun"; f.StatusColor = "#22C55E"; f.Selected = true; }
                        else { f.Status = problem; f.StatusColor = "#F59E0B"; if (problem.IndexOf("TRIM") >= 0) Interlocked.Increment(ref Trimmed); }
                    }
                    else if (n > 0 && IsZero(head, 0, n) && ZeroedOnDisk(f)) { f.Status = "Golit de SSD (TRIM)"; f.StatusColor = "#EF4444"; Interlocked.Increment(ref Trimmed); }
                    else { f.Status = "Probabil suprascris"; f.StatusColor = "#EF4444"; }
                }
                Count(f.Kind);
                NewItems.Enqueue(f);
                i2++;
                Done = i2;
            }
            Phase = "Gata";
        }

        // verifica tot fisierul, nu doar inceputul: zone golite (TRIM/suprascrise) si finalul corect pe tip
        string CheckIntegrity(FoundFile f)
        {
            if (f.Resident != null) return null;
            if (f.Runs == null) return null;
            long size = f.Size;
            byte[] buf = new byte[1 << 16];
            int zeroBlocks = 0, blocks = 0;
            long pos = 0;
            byte[] tail = new byte[(int)Math.Min(size, 65536)];
            long sampleStep = size <= (32L << 20) ? 1 : Math.Max(1, (size / buf.Length) / 256); // fisiere mari: ~256 mostre
            long idx = 0;
            foreach (long[] run in f.Runs)
            {
                long bytes = Math.Min(run[1] * cluster, size - pos);
                if (bytes <= 0) break;
                if (run[0] < 0) { pos += bytes; continue; }   // zona rara (sparse), normala
                for (long o = 0; o < bytes; o += buf.Length, idx++)
                {
                    int c = (int)Math.Min(buf.Length, bytes - o);
                    bool inTail = pos + o + c > size - tail.Length;
                    if (!inTail && idx % sampleStep != 0) continue;
                    int n = rd.Read(run[0] * cluster + o, buf, 0, c);
                    if (n <= 0) return "Necitibil (sector defect)";
                    for (int b = 0; b + 4096 <= n; b += 4096) { blocks++; if (IsZero(buf, b, 4096)) zeroBlocks++; }
                    if (inTail)
                    {
                        long tStart = size - tail.Length;
                        for (int k = 0; k < n; k++)
                        {
                            long ap = pos + o + k;
                            if (ap >= tStart && ap < size) tail[ap - tStart] = buf[k];
                        }
                    }
                    if (CancelRequested) return null;
                }
                pos += bytes;
            }
            string ext = f.Name.Substring(f.Name.LastIndexOf('.') + 1).ToLowerInvariant();
            // in JPG, Office (zip) si PDF un bloc de 4 KB complet gol practic nu apare: e semn de zona golita/suprascrisa
            bool compressed = ext == "jpg" || ext == "jpeg" || ext == "jpe" || ext == "jfif" || ext.EndsWith("x") || ext == "docm" || ext == "xlsm" || ext == "pdf";
            if (compressed && zeroBlocks > 0)
                return zeroBlocks * 2 >= blocks ? "Golit de SSD (TRIM)" : "Parțial golit (TRIM)";
            int tl = tail.Length;
            while (tl > 0 && tail[tl - 1] == 0) tl--;     // zerouri de umplutura la final
            if (tl == 0) return "Golit de SSD (TRIM)";
            string t = Encoding.ASCII.GetString(tail, Math.Max(0, tl - 2048), Math.Min(tl, 2048));
            if (ext == "pdf")
            {
                if (t.IndexOf("%%EOF") < 0) return zeroBlocks > 0 ? "Parțial golit (TRIM)" : "Incomplet (lipsește finalul)";
                if (zeroBlocks * 4 > blocks) return "Parțial golit (TRIM)";
            }
            else if (compressed && ext != "pdf" && ext != "jpg" && ext != "jpeg" && ext != "jpe" && ext != "jfif")
            {
                bool eocd = false;
                // sfarsitul arhivei ZIP se termina chiar cu zerouri (lungimea comentariului), deci cautam in toata coada
                for (int k = tail.Length - 22; k >= 0 && !eocd; k--)
                    if (tail[k] == 0x50 && tail[k + 1] == 0x4B && tail[k + 2] == 5 && tail[k + 3] == 6) eocd = true;
                if (!eocd) return "Incomplet (lipsește finalul)";
            }
            else if (ext == "jpg" || ext == "jpeg" || ext == "jpe" || ext == "jfif")
            {
                bool end = false;
                for (int k = tl - 2; k >= Math.Max(0, tl - 64) && !end; k--) if (tail[k] == 0xFF && tail[k + 1] == 0xD9) end = true;
                if (!end) return "Incomplet (lipsește finalul)";
            }
            else if (zeroBlocks * 4 > blocks) return "Parțial golit (TRIM)";
            return null;
        }

        // verifica inca 3 zone din fisier: daca toate sunt zero, SSD-ul a golit fisierul (TRIM)
        bool ZeroedOnDisk(FoundFile f)
        {
            if (f.Runs == null) return false;
            byte[] b = new byte[4096];
            int checks = 0;
            foreach (long[] run in f.Runs)
            {
                if (run[0] < 0) continue;
                long[] offs = new long[] { 0, run[1] / 2, Math.Max(0, run[1] - 1) };
                foreach (long o in offs)
                {
                    int n = rd.Read((run[0] + o) * cluster, b, 0, b.Length);
                    if (n > 0 && !IsZero(b, 0, n)) return false;
                    if (++checks >= 6) return true;
                }
            }
            return checks > 0;
        }

        void AddRecycleBin(List<KeyValuePair<long, RecInfo>> recycle, Dictionary<long, DirEntry> dirs, List<FoundFile> cands, Dictionary<FoundFile, long> parentOf)
        {
            Dictionary<string, string> original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<long, RecInfo> kv in recycle)
            {
                RecInfo ri = kv.Value;
                if (ri.Name[1] != 'I' || ri.Resident == null || ri.Resident.Length < 30) continue;
                try
                {
                    byte[] d = ri.Resident;
                    long ver = BitConverter.ToInt64(d, 0);
                    string path = null;
                    if (ver == 2 && d.Length >= 28)
                    {
                        int len = BitConverter.ToInt32(d, 24);
                        if (len > 0 && 28 + len * 2 <= d.Length) path = Encoding.Unicode.GetString(d, 28, len * 2).TrimEnd('\0');
                    }
                    else if (ver == 1 && d.Length >= 24 + 520) path = Encoding.Unicode.GetString(d, 24, 520).Split('\0')[0];
                    if (!string.IsNullOrEmpty(path)) original[ri.Name.Substring(2)] = path;
                }
                catch (Exception) { }
            }
            foreach (KeyValuePair<long, RecInfo> kv in recycle)
            {
                RecInfo ri = kv.Value;
                if (ri.Name[1] != 'R') continue;
                string orig;
                if (!original.TryGetValue(ri.Name.Substring(2), out orig)) continue;
                string name = Path.GetFileName(orig);
                string kind = KindFromName(name);
                if (kind == null) continue;
                if (!Wanted(kind))
                {
                    int sk;
                    SkippedKinds.TryGetValue(kind, out sk);
                    SkippedKinds[kind] = sk + 1;
                    continue;
                }
                FoundFile f = new FoundFile();
                f.Name = name;
                f.Kind = kind;
                f.KindColor = KindColorOf(kind);
                f.Size = ri.DataSize;
                f.SizeText = FormatSize(ri.DataSize);
                f.RecordNo = kv.Key;
                f.Runs = ri.Runs;
                f.Resident = ri.Resident;
                f.ClusterSize = cluster;
                f.Folder = "Coșul de reciclare (era în " + (Path.GetDirectoryName(orig) ?? "") + ")";
                f.Status2 = "În Coșul de reciclare";
                if (ri.Unsupported) { f.Status = "Comprimat/criptat"; f.StatusColor = "#F59E0B"; f.Runs = null; f.Resident = null; }
                cands.Add(f);
                parentOf[f] = ri.Parent;
                Interlocked.Increment(ref InRecycleBin);
            }
        }

        static string BuildPath(Dictionary<long, DirEntry> dirs, long parent)
        {
            List<string> parts = new List<string>();
            long cur = parent;
            for (int i = 0; i < 64; i++)
            {
                if (cur == 5) break;
                DirEntry de;
                if (!dirs.TryGetValue(cur, out de)) { parts.Add("(dosar necunoscut)"); break; }
                parts.Add(de.Name);
                if (de.Parent == cur) break;
                cur = de.Parent;
            }
            parts.Reverse();
            return "\\" + string.Join("\\", parts.ToArray());
        }

        void RecoverList(List<FoundFile> items)
        {
            Phase = "Recuperez fisierele selectate...";
            if (cluster == 0 && !OpenNtfs()) throw new Exception("Nu pot citi partitia NTFS.");
            long tot = 0;
            foreach (FoundFile f in items) tot += f.Size;
            Total = Math.Max(tot, 1);
            Done = 0;
            byte[] buf = new byte[1 << 20];
            string baseDir = Path.Combine(DestDir, "Recuperate_cu_nume");
            foreach (FoundFile f in items)
            {
                if (CancelRequested) break;
                if (f.Runs == null && f.Resident == null) { Interlocked.Add(ref Done, f.Size); continue; }
                string target = null;
                try
                {
                    string rel = f.Folder ?? "";
                    StringBuilder sb = new StringBuilder();
                    foreach (string part in rel.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (sb.Length > 0) sb.Append('\\');
                        sb.Append(SafeName(part));
                    }
                    string dir = Path.Combine(baseDir, sb.ToString());
                    if (dir.Length > 200) dir = Path.Combine(baseDir, "_cai_lungi");
                    Directory.CreateDirectory(dir);
                    target = UniquePath(Path.Combine(dir, SafeName(f.Name)));
                    using (FileStream o = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        if (f.Resident != null) { o.Write(f.Resident, 0, f.Resident.Length); Interlocked.Add(ref Done, f.Resident.Length); }
                        else ReadRuns(f.Runs, f.Size, o, buf, true);
                    }
                    f.SavedPath = target;
                    if (f.Status == "Bun" || f.Status == "În Coșul de reciclare") f.Status = "Recuperat";
                    else f.Status = "Recuperat (verifica-l)";
                    f.StatusColor = f.Status == "Recuperat" ? "#22C55E" : "#F59E0B";
                    Interlocked.Increment(ref Recovered);
                }
                catch (Exception ex)
                {
                    f.Status = "Eroare: " + ex.Message;
                    f.StatusColor = "#EF4444";
                    Interlocked.Increment(ref Failed);
                }
            }
            Phase = "Gata";
        }

        // ============================================================ cautare profunda (dupa semnatura)
        public static int Detect(byte[] b, int i)
        {
            if (b[i] == 0xFF && b[i + 1] == 0xD8 && b[i + 2] == 0xFF && b[i + 3] >= 0xC0) return 1;
            if (b[i] == 0x25 && b[i + 1] == 0x50 && b[i + 2] == 0x44 && b[i + 3] == 0x46 && b[i + 4] == 0x2D) return 2;
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 3 && b[i + 3] == 4) return 3;
            if (b[i] == 0xD0 && b[i + 1] == 0xCF && b[i + 2] == 0x11 && b[i + 3] == 0xE0 &&
                b[i + 4] == 0xA1 && b[i + 5] == 0xB1 && b[i + 6] == 0x1A && b[i + 7] == 0xE1) return 4;
            return 0;
        }

        void DeepScan()
        {
            Phase = "Pregatesc scanarea...";
            bitmap = null;
            if (FreeSpaceOnly && OpenNtfs())
            {
                try { LoadBitmap(); } catch (Exception) { bitmap = null; }
            }
            Total = rd.Length;
            Done = 0;
            Phase = bitmap != null ? "Scanez spatiul liber al partitiei..." : "Scanez tot discul, sector cu sector...";
            byte[] sb = new byte[RawReader.Block];
            long pos = 0, skipUntil = 0;
            while (pos < rd.Length && !CancelRequested)
            {
                long blockLen = Math.Min((long)sb.Length, rd.Length - pos);
                if (bitmap != null && RangeFullyUsed(pos, blockLen))
                {
                    pos += blockLen;
                    Done = pos;
                    continue;
                }
                int n = rd.Read(pos, sb, 0, sb.Length);
                if (n <= 0) break;
                for (int i = 0; i + 16 <= n; i += 512)
                {
                    long abs = pos + i;
                    if (abs < skipUntil) continue;
                    if (bitmap != null && ClusterUsed(abs / cluster)) continue;
                    int t = Detect(sb, i);
                    if (t == 0) continue;
                    long end = TryCarve(t, abs);
                    if (end > abs) skipUntil = ((end + 511) / 512) * 512;
                    if (CancelRequested) break;
                }
                pos = Math.Max(pos + n, skipUntil);
                Done = Math.Min(pos, rd.Length);
            }
            Phase = "Gata";
        }

        long TryCarve(int t, long abs)
        {
            try
            {
                string ext;
                if (t == 1 && WantJpg)
                {
                    long len = CarveJpeg(abs, 80L << 20);
                    if (len > 0)
                    {
                        if (!(SkipSmallJpg && len < 20 * 1024)) Save(abs, len, "Poze", ".jpg");
                        return abs + len;
                    }
                }
                else if (t == 2 && WantPdf)
                {
                    long len = CarvePdf(abs, 400L << 20);
                    if (len > 0) { Save(abs, len, "PDF", ".pdf"); return abs + len; }
                }
                else if (t == 3 && (WantWord || WantExcel || WantPpt))
                {
                    long len = CarveZip(abs, 400L << 20, out ext);
                    if (len > 0)
                    {
                        string k = KindFromName("x" + ext);
                        if (Wanted(k)) Save(abs, len, k, ext);
                        return abs + len;
                    }
                }
                else if (t == 4 && (WantWord || WantExcel || WantPpt))
                {
                    long len = CarveOle(abs, 400L << 20, out ext);
                    if (len > 0)
                    {
                        string k = KindFromName("x" + ext);
                        if (Wanted(k)) Save(abs, len, k, ext);
                        return abs + len;
                    }
                }
            }
            catch (Exception) { }
            return 0;
        }

        public long CarveJpeg(long start, long max)
        {
            Seq s = new Seq(rd, start + 2);
            bool sof = false, sos = false;
            while (s.Pos - start < max)
            {
                int b = s.Next();
                if (b != 0xFF) return 0;
                int m = s.Next();
                while (m == 0xFF) m = s.Next();
                if (m < 0) return 0;
                if (m == 0xD9) return (sof && sos) ? s.Pos - start : 0;
                if ((m >= 0xD0 && m <= 0xD7) || m == 0x01) continue;
                int hi = s.Next(), lo = s.Next();
                if (hi < 0 || lo < 0) return 0;
                int len = (hi << 8) | lo;
                if (len < 2) return 0;
                if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) sof = true;
                s.Pos += len - 2;
                if (m == 0xDA)
                {
                    sos = true;
                    while (true)
                    {
                        if (s.Pos - start >= max) return 0;
                        int x = s.Next();
                        if (x < 0) return 0;
                        if (x != 0xFF) continue;
                        int y = s.Next();
                        while (y == 0xFF) y = s.Next();
                        if (y < 0) return 0;
                        if (y == 0x00 || (y >= 0xD0 && y <= 0xD7)) continue;
                        if (y == 0xD9) return sof ? s.Pos - start : 0;
                        s.Pos -= 2; // alt marker (ex. JPEG progresiv) - il trateaza bucla exterioara
                        break;
                    }
                }
            }
            return 0;
        }

        public long CarvePdf(long start, long max)
        {
            byte[] head = new byte[1024];
            int hn = rd.Read(start, head, 0, head.Length);
            string hs = Encoding.ASCII.GetString(head, 0, hn);
            int li = hs.IndexOf("/Linearized");
            if (li >= 0)
            {
                Match m = Regex.Match(hs.Substring(li), @"/L\s+(\d+)");
                long L;
                if (m.Success && long.TryParse(m.Groups[1].Value, out L) && L > 64 && L <= max)
                {
                    long ts = Math.Max(start, start + L - 1024);
                    byte[] tail = new byte[1024];
                    int tn = rd.Read(ts, tail, 0, (int)(start + L - ts));
                    if (Encoding.ASCII.GetString(tail, 0, tn).IndexOf("%%EOF") >= 0) return L;
                }
            }

            byte[] blk = new byte[65536];
            long p = start, lastEnd = 0;
            while (p - start < max)
            {
                int n = rd.Read(p, blk, 0, blk.Length);
                if (n <= 0) break;
                bool stop = false;
                for (int s = 0; s < n; s += 512)
                {
                    long sp = p + s;
                    int secLen = Math.Min(512, n - s);
                    if (sp > start)
                    {
                        if (lastEnd > 0)
                        {
                            if ((secLen >= 16 && Detect(blk, s) != 0) || IsZero(blk, s, secLen)) { stop = true; break; }
                            if (sp - lastEnd > (16L << 20)) { stop = true; break; }
                        }
                        else if (secLen >= 16 && Detect(blk, s) == 2) { stop = true; break; }
                    }
                    int e = Math.Min(n, s + 512 + 4);
                    for (int k = s; k + 5 <= e && k < s + 512; k++)
                    {
                        if (blk[k] == '%' && blk[k + 1] == '%' && blk[k + 2] == 'E' && blk[k + 3] == 'O' && blk[k + 4] == 'F')
                        {
                            long endp = p + k + 5;
                            int q = k + 5;
                            if (q < n && blk[q] == '\r') { endp++; q++; }
                            if (q < n && blk[q] == '\n') { endp++; }
                            lastEnd = endp;
                        }
                    }
                }
                if (stop) break;
                p += n;
            }
            return lastEnd > 0 ? lastEnd - start : 0;
        }

        // parcurge antetele locale ZIP; daca structura nu e coerenta renunta repede
        public long CarveZip(long start, long max, out string ext)
        {
            ext = null;
            byte[] h = new byte[30];
            long p = start;
            int entries = 0;
            while (p - start < max)
            {
                if (rd.Read(p, h, 0, 30) < 30) return 0;
                uint sig = BitConverter.ToUInt32(h, 0);
                if (sig == 0x04034b50)
                {
                    int flags = BitConverter.ToUInt16(h, 6);
                    uint csize = BitConverter.ToUInt32(h, 18);
                    int nl = BitConverter.ToUInt16(h, 26), xl = BitConverter.ToUInt16(h, 28);
                    if (nl == 0 || nl > 1024) return 0;
                    long data = p + 30 + nl + xl;
                    if ((flags & 8) != 0 && csize == 0)
                    {
                        long nx = FindPk(data, start + max);
                        if (nx < 0) return 0;
                        p = nx;
                    }
                    else
                    {
                        p = data + csize;
                    }
                    if ((flags & 8) != 0)
                    {
                        if (rd.Read(p, h, 0, 4) == 4 && BitConverter.ToUInt32(h, 0) == 0x08074b50) p += 16;
                        else if (csize != 0) p += 12;
                    }
                    if (++entries > 200000) return 0;
                }
                else if (sig == 0x02014b50 || sig == 0x06054b50)
                {
                    if (entries == 0) return 0;
                    return FindEocd(start, p, Math.Min(start + max, p + (64L << 20)), out ext);
                }
                else return 0;
            }
            return 0;
        }

        long FindPk(long from, long limit)
        {
            byte[] blk = new byte[65536];
            long p = from;
            while (p < limit)
            {
                int n = rd.Read(p, blk, 0, blk.Length);
                if (n < 4) return -1;
                for (int k = 0; k + 4 <= n; k++)
                {
                    if (blk[k] != 0x50 || blk[k + 1] != 0x4B) continue;
                    int a = blk[k + 2], b = blk[k + 3];
                    if ((a == 3 && b == 4) || (a == 1 && b == 2) || (a == 7 && b == 8)) return p + k;
                }
                if (n < blk.Length) return -1;
                p += n - 3;
            }
            return -1;
        }

        long FindEocd(long start, long from, long limit, out string ext)
        {
            ext = null;
            byte[] blk = new byte[65536];
            long p = from;
            while (p < limit)
            {
                int n = rd.Read(p, blk, 0, blk.Length);
                if (n < 22) break;
                for (int k = 0; k + 22 <= n; k++)
                {
                    if (blk[k] != 0x50 || blk[k + 1] != 0x4B || blk[k + 2] != 5 || blk[k + 3] != 6) continue;
                    long eocd = p + k;
                    uint cdSize = BitConverter.ToUInt32(blk, k + 12);
                    uint cdOff = BitConverter.ToUInt32(blk, k + 16);
                    int cLen = BitConverter.ToUInt16(blk, k + 20);
                    if ((long)cdOff + cdSize == eocd - start)
                    {
                        ext = ClassifyZip(start + cdOff, cdSize);
                        return eocd - start + 22 + cLen;
                    }
                }
                if (n < blk.Length) break;
                p += n - 21;
            }
            return 0;
        }

        string ClassifyZip(long cdStart, uint cdSize)
        {
            int sz = (int)Math.Min(cdSize, 8u << 20);
            byte[] cd = new byte[sz];
            int n = rd.Read(cdStart, cd, 0, sz);
            int p = 0;
            while (p + 46 <= n && BitConverter.ToUInt32(cd, p) == 0x02014b50)
            {
                int nl = BitConverter.ToUInt16(cd, p + 28), xl = BitConverter.ToUInt16(cd, p + 30), cl = BitConverter.ToUInt16(cd, p + 32);
                if (p + 46 + nl > n) break;
                string name = Encoding.UTF8.GetString(cd, p + 46, nl);
                if (name.StartsWith("word/")) return ".docx";
                if (name.StartsWith("xl/")) return ".xlsx";
                if (name.StartsWith("ppt/")) return ".pptx";
                p += 46 + nl + xl + cl;
            }
            return ".zip";
        }

        public long CarveOle(long start, long max, out string ext)
        {
            ext = null;
            byte[] h = new byte[512];
            if (rd.Read(start, h, 0, 512) < 512) return 0;
            int shift = BitConverter.ToUInt16(h, 0x1E);
            if (shift != 9 && shift != 12) return 0;
            int ss = 1 << shift;
            int per = ss / 4;
            uint numFat = BitConverter.ToUInt32(h, 0x2C);
            uint dirStart = BitConverter.ToUInt32(h, 0x30);
            uint difStart = BitConverter.ToUInt32(h, 0x44);
            uint numDif = BitConverter.ToUInt32(h, 0x48);
            if (numFat == 0 || numFat > 20000 || numDif > 1000) return 0;

            List<uint> fatSecs = new List<uint>();
            for (int i = 0; i < 109 && fatSecs.Count < numFat; i++) fatSecs.Add(BitConverter.ToUInt32(h, 0x4C + 4 * i));
            byte[] sb = new byte[ss];
            uint d = difStart;
            int guard = 0;
            while (fatSecs.Count < numFat && d < 0xFFFFFFFA && guard++ <= numDif + 1)
            {
                if ((long)(d + 1) * ss > max) return 0;
                if (rd.Read(start + (long)(d + 1) * ss, sb, 0, ss) < ss) return 0;
                for (int i = 0; i < per - 1 && fatSecs.Count < numFat; i++) fatSecs.Add(BitConverter.ToUInt32(sb, 4 * i));
                d = BitConverter.ToUInt32(sb, ss - 4);
            }
            if (fatSecs.Count < numFat) return 0;

            uint[] fat = new uint[fatSecs.Count * per];
            long maxUsed = 0;
            for (int f = 0; f < fatSecs.Count; f++)
            {
                uint fsec = fatSecs[f];
                if (fsec >= 0xFFFFFFFA) return 0;
                if ((long)(fsec + 1) * ss > max) return 0;
                if (rd.Read(start + (long)(fsec + 1) * ss, sb, 0, ss) < ss) return 0;
                for (int i = 0; i < per; i++)
                {
                    uint v = BitConverter.ToUInt32(sb, 4 * i);
                    fat[f * per + i] = v;
                    if (v != 0xFFFFFFFF && f * per + i > maxUsed) maxUsed = f * per + i;
                }
            }
            long size = (maxUsed + 2) * (long)ss;
            if (size > max || size < 1024) return 0;
            ext = ClassifyOle(start, ss, dirStart, fat);
            return size;
        }

        string ClassifyOle(long start, int ss, uint dirStart, uint[] fat)
        {
            byte[] sb = new byte[ss];
            uint s = dirStart;
            for (int g = 0; g < 64 && s < 0xFFFFFFFA; g++)
            {
                if (rd.Read(start + (long)(s + 1) * ss, sb, 0, ss) < ss) break;
                for (int e = 0; e + 128 <= ss; e += 128)
                {
                    int nl = BitConverter.ToUInt16(sb, e + 0x40);
                    if (nl < 2 || nl > 64) continue;
                    string name = Encoding.Unicode.GetString(sb, e, nl - 2);
                    if (name == "WordDocument") return ".doc";
                    if (name == "Workbook" || name == "Book") return ".xls";
                    if (name == "PowerPoint Document") return ".ppt";
                }
                if (s >= fat.Length) break;
                s = fat[s];
            }
            return ".ole";
        }

        // ------------------------------------------------------------ salvare + denumire inteligenta
        void Save(long start, long len, string kind, string ext)
        {
            string dir = Path.Combine(Path.Combine(DestDir, "Recuperare_profunda"), kind);
            Directory.CreateDirectory(dir);
            int id = Interlocked.Increment(ref seq);
            string tmp = Path.Combine(dir, "_temp_" + id + ext);
            string hash;
            using (MD5 md5 = MD5.Create())
            {
                using (FileStream o = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    byte[] b = new byte[1 << 20];
                    long p = 0;
                    while (p < len)
                    {
                        int c = (int)Math.Min(b.Length, len - p);
                        int n = rd.Read(start + p, b, 0, c);
                        if (n <= 0) break;
                        o.Write(b, 0, n);
                        md5.TransformBlock(b, 0, n, null, 0);
                        p += n;
                    }
                }
                md5.TransformFinalBlock(new byte[0], 0, 0);
                hash = BitConverter.ToString(md5.Hash);
            }
            if (!hashes.Add(hash))
            {
                File.Delete(tmp);
                Interlocked.Increment(ref Duplicates);
                return;
            }
            string nice, status;
            Describe(tmp, kind, ext, id, out nice, out status);
            string target = UniquePath(Path.Combine(dir, SafeName(nice)));
            File.Move(tmp, target);

            FoundFile f = new FoundFile();
            f.Name = Path.GetFileName(target);
            f.Kind = kind;
            f.KindColor = KindColorOf(kind);
            f.Size = len;
            f.SizeText = FormatSize(len);
            f.Folder = "la " + FormatSize(start) + " pe disc";
            f.Status = status;
            f.StatusColor = status == "Bun" ? "#22C55E" : "#F59E0B";
            f.SavedPath = target;
            Count(kind);
            Interlocked.Increment(ref Recovered);
            NewItems.Enqueue(f);
        }

        static int ReadFull(Stream s, byte[] b, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(b, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        static string DateFrom(Match m)
        {
            return m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value;
        }

        static void Describe(string path, string kind, string ext, int id, out string name, out string status)
        {
            status = "Bun";
            string num = id.ToString("00000");
            name = kind + "_" + num + ext;
            try
            {
                if (ext == ".jpg")
                {
                    byte[] b = new byte[128 * 1024];
                    int n;
                    using (FileStream f = File.OpenRead(path)) n = ReadFull(f, b, b.Length);
                    Match m = Regex.Match(Encoding.ASCII.GetString(b, 0, n), @"((?:19|20)\d\d):(\d\d):(\d\d) (\d\d):(\d\d):(\d\d)");
                    if (m.Success)
                        name = "Poza_" + DateFrom(m) + "_" + m.Groups[4].Value + "-" + m.Groups[5].Value + "-" + m.Groups[6].Value + "_" + num + ".jpg";
                    else name = "Poza_" + num + ".jpg";
                }
                else if (ext == ".pdf")
                {
                    string head, tail;
                    using (FileStream f = File.OpenRead(path))
                    {
                        byte[] b = new byte[(int)Math.Min(f.Length, 256 * 1024)];
                        ReadFull(f, b, b.Length);
                        head = Encoding.GetEncoding(28591).GetString(b);
                        f.Position = Math.Max(0, f.Length - 256 * 1024);
                        int tn = ReadFull(f, b, b.Length);
                        tail = Encoding.GetEncoding(28591).GetString(b, 0, tn);
                    }
                    string all = head + tail;
                    string title = null, date = null;
                    Match mt = Regex.Match(all, @"/Title\s*\(([^)\\]{2,100})\)");
                    if (mt.Success && !mt.Groups[1].Value.StartsWith("\u00FE\u00FF")) title = mt.Groups[1].Value.Trim();
                    Match md = Regex.Match(all, @"/CreationDate\s*\(D:((?:19|20)\d\d)(\d\d)(\d\d)");
                    if (md.Success) date = DateFrom(md);
                    name = (string.IsNullOrEmpty(title) ? "Document_PDF" : title) + (date != null ? "_" + date : "") + "_" + num + ".pdf";
                    string t1k = tail.Length > 2048 ? tail.Substring(tail.Length - 2048) : tail;
                    if (t1k.IndexOf("%%EOF") < 0) status = "Posibil incomplet";
                }
                else if (ext == ".docx" || ext == ".xlsx" || ext == ".pptx")
                {
                    string main = ext == ".docx" ? "word/document.xml" : ext == ".xlsx" ? "xl/workbook.xml" : "ppt/presentation.xml";
                    string title, date;
                    bool ok = ZipInspect(path, main, out title, out date);
                    string baseName = kind == "Word" ? "Document_Word" : kind == "Excel" ? "Tabel_Excel" : "Prezentare";
                    name = (string.IsNullOrEmpty(title) ? baseName : title) + (date != null ? "_" + date : "") + "_" + num + ext;
                    if (!ok) status = "Posibil deteriorat";
                }
            }
            catch (Exception) { status = "Posibil deteriorat"; }
        }

        static bool ZipInspect(string path, string mainEntry, out string title, out string date)
        {
            title = null;
            date = null;
            using (FileStream f = File.OpenRead(path))
            {
                long L = f.Length;
                if (L < 22) return false;
                int tailLen = (int)Math.Min(L, 65536 + 22);
                byte[] tail = new byte[tailLen];
                f.Position = L - tailLen;
                ReadFull(f, tail, tailLen);
                int e = -1;
                for (int k = tailLen - 22; k >= 0; k--)
                    if (tail[k] == 0x50 && tail[k + 1] == 0x4B && tail[k + 2] == 5 && tail[k + 3] == 6) { e = k; break; }
                if (e < 0) return false;
                uint cdSize = BitConverter.ToUInt32(tail, e + 12), cdOff = BitConverter.ToUInt32(tail, e + 16);
                if ((long)cdOff + cdSize > L || cdSize > (32u << 20)) return false;
                byte[] cd = new byte[cdSize];
                f.Position = cdOff;
                ReadFull(f, cd, (int)cdSize);
                bool mainOk = false;
                int p = 0;
                while (p + 46 <= cd.Length && BitConverter.ToUInt32(cd, p) == 0x02014b50)
                {
                    int method = BitConverter.ToUInt16(cd, p + 10);
                    uint csize = BitConverter.ToUInt32(cd, p + 20), usize = BitConverter.ToUInt32(cd, p + 24);
                    int nl = BitConverter.ToUInt16(cd, p + 28), xl = BitConverter.ToUInt16(cd, p + 30), cl = BitConverter.ToUInt16(cd, p + 32);
                    uint lho = BitConverter.ToUInt32(cd, p + 42);
                    if (p + 46 + nl > cd.Length) break;
                    string name = Encoding.UTF8.GetString(cd, p + 46, nl);
                    if (name == mainEntry)
                    {
                        mainOk = ReadEntry(f, lho, method, csize, usize) != null;
                    }
                    else if (name == "docProps/core.xml")
                    {
                        byte[] data = ReadEntry(f, lho, method, csize, usize);
                        if (data != null)
                        {
                            string xml = Encoding.UTF8.GetString(data);
                            title = XmlTag(xml, "dc:title");
                            string m = XmlTag(xml, "dcterms:modified");
                            if (m != null && m.Length >= 10) date = m.Substring(0, 10);
                        }
                    }
                    p += 46 + nl + xl + cl;
                }
                return mainOk;
            }
        }

        static byte[] ReadEntry(FileStream f, long lho, int method, uint csize, uint usize)
        {
            try
            {
                if (csize > (200u << 20) || usize > (400u << 20)) return null;
                byte[] lh = new byte[30];
                f.Position = lho;
                if (ReadFull(f, lh, 30) < 30) return null;
                if (BitConverter.ToUInt32(lh, 0) != 0x04034b50) return null;
                int nl = BitConverter.ToUInt16(lh, 26), xl = BitConverter.ToUInt16(lh, 28);
                f.Position = lho + 30 + nl + xl;
                byte[] comp = new byte[csize];
                if (ReadFull(f, comp, (int)csize) < csize) return null;
                if (method == 0) return comp;
                if (method != 8) return null;
                using (MemoryStream ms = new MemoryStream(comp))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream o = new MemoryStream())
                {
                    ds.CopyTo(o);
                    byte[] r = o.ToArray();
                    return r.Length == usize ? r : null;
                }
            }
            catch (Exception) { return null; }
        }

        static string XmlTag(string xml, string tag)
        {
            int a = xml.IndexOf("<" + tag);
            if (a < 0) return null;
            int gt = xml.IndexOf('>', a);
            if (gt < 0 || xml[gt - 1] == '/') return null;
            int b = xml.IndexOf("</" + tag + ">", gt);
            if (b < 0) return null;
            string v = xml.Substring(gt + 1, b - gt - 1).Trim();
            v = v.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
            if (v.Length > 80) v = v.Substring(0, 80);
            return v.Length == 0 ? null : v;
        }
    }
}
