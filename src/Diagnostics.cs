using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;

namespace SODPerformance
{
    /// <summary>
    /// Splits each frame into four phases using timestamps from our own MonoBehaviour callbacks:
    ///   wait    = OnGUI repaint (end of rendering) -> first FixedUpdate / Update of the next frame:
    ///             present, GPU sync, frame-rate cap, input, EarlyUpdate.
    ///   physics = first FixedUpdate -> our Update: physics steps (FixedUpdate scripts + simulation), PreUpdate.
    ///   scripts = our Update -> our LateUpdate: game Update() scripts, coroutines, Animator/Director updates.
    ///   render  = our LateUpdate -> OnGUI repaint: remaining LateUpdate scripts + HDRP rendering on the main thread.
    /// Script order inside a phase is arbitrary, so the borders are approximate, but the four parts add up to the
    /// whole frame and show whether time goes to scripts, rendering, physics or waiting (GPU / frame cap).
    /// </summary>
    internal static class Phases
    {
        internal const int Wait = 0, Physics = 1, Scripts = 2, Render = 3, Count = 4;
        internal static readonly string[] Names = { "wait (GPU/present/cap)", "physics + early update", "scripts + animation", "render (main thread)" };

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static long tUpdate, tLate, tRepaint, tFirstFixed;
        private static bool haveFixed, haveLate, haveRepaint, haveUpdate;
        internal static int FixedSteps;

        // Last completed frame
        internal static readonly double[] Last = new double[Count];
        internal static double LastFrameMs;
        internal static int LastFixedSteps;

        // Window (overlay) and session (report, per A/B state)
        private static readonly double[] win = new double[Count];
        private static long winFrames;
        internal static readonly double[] WindowAvg = new double[Count];
        internal static readonly double[][] Ses = { new double[Count], new double[Count] };
        internal static readonly long[] SesFrames = new long[2];
        internal static readonly long[] SesFixedSteps = new long[2];

        internal static void ResetSession()
        {
            for (int i = 0; i < 2; i++) { Array.Clear(Ses[i], 0, Ses[i].Length); SesFrames[i] = 0; SesFixedSteps[i] = 0; }
        }

        internal static void OnFixedUpdate()
        {
            if (!haveFixed)
            {
                tFirstFixed = Stopwatch.GetTimestamp();
                haveFixed = true;
            }
            FixedSteps++;
        }

        internal static void OnLateUpdate()
        {
            if (!haveLate)
            {
                tLate = Stopwatch.GetTimestamp();
                haveLate = true;
            }
        }

        internal static void OnRepaint()
        {
            if (!haveRepaint)
            {
                tRepaint = Stopwatch.GetTimestamp();
                haveRepaint = true;
            }
        }

        /// <summary>Called at the start of our Update. Closes the previous frame (previous Update -> now). Returns false on the first call.</summary>
        internal static bool OnUpdate(bool measuring, int state)
        {
            long now = Stopwatch.GetTimestamp();
            bool closed = false;
            if (haveUpdate && haveLate)
            {
                long late = tLate;
                long repaint = haveRepaint ? tRepaint : late;
                long fixedStart = haveFixed && tFirstFixed > repaint ? tFirstFixed : now;

                Last[Scripts] = Math.Max(0, late - tUpdate) * TicksToMs;
                Last[Render] = Math.Max(0, repaint - late) * TicksToMs;
                Last[Wait] = Math.Max(0, fixedStart - repaint) * TicksToMs;
                Last[Physics] = Math.Max(0, now - fixedStart) * TicksToMs;
                LastFrameMs = (now - tUpdate) * TicksToMs;
                LastFixedSteps = FixedSteps;

                for (int i = 0; i < Count; i++) win[i] += Last[i];
                winFrames++;
                if (measuring)
                {
                    for (int i = 0; i < Count; i++) Ses[state][i] += Last[i];
                    SesFrames[state]++;
                    SesFixedSteps[state] += FixedSteps;
                }
                closed = true;
            }

            tUpdate = now;
            haveUpdate = true;
            haveLate = haveRepaint = haveFixed = false;
            FixedSteps = 0;
            return closed;
        }

        internal static void CloseWindow()
        {
            if (winFrames == 0) return;
            for (int i = 0; i < Count; i++)
            {
                WindowAvg[i] = win[i] / winFrames;
                win[i] = 0;
            }
            winFrames = 0;
        }

