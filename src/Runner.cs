using System;
using UnityEngine;

namespace SODPerformance
{
    /// <summary>
    /// The only class registered in IL2CPP. It keeps no managed state and hands everything to static code,
    /// so no method needs [HideFromIl2Cpp] and the injected type stays trivial.
    /// </summary>
    public class PerfRunner : MonoBehaviour
    {
        public PerfRunner(IntPtr ptr) : base(ptr) { }

        public void FixedUpdate()
        {
            Phases.OnFixedUpdate();
        }

        public void Update()
        {
            try { Runner.Tick(); }
            catch (Exception e) { Runner.ReportError("update", e); }
        }

        public void LateUpdate()
        {
            Phases.OnLateUpdate();
        }

        public void OnGUI()
        {
            try
            {
                Event ev = Event.current;
                if (ev != null && ev.type == EventType.Repaint) Phases.OnRepaint();
                Overlay.Draw();
            }
            catch (Exception e) { Runner.ReportError("overlay", e); }
        }
    }

    internal static class Runner
    {
        internal static bool OptimizationsOn;
        /// <summary>A city is loaded, the game is unpaused and time runs at normal speed (fast-forward is counted separately).</summary>
        internal static bool MeasuringNow;
        internal static bool FastForwardNow;
        internal static double FastForwardSeconds;
        internal static long FastForwardFrames;
        internal static bool OverlayVisible;
        internal static bool AutoAB;
        internal static int ABSwitches;
        internal static float ABRemaining;
        internal static string LastReportPath = "";
        internal static long CutsPerSecond;
        /// <summary>Short "mod is active" note after a city load while the overlay is hidden.</summary>
        internal static float ToastUntil;

        private static bool initialized;
        private static bool keysBroken;
        private static float nextWindow, nextSample, nextAutoReport, abTimer;
        private static int errorCount;

        private static void Init()
        {
            initialized = true;
            OptimizationsOn = PerfConfig.OptimizationsEnabled.Value;
            OverlayVisible = PerfConfig.OverlayOnStart.Value;
            AutoAB = PerfConfig.AutoABSeconds.Value > 0;
            float now = Time.realtimeSinceStartup;
            nextAutoReport = now + Math.Max(10, PerfConfig.AutoReportSeconds.Value);
            if (AutoAB) Plugin.L.LogInfo($"Auto A/B test mode: optimizations switch ON/OFF every {PerfConfig.AutoABSeconds.Value} s of play.");
        }

        internal static void Tick()
        {
            if (!initialized) Init();

            float now = Time.realtimeSinceStartup;
            float frameMs = Time.unscaledDeltaTime * 1000f;

            // 1) Account for the frame that just ended, under the state that was active during it.
            AiTimeBudget.FrameReset();
            GameProbe.CheckSession();
            bool playing = GameProbe.IsPlaying();
            float timeMult = GameProbe.TimeMultiplierNow();
            bool fastForward = playing && timeMult > 1.01f;
            bool measuring = playing && !fastForward;
            int state = OptimizationsOn ? 1 : 0;
            if (fastForward)
            {
                FastForwardFrames++;
                FastForwardSeconds += Time.unscaledDeltaTime;
            }

            Sampler.FrameTick(Time.frameCount, frameMs, SamplerTag(measuring, fastForward));
            Phases.OnUpdate(measuring, state);
            GcStats.PerFrame(measuring, state, now);
            Spikes.Check(measuring, state);          // must run before Profiler.EndFrame clears per-frame ticks
            Profiler.EndFrame(measuring, state);
            FrameStats.Add(frameMs, measuring, state);
            SlowSeconds.AddFrame(frameMs, measuring);

            // 2) Input and test-mode switching (affects the next frame).
            HandleKeys();
            if (AutoAB && measuring)
            {
                int period = Math.Max(5, PerfConfig.AutoABSeconds.Value);
                abTimer += Time.unscaledDeltaTime;
                ABRemaining = period - abTimer;
                if (abTimer >= period)
                {
                    abTimer = 0f;
                    OptimizationsOn = !OptimizationsOn;
                    ABSwitches++;
                    Overlay.Rebuild();
                }
            }

            // 3) Apply optimizations (they take effect from the next frame).
            AiGovernor.Tick(OptimizationsOn, frameMs, now);
            LightBudget.Tick(OptimizationsOn);
            LogTweaks.Tick(OptimizationsOn);
            ShadowTuning.Tick(OptimizationsOn);
            GcTuning.Tick(OptimizationsOn);
            OtherLightShadows.Tick(OptimizationsOn, now);
            LoadProfiler.Tick(playing);
            CullingCache.Tick(OptimizationsOn, playing, now);
            CpuPlacement.Tick(OptimizationsOn);
            // Also during loading and city generation ("Creating interiors" asks for breakers constantly). The index is
            // brought up to date before every answer and hands the question back to the game when it cannot be.
            BreakerLookup.EnabledNow = OptimizationsOn && BreakerLookup.Installed;
            SoundOcclusionDefer.EnabledNow = OptimizationsOn && SoundOcclusionDefer.Installed && PerfConfig.DeferSoundOcclusion.Value;
            SoundOcclusionDefer.Tick();
            RoomHideDefer.EnabledNow = OptimizationsOn && RoomHideDefer.Installed && PerfConfig.DeferRoomHiding.Value && playing;
            RoomHideDefer.ShowEnabledNow = OptimizationsOn && RoomHideDefer.Installed && PerfConfig.SpreadFarRoomShowing.Value && playing;
            if (Profiler.ModRoomVisibilityId >= 0) Profiler.Begin(Profiler.ModRoomVisibilityId);
            RoomHideDefer.Tick();
            if (Profiler.ModRoomVisibilityId >= 0) Profiler.End(Profiler.ModRoomVisibilityId);
            RoomContentLoader.EnabledNow = OptimizationsOn && PerfConfig.SmoothRoomContentLoading.Value && playing;
            if (Profiler.ModRoomContentsId >= 0) Profiler.Begin(Profiler.ModRoomContentsId);
            RoomContentLoader.Tick();
            if (Profiler.ModRoomContentsId >= 0) Profiler.End(Profiler.ModRoomContentsId);
            QuietGc.Tick(OptimizationsOn, GameProbe.HasCity, playing, fastForward, now);
            TrashTrickle.Tick(OptimizationsOn, playing, now);
            MovementStagger.EnabledNow = OptimizationsOn && MovementStagger.Installed;
            // Safety: if the AI queue gets long, let the game run its full count again until it catches up.
            bool queueTooLong = AiGovernor.AiDelaySeconds > PerfConfig.MaxAiDelaySeconds.Value * 2f;
            AiTimeBudget.EnabledNow = OptimizationsOn && AiTimeBudget.Installed && PerfConfig.AiFrameBudgetMs.Value > 0f
                                      && !fastForward && AiGovernor.Vanilla > 0 && !queueTooLong;
            AiTimeBudget.Status = !AiTimeBudget.Installed ? "not installed"
                                : !OptimizationsOn ? "off"
                                : fastForward ? "off (fast-forward)"
                                : queueTooLong ? "paused (AI queue long)"
                                : AiTimeBudget.EnabledNow ? "active" : "waiting for city";
            MeasuringNow = measuring;
            FastForwardNow = fastForward;

            // 4) Periodic work.
            if (now >= nextWindow)
            {
                nextWindow = now + 1f;
                Profiler.CloseWindow();
                Phases.CloseWindow();
                SlowSeconds.CloseSecond();
                MovementStagger.CloseWindow();
                CutsPerSecond = AiTimeBudget.WindowCuts;
                AiTimeBudget.WindowCuts = 0;
            }

            if (now >= nextSample)
            {
                nextSample = now + 0.5f;
                FrameStats.ComputeWindow();
                GameProbe.Sample();
                GcStats.Sample();
                QuietGc.Sample(measuring, now);
                if (OverlayVisible) Overlay.Rebuild();
            }

            int auto = PerfConfig.AutoReportSeconds.Value;
            if (auto > 0 && measuring && now >= nextAutoReport)
            {
                nextAutoReport = now + Math.Max(10, auto);
                Report.Write(manual: false);
            }
        }

