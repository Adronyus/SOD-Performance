using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Il2CppInterop.Runtime;

namespace SODPerformance
{
    /// <summary>
    /// Sampling profiler. A small native helper (sodsampler.dll, a plain Win32 thread) pauses the main thread about 1000x
    /// per second for a few microseconds, records its call stack (function start addresses, from the modules' x64 unwind
    /// tables) and lets it continue. This side drains those samples on a background thread, counts how often each game
    /// function was running or on the stack, and names them with the IL2CPP metadata (Class.Method). It shows where loading
    /// and frame time actually go, including code no hook covers.
    /// </summary>
    internal static unsafe class Sampler
    {
        private const int MaxDepth = 64;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Sample
        {
            public int Tag, Frame, N, Pad;
            public fixed ulong Fn[MaxDepth];
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool K32EnumProcessModules(IntPtr process, IntPtr[] modules, int cb, out int needed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int K32GetModuleBaseNameW(IntPtr process, IntPtr module, StringBuilder name, int size);
        [StructLayout(LayoutKind.Sequential)] private struct MODULEINFO { public IntPtr lpBaseOfDll; public int SizeOfImage; public IntPtr EntryPoint; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool K32GetModuleInformation(IntPtr process, IntPtr module, out MODULEINFO info, int cb);

        private static delegate* unmanaged[Cdecl]<uint, ulong*, int, int, int> pStart;
        private static delegate* unmanaged[Cdecl]<int, int, void> pSetTag;
        private static delegate* unmanaged[Cdecl]<Sample*, int, int> pRead;
        private static delegate* unmanaged[Cdecl]<long*, void> pStats;
        private static delegate* unmanaged[Cdecl]<int> pSize;

        internal static uint MainNativeThreadId;
        internal static bool Running;
        internal static string Status = "not started";
        internal static double NamesMs;

        // tags
        internal const int TagPlaying = 0, TagLoading = 1, TagPaused = 2, TagFastForward = 3;

        private struct Mod { public ulong Base, End; public string Name; }
        private static Mod[] mods = Array.Empty<Mod>();
        private static ulong gaBase, gaEnd;
        private static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>();

        internal sealed class Bucket
        {
            public long Samples;
            public readonly Dictionary<ulong, int> Incl = new Dictionary<ulong, int>();
            public readonly Dictionary<ulong, int> TopCs = new Dictionary<ulong, int>();
            public readonly Dictionary<string, int> LeafModule = new Dictionary<string, int>();
        }

        private static readonly object sync = new object();
        internal static readonly Bucket Playing = new Bucket(), SlowFrames = new Bucket(), Loading = new Bucket();
        internal static readonly Dictionary<int, Bucket> LoadingByState = new Dictionary<int, Bucket>();
        internal static readonly Dictionary<int, string> StateNames = new Dictionary<int, string>();

        // frame durations, written by the main thread, read by the drain thread
        private static readonly float[] frameMs = new float[8192];
        private static volatile int lastCompletedFrame = -1;
        private static Sample* buf;
        private static Sample[] pending = new Sample[4096];
        private static int pendingCount;

        internal static void CaptureMainThread() => MainNativeThreadId = GetCurrentThreadId();

        internal static void Start()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) { Status = "not Windows"; return; }
                if (!Native.Load()) { Status = "native helper not loaded: " + Native.Error; return; }
                IntPtr lib = Native.Lib;
                pStart = (delegate* unmanaged[Cdecl]<uint, ulong*, int, int, int>)NativeLibrary.GetExport(lib, "sod_start");
                pSetTag = (delegate* unmanaged[Cdecl]<int, int, void>)NativeLibrary.GetExport(lib, "sod_set_tag");
                pRead = (delegate* unmanaged[Cdecl]<Sample*, int, int>)NativeLibrary.GetExport(lib, "sod_read");
                pStats = (delegate* unmanaged[Cdecl]<long*, void>)NativeLibrary.GetExport(lib, "sod_stats");
                pSize = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(lib, "sod_sample_size");
                if (pSize() != sizeof(Sample)) { Status = $"sample size mismatch ({pSize()} vs {sizeof(Sample)})"; return; }

                BuildModules();
                var sw = Stopwatch.StartNew();
                BuildNames();
                NamesMs = sw.Elapsed.TotalMilliseconds;

                ulong[] bases = mods.Select(m => m.Base).ToArray();
                int r;
                pSetTag(TagPaused, 0);
                fixed (ulong* b = bases) r = pStart(MainNativeThreadId, b, bases.Length, Math.Max(250, PerfConfig.SamplingIntervalUs.Value));
                if (r != 0) { Status = $"native start failed ({r})"; return; }

                buf = (Sample*)Marshal.AllocHGlobal(sizeof(Sample) * 2048);
                var t = new Thread(DrainLoop) { IsBackground = true, Name = "SODPerformance sampler drain", Priority = ThreadPriority.BelowNormal };
                t.Start();
                Running = true;
                Status = $"running ({mods.Length} modules, {names.Count} named functions, names built in {NamesMs:0} ms)";
                Plugin.L.LogInfo("Sampling profiler: " + Status);
            }
            catch (Exception e)
            {
                Status = $"error: {e.GetType().Name}: {e.Message}";
                Plugin.L.LogWarning("Sampling profiler not started: " + Status);
            }
        }

