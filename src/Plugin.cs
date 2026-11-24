using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace SODPerformance
{
    /// <summary>
    /// SOD Performance: measures where Shadows of Doubt spends its CPU frame time and applies
    /// reversible, data-driven optimizations (F10 toggles them for A/B comparison).
    /// </summary>
#if WORKSHOP
    // Workshop build (SOD Extender): no BepInEx plugin class; WorkshopMod starts the mod. Shared constants and state only.
    internal static class Plugin
#else
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BasePlugin
#endif
    {
        public const string Guid = "com.adronyus.sod.performance";
        public const string Name = "SOD Performance";
        public const string Version = "1.0.0";

        internal static ManualLogSource L;
        internal static Harmony Harmony;
        internal static string Host = "?";

#if !WORKSHOP
        public override void Load()
        {
            if (!Mod.TryClaim("BepInEx plugin"))
            {
                Log.LogWarning($"{Name} is already running ({Mod.RunningHost}); this copy stays off. Remove one of the two installs.");
                return;
            }
            Mod.Start(Log, Config, "BepInEx plugin");
            Mod.CreateRunner();
        }
#endif
    }
}
