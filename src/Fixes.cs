using System;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace SODPerformance
{
    /// <summary>
    /// Fix 1 (0.3): a time budget for citizen AI decisions per frame.
    ///
    /// In vanilla, CitizenBehaviour.Update takes citizens off its update list and runs AITick on each until a per-frame
    /// count limit (AITicksPerFrame, scaled by the game speed) is reached, checking that limit again before each citizen.
    /// Normally a decision costs 0.15-0.3 ms, but some
    /// (e.g. GetSleep/PrepForSleep1, Work/WorkAtDesk, Wander) take 10-170 ms. Several of those landing in one frame
    /// produced most of the 100-300 ms stutters in testing.
    ///
    /// After each decision we add up the time spent; once it passes FrameBudgetMs we set AITicksPerFrame to 0, so the
    /// loop's own check ends it before the next citizen is popped (nobody is skipped or dropped, the rest of the queue just
    /// waits a frame, exactly like when the count limit is hit). The postfix restores the value. At least one decision
    /// always runs per frame. Not applied while fast-forwarding, so the simulation keeps up with game time.
    /// </summary>
    internal static class AiTimeBudget
    {
        internal static bool InScheduler;
        internal static bool InLateUpdate;
        /// <summary>Set every frame by the runner: optimizations on, budget > 0, normal game speed, vanilla budget known.</summary>
        internal static bool EnabledNow;

        private static bool active;
        private static bool cut;
        private static int restore;
        private static CitizenBehaviour cb;
        private static long tickStart;
        private static int depth;
        private static double spentMs;

        internal static double LastSpentMs;
        internal static long WindowCuts;
        internal static readonly long[] SesCuts = new long[2];
        internal static bool Installed;
        internal static string Status = "";

        internal static void Install(Harmony harmony)
        {
            try
            {
                var update = AccessTools.GetDeclaredMethods(typeof(CitizenBehaviour)).First(m => m.Name == "Update" && m.GetParameters().Length == 0);
                var late = AccessTools.GetDeclaredMethods(typeof(CitizenBehaviour)).First(m => m.Name == "LateUpdate" && m.GetParameters().Length == 0);
                var tick = AccessTools.GetDeclaredMethods(typeof(NewAIController)).First(m => m.Name == "AITick" && !m.IsStatic);
                harmony.Patch(update, prefix: H(nameof(SchedPrefix)), postfix: H(nameof(SchedPostfix)));
                harmony.Patch(late, prefix: H(nameof(LatePrefix)), postfix: H(nameof(LatePostfix)));
                harmony.Patch(tick, prefix: H(nameof(TickPrefix)), postfix: H(nameof(TickPostfix)));
                Installed = true;
                Plugin.L.LogInfo($"AI time budget installed ({PerfConfig.AiFrameBudgetMs.Value} ms per frame).");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"AI time budget not installed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static HarmonyMethod H(string name) => new HarmonyMethod(AccessTools.Method(typeof(AiTimeBudget), name));

        public static void SchedPrefix(CitizenBehaviour __instance)
        {
            InScheduler = true;
            depth = 0;
            spentMs = 0;
            cut = false;
            active = EnabledNow;
            cb = active ? __instance : null;
        }

        public static void SchedPostfix()
        {
            if (cut && cb != null)
            {
                try { cb.AITicksPerFrame = restore; }
                catch (Exception e) { Runner.ReportError("ai-budget-restore", e); }
            }
            LastSpentMs = spentMs;
            InScheduler = false;
            active = false;
            cut = false;
            cb = null;
            depth = 0;
        }

        public static void LatePrefix() => InLateUpdate = true;
        public static void LatePostfix() => InLateUpdate = false;

        public static void TickPrefix()
        {
            if (InScheduler && depth++ == 0) tickStart = Stopwatch.GetTimestamp();
        }

        public static void TickPostfix()
        {
            if (!InScheduler) return;
            if (--depth > 0) return;
            depth = 0;
            spentMs += (Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency;
            if (!active || cut || spentMs < PerfConfig.AiFrameBudgetMs.Value) return;
            try
            {
                restore = cb.AITicksPerFrame;
                cb.AITicksPerFrame = 0;   // the vanilla loop checks this before popping the next citizen
                cut = true;
                WindowCuts++;
                if (Runner.MeasuringNow) SesCuts[Runner.OptimizationsOn ? 1 : 0]++;
            }
            catch (Exception e)
            {
                active = false;
                Runner.ReportError("ai-budget", e);
            }
        }

        /// <summary>Called from our Update (never inside CitizenBehaviour.Update): clears state left behind by an exception.</summary>
        internal static void FrameReset()
        {
            InScheduler = false;
            InLateUpdate = false;
            active = false;
            cut = false;
            cb = null;
            depth = 0;
        }
    }

    /// <summary>
    /// Fix 2 (0.3, experiment): shorter shadow distance for lamps/room lights, and an optional shadow-update budget.
    ///
    /// In vanilla, each light's shadow fade distance comes from its preset multiplied by Game.shadowFadeDistanceMultiplier,
    /// and Game.SetLightDistance refreshes every light the same way. Scaling that multiplier and refreshing the lights
    /// reproduces the game's own mechanism with a smaller value. A big test city had about 500 shadow-casting lights.
    /// CitizenBehaviour.Update re-renders at most Game.maxUpdateDynamicShadowsPerFrame queued shadow maps per frame.
    /// </summary>
    internal static class ShadowTuning
    {
        private static float gameMult = -1f, appliedMult = -1f;
        private static int gameMaxUpdates = int.MinValue, appliedMaxUpdates = int.MinValue;
        internal static float GameMult => gameMult;
        internal static float AppliedMult => appliedMult;
        internal static int GameMaxUpdates => gameMaxUpdates;
        internal static int AppliedMaxUpdates => appliedMaxUpdates;
        internal static int LastRefreshCount;
        internal static double LastRefreshMs;

        internal static void Tick(bool optimizationsOn)
        {
            Game g = Game.Instance;
            if (g == null) return;

            // Shadow fade distance multiplier. If the value differs from what we last applied, the game (or its options menu) set it.
            float cur = g.shadowFadeDistanceMultiplier;
            if (appliedMult < 0f || Math.Abs(cur - appliedMult) > 0.0001f) gameMult = cur;
            float scale = optimizationsOn ? Math.Max(0.1f, Math.Min(1f, PerfConfig.ShadowDistanceScale.Value)) : 1f;
            float want = gameMult * scale;
            if (Math.Abs(cur - want) > 0.0001f)
            {
                g.shadowFadeDistanceMultiplier = want;
                RefreshLights();
            }
            appliedMult = want;

            // Shadow map updates per frame.
            int curU = g.maxUpdateDynamicShadowsPerFrame;
            if (appliedMaxUpdates == int.MinValue || curU != appliedMaxUpdates) gameMaxUpdates = curU;
            int over = PerfConfig.MaxShadowUpdatesPerFrame.Value;
            int wantU = optimizationsOn && over >= 0 ? over : gameMaxUpdates;
            if (curU != wantU) g.maxUpdateDynamicShadowsPerFrame = wantU;
            appliedMaxUpdates = wantU;
        }

        /// <summary>Same as the end of Game.SetLightDistance: re-apply fade distances on every LightController (inactive ones too).</summary>
        private static void RefreshLights()
        {
            long t0 = Stopwatch.GetTimestamp();
            int n = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LightController>(), true);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        LightController lc = arr[i]?.TryCast<LightController>();
                        if (lc == null || !lc.isSetup) continue;
                        lc.UpdateFadeDistances();
                        n++;
                    }
                }
            }
            catch (Exception e)
            {
                Runner.ReportError("shadow-refresh", e);
            }
            LastRefreshCount = n;
            LastRefreshMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        internal static void ResetForNewSession()
        {
            gameMult = appliedMult = -1f;
            gameMaxUpdates = appliedMaxUpdates = int.MinValue;
        }
    }
}