        private static void BuildModules()
        {
            var list = new List<Mod>();
            IntPtr proc = GetCurrentProcess();
            var handles = new IntPtr[1024];
            if (!K32EnumProcessModules(proc, handles, handles.Length * IntPtr.Size, out int needed)) throw new InvalidOperationException("EnumProcessModules failed");
            int count = Math.Min(handles.Length, needed / IntPtr.Size);
            var nameBuf = new StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                if (!K32GetModuleInformation(proc, handles[i], out MODULEINFO mi, Marshal.SizeOf<MODULEINFO>())) continue;
                nameBuf.Clear();
                K32GetModuleBaseNameW(proc, handles[i], nameBuf, nameBuf.Capacity);
                ulong b = (ulong)mi.lpBaseOfDll.ToInt64();
                list.Add(new Mod { Base = b, End = b + (ulong)mi.SizeOfImage, Name = nameBuf.ToString() });
            }
            int Rank(Mod m)
            {
                string n = m.Name.ToLowerInvariant();
                if (n == "gameassembly.dll") return 0;
                if (n == "unityplayer.dll") return 1;
                if (n is "ntdll.dll" or "kernel32.dll" or "kernelbase.dll" or "ucrtbase.dll" or "msvcrt.dll" or "vcruntime140.dll" or "win32u.dll" or "user32.dll") return 2;
                if (n.StartsWith("d3d") || n.StartsWith("dxgi") || n.StartsWith("nv") || n.StartsWith("fmod")) return 3;
                return 4;
            }
            mods = list.OrderBy(Rank).Take(96).ToArray();
            foreach (var m in mods)
                if (m.Name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) { gaBase = m.Base; gaEnd = m.End; }
        }

        private static readonly string[] wantedImages =
        {
            "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "Unity.RenderPipelines.HighDefinition.Runtime.dll",
            "Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.CoreModule.dll", "mscorlib.dll", "System.dll", "System.Core.dll",
            "Unity.TextMeshPro.dll", "UnityEngine.UI.dll", "Rewired_Core.dll", "FMODUnity.dll", "DOTween.dll", "UnityEngine.PhysicsModule.dll",
            "UnityEngine.AnimationModule.dll", "Unity.Collections.dll",
        };

