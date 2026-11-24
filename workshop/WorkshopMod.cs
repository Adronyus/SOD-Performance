using SodExtender;

namespace SODPerformance
{
    /// <summary>
    /// Entry point when the mod comes from the Steam Workshop (or the game's Mods folder) through SOD Extender.
    /// SOD Extender loads Code/*.dll at the main menu and calls OnLoad. Settings live in BepInEx/config/SodMods/.
    /// </summary>
    public sealed class WorkshopMod : SodMod
    {
        private const string Host = "SOD Extender mod";

        public override void OnLoad()
        {
            if (!Mod.TryClaim(Host))
            {
                // A BepInEx-plugin copy (Thunderstore / manual install) already runs: one copy is enough.
                Log.LogWarning($"{Plugin.Name} is already running ({Mod.RunningHost}); the Workshop copy stays off. " +
                               "Remove one of the two installs.");
                return;
            }
            Mod.Start(Log, Config, Host, Harmony);
            Mod.CreateRunner();
        }

    }
}