namespace SODPerformance
{
    /// <summary>
    /// Fix 3 (0.4): stagger far-away citizens' movement updates.
    ///
    /// In vanilla, CitizenBehaviour.LateUpdate updates the movement of citizens who are not visible on a fixed tick
    /// (0.2 s of game time), less often the lower their detail tier: low on every 11th tick, veryLow on every 17th, all
    /// selected by one shared tick counter. So every 17th tick all ~1,900 veryLow citizens are moved in ONE frame
    /// (~110 ms in a 2,120-citizen city).
    ///
    /// While LateUpdate runs we give the game a counter V with the same value mod 10 (so high/medium behave exactly as before)
    /// that is never divisible by 11 or 17, so the game's own pass never does the low/veryLow burst. If the pass ran, we put the
    /// real counter back (+1, with the game's wrap at 1501) and move the citizens whose list index puts them on this tick:
    /// (index + counter) % 17 == 0 for veryLow, % 11 for low. Same eligibility as the game (ai present and enabled, not visible,
    /// tier matches), same period per citizen; FrequentUpdate uses each citizen's own time since its last update, so a
    /// different phase changes nothing about the result. Nothing else in the game reads that tick counter.
    /// </summary>
    internal static class MovementStagger
    {
        internal static bool EnabledNow;
        internal static bool Installed;