        /// <summary>Reads Class.Method names and native code addresses from the IL2CPP metadata (main thread).</summary>
        private static void BuildNames()
        {
            IntPtr domain = IL2CPP.il2cpp_domain_get();
            uint count = 0;
            IntPtr* asms = IL2CPP.il2cpp_domain_get_assemblies(domain, ref count);
            for (uint i = 0; i < count; i++)
            {
                IntPtr image = IL2CPP.il2cpp_assembly_get_image(asms[i]);
                string iname = IL2CPP.il2cpp_image_get_name_(image) ?? "";
                if (Array.IndexOf(wantedImages, iname) < 0) continue;
                uint cc = IL2CPP.il2cpp_image_get_class_count(image);
                for (uint j = 0; j < cc; j++)
                {
                    IntPtr klass = IL2CPP.il2cpp_image_get_class(image, j);
                    if (klass == IntPtr.Zero) continue;
                    string cn = null;
                    IntPtr iter = IntPtr.Zero;
                    IntPtr m;
                    while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                    {
                        IntPtr code = Marshal.ReadIntPtr(m);
                        if (code == IntPtr.Zero) continue;
                        ulong a = (ulong)code.ToInt64();
                        if (names.ContainsKey(a)) continue;
                        cn ??= ClassName(klass);
                        names[a] = cn + "." + IL2CPP.il2cpp_method_get_name_(m);
                    }
                }
            }
        }

        private static string ClassName(IntPtr klass)
        {
            string n = IL2CPP.il2cpp_class_get_name_(klass) ?? "?";
            IntPtr outer = IL2CPP.il2cpp_class_get_declaring_type(klass);
            int guard = 0;
            while (outer != IntPtr.Zero && guard++ < 5)
            {
                n = (IL2CPP.il2cpp_class_get_name_(outer) ?? "?") + "+" + n;
                outer = IL2CPP.il2cpp_class_get_declaring_type(outer);
            }
            return n;
        }

        /// <summary>Main thread, once per frame: tells the sampler what the following samples belong to.</summary>
        internal static void FrameTick(int frame, float lastFrameMs, int tag)
        {
            if (!Running) return;
            if (frame > 0)
            {
                frameMs[(frame - 1) & 8191] = lastFrameMs;
                lastCompletedFrame = frame - 1;
            }
            pSetTag(tag, frame);
        }

        internal static int LoadTag(int state) => TagLoading | ((state + 1) << 8);

