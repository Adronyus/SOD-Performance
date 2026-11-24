using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SODPerformance
{
    /// <summary>
    /// Start-up shared by both ways the mod can be loaded: as a BepInEx plugin (BepInEx/plugins, Thunderstore, manual
    /// install) and as a Steam Workshop code mod through SOD Extender (the WORKSHOP build, see workshop/WorkshopMod.cs).
    /// Only the first copy that starts in a game process runs; a second install stays off.
    /// </summary>
    public static class Mod
    {
        private const string ClaimKey = "com.adronyus.sod.performance.running";
        private static readonly object claimLock = new object();

        public static string RunningHost => AppDomain.CurrentDomain.GetData(ClaimKey) as string ?? "";

        /// <summary>True if this copy may start (process-wide, so it also works between two copies of the DLL).</summary>
        public static bool TryClaim(string host)
        {
            lock (claimLock)
            {
                if (AppDomain.CurrentDomain.GetData(ClaimKey) is string) return false;
                AppDomain.CurrentDomain.SetData(ClaimKey, $"{host}: {typeof(Mod).Assembly.Location}");
                return true;
            }
        }

        /// <param name="harmony">Patch owner. SOD Extender passes the mod's own instance, so it can remove the patches if start-up fails.</param>
        public static void Start(ManualLogSource log, ConfigFile config, string host, Harmony harmony = null)
        {
            Plugin.L = log;
            Plugin.Host = host;
            PerfConfig.Bind(config);

            LoadProfiler.MainThreadId = Environment.CurrentManagedThreadId;
            Sampler.CaptureMainThread();
            harmony = Plugin.Harmony = harmony ?? new Harmony(Plugin.Guid);
            LoadProfiler.Install(harmony);
            if (PerfConfig.CullingCache.Value) CullingCache.Install(harmony);
            if (PerfConfig.DeferSoundOcclusion.Value) SoundOcclusionDefer.Install(harmony);
            if (PerfConfig.FastPresetCompareOn.Value) FastPresetCompare.Install();
            if (PerfConfig.FastBreakerLookup.Value) BreakerLookup.Install(harmony);
            RoomHideDefer.Install(harmony);   // always installed: also measures show vs hide time; deferral itself follows the setting
            if (PerfConfig.AiFrameBudgetMs.Value > 0f) AiTimeBudget.Install(harmony);
            if (PerfConfig.StaggerFarMovement.Value) MovementStagger.Install(harmony);

            if (PerfConfig.ProfilerEnabled.Value)
            {
                Profiler.Install(harmony, PerfConfig.ProfilePerInstanceUpdates.Value);
                if (PerfConfig.CatchAiSpikes.Value) AiSpikes.Install(harmony, PerfConfig.CatchMovementSpikes.Value);
            }

            if (PerfConfig.SamplingProfiler.Value) Sampler.Start();
            ClassInjector.RegisterTypeInIl2Cpp<PerfRunner>();

            log.LogInfo($"{Plugin.Name} {Plugin.Version} loaded as {host}. Optimizations {(PerfConfig.OptimizationsEnabled.Value ? "ON" : "OFF")}. " +
                        $"Keys: {PerfConfig.OverlayKey.Value}=overlay, {PerfConfig.ReportKey.Value}=report, {PerfConfig.ToggleKey.Value}=optimizations on/off.");
        }

        /// <summary>The per-frame driver: a hidden object that survives scene changes.</summary>
        public static void CreateRunner()
        {
            var go = new GameObject("SODPerformance");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<PerfRunner>();
        }
    }
}