        internal static double SesAvg(int state, int phase) => SesFrames[state] == 0 ? 0 : Ses[state][phase] / SesFrames[state];
    }

    /// <summary>IL2CPP garbage-collector statistics (the game's heap, not BepInEx's .NET heap).</summary>
    internal static class GcStats
    {
        internal static int LastCount = -1;
        internal static int CollectionsThisFrame;
        internal static readonly long[] SesCollections = new long[2];
        internal static void ResetSession() { SesCollections[0] = SesCollections[1] = 0; }
        internal static long HeapUsedMb, HeapSizeMb;
        internal static string Mode = "?";
        internal static bool Incremental;
        internal static ulong SliceNs;
        internal static bool Broken;

        // Recent collection timestamps for a "per minute" rate.
        private static readonly Queue<float> recent = new Queue<float>();
        internal static int PerMinute => recent.Count;

        internal static void PerFrame(bool measuring, int state, float now)
        {
            if (Broken) return;
            try
            {
                int c = Il2CppSystem.GC.CollectionCount(0);
                CollectionsThisFrame = LastCount < 0 ? 0 : Math.Max(0, c - LastCount);
                LastCount = c;
                if (CollectionsThisFrame > 0)
                {
                    for (int i = 0; i < CollectionsThisFrame; i++) recent.Enqueue(now);
                    if (measuring) SesCollections[state] += CollectionsThisFrame;
                }
                while (recent.Count > 0 && now - recent.Peek() > 60f) recent.Dequeue();
            }
            catch (Exception e)
            {
                Broken = true;
                Runner.ReportError("gc-count", e);
            }
        }