        /// <summary>A new city is loading: every statistic in the report starts again, so a report describes one city.</summary>
        internal static void ResetSessionStats()
        {
            FrameStats.ResetSession();
            Phases.ResetSession();
            GcStats.ResetSession();
            Spikes.ResetSession();
            Profiler.ResetSession();
            SlowSeconds.ResetSession();
            AiSpikes.Tick.ResetSession();
            AiSpikes.Move.ResetSession();
            FastForwardSeconds = 0;
            FastForwardFrames = 0;
            AiTimeBudget.SesCuts[0] = AiTimeBudget.SesCuts[1] = 0;
        }

        private static int SamplerTag(bool measuring, bool fastForward)
        {
            if (!Sampler.Running) return Sampler.TagPaused;
            if (LoadProfiler.Loading)
            {
                int st = -1;
                try
                {
                    CityConstructor cc = CityConstructor.Instance;
                    if (cc != null)
                    {
                        st = (int)cc.loadState;
                        if (!Sampler.StateNames.ContainsKey(st)) Sampler.StateNames[st] = cc.loadState.ToString();
                    }
                }
                catch { }
                return Sampler.LoadTag(st);
            }
            if (fastForward) return Sampler.TagFastForward;
            return measuring ? Sampler.TagPlaying : Sampler.TagPaused;
        }

        private static void HandleKeys()
        {
            if (keysBroken) return;
            try
            {
                if (Input.GetKeyDown(PerfConfig.OverlayKey.Value))
                {
                    OverlayVisible = !OverlayVisible;
                    if (OverlayVisible) Overlay.Rebuild();
                }
                if (Input.GetKeyDown(PerfConfig.ToggleKey.Value))
                {
                    OptimizationsOn = !OptimizationsOn;
                    if (AutoAB)
                    {
                        AutoAB = false;
                        Plugin.L.LogInfo("Auto A/B test mode stopped by toggle key.");
                    }
                    Plugin.L.LogInfo($"Optimizations {(OptimizationsOn ? "ON" : "OFF")} (manual toggle).");
                    Overlay.Rebuild();
                }
                if (Input.GetKeyDown(PerfConfig.ReportKey.Value))
                {
                    Report.Write(manual: true);
                    Overlay.Rebuild();
                }
            }
            catch (Exception e)
            {
                keysBroken = true;
                Plugin.L.LogWarning($"Hotkeys disabled: legacy Unity input is not available ({e.GetType().Name}: {e.Message}). " +
                                    "Use the config file instead; reports are still written automatically.");
            }
        }

        /// <summary>Logs the first few errors with details, then goes quiet so the mod never becomes a log flood itself.</summary>
        internal static void ReportError(string where, Exception e)
        {
            errorCount++;
            if (errorCount <= 5)
                Plugin.L.LogError($"[{where}] {e}");
            else if (errorCount == 6)
                Plugin.L.LogError("Further errors suppressed.");
        }
    }
}
