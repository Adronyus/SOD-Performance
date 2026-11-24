using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SODPerformance
{
    internal enum AiBudgetMode
    {
        /// <summary>Leave CitizenBehaviour.AITicksPerFrame at the game's value.</summary>
        Vanilla,
        /// <summary>Use FixedBudget AI ticks per frame.</summary>
        Fixed,
        /// <summary>Lower the AI tick budget while the frame rate is under TargetFps, raise it back when there is headroom.</summary>
        Adaptive,
    }

    internal static class PerfConfig
    {
        // General
        internal static ConfigEntry<bool> OptimizationsEnabled;
        internal static ConfigEntry<bool> OverlayOnStart;
        internal static ConfigEntry<KeyCode> OverlayKey;
        internal static ConfigEntry<KeyCode> ReportKey;
        internal static ConfigEntry<KeyCode> ToggleKey;

        // AI
        internal static ConfigEntry<AiBudgetMode> AiMode;
        internal static ConfigEntry<int> TargetFps;
        internal static ConfigEntry<int> MinBudgetPercent;
        internal static ConfigEntry<int> MaxBudgetPercent;
        internal static ConfigEntry<int> FixedBudget;
        internal static ConfigEntry<float> MaxAiDelaySeconds;
        internal static ConfigEntry<bool> VanillaWhileFastForwarding;
        internal static ConfigEntry<float> AiFrameBudgetMs;
        internal static ConfigEntry<bool> StaggerFarMovement;
        internal static ConfigEntry<float> GcSliceMs;
        internal static ConfigEntry<bool> TrashTrickle;
        internal static ConfigEntry<int> ConfigVersion;
        internal static ConfigEntry<bool> CullingCache;
        internal static ConfigEntry<bool> PreferVCacheCores;
        internal static ConfigEntry<bool> QuietGc;
        internal static ConfigEntry<bool> DeferSoundOcclusion;
        internal static ConfigEntry<bool> DeferRoomHiding;
        internal static ConfigEntry<float> HideBudgetMs;
        internal static ConfigEntry<bool> SpreadFarRoomShowing;
        internal static ConfigEntry<bool> SmoothRoomContentLoading;
        internal static ConfigEntry<bool> FastPresetCompareOn;
        internal static ConfigEntry<bool> FastBreakerLookup;
        internal static ConfigEntry<float> ContentBudgetMs;
        internal static ConfigEntry<float> ContentBudgetBusyMs;
        internal static ConfigEntry<float> ShowNearDistance;
        internal static ConfigEntry<float> ShowBudgetMs;
        internal static ConfigEntry<float> ShowMaxDelayMs;
        internal static ConfigEntry<float> OtherLightShadowDistance;

        // Shadows
        internal static ConfigEntry<float> ShadowDistanceScale;
        internal static ConfigEntry<int> MaxShadowUpdatesPerFrame;

        // Logging
        internal static ConfigEntry<bool> StripLogStackTraces;
        internal static ConfigEntry<bool> StripErrorStackTraces;

        // Lights
        internal static ConfigEntry<int> LightChecksPerFrame;

        // Profiler
        internal static ConfigEntry<bool> ProfilerEnabled;
        internal static ConfigEntry<bool> ProfilePerInstanceUpdates;
        internal static ConfigEntry<int> AutoReportSeconds;
        internal static ConfigEntry<int> AutoABSeconds;
        internal static ConfigEntry<float> SpikeThresholdMs;
        internal static ConfigEntry<bool> CatchAiSpikes;
        internal static ConfigEntry<float> AiSpikeMs;
        internal static ConfigEntry<bool> CatchMovementSpikes;
        internal static ConfigEntry<bool> SamplingProfiler;
        internal static ConfigEntry<int> SamplingIntervalUs;

        internal static void Bind(ConfigFile c)
        {
            OptimizationsEnabled = c.Bind("General", "OptimizationsEnabled", true,
                "Master switch for every optimization below. Toggle in game with ToggleKey to compare against vanilla.");
            OverlayOnStart = c.Bind("General", "OverlayOnStart", false,
                "Show the performance overlay when the game starts.");
            OverlayKey = c.Bind("General", "OverlayKey", KeyCode.F8, "Show/hide the performance overlay.");
            ReportKey = c.Bind("General", "ReportKey", KeyCode.F7,
                "Write a detailed report to BepInEx/SODPerformance/ (includes object counts; causes one short hitch).");
            // 0.1.0 shipped with F9 here by mistake: F9 is the game's quickload and CityRenewal's test-spawn key.
            if (ReportKey.Value == KeyCode.F9)
            {
                ReportKey.Value = KeyCode.F7;
                Plugin.L?.LogInfo("ReportKey moved from F9 to F7 (F9 is used by the game and by CityRenewal).");
            }
            ToggleKey = c.Bind("General", "ToggleKey", KeyCode.F10, "Turn all optimizations on/off (A/B test).");

            AiMode = c.Bind("AI", "BudgetMode", AiBudgetMode.Vanilla,
                "How many citizen AI decisions (NewAIController.AITick) the game may run per frame. " +
                "Vanilla = game value (20). Fixed = FixedBudget. Adaptive = scale between MinBudgetPercent and MaxBudgetPercent of vanilla to hold TargetFps.");
            TargetFps = c.Bind("AI", "TargetFps", 60,
                new ConfigDescription("Frame rate the adaptive governor tries to hold.", new AcceptableValueRange<int>(20, 360)));
            MinBudgetPercent = c.Bind("AI", "MinBudgetPercent", 40,
                new ConfigDescription("Lowest AI budget the adaptive governor may use, as % of vanilla.", new AcceptableValueRange<int>(5, 100)));
            MaxBudgetPercent = c.Bind("AI", "MaxBudgetPercent", 100,
                new ConfigDescription("Highest AI budget the adaptive governor may use, as % of vanilla. Above 100 lets AI catch up faster when you have FPS headroom.", new AcceptableValueRange<int>(50, 300)));
            FixedBudget = c.Bind("AI", "FixedBudget", 10,
                new ConfigDescription("AI ticks per frame in Fixed mode.", new AcceptableValueRange<int>(1, 200)));
            MaxAiDelaySeconds = c.Bind("AI", "MaxAiDelaySeconds", 2.0f,
                new ConfigDescription("Safety limit. If the AI queue would take longer than this many seconds to work through at the current budget, " +
                                      "the governor will not go below the vanilla budget, so citizens never fall far behind.", new AcceptableValueRange<float>(0.25f, 30f)));
            VanillaWhileFastForwarding = c.Bind("AI", "VanillaWhileFastForwarding", true,
                "Use the vanilla AI budget while game time runs faster than normal (sleeping/waiting), so the simulation keeps up.");

            AiFrameBudgetMs = c.Bind("AI", "FrameBudgetMs", 4f,
                new ConfigDescription("Time budget for citizen AI decisions per frame. Once this much time has been spent, the rest of the queue " +
                                      "waits for the next frame (at least one decision always runs). Stops several slow decisions from stacking " +
                                      "into one long freeze. 0 = off (vanilla: only the per-frame count limit). Not applied while fast-forwarding.",
                                      new AcceptableValueRange<float>(0f, 50f)));

            StaggerFarMovement = c.Bind("AI", "StaggerFarMovement", true,
                "Spread the movement updates of far-away citizens evenly over time. Vanilla moves ALL ~1,900 far (veryLow) citizens in the " +
                "same frame every 17th 0.2 s tick (and all 'low' ones every 11th), which causes a ~110 ms freeze every ~3.5 s in a big city. " +
                "With this on, each citizen still moves exactly as often, but 1/17 (1/11) of them per tick.");
            GcSliceMs = c.Bind("Memory", "IncrementalGcSliceMs", 0f,
                new ConfigDescription("Experiment: time per frame the incremental garbage collector may work while a collection is running " +
                                      "(game default 3 ms). 0 = vanilla. Tested at 6 ms in 0.4: no fewer GC stutters, so it is off by default.",
                                      new AcceptableValueRange<float>(0f, 20f)));
            if (Math.Abs(GcSliceMs.Value - 6f) < 0.001f)
            {
                GcSliceMs.Value = 0f;   // 0.4 test default; the experiment did not help
                Plugin.L?.LogInfo("IncrementalGcSliceMs reset from the 0.4 experiment value (6) to vanilla (0).");
            }
            TrashTrickle = c.Bind("Memory", "TrashTrickle", false,
                "Experiment (off): run the game's trash cleanup every 10 s instead of hourly. Tested in 0.5: each Interactable.Delete costs ~5 ms, " +
                "so the small batches (~30 items) caused ~150 ms stutters every 10 s. Kept only for testing.");

            PreferVCacheCores = c.Bind("CPU", "PreferVCacheCores", true,
                "On CPUs with AMD 3D V-Cache on only some cores (Ryzen 7950X3D, 9950X3D, ...), keep the game on the V-Cache cores " +
                "while optimizations are ON. Only this game's process is affected; it goes back to all cores when OFF. No effect on other CPUs.");
            QuietGc = c.Bind("Memory", "CollectWhilePaused", false,
                "Experimental, off: a forced full garbage collection takes 2-3 s on a big city's heap (the automatic ones are " +
                "incremental and take ~0.2 s), which made opening the inventory/menu freeze. Do not turn on.");
            DeferRoomHiding = c.Bind("Rooms", "DeferRoomHiding", true,
                "When you change rooms, rooms that are no longer visible are hidden over the next frames (a few ms per frame) " +
                "instead of all in the same frame as the newly visible rooms are shown.");
            HideBudgetMs = c.Bind("Rooms", "HideBudgetMs", 2f, "Milliseconds per frame for hiding rooms (at least one room per frame).");
            SpreadFarRoomShowing = c.Bind("Rooms", "SpreadFarRoomShowing", true,
                "When you change rooms, rooms that become visible farther than ShowNearDistance are shown nearest-first over the next " +
                "frames instead of all in one frame. Near rooms are always shown at once; nothing waits longer than ShowMaxDelayMs.");
            FastPresetCompareOn = c.Bind("Loading", "FastPresetCompare", true,
                "Replace the game's preset comparison (SoCustomComparison.Equals, ~half of all loading time) with an equivalent that " +
                "does not look up type information under a lock. Needs sodsampler.native. Takes effect on game start.");
            FastBreakerLookup = c.Bind("Rooms", "FastBreakerLookup", true,
                "Find an address's breaker box (security/doors/lights) through an id index instead of scanning every interactable " +
                "in the city each time a camera, alarm or light spawns.");
            SmoothRoomContentLoading = c.Bind("Rooms", "SmoothRoomContentLoading", true,
                "Load the furniture of newly visible rooms nearest-first, a few pieces per frame (ContentBudgetMs), instead of a " +
                "whole room per frame (20-30 ms each). Removes the run of small stutters after every room change.");
            ContentBudgetMs = c.Bind("Rooms", "ContentBudgetMs", 4f, "Milliseconds per frame for loading room contents (at least one piece per frame).");
            ContentBudgetBusyMs = c.Bind("Rooms", "ContentBudgetBusyMs", 6f, "Budget when more than 40 rooms are waiting.");
            ShowNearDistance = c.Bind("Rooms", "ShowNearDistance", 25f, "Rooms closer than this (metres from the camera) are shown immediately.");
            ShowBudgetMs = c.Bind("Rooms", "ShowBudgetMs", 4f, "Milliseconds per frame for showing far rooms (at least one per frame).");
            ShowMaxDelayMs = c.Bind("Rooms", "ShowMaxDelayMs", 250f, "A far room is shown at the latest this many milliseconds after it became visible.");
            DeferSoundOcclusion = c.Bind("Rooms", "DeferSoundOcclusion", true,
                "When the player's room changes, recalculate looping-sound occlusion one frame later instead of in the same " +
                "frame as the room loading.");
            CullingCache = c.Bind("Loading", "RoomVisibilityCache", true,
                "Remember every room's visibility data (culling tree) per city on disk, and reuse it next time instead of recalculating. " +
                "The game recalculates all rooms every session (during loading and then in the background); entering a room that is not " +
                "done yet freezes for 100-300 ms. The first session builds the cache; later sessions load faster and skip those freezes. " +
                "Stored in BepInEx/cache/SODPerformance/. Requires restart.");

            ShadowDistanceScale = c.Bind("Shadows", "DistanceScale", 0.5f,
                new ConfigDescription("Multiplies the game's shadow fade distance for lamps and room lights (Game.shadowFadeDistanceMultiplier). " +
                                      "0.5 = shadows from lights fade out at half the usual distance, so fewer shadow maps are rendered. 1 = vanilla.",
                                      new AcceptableValueRange<float>(0.1f, 1f)));
            OtherLightShadowDistance = c.Bind("Shadows", "OtherLightShadowDistance", 25f,
                new ConfigDescription("Shadow fade distance cap (meters) for shadow-casting lights NOT managed by the game's LightController " +
                                      "(about 250 in your city, most rendering shadows every frame with HDRP's default 'never fade' distance). " +
                                      "Lights closer than this keep their shadows. 0 = vanilla.", new AcceptableValueRange<float>(0f, 500f)));
            MaxShadowUpdatesPerFrame = c.Bind("Shadows", "MaxShadowUpdatesPerFrame", -1,
                "Override Game.maxUpdateDynamicShadowsPerFrame: how many queued light shadow maps the game re-renders per frame. -1 = vanilla.");

            StripLogStackTraces = c.Bind("Logging", "StripLogStackTraces", true,
                "Stop Unity from capturing a stack trace for every Log/Warning message. The game logs many warnings and each stack capture costs CPU.");
            StripErrorStackTraces = c.Bind("Logging", "StripErrorStackTraces", false,
                "Also strip stack traces from Error/Exception messages. Saves more CPU during error floods, but makes bug reports less useful.");

            LightChecksPerFrame = c.Bind("Lights", "LightChecksPerFrame", -1,
                "Override LightCullingController.lightsToCheckPerFrame (game default 10). -1 = leave vanilla. " +
                "Higher culls hidden lights sooner (less GPU work) but costs more CPU per frame; lower is the opposite. Experimental.");

            ProfilerEnabled = c.Bind("Profiler", "Enabled", false,
                "Time the game's main per-frame systems (about 25 hooks on once-per-frame methods; overhead is tiny). Requires restart.");
            ProfilePerInstanceUpdates = c.Bind("Profiler", "ProfilePerInstanceUpdates", false,
                "Also time per-object Update methods (lights, interactables, speech, neon signs...). These run hundreds of times per frame, " +
                "so the profiler itself adds noticeable overhead: use only for a short diagnostic run. Requires restart.");
            AutoReportSeconds = c.Bind("Profiler", "AutoReportSeconds", 0,
                "Every N seconds of play, overwrite BepInEx/SODPerformance/report-latest.txt with the current measurements. 0 = off.");
            AutoABSeconds = c.Bind("Profiler", "AutoABSeconds", 0,
                "Test mode: switch optimizations ON/OFF automatically every N seconds of play, so both halves of the comparison " +
                "see the same places, crowds and weather. Pressing the toggle key stops it. 0 = off (use 0 for normal play).");
            SpikeThresholdMs = c.Bind("Profiler", "SpikeThresholdMs", 50f,
                "Frames slower than this are recorded as spikes (stutters) with a breakdown of what was running.");
            CatchAiSpikes = c.Bind("Profiler", "CatchAiSpikes", false,
                "Record which citizen and which AI goal/action was running when a single AI decision is slower than AiSpikeMs. Requires restart.");
            AiSpikeMs = c.Bind("Profiler", "AiSpikeMs", 8f, "Threshold for CatchAiSpikes, in milliseconds.");
            CatchMovementSpikes = c.Bind("Profiler", "CatchMovementSpikes", false,
                "Also record which citizen was being moved/animated when NewAIController.FrequentUpdate takes longer than AiSpikeMs. " +
                "Adds a small cost per visible citizen per frame (diagnostic). Requires restart.");

            // One-time move from the 0.1-0.5 test settings to the play settings (only values the user never changed are touched).
            SamplingProfiler = c.Bind("Profiler", "SamplingProfiler", false,
                "Sample the main thread's call stack ~500x per second (native helper sodsampler.dll) and list in the report which " +
                "game functions the loading and frame time go into. Costs about 1-2% of main-thread time.");
            SamplingIntervalUs = c.Bind("Profiler", "SamplingIntervalUs", 2000, "Microseconds between samples (minimum 250).");
            ConfigVersion = c.Bind("General", "ConfigVersion", 0, "Internal. Do not edit.");
            if (ConfigVersion.Value < 6)
            {
                if (AutoABSeconds.Value == 30) AutoABSeconds.Value = 0;
                if (OverlayOnStart.Value) OverlayOnStart.Value = false;
                if (TrashTrickle.Value) TrashTrickle.Value = false;
                if (CatchMovementSpikes.Value) CatchMovementSpikes.Value = false;
                if (AiMode.Value == AiBudgetMode.Adaptive) AiMode.Value = AiBudgetMode.Vanilla;
                ConfigVersion.Value = 6;
                Plugin.L?.LogInfo("Settings moved from the A/B test build to the play build (auto A/B off, overlay hidden until F8, trash trickle off, AI count vanilla).");
            }
            if (ConfigVersion.Value < 7)
            {
                QuietGc.Value = false;   // 0.8.0 default caused 2-3 s freezes when pausing
                ConfigVersion.Value = 7;
            }
            if (ConfigVersion.Value < 8)
            {
                SamplingProfiler.Value = false;   // 0.9.0 had it on; it can pause the main thread when all cores are busy
                if (ContentBudgetMs.Value == 6f) ContentBudgetMs.Value = 4f;
                if (ContentBudgetBusyMs.Value == 10f) ContentBudgetBusyMs.Value = 6f;
                ConfigVersion.Value = 8;
            }
            if (ConfigVersion.Value < 9)
            {
                // 1.0.0 public release: the testing tools are off by default (F7 report and F8 overlay still work).
                ProfilerEnabled.Value = false;
                CatchAiSpikes.Value = false;
                if (AutoReportSeconds.Value == 60) AutoReportSeconds.Value = 0;
                ConfigVersion.Value = 9;
            }

        }
    }
}