        private static bool active;
        private static int realCounter, fakeCounter;
        private static CitizenBehaviour cb;

        internal static long WindowUpdates, WindowTicks;
        internal static double WindowMaxMs;
        internal static long UpdatesLastSecond, TicksLastSecond;
        internal static double MaxMsLastSecond;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var late = AccessTools.GetDeclaredMethods(typeof(CitizenBehaviour)).First(m => m.Name == "LateUpdate" && m.GetParameters().Length == 0);
                // Priority.First on the postfix: run before the profiler's postfix (so the work is counted inside LateUpdate)
                // and before the AI budget's LateUpdate postfix (so spike context still says "movement").
                harmony.Patch(late,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(MovementStagger), nameof(Prefix))) { priority = Priority.First },
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(MovementStagger), nameof(Postfix))) { priority = Priority.First });
                Installed = true;
                Plugin.L.LogInfo("Movement stagger installed.");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Movement stagger not installed: {e.GetType().Name}: {e.Message}");
            }
        }

        public static void Prefix(CitizenBehaviour __instance)
        {
            active = false;
            if (!EnabledNow || __instance == null) return;
            try
            {
                cb = __instance;
                realCounter = cb.frequentTickCounter;
                fakeCounter = FakeFor(realCounter);
                cb.frequentTickCounter = fakeCounter;
                active = true;
            }
            catch (Exception e)
            {
                Runner.ReportError("stagger-pre", e);
            }
        }

        public static void Postfix()
        {
            if (!active) return;
            active = false;
            try
            {
                bool ticked = cb.frequentTickCounter == fakeCounter + 1;
                int next = ticked ? (realCounter + 1 >= 1501 ? 1 : realCounter + 1) : realCounter;
                cb.frequentTickCounter = next;
                if (ticked) RunSlices(realCounter);
            }
            catch (Exception e)
            {
                Runner.ReportError("stagger-post", e);
            }
            finally
            {
                cb = null;
            }
        }

        /// <summary>A counter value with the same remainder mod 10 (keeps the %2 and %5 tests) that is never a multiple of 11 or 17.</summary>
        internal static int FakeFor(int real)
        {
            int baseMod = ((real % 10) + 10) % 10;
            for (int k = 1; k < 140; k++)
            {
                int v = baseMod + 10 * k;
                if (v % 11 != 0 && v % 17 != 0) return v;
            }
            return 13; // unreachable in practice
        }

        private static void RunSlices(int phase)
        {
            long t0 = Stopwatch.GetTimestamp();
            int updated = 0;
            CityData city = CityData.Instance;
            var list = city?.citizenDirectory;
            if (list == null) return;
            int n = list.Count;
            updated += Slice(list, n, phase, 17, NewAIController.AITickRate.veryLow);
            updated += Slice(list, n, phase, 11, NewAIController.AITickRate.low);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            WindowUpdates += updated;
            WindowTicks++;
            if (ms > WindowMaxMs) WindowMaxMs = ms;
        }

        private static int Slice(Il2CppSystem.Collections.Generic.List<Citizen> list, int n, int phase, int period, NewAIController.AITickRate tier)
        {
            int count = 0;
            int start = ((-phase % period) + period) % period;   // (index + phase) % period == 0
            for (int i = start; i < n; i += period)
            {
                Citizen c = list[i];
                if (c == null || c.visible) continue;
                NewAIController ai = c.ai;
                if (ai == null || !ai.enabled || ai.tickRate != tier) continue;
                ai.FrequentUpdate();
                count++;
            }
            return count;
        }

        internal static void CloseWindow()
        {
            UpdatesLastSecond = WindowUpdates;
            TicksLastSecond = WindowTicks;
            MaxMsLastSecond = WindowMaxMs;
            WindowUpdates = WindowTicks = 0;
            WindowMaxMs = 0;
        }
    }

    /// <summary>Experiment (0.4): give the incremental GC more time per frame so it finishes before a full stop-the-world collection.</summary>
    internal static class GcTuning
    {
        private static ulong original;
        private static bool captured;
        private static ulong applied;

        internal static void Tick(bool optimizationsOn)
        {
            try
            {
                if (!UnityEngine.Scripting.GarbageCollector.isIncremental) return;
                ulong cur = UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds;
                if (!captured || (cur != applied)) { original = cur; captured = true; }
                float ms = PerfConfig.GcSliceMs.Value;
                ulong want = optimizationsOn && ms > 0 ? (ulong)(ms * 1_000_000) : original;
                if (cur != want) UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = want;
                applied = want;
            }
            catch (Exception e)
            {
                Runner.ReportError("gc-slice", e);
            }
        }

        internal static ulong Original => original;
    }
}