        private static void DrainLoop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(100);
                    int n;
                    while ((n = pRead(buf, 2048)) > 0)
                        for (int i = 0; i < n; i++) Consider(buf[i]);
                    // samples whose frame has finished in the meantime
                    if (pendingCount > 0)
                    {
                        int keep = 0;
                        for (int i = 0; i < pendingCount; i++)
                        {
                            if (pending[i].Frame <= lastCompletedFrame) Count(pending[i]);
                            else pending[keep++] = pending[i];
                        }
                        pendingCount = keep;
                    }
                }
                catch (Exception e)
                {
                    Status = "drain error: " + e.Message;
                    Thread.Sleep(1000);
                }
            }
        }

        private static void Consider(Sample s)
        {
            int kind = s.Tag & 0xff;
            if (kind == TagPlaying && s.Frame > lastCompletedFrame)
            {
                if (pendingCount < pending.Length) pending[pendingCount++] = s;
                return;
            }
            Count(s);
        }

        private static readonly ulong[] seen = new ulong[MaxDepth];

        private static void Count(Sample s)
        {
            int kind = s.Tag & 0xff;
            if (kind == TagPaused || kind == TagFastForward || s.N <= 0) return;
            lock (sync)
            {
                if (kind == TagPlaying)
                {
                    Add(Playing, s);
                    if (frameMs[s.Frame & 8191] >= 40f) Add(SlowFrames, s);
                }
                else if (kind == TagLoading)
                {
                    Add(Loading, s);
                    int state = (s.Tag >> 8) - 1;
                    if (!LoadingByState.TryGetValue(state, out var b)) LoadingByState[state] = b = new Bucket();
                    Add(b, s);
                }
            }
        }

        private static void Add(Bucket b, Sample s)
        {
            b.Samples++;
            int n = Math.Min(s.N, MaxDepth);
            int distinct = 0;
            bool topDone = false;
            for (int i = 0; i < n; i++)
            {
                ulong a = s.Fn[i];
                if (i == 0)
                {
                    string mod = ModuleOf(a);
                    b.LeafModule[mod] = b.LeafModule.TryGetValue(mod, out int lm) ? lm + 1 : 1;
                }
                if (a < gaBase || a >= gaEnd) continue;
                if (!topDone)
                {
                    topDone = true;
                    b.TopCs[a] = b.TopCs.TryGetValue(a, out int tc) ? tc + 1 : 1;
                }
                bool dup = false;
                for (int k = 0; k < distinct; k++) if (seen[k] == a) { dup = true; break; }
                if (dup) continue;
                seen[distinct++] = a;
                b.Incl[a] = b.Incl.TryGetValue(a, out int c) ? c + 1 : 1;
            }
        }

        private static string ModuleOf(ulong a)
        {
            foreach (var m in mods) if (a >= m.Base && a < m.End) return m.Name;
            return "(generated code)";
        }

        internal static string Name(ulong a)
        {
            if (names.TryGetValue(a, out string n)) return n;
            foreach (var m in mods) if (a >= m.Base && a < m.End) return $"{m.Name}+0x{a - m.Base:X}";
            return $"0x{a:X}";
        }

        internal static long[] Stats()
        {
            var o = new long[5];
            if (Running) fixed (long* p = o) pStats(p);
            return o;
        }

        // ---------------- report ----------------

        internal static void AppendReport(StringBuilder sb)
        {
            long[] st = Stats();
            sb.AppendLine($"Sampler: {Status}");
            sb.AppendLine($"Samples taken {st[0]}, failed {st[1]}, longest pause of the main thread for one sample {st[2]} µs, dropped (buffer full) {st[3]}");
            if (!Running) return;
            lock (sync)
            {
                Section(sb, "LOADING (all steps)", Loading, 30, 15);
                foreach (var kv in LoadingByState.OrderByDescending(k => k.Value.Samples).Take(6))
                {
                    string sn = StateNames.TryGetValue(kv.Key, out string s) ? s : kv.Key.ToString();
                    Section(sb, $"LOADING step '{sn}'", kv.Value, 15, 0);
                }
                Section(sb, "PLAYING (all frames)", Playing, 40, 25);
                Section(sb, "PLAYING, frames slower than 40 ms only", SlowFrames, 30, 20);
            }
        }

        private static void Section(StringBuilder sb, string title, Bucket b, int inclTop, int topCsTop)
        {
            sb.AppendLine();
            sb.AppendLine($"-- {title}: {b.Samples} samples (~{b.Samples * Math.Max(250, PerfConfig.SamplingIntervalUs.Value) / 1000000.0:0.0} s of main-thread time)");
            if (b.Samples == 0) return;
            double tot = b.Samples;
            sb.Append("   where the main thread was (module): ");
            sb.AppendLine(string.Join(", ", b.LeafModule.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key} {100.0 * k.Value / tot:0}%")));
            if (topCsTop > 0)
            {
                sb.AppendLine("   game code running or calling the engine at that moment (innermost game function):");
                foreach (var kv in b.TopCs.OrderByDescending(k => k.Value).Take(topCsTop))
                    sb.AppendLine($"     {100.0 * kv.Value / tot,5:0.0}%  {Name(kv.Key)}");
            }
            sb.AppendLine("   game functions on the stack (inclusive: the function plus everything it called):");
            foreach (var kv in b.Incl.OrderByDescending(k => k.Value).Take(inclTop))
                sb.AppendLine($"     {100.0 * kv.Value / tot,5:0.0}%  {Name(kv.Key)}");
        }
    }
}