        internal static void Sample()
        {
            try
            {
                HeapUsedMb = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024);
                HeapSizeMb = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / (1024 * 1024);
                Incremental = UnityEngine.Scripting.GarbageCollector.isIncremental;
                Mode = UnityEngine.Scripting.GarbageCollector.GCMode.ToString();
                SliceNs = UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds;
            }
            catch (Exception e)
            {
                Runner.ReportError("gc-sample", e);
            }
        }
    }

    /// <summary>Records frames slower than SpikeThresholdMs with what was running in them.</summary>
    internal static class Spikes
    {
        internal sealed class Spike
        {
            public DateTime Time;
            public int State;
            public double FrameMs;
            public double[] Phases = new double[SODPerformance.Phases.Count];
            public int FixedSteps;
            public int GcCollections;
            public List<(string label, double ms)> TopSections = new List<(string, double)>();
            public List<string> SlowAi = new List<string>();
            public string Cause;
        }

        private const int Keep = 300;
        internal static readonly List<Spike> Recent = new List<Spike>();
        internal static readonly long[] SesCount = new long[2];
        internal static readonly Dictionary<string, (int count, double totalMs)>[] ByCause =
            { new Dictionary<string, (int, double)>(), new Dictionary<string, (int, double)>() };
        internal static string LastCause = "";

        internal static void ResetSession()
        {
            Recent.Clear();
            SesCount[0] = SesCount[1] = 0;
            ByCause[0].Clear();
            ByCause[1].Clear();
            LastCause = "";
        }

        /// <summary>Call after Phases.OnUpdate closed a frame and before Profiler.EndFrame resets the per-frame section ticks.</summary>
        internal static void Check(bool measuring, int state)
        {
            double frame = Phases.LastFrameMs;
            if (!measuring || frame < PerfConfig.SpikeThresholdMs.Value)
            {
                AiSpikes.ClearFrame();
                return;
            }

            var s = new Spike
            {
                Time = DateTime.Now,
                State = state,
                FrameMs = frame,
                FixedSteps = Phases.LastFixedSteps,
                GcCollections = GcStats.CollectionsThisFrame,
            };
            Array.Copy(Phases.Last, s.Phases, Phases.Count);

            double biggestSection = 0, biggestNested = 0;
            string biggestSectionLabel = null, biggestNestedLabel = null;
            if (Profiler.Installed)
            {
                foreach (var sec in Profiler.Active)
                {
                    double ms = Profiler.FrameTicksOf(sec.Id) * Profiler.TicksToMs;
                    if (ms >= 1.0) s.TopSections.Add((sec.Label.Trim(), ms));
                    if (!sec.Nested && ms > biggestSection)
                    {
                        biggestSection = ms;
                        biggestSectionLabel = sec.Label.Trim();
                    }
                    if (sec.Nested && ms > biggestNested)
                    {
                        biggestNested = ms;
                        biggestNestedLabel = sec.Label.Trim();
                    }
                }
                s.TopSections = s.TopSections.OrderByDescending(x => x.ms).Take(4).ToList();
            }
            s.SlowAi.AddRange(AiSpikes.TakeFrame());

            // Classification, most specific first.
            if (s.GcCollections > 0) s.Cause = "garbage collection (GC)";
            else if (biggestSectionLabel != null && biggestSection >= frame * 0.4) s.Cause = "script: " + biggestSectionLabel;
            // A nested section whose caller is not profiled (e.g. room culling from a coroutine).
            else if (biggestNestedLabel != null && biggestNested >= frame * 0.4) s.Cause = "script: " + biggestNestedLabel;
            else if (s.FixedSteps >= 4 && s.Phases[Phases.Physics] >= frame * 0.4) s.Cause = "physics catch-up";
            else
            {
                int max = 0;
                for (int i = 1; i < Phases.Count; i++) if (s.Phases[i] > s.Phases[max]) max = i;
                s.Cause = max == Phases.Scripts ? "unprofiled scripts/animation"
                        : max == Phases.Render ? "rendering"
                        : max == Phases.Wait ? "wait (GPU/present/loading)"
                        : "physics";
            }
            LastCause = s.Cause;

            SesCount[state]++;
            var dict = ByCause[state];
            dict.TryGetValue(s.Cause, out var agg);
            dict[s.Cause] = (agg.count + 1, agg.totalMs + frame);

            Recent.Add(s);
            if (Recent.Count > Keep) Recent.RemoveAt(0);
        }

        internal static string Describe(Spike s)
        {
            var sb = new StringBuilder();
            sb.Append($"{s.Time:HH:mm:ss} {(s.State == 1 ? "ON " : "OFF")} {s.FrameMs,6:0} ms  {s.Cause}");
            sb.Append($"  | wait {s.Phases[Phases.Wait]:0} phys {s.Phases[Phases.Physics]:0} ({s.FixedSteps} steps) scripts {s.Phases[Phases.Scripts]:0} render {s.Phases[Phases.Render]:0}");
            if (s.GcCollections > 0) sb.Append($" | GC x{s.GcCollections}");
            if (s.TopSections.Count > 0) sb.Append(" | " + string.Join(", ", s.TopSections.Select(x => $"{x.label} {x.ms:0}")));
            foreach (var ai in s.SlowAi) sb.Append("\n            " + ai);
            return sb.ToString();
        }
    }

    /// <summary>Remembers which citizen/goal/action was running when one AI call took a long time.</summary>
    internal sealed class SpikeCatcher
    {
        internal readonly string Name;
        private readonly long[] starts = new long[16];
        private int depth;
        internal readonly List<string> Frame = new List<string>();
        internal readonly Dictionary<string, (int count, double worstMs, double totalMs)> ByAction = new Dictionary<string, (int, double, double)>();
        internal readonly Dictionary<string, int> ByContext = new Dictionary<string, int>();
        internal long Total;

        internal SpikeCatcher(string name) { Name = name; }

        internal void ResetSession()
        {
            Frame.Clear();
            ByAction.Clear();
            ByContext.Clear();
            Total = 0;
        }

        internal void Pre()
        {
            if (depth < starts.Length) starts[depth] = Stopwatch.GetTimestamp();
            depth++;
        }

        internal void Post(NewAIController ai)
        {
            depth--;
            if (depth < 0) { depth = 0; return; }
            if (depth >= starts.Length) return;
            double ms = (Stopwatch.GetTimestamp() - starts[depth]) * 1000.0 / Stopwatch.Frequency;
            if (ms < PerfConfig.AiSpikeMs.Value || !Runner.MeasuringNow) return;
            try { Record(ai, ms); }
            catch (Exception e) { Runner.ReportError("spike-" + Name, e); }
        }

        private void Record(NewAIController ai, double ms)
        {
            string who = "?", goal = "-", action = "-", tier = "?";
            if (ai != null)
            {
                Human h = ai.human;
                if (h != null) who = h.citizenName ?? "?";
                NewAIGoal g = ai.currentGoal;
                if (g != null) goal = g.name ?? "-";
                NewAIAction a = ai.currentAction;
                if (a != null) action = a.name ?? "-";
                tier = ai.tickRate.ToString();
            }
            string ctx = AiTimeBudget.InScheduler ? "AI queue" : AiTimeBudget.InLateUpdate ? "movement" : "other";
            Total++;
            string key = $"{goal} / {action}";
            ByAction.TryGetValue(key, out var agg);
            ByAction[key] = (agg.count + 1, Math.Max(agg.worstMs, ms), agg.totalMs + ms);
            ByContext.TryGetValue(ctx, out int c);
            ByContext[ctx] = c + 1;
            if (Frame.Count < 8) Frame.Add($"{Name} {ms:0.0} ms  {who}  goal '{goal}'  action '{action}'  tier {tier}  via {ctx}");
        }

        internal void Reset()
        {
            Frame.Clear();
            depth = 0;
        }
    }

    internal static class AiSpikes
    {
        internal static readonly SpikeCatcher Tick = new SpikeCatcher("slow decision");
        internal static readonly SpikeCatcher Move = new SpikeCatcher("slow movement");

        internal static void Install(Harmony harmony, bool movement)
        {
            Patch(harmony, "AITick", nameof(TickPre), nameof(TickPost));
            if (movement) Patch(harmony, "FrequentUpdate", nameof(MovePre), nameof(MovePost));
        }

        private static void Patch(Harmony harmony, string method, string pre, string post)
        {
            try
            {
                var target = AccessTools.GetDeclaredMethods(typeof(NewAIController)).FirstOrDefault(m => m.Name == method && !m.IsStatic);
                if (target == null) { Plugin.L.LogWarning($"Spike catcher: {method} not found."); return; }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(AiSpikes), pre)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(AiSpikes), post)));
                Plugin.L.LogInfo($"Spike catcher installed on NewAIController.{method}.");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Spike catcher not installed on {method}: {e.GetType().Name}: {e.Message}");
            }
        }

        public static void TickPre() => Tick.Pre();
        public static void TickPost(NewAIController __instance) => Tick.Post(__instance);
        public static void MovePre() => Move.Pre();
        public static void MovePost(NewAIController __instance) => Move.Post(__instance);

        internal static List<string> TakeFrame()
        {
            var copy = new List<string>(Tick.Frame);
            copy.AddRange(Move.Frame);
            Tick.Reset();
            Move.Reset();
            return copy;
        }

        internal static void ClearFrame()
        {
            Tick.Reset();
            Move.Reset();
        }
    }

    /// <summary>
    /// Seconds that averaged under 45 fps (sustained drops, e.g. fights or running through the city), with what the frame
    /// time went into during that second. Stutters (single long frames) are recorded separately.
    /// </summary>
    internal static class SlowSeconds
    {
        internal sealed class Entry
        {
            public DateTime Time;
            public double Fps;
            public double[] Phases = new double[4];
            public string Top = "";
            public int VeryHigh, Visible;
        }

        internal static readonly System.Collections.Generic.List<Entry> Recent = new System.Collections.Generic.List<Entry>();
        internal static long Total;
        internal static void ResetSession() { Recent.Clear(); Total = 0; }
        private static double secMs;
        private static int secFrames, secMeasured;

        internal static void AddFrame(float frameMs, bool measuring)
        {
            secMs += frameMs;
            secFrames++;
            if (measuring) secMeasured++;
        }

        /// <summary>Called once per second, after Profiler.CloseWindow and Phases.CloseWindow.</summary>
        internal static void CloseSecond()
        {
            try
            {
                if (secFrames == 0) return;
                double avg = secMs / secFrames;
                bool allMeasured = secMeasured == secFrames;
                if (allMeasured && avg > 1000.0 / 45.0)
                {
                    Total++;
                    var e = new Entry { Time = DateTime.Now, Fps = 1000.0 / avg, VeryHigh = GameProbe.VeryHigh, Visible = GameProbe.VisibleHumans };
                    for (int i = 0; i < 4; i++) e.Phases[i] = global::SODPerformance.Phases.WindowAvg[i];
                    var top = Profiler.Active.Where(x => x.AvgMs >= 0.5).OrderByDescending(x => x.AvgMs).Take(5)
                        .Select(x => $"{x.Label.Trim()} {x.AvgMs:0.0}");
                    e.Top = string.Join(", ", top);
                    Recent.Add(e);
                    if (Recent.Count > 40) Recent.RemoveAt(0);
                }
            }
            catch { }
            finally
            {
                secMs = 0;
                secFrames = 0;
                secMeasured = 0;
            }
        }
    }
}