namespace SODPerformance
{
    /// <summary>
    /// Fix 4 (0.5): cap the shadow fade distance of shadow-casting lights that the game's LightController does not manage.
    /// The 0.4 report found ~250 such lights (groups 'Spot Light', 'PointLight', ...), most with shadow update mode EveryFrame and
    /// all of one group "in shadow range" from wherever the player stood, i.e. HDRP's default fade distance. The original value of
    /// each light is remembered and restored when optimizations are OFF.
    /// </summary>
    internal static class OtherLightShadows
    {
        private sealed class Entry
        {
            public UnityEngine.Rendering.HighDefinition.HDAdditionalLightData Hd;
            public float Original;
            public float Applied = -1f;
            public string Group;
        }

        private static readonly System.Collections.Generic.Dictionary<IntPtr, Entry> tracked = new System.Collections.Generic.Dictionary<IntPtr, Entry>();
        private static float nextScan;
        private static bool appliedOn;
        private static float appliedCap = -1f;
        internal static int Tracked, Capped;
        internal static double LastScanMs;

        internal static void Tick(bool optimizationsOn, float now)
        {
            float cap = PerfConfig.OtherLightShadowDistance.Value;
            bool want = optimizationsOn && cap > 0f;
            if (now < nextScan && want == appliedOn && Math.Abs(cap - appliedCap) < 0.001f) return;
            nextScan = now + 20f;   // 0.5 scanned every 5 s and took ~27 ms; new lights appear only when the city streams in
            appliedOn = want;
            appliedCap = cap;
            Scan(want, cap);
        }

