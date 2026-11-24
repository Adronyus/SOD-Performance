using System;

namespace SODPerformance
{
    /// <summary>Cheap snapshots of game state for the overlay and reports (sampled ~2x per second, not every frame).</summary>
    internal static class GameProbe
    {
        internal static bool HasCity;
        internal static int VeryHigh, High, Medium, Low, VeryLow;
        internal static int Queue, Backlog, Executed, VisibleHumans, Citizens;
        internal static int LightsTracked, LightsCulled, LightsNotCulled, LightChecks;
        internal static float TimeMult = 1f;
        internal static int ShadowQueue, LastShadowsUpdated, MaxShadowUpdates;
        internal static float ShadowFadeMult;
        internal static string TimeSpeed = "-";

        private static IntPtr lastCitizenBehaviour = IntPtr.Zero;

        /// <summary>True while a city is loaded and the game is unpaused (SessionData.play).</summary>
        internal static bool IsPlaying()
        {
            SessionData sd = SessionData.Instance;
            return sd != null && sd.play;
        }

        internal static float TimeMultiplierNow()
        {
            SessionData sd = SessionData.Instance;
            return sd == null ? 1f : sd.currentTimeMultiplier;
        }

        /// <summary>Detects a new CitizenBehaviour (new game / load) so captured vanilla values are re-read.</summary>
        internal static void CheckSession()
        {
            CitizenBehaviour cb = CitizenBehaviour.Instance;
            IntPtr p = cb == null ? IntPtr.Zero : cb.Pointer;
            if (p != lastCitizenBehaviour)
            {
                lastCitizenBehaviour = p;
                AiGovernor.ResetForNewSession();
                RoomHideDefer.Reset();
                BreakerLookup.Reset();
                // (the room cache re-checks the city identity itself on every use)
                LightBudget.ResetForNewSession();
            }
        }

        internal static void Sample()
        {
            try
            {
                CitizenBehaviour cb = CitizenBehaviour.Instance;
                HasCity = cb != null;
                if (HasCity)
                {
                    VeryHigh = Count(cb.veryHighTickRate);
                    High = Count(cb.highTickRate);
                    Medium = Count(cb.mediumTickRate);
                    Low = Count(cb.lowTickRate);
                    VeryLow = Count(cb.veryLowTickRate);
                    Queue = Count(cb.updateList);
                    Backlog = cb.aiTickBacklog;
                    Executed = cb.executionsThisFrame;
                    VisibleHumans = cb.visibleHumans;
                    ShadowQueue = Count(cb.lightUpdateQueue);
                }

                Game game = Game.Instance;
                if (game != null)
                {
                    LastShadowsUpdated = game.lastShadowsUpdatedCount;
                    MaxShadowUpdates = game.maxUpdateDynamicShadowsPerFrame;
                    ShadowFadeMult = game.shadowFadeDistanceMultiplier;
                }

                CityData city = CityData.Instance;
                Citizens = city != null ? Count(city.citizenDirectory) : 0;

                LightCullingController lc = LightCullingController.Instance;
                if (lc != null)
                {
                    LightsTracked = Count(lc.lightsToCheck);
                    LightsCulled = Count(lc.culledLights);
                    LightsNotCulled = Count(lc.notCulled);
                    LightChecks = lc.lightsToCheckPerFrame;
                }

                SessionData sd = SessionData.Instance;
                if (sd != null)
                {
                    TimeMult = sd.currentTimeMultiplier;
                    TimeSpeed = sd.currentTimeSpeed.ToString();
                }
            }
            catch (Exception e)
            {
                Runner.ReportError("sample", e);
            }
        }

        private static int Count<T>(Il2CppSystem.Collections.Generic.List<T> list) => list == null ? 0 : list.Count;
    }
}
