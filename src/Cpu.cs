using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SODPerformance
{
    /// <summary>
    /// CPUs like the Ryzen 9 7950X3D / 9950X3D have the large 3D V-Cache on only one of their two core groups (CCDs).
    /// Shadows of Doubt is limited by its main thread and works on a huge amount of data (5+ GB heap), so it runs
    /// noticeably faster on the V-Cache cores. Windows only moves a game there when its game detection recognises it.
    /// This keeps the game process on the V-Cache cores while optimizations are ON (process affinity; nothing system-wide)
    /// and measures where the main thread actually ran.
    /// </summary>
    internal static class CpuPlacement
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessorNumber();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);

        private const int RelationCache = 2;

        internal struct L3 { public ulong Mask; public int Group; public uint Bytes; }

        internal static bool Detected, HasVCache, Applied, Broken;
        internal static ulong VCacheMask, OriginalMask;
        internal static string Layout = "not detected yet";
        internal static string Status = "-";
        internal static int Switches;

        // [0] = not pinned (before pinning, or optimizations OFF), [1] = pinned
        internal static readonly long[] Frames = new long[2];
        internal static readonly long[] FramesOnVCache = new long[2];
        private static long unpinnedSamplesAtStart;

        internal static void Tick(bool optimizationsOn)
        {
            if (Broken) return;
            try
            {
                if (!Detected) Detect();

                if (HasVCache)
                {
                    uint cpu = GetCurrentProcessorNumber();
                    int i = Applied ? 1 : 0;
                    Frames[i]++;
                    if (cpu < 64 && ((VCacheMask >> (int)cpu) & 1UL) != 0) FramesOnVCache[i]++;
                    if (!Applied) unpinnedSamplesAtStart++;
                }

                // Watch where Windows puts the game for ~2 s before pinning, so the report can show whether it mattered.
                bool want = optimizationsOn && HasVCache && PerfConfig.PreferVCacheCores.Value && unpinnedSamplesAtStart >= 120;
                if (want != Applied) Apply(want);
            }
            catch (Exception e)
            {
                Broken = true;
                Status = "error: " + e.GetType().Name;
                Runner.ReportError("cpu-placement", e);
            }
        }

        private static void Apply(bool pin)
        {
            ulong mask = pin ? VCacheMask : OriginalMask;
            if (mask == 0) { Broken = true; Status = "no usable mask"; return; }
            if (!SetProcessAffinityMask(GetCurrentProcess(), new UIntPtr(mask)))
            {
                Broken = true;
                Status = $"could not set affinity (error {Marshal.GetLastWin32Error()})";
                Plugin.L.LogWarning("CPU: " + Status);
                return;
            }
            Applied = pin;
            Switches++;
            Status = pin ? $"game kept on V-Cache cores {MaskToText(mask)}" : $"normal (all cores {MaskToText(mask)})";
            Plugin.L.LogInfo("CPU: " + Status);
        }

        private static void Detect()
        {
            Detected = true;
            if (!OperatingSystem.IsWindows()) { Layout = "not Windows"; return; }

            if (!GetProcessAffinityMask(GetCurrentProcess(), out UIntPtr proc, out UIntPtr sys))
            {
                Layout = $"affinity query failed (error {Marshal.GetLastWin32Error()})";
                return;
            }
            OriginalMask = proc.ToUInt64();

            uint len = 0;
            GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref len);
            if (len == 0 || len > 1 << 20) { Layout = "cache query failed"; return; }
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationCache, buf, ref len))
                {
                    Layout = $"cache query failed (error {Marshal.GetLastWin32Error()})";
                    return;
                }
                var bytes = new byte[len];
                Marshal.Copy(buf, bytes, 0, (int)len);
                List<L3> l3 = ParseL3(bytes, (int)len);
                Decide(l3, OriginalMask, out VCacheMask, out HasVCache, out Layout);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            Plugin.L.LogInfo($"CPU: {Layout}");
        }

        /// <summary>Parses SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records (RelationCache) and returns the level-3 caches.</summary>
        internal static List<L3> ParseL3(byte[] b, int len)
        {
            var result = new List<L3>();
            int off = 0;
            while (off + 8 <= len)
            {
                int rel = BitConverter.ToInt32(b, off);
                int size = BitConverter.ToInt32(b, off + 4);
                if (size <= 0 || off + size > len) break;
                if (rel == RelationCache && size >= 50)
                {
                    byte level = b[off + 8];
                    uint cacheBytes = BitConverter.ToUInt32(b, off + 12);
                    ulong mask = BitConverter.ToUInt64(b, off + 40);
                    int group = BitConverter.ToUInt16(b, off + 48);
                    if (level == 3) result.Add(new L3 { Mask = mask, Group = group, Bytes = cacheBytes });
                }
                off += size;
            }
            return result;
        }

        /// <summary>V-Cache cores = the L3 domain(s) clearly larger than the others (96 MB vs 32 MB on X3D parts).</summary>
        internal static void Decide(List<L3> l3, ulong allowed, out ulong vcache, out bool has, out string layout)
        {
            vcache = 0;
            has = false;
            var sb = new StringBuilder();
            foreach (var c in l3)
                sb.Append($"L3 {c.Bytes / (1024 * 1024)} MB on CPUs {MaskToText(c.Mask)}{(c.Group != 0 ? $" (group {c.Group})" : "")}; ");
            if (l3.Count < 2)
            {
                layout = sb.Append("one L3 domain: nothing to do").ToString();
                return;
            }
            if (l3.Exists(c => c.Group != 0))
            {
                layout = sb.Append("more than one processor group: left alone").ToString();
                return;
            }
            uint max = 0, min = uint.MaxValue;
            foreach (var c in l3) { if (c.Bytes > max) max = c.Bytes; if (c.Bytes < min) min = c.Bytes; }
            if (min == 0 || max < min * 3UL / 2UL)
            {
                layout = sb.Append("all L3 domains are the same size: nothing to do").ToString();
                return;
            }
            foreach (var c in l3) if (c.Bytes == max) vcache |= c.Mask;
            vcache &= allowed;
            has = vcache != 0;
            layout = sb.Append(has ? $"V-Cache cores: {MaskToText(vcache)}" : "V-Cache cores not in the allowed set").ToString();
        }

        internal static string MaskToText(ulong mask)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < 64)
            {
                if (((mask >> i) & 1UL) == 0) { i++; continue; }
                int j = i;
                while (j + 1 < 64 && ((mask >> (j + 1)) & 1UL) != 0) j++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(i == j ? $"{i}" : $"{i}-{j}");
                i = j + 1;
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        internal static string Percent(int i) => Frames[i] == 0 ? "-" : $"{100.0 * FramesOnVCache[i] / Frames[i]:0}% of {Frames[i]} frames";
    }
}
