using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SODPerformance
{
    /// <summary>
    /// Lightweight section profiler. Each target method gets its own prefix/postfix pair
    /// (ProfilerHooks.PreN/PostN) that records Stopwatch ticks into a slot, so no Harmony
    /// special parameters are needed. Only methods with a unique native address (checked against
    /// the game's method list) are hooked, so a detour never touches unrelated code.
    /// </summary>
    internal static class Profiler
    {
        internal sealed class Section
        {
            public int Id;
            public string Label;
            public string Target;
            public bool Installed;
            /// <summary>Runs inside another profiled section; excluded from the "profiled total".</summary>
            public bool Nested;
            public bool PerInstance;

            public long FrameTicks;
            public int FrameCalls;

            public long WinTicks;
            public long WinCalls;
            public long WinMaxFrame;

            // [0] = optimizations OFF, [1] = ON. Only accumulated while a city is being played.
            public readonly long[] SesTicks = new long[2];
            public readonly long[] SesCalls = new long[2];
            public readonly long[] SesMaxFrame = new long[2];

            // Last closed window, for the overlay.
            public double AvgMs;
            public double MaxMs;
            public double CallsPerFrame;
        }

        internal static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        internal static readonly Section[] Sections = new Section[ProfilerHooks.Count];
        internal static int SectionCount;
        internal static bool Installed;
        internal static int AiTickId = -1;
        internal static int ModRoomContentsId = -1, ModRoomVisibilityId = -1;

        private static readonly long[] start = new long[ProfilerHooks.Count];
        private static readonly int[] depth = new int[ProfilerHooks.Count];
        private static long winFrames;
        internal static readonly long[] SesFrames = new long[2];

        // Called from the generated hooks. Keep these tiny.
        // A method can re-enter itself (AITick -> CreateNewGoal -> AITick for another citizen), so only the
        // outermost call is timed; that avoids counting nested time twice.
        internal static void Begin(int id)
        {
            if (Environment.CurrentManagedThreadId != LoadProfiler.MainThreadId) return;   // some hooked methods also run on worker threads
            if (depth[id]++ == 0) start[id] = Stopwatch.GetTimestamp();
        }

        internal static void End(int id)
        {
            if (Environment.CurrentManagedThreadId != LoadProfiler.MainThreadId) return;
            var s = Sections[id];
            if (s == null) return;
            s.FrameCalls++;
            if (--depth[id] == 0) s.FrameTicks += Stopwatch.GetTimestamp() - start[id];
            else if (depth[id] < 0) depth[id] = 0;
        }

        /// <summary>Per-section ticks recorded so far this frame (read by the spike recorder before EndFrame).</summary>
        internal static long FrameTicksOf(int id) => Sections[id]?.FrameTicks ?? 0;

        internal static void Install(Harmony harmony, bool perInstance)
        {
            // Once-per-frame managers (singletons). Unique native addresses verified in METHOD-MAP.csv.
            Add(harmony, "AI scheduler", typeof(CitizenBehaviour), "Update");
            AiTickId = Add(harmony, "  AI decisions (AITick)", typeof(NewAIController), "AITick", nested: true);
            Add(harmony, "AI movement/anim", typeof(CitizenBehaviour), "LateUpdate");
            Add(harmony, "AI routine queue", typeof(CitizenBehaviour), "RoutineCheck");
            Add(harmony, "World sim (GameWorldCheck)", typeof(CitizenBehaviour), "GameWorldCheck");
            Add(harmony, "Murder controller", typeof(MurderController), "Update");
            Add(harmony, "Light culling", typeof(LightCullingController), "Update");
            Add(harmony, "Geometry culling", typeof(GeometryCullingController), "Update");
            Add(harmony, "  Room culling rebuild", typeof(GeometryCullingController), "UpdateCullingForRoom", nested: true);
            Add(harmony, "Session/time/weather", typeof(SessionData), "Update");
            Add(harmony, "Player", typeof(Player), "Update");
            Add(harmony, "Player (late)", typeof(Player), "LateUpdate");
            Add(harmony, "Camera (late)", typeof(CameraController), "LateUpdate");
            Add(harmony, "Input", typeof(InputController), "Update");
            Add(harmony, "Interaction", typeof(InteractionController), "Update");
            Add(harmony, "Interface/HUD", typeof(InterfaceController), "Update");
            Add(harmony, "First-person items", typeof(FirstPersonItemController), "Update");
            Add(harmony, "Status effects", typeof(StatusController), "Update");
            Add(harmony, "Game messages", typeof(GameMessageController), "Update");
            Add(harmony, "Map", typeof(MapController), "Update");
            Add(harmony, "Case board", typeof(CasePanelController), "Update");
            Add(harmony, "City streaming", typeof(CityConstructor), "Update");
            Add(harmony, "Mesh pooling", typeof(MeshPoolingController), "Update");
            Add(harmony, "Music", typeof(MusicController), "Update");
            Add(harmony, "Player apartment", typeof(PlayerApartmentController), "Update");

            // 0.3: break down the rare but huge Session/time/weather and hourly freezes (all run inside other sections).
            Add(harmony, "  weather change", typeof(SessionData), "ExecuteWeatherChange", nested: true);
            Add(harmony, "  wetness change", typeof(SessionData), "ExecuteWetnessChange", nested: true);
            Add(harmony, "  wind change", typeof(SessionData), "ExecuteWindChange", nested: true);
            Add(harmony, "  scene visuals (time of day)", typeof(SessionData), "SetSceneVisuals", nested: true);
            Add(harmony, "  skybox gradient", typeof(SessionData), "UpdateSkyboxGraidentTargets", nested: true);
            Add(harmony, "  physics sync", typeof(SessionData), "ExecuteSyncPhysics", nested: true);
            Add(harmony, "  scene profile", typeof(SessionData), "SetSceneProfile", nested: true);
            Add(harmony, "  hourly citizen update", typeof(CitizenBehaviour), "OnHourChange", nested: true);
            Add(harmony, "  daily citizen update", typeof(CitizenBehaviour), "OnDayChange", nested: true);
            Add(harmony, "  light level loop", typeof(CitizenBehaviour), "LightLevelLoop", nested: true);
            Add(harmony, "  player culling", typeof(Player), "UpdateCulling", nested: true);
            // 0.4: the three things CitizenBehaviour.OnHourChange calls (one of them froze for 0.8 s)
            Add(harmony, "  hourly: company open hours", typeof(Company), "UpdateOpenHoursBasedOnStaff", nested: true);
            Add(harmony, "  hourly: trash cleanup", typeof(CleanupController), "TrashUpdate", nested: true);
            Add(harmony, "  sound occlusion (room change/hourly)", typeof(AudioController), "UpdateAllLoopingSoundOcclusion", nested: true);
            // 0.8: what a room change actually does (UpdateCullingForRoom -> GenerateDynamicCulling + ExecuteCurrentCullingTree
            // -> NewRoom.SetVisible -> LoadGeometryRoom / LoadRoomStuff / UnloadInteractable), and the gradual object streaming.
            Add(harmony, "  room: dynamic culling", typeof(GeometryCullingController), "GenerateDynamicCulling", nested: true);
            Add(harmony, "  room: show/hide rooms", typeof(GeometryCullingController), "ExecuteCurrentCullingTree", nested: true);
            Add(harmony, "  room: set visible", typeof(NewRoom), "SetVisible", nested: true);
            Add(harmony, "  room: build geometry", typeof(GenerationController), "LoadGeometryRoom", nested: true);
            Add(harmony, "  room: load contents", typeof(NewRoom), "LoadRoomStuff", nested: true);
            Add(harmony, "  room: static batching", typeof(NewRoom), "ExecuteStaticBatching", nested: true);
            Add(harmony, "  load furniture", typeof(FurnitureClusterLocation), "LoadFurnitureToWorld", nested: true);
            Add(harmony, "  load object", typeof(Interactable), "LoadInteractableToWorld", nested: true);
            Add(harmony, "  unload object", typeof(Interactable), "UnloadInteractable", nested: true);
            Add(harmony, "Object streaming (late)", typeof(ObjectPoolingController), "LateUpdate");
            Add(harmony, "  spawn objects (gradual)", typeof(ObjectPoolingController), "ExecuteInteractableCheckingPool", nested: true);
            Add(harmony, "  furniture range checks", typeof(ObjectPoolingController), "ExecuteFurnitureCheckingPool", nested: true);
            Add(harmony, "  AI goal tick", typeof(NewAIGoal), "AITick", nested: true);
            Add(harmony, "  AI pathfinding", typeof(PathFinder), "GetPath", nested: true);
            ModRoomContentsId = AddManual("Mod: room contents loader");
            ModRoomVisibilityId = AddManual("Mod: room show/hide spreading");
            if (PerfConfig.CatchMovementSpikes.Value)   // ~90 calls per frame: diagnostic runs only
                Add(harmony, "  AI movement per citizen", typeof(NewAIController), "FrequentUpdate", nested: true);

            if (perInstance)
            {
                // Hundreds of calls per frame: the hooks themselves cost time. Diagnostic runs only.
                Add(harmony, "[obj] Lights", typeof(LightController), "Update", perInstanceMethod: true);
                Add(harmony, "[obj] Interactables", typeof(InteractableController), "Update", perInstanceMethod: true);
                Add(harmony, "[obj] Speech", typeof(SpeechController), "Update", perInstanceMethod: true);
                Add(harmony, "[obj] Speech bubbles", typeof(SpeechBubbleController), "Update", perInstanceMethod: true);
                Add(harmony, "[obj] Neon signs", typeof(NeonSignController), "Update", perInstanceMethod: true);
                Add(harmony, "[obj] Security systems", typeof(SecuritySystem), "Update", perInstanceMethod: true);
            }

            Installed = Sections.Take(SectionCount).Any(s => s != null && s.Installed);
            int ok = Sections.Take(SectionCount).Count(s => s != null && s.Installed);
            Plugin.L.LogInfo($"Profiler: {ok}/{SectionCount} sections hooked.");
        }

        internal static void ResetSession()
        {
            SesFrames[0] = SesFrames[1] = 0;
            for (int i = 0; i < SectionCount; i++)
            {
                var sec = Sections[i];
                if (sec == null) continue;
                sec.SesTicks[0] = sec.SesTicks[1] = 0;
                sec.SesCalls[0] = sec.SesCalls[1] = 0;
            }
        }

        /// <summary>A section timed by the mod's own code (Begin/End called directly), e.g. work the mod does in its runner.</summary>
        internal static int AddManual(string label, bool nested = false)
        {
            int id = SectionCount;
            if (id >= ProfilerHooks.Count) return -1;
            Sections[id] = new Section { Id = id, Label = label, Target = "(mod)", Nested = nested, Installed = true };
            SectionCount++;
            return id;
        }

        private static int Add(Harmony harmony, string label, Type type, string method, bool nested = false, bool perInstanceMethod = false)
        {
            int id = SectionCount;
            if (id >= ProfilerHooks.Count)
            {
                Plugin.L.LogWarning($"Profiler: no free hook slot for {type.Name}.{method}");
                return -1;
            }

            var section = new Section { Id = id, Label = label, Target = $"{type.Name}.{method}", Nested = nested, PerInstance = perInstanceMethod };
            Sections[id] = section;
            SectionCount++;

            try
            {
                MethodInfo target = FindMethod(type, method);
                if (target == null)
                {
                    Plugin.L.LogWarning($"Profiler: {type.Name}.{method} not found in interop assembly; skipped.");
                    return id;
                }

                var pre = new HarmonyMethod(AccessTools.Method(typeof(ProfilerHooks), "Pre" + id));
                var post = new HarmonyMethod(AccessTools.Method(typeof(ProfilerHooks), "Post" + id));
                harmony.Patch(target, prefix: pre, postfix: post);
                section.Installed = true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Profiler: could not hook {type.Name}.{method}: {e.GetType().Name}: {e.Message}");
            }
            return id;
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            List<MethodInfo> candidates = AccessTools.GetDeclaredMethods(type)
                .Where(m => m.Name == name && !m.IsGenericMethodDefinition)
                .ToList();
            if (candidates.Count > 1 && candidates.Any(m => !m.IsStatic)) candidates = candidates.Where(m => !m.IsStatic).ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            // Prefer the parameterless overload (Unity message methods).
            return candidates.FirstOrDefault(m => m.GetParameters().Length == 0) ?? candidates[0];
        }

        /// <summary>Called once per frame by the runner. Moves this frame's numbers into the window and session totals.</summary>
        internal static void EndFrame(bool measuring, int state)
        {
            for (int i = 0; i < SectionCount; i++)
            {
                var s = Sections[i];
                if (s == null) continue;
                long t = s.FrameTicks;
                s.WinTicks += t;
                s.WinCalls += s.FrameCalls;
                if (t > s.WinMaxFrame) s.WinMaxFrame = t;
                if (measuring)
                {
                    s.SesTicks[state] += t;
                    s.SesCalls[state] += s.FrameCalls;
                    if (t > s.SesMaxFrame[state]) s.SesMaxFrame[state] = t;
                }
                s.FrameTicks = 0;
                s.FrameCalls = 0;
                // Nothing hooked is on the call stack while our Update runs; clears any depth left by an exception.
                depth[i] = 0;
            }
            winFrames++;
            if (measuring) SesFrames[state]++;
        }

        /// <summary>Closes the display window (about once per second).</summary>
        internal static void CloseWindow()
        {
            long frames = Math.Max(1, winFrames);
            for (int i = 0; i < SectionCount; i++)
            {
                var s = Sections[i];
                if (s == null) continue;
                s.AvgMs = s.WinTicks * TicksToMs / frames;
                s.MaxMs = s.WinMaxFrame * TicksToMs;
                s.CallsPerFrame = (double)s.WinCalls / frames;
                s.WinTicks = 0;
                s.WinCalls = 0;
                s.WinMaxFrame = 0;
            }
            winFrames = 0;
        }

        internal static IEnumerable<Section> Active => Sections.Take(SectionCount).Where(s => s != null && s.Installed);

        /// <summary>Average cost of a single NewAIController.AITick over the last window, or -1 if unknown/too few samples.</summary>
        internal static double AiPerTickMs
        {
            get
            {
                if (AiTickId < 0) return -1;
                var s = Sections[AiTickId];
                if (s == null || !s.Installed || s.CallsPerFrame < 0.5) return -1;
                return s.AvgMs / s.CallsPerFrame;
            }
        }
    }
}