        private static void Scan(bool want, float cap)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var owned = new System.Collections.Generic.HashSet<IntPtr>();
                var lcs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LightController>());
                if (lcs != null)
                    for (int i = 0; i < lcs.Length; i++)
                    {
                        var lc = lcs[i]?.TryCast<LightController>();
                        var lcLight = lc?.lightComponent;
                        if (lcLight != null) owned.Add(lcLight.Pointer);
                    }

                var lights = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.Light>());
                int capped = 0;
                if (lights != null)
                {
                    for (int i = 0; i < lights.Length; i++)
                    {
                        try
                        {
                        var l = lights[i]?.TryCast<UnityEngine.Light>();
                        if (l == null) continue;
                        IntPtr ptr = l.Pointer;
                        if (owned.Contains(ptr)) continue;
                        // A destroyed light's address can be reused by a new one: a destroyed HDAdditionalLightData compares equal to null.
                        if (tracked.TryGetValue(ptr, out var e) && e.Hd == null)
                        {
                            tracked.Remove(ptr);
                            e = null;
                        }
                        if (e == null)
                        {
                            if (l.type == UnityEngine.LightType.Directional || l.shadows == UnityEngine.LightShadows.None) continue;
                            var hd = l.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
                            if (hd == null) continue;
                            string parent = l.transform.parent != null ? l.transform.parent.name : "-";
                            e = new Entry { Hd = hd, Original = hd.shadowFadeDistance, Group = $"{l.type} '{Clean(l.gameObject.name)}' under '{Clean(parent)}'" };
                            tracked[ptr] = e;
                        }
                        float target = want ? Math.Min(e.Original, cap) : e.Original;
                        if (Math.Abs(e.Applied - target) > 0.01f)
                        {
                            e.Hd.shadowFadeDistance = target;
                            e.Applied = target;
                        }
                        if (want && e.Original > cap) capped++;
                        }
                        catch (Exception exOne)
                        {
                            Runner.ReportError("other-light", exOne);
                        }
                    }
                }
                Tracked = tracked.Count;
                Capped = capped;
            }
            catch (Exception ex)
            {
                Runner.ReportError("other-light-shadows", ex);
            }
            LastScanMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        internal static string Clean(string n)
        {
            if (string.IsNullOrEmpty(n)) return "?";
            int cut = n.IndexOf('(');
            return (cut > 0 ? n.Substring(0, cut) : n).Trim();
        }

        /// <summary>Per group: count, average original fade distance (for the report).</summary>
        internal static System.Collections.Generic.IEnumerable<(string group, int count, float avgOriginal)> Groups()
        {
            return tracked.Values.GroupBy(e => e.Group).Select(g => (g.Key, g.Count(), g.Average(e => e.Original))).OrderByDescending(g => g.Item2);
        }
    }

    /// <summary>
    /// Fix 5 (0.5, experiment, off): trash trickle. Once per in-game hour, CleanupController.TrashUpdate deletes discarded items
    /// (only safe, expired ones away from the player) until 250 remain, all in one frame (1,046 ms in testing). Calling the same method every 10 s when the
    /// list is just above 250 keeps every batch small; the selection rules are the game's own.
    /// </summary>
    internal static class TrashTrickle
    {
        private const int GameLimit = 250;
        private static float next;
        internal static int Calls, LastRemoved, TotalRemoved;
        internal static double LastMs, WorstMs;

        internal static void Tick(bool optimizationsOn, bool playing, float now)
        {
            if (!optimizationsOn || !playing || !PerfConfig.TrashTrickle.Value || now < next) return;
            next = now + 10f;
            try
            {
                CleanupController cc = CleanupController.Instance;
                var list = cc?.trash;
                if (list == null || list.Count <= GameLimit + 10) return;
                long t0 = Stopwatch.GetTimestamp();
                cc.TrashUpdate();
                LastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (LastMs > WorstMs) WorstMs = LastMs;
                Calls++;
                LastRemoved = cc.trashRemovedLastUpdate;
                TotalRemoved += Math.Max(0, LastRemoved);
            }
            catch (Exception e)
            {
                Runner.ReportError("trash-trickle", e);
            }
        }
    }
}
