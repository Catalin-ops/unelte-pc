// Sanatate PC - Info PC Electric
// Test de memorie RAM direct in Windows (fara repornire): scrie tipare in memoria libera si le verifica.
// Limita: memoria folosita de Windows si de programe nu poate fi testata din aplicatie.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace InfoPcDisk
{
    public class RamTestResult
    {
        public DateTime Date;
        public double TestedGB;
        public int Passes;
        public long Errors;
        public bool Completed;
        public string Machine = "";

        static string FilePath { get { return Path.Combine(Licensing.AppDataDir, "test_ram.txt"); } }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Licensing.AppDataDir);
                File.WriteAllText(FilePath, string.Join(";", new string[] {
                    Date.ToString("o", CultureInfo.InvariantCulture), TestedGB.ToString("0.00", CultureInfo.InvariantCulture),
                    Passes.ToString(), Errors.ToString(), Completed ? "1" : "0", Environment.MachineName }), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        public static RamTestResult Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string[] p = File.ReadAllText(FilePath, Encoding.UTF8).Trim().Split(';');
                if (p.Length < 5) return null;
                RamTestResult r = new RamTestResult();
                r.Date = DateTime.Parse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                r.TestedGB = double.Parse(p[1], CultureInfo.InvariantCulture);
                r.Passes = int.Parse(p[2]);
                r.Errors = long.Parse(p[3]);
                r.Completed = p[4] == "1";
                r.Machine = p.Length > 5 ? p[5] : "";
                if (r.Machine.Length > 0 && !string.Equals(r.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
                return r;
            }
            catch (Exception) { return null; }
        }
    }

    public unsafe class RamTester
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualLock(IntPtr addr, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessWorkingSetSize(IntPtr proc, UIntPtr min, UIntPtr max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [StructLayout(LayoutKind.Sequential)]
        struct MEMSTAT
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);

        const long Chunk = 64L << 20;   // blocuri de 64 MB
        public const int PatternsPerPass = 6;

        public long TargetBytes;
        public int Passes = 1;
        public long SimulateFlipAt = -1;   // doar pentru testul automat: strica intentionat un bit dupa scriere
        // audit: dupa ce trece termenul, testul se opreste la capatul tiparului curent (dar termina cel putin o trecere)
        public DateTime Deadline = DateTime.MaxValue;
        public int PassesDone;
        public bool StoppedByDeadline;

        public volatile bool Cancel;
        public volatile bool Finished = true;
        public volatile string Phase = "";
        public volatile string Error;
        public long Done, Total, Errors;
        public int CurrentPass;
        public long AllocatedBytes;
        public bool LockedInRam;
        public DateTime Start;
        public List<string> Log = new List<string>();
        readonly object logSync = new object();

        List<IntPtr> chunks = new List<IntPtr>();

        public static void MemoryInfo(out long total, out long avail)
        {
            MEMSTAT m = new MEMSTAT(); m.dwLength = (uint)Marshal.SizeOf(typeof(MEMSTAT));
            GlobalMemoryStatusEx(ref m);
            total = (long)m.ullTotalPhys; avail = (long)m.ullAvailPhys;
        }

        // cat se poate testa fara sa sufoce Windows: memoria disponibila minus o rezerva
        public static long SuggestedBytes()
        {
            long total, avail;
            MemoryInfo(out total, out avail);
            long reserve = Math.Max(1L << 30, total / 8);
            long s = avail - reserve;
            if (s < (256L << 20)) s = Math.Min(avail / 2, 256L << 20);
            return Math.Max(0, s - s % Chunk);
        }

        public string[] LogLines() { lock (logSync) return Log.ToArray(); }

        void AddLog(string s)
        {
            lock (logSync) { if (Log.Count < 200) Log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + s); }
        }

        public void StartAsync()
        {
            Finished = false;
            Cancel = false;
            Error = null;
            Errors = 0; Done = 0; CurrentPass = 0; PassesDone = 0; StoppedByDeadline = false;
            Start = DateTime.Now;
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Priority = ThreadPriority.AboveNormal;
            t.Start();
        }

        void Run()
        {
            try
            {
                Phase = "Rezerv memoria pentru test…";
                long want = TargetBytes - TargetBytes % Chunk;
                try { SetProcessWorkingSetSize(GetCurrentProcess(), (UIntPtr)(ulong)(want + (128L << 20)), (UIntPtr)(ulong)(want + (512L << 20))); } catch (Exception) { }
                int locked = 0;
                while (AllocatedBytes < want && !Cancel)
                {
                    IntPtr p = VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)Chunk, 0x3000, 0x04);   // MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE
                    if (p == IntPtr.Zero) break;
                    if (VirtualLock(p, (UIntPtr)(ulong)Chunk)) locked++;
                    chunks.Add(p);
                    AllocatedBytes += Chunk;
                }
                LockedInRam = locked == chunks.Count && chunks.Count > 0;
                if (chunks.Count == 0) { Error = "Nu am putut rezerva memorie pentru test."; return; }
                AddLog("Memorie rezervată: " + (AllocatedBytes / (double)(1L << 30)).ToString("0.0") + " GB în " + chunks.Count + " blocuri" + (LockedInRam ? " (blocată în RAM)" : " (o parte poate fi mutată pe disc, testul poate fi mai lent)"));
                Total = AllocatedBytes * 2L * PatternsPerPass * Passes;

                int threads = Math.Max(1, Math.Min(Environment.ProcessorCount, chunks.Count));
                for (int pass = 1; pass <= Passes && !Cancel; pass++)
                {
                    CurrentPass = pass;
                    for (int pat = 0; pat < PatternsPerPass && !Cancel; pat++)
                    {
                        Phase = "Trecerea " + pass + " din " + Passes + " · " + PatternName(pat);
                        RunParallel(threads, pass, pat, true);
                        if (Cancel) break;
                        if (SimulateFlipAt >= 0 && pass == 1 && pat == 0) FlipForTest();
                        RunParallel(threads, pass, pat, false);
                        if (pass > 1 && DateTime.Now > Deadline) { StoppedByDeadline = true; break; }
                    }
                    if (!Cancel && !StoppedByDeadline) PassesDone = pass;
                    if (StoppedByDeadline) { AddLog("Timpul alocat s-a terminat în trecerea " + pass + "."); break; }
                    if (DateTime.Now > Deadline && pass < Passes) { StoppedByDeadline = true; AddLog("Trecerea " + pass + " terminată: " + Errors + (Errors == 1 ? " eroare" : " erori") + ". Timpul alocat s-a terminat."); break; }
                    AddLog("Trecerea " + pass + " terminată: " + Errors + (Errors == 1 ? " eroare" : " erori") + " până acum.");
                }
                Phase = Cancel ? "Oprit" : "Gata";
            }
            catch (Exception ex) { Error = ex.Message; }
            finally
            {
                foreach (IntPtr p in chunks) VirtualFree(p, UIntPtr.Zero, 0x8000);   // MEM_RELEASE
                chunks.Clear();
                Finished = true;
            }
        }

        void FlipForTest()
        {
            long c = SimulateFlipAt / Chunk, off = SimulateFlipAt % Chunk;
            if (c < chunks.Count) { byte* b = (byte*)chunks[(int)c]; b[off] ^= 0x10; }
        }

        public static string PatternName(int pat)
        {
            switch (pat)
            {
                case 0: return "adrese proprii";
                case 1: return "adrese inversate";
                case 2: return "șah 1010";
                case 3: return "șah 0101";
                case 4: return "valori aleatoare";
                default: return "biți plimbați";
            }
        }

        void RunParallel(int threads, int pass, int pat, bool write)
        {
            Thread[] ts = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                int id = t;
                ts[t] = new Thread(delegate()
                {
                    for (int c = id; c < chunks.Count && !Cancel; c += threads) DoChunk(c, pass, pat, write);
                });
                ts[t].IsBackground = true;
                ts[t].Start();
            }
            foreach (Thread t in ts) t.Join();
        }

        static ulong Expected(int pat, ulong addr, ulong seed, ref ulong rnd)
        {
            switch (pat)
            {
                case 0: return addr ^ seed;
                case 1: return ~(addr ^ seed);
                case 2: return 0xAAAAAAAAAAAAAAAAUL;
                case 3: return 0x5555555555555555UL;
                case 4: rnd ^= rnd << 13; rnd ^= rnd >> 7; rnd ^= rnd << 17; return rnd;
                default: return 1UL << (int)((addr >> 3) & 63);
            }
        }

        void DoChunk(int c, int pass, int pat, bool write)
        {
            ulong* p = (ulong*)chunks[c];
            long n = Chunk / 8;
            ulong baseAddr = (ulong)c * (ulong)Chunk;
            ulong seed = 0x9E3779B97F4A7C15UL * (ulong)(pass * 31 + pat + 1);
            ulong rnd = seed ^ (ulong)(c + 1) * 0xD1B54A32D192ED03UL;
            if (rnd == 0) rnd = 1;
            const long step = 1L << 20;   // raportam progresul la fiecare 8 MB
            for (long i0 = 0; i0 < n && !Cancel; i0 += step)
            {
                long end = Math.Min(n, i0 + step);
                if (write)
                {
                    for (long i = i0; i < end; i++) p[i] = Expected(pat, baseAddr + (ulong)(i << 3), seed, ref rnd);
                }
                else
                {
                    for (long i = i0; i < end; i++)
                    {
                        ulong e = Expected(pat, baseAddr + (ulong)(i << 3), seed, ref rnd);
                        ulong v = p[i];
                        if (v != e)
                        {
                            long k = Interlocked.Increment(ref Errors);
                            if (k <= 50)
                                AddLog("EROARE la " + ((long)baseAddr + (i << 3)).ToString("X") + ": scris " + e.ToString("X16") + ", citit " + v.ToString("X16") +
                                    " (biți diferiți: " + BitCount(e ^ v) + ")");
                        }
                    }
                }
                Interlocked.Add(ref Done, (end - i0) * 8);
            }
        }

        static int BitCount(ulong x) { int n = 0; while (x != 0) { n += (int)(x & 1); x >>= 1; } return n; }
    }
}
