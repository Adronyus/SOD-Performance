using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SODPerformance
{
    /// <summary>
    /// Every time the player's room changes, GeometryCullingController.UpdateCullingForRoom also recalculates the
    /// occlusion of every looping sound in the city (AudioController.UpdateAllLoopingSoundOcclusion, 5-20 ms, worst 95 ms
    /// in testing) in the same frame as the room loading. This moves that one call to the next frame, where
    /// nothing else heavy happens. Sounds update one frame (~20 ms) later: not audible.
    /// </summary>
    internal static class SoundOcclusionDefer
    {
        internal static bool Installed, EnabledNow;
        internal static int Deferred, RunLater;
        internal static double LastMs, WorstMs;

        private static int cullDepth;
        private static bool pending, running;
        private static int pendingFrame;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var cull = AccessTools.Method(typeof(GeometryCullingController), "UpdateCullingForRoom");
                var occ = AccessTools.Method(typeof(AudioController), "UpdateAllLoopingSoundOcclusion");
                if (cull == null || occ == null) { Plugin.L.LogWarning("Sound occlusion deferral: methods not found."); return; }
                harmony.Patch(cull,
                    prefix: new HarmonyMethod(typeof(SoundOcclusionDefer), nameof(CullPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(SoundOcclusionDefer), nameof(CullPostfix)) { priority = Priority.Last });
                harmony.Patch(occ, prefix: new HarmonyMethod(typeof(SoundOcclusionDefer), nameof(OcclusionPrefix)) { priority = Priority.First });
                Installed = true;
                Plugin.L.LogInfo("Sound occlusion deferral installed.");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Sound occlusion deferral not installed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool Main => Environment.CurrentManagedThreadId == LoadProfiler.MainThreadId;

        public static void CullPrefix() { if (Main) cullDepth++; }
        public static void CullPostfix() { if (Main && cullDepth > 0) cullDepth--; }

        public static bool OcclusionPrefix()
        {
            if (!EnabledNow || running || cullDepth == 0 || !Main) return true;
            pending = true;
            pendingFrame = Time.frameCount;
            Deferred++;
            return false;
        }

        /// <summary>Runs the deferred update in a later frame (called from the runner).</summary>
        internal static void Tick()
        {
            if (!pending || cullDepth > 0) return;
            if (Time.frameCount <= pendingFrame && EnabledNow) return;
            pending = false;
            try
            {
                AudioController ac = AudioController.Instance;
                if (ac == null) return;
                running = true;
                long t0 = Stopwatch.GetTimestamp();
                ac.UpdateAllLoopingSoundOcclusion();
                LastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (LastMs > WorstMs) WorstMs = LastMs;
                RunLater++;
            }
            catch (Exception e)
            {
                Runner.ReportError("sound-occlusion", e);
            }
            finally
            {
                running = false;
            }
        }
    }

    /// <summary>
    /// A room change runs GeometryCullingController.ExecuteCurrentCullingTree, which calls NewRoom.SetVisible for every room
    /// that enters or leaves the visible set in one frame (0.8 report: 79 freezes, average 121 ms, almost all of it inside
    /// SetVisible). Rooms that become visible must appear at once, but rooms that are no longer visible are behind walls
    /// anyway, so this hides those over the next frames within a small time budget. A room that becomes visible again before
    /// its turn simply stays visible. Also measures how the SetVisible time splits between showing and hiding.
    /// </summary>
    internal static class RoomHideDefer
    {
        internal static bool Installed, EnabledNow;
        internal static long Deferred, HiddenLater, Cancelled;
        internal static double LastFrameMs, WorstFrameMs;
        internal static int QueueNow, QueueMax;
        // Time spent in SetVisible, split: [0] hide, [1] show (outermost calls only)
        internal static readonly double[] SetVisibleMs = new double[2];
        internal static readonly long[] SetVisibleCalls = new long[2];
        internal static double WorstShowMs, WorstHideMs;

        private struct Pending { public NewRoom Room; public IntPtr Key; public bool Force, Immediate, ImmediateStuff; public float Since; public float Dist; }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Pending> pending = new System.Collections.Generic.Dictionary<IntPtr, Pending>();
        private static readonly System.Collections.Generic.List<IntPtr> order = new System.Collections.Generic.List<IntPtr>();

        // 0.8.2: far rooms that become visible are shown nearest-first over the next frames (near rooms still at once).
        internal static bool ShowEnabledNow;
        internal static long ShowDeferred, ShownLater, ShowCancelled, ShowForcedLate;
        internal static double WorstShowFrameMs, MaxShowDelayMs;
        internal static int ShowQueueMax;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Pending> pendingShow = new System.Collections.Generic.Dictionary<IntPtr, Pending>();
        private static readonly System.Collections.Generic.List<Pending> showBatch = new System.Collections.Generic.List<Pending>();
        private static Vector3 viewPos;
        private static bool viewPosOk;
        private static int execDepth, visDepth;
        private static bool execImmediate, processing;
        private static long visStart;
        private static bool visVal;
        private static bool lastSkipped;   // a skipped call runs no game code between our prefix and postfix, so one flag is enough

        internal static void Install(Harmony harmony)
        {
            try
            {
                var exec = AccessTools.Method(typeof(GeometryCullingController), "ExecuteCurrentCullingTree");
                var vis = AccessTools.Method(typeof(NewRoom), "SetVisible");
                if (exec == null || vis == null) { Plugin.L.LogWarning("Room hide deferral: methods not found."); return; }
                harmony.Patch(exec,
                    prefix: new HarmonyMethod(typeof(RoomHideDefer), nameof(ExecPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(RoomHideDefer), nameof(ExecPostfix)) { priority = Priority.Last });
                harmony.Patch(vis,
                    prefix: new HarmonyMethod(typeof(RoomHideDefer), nameof(VisPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(RoomHideDefer), nameof(VisPostfix)) { priority = Priority.Last });
                Installed = true;
                Plugin.L.LogInfo("Room hide deferral installed.");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Room hide deferral not installed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool Main => Environment.CurrentManagedThreadId == LoadProfiler.MainThreadId;

        public static void ExecPrefix(bool __0)
        {
            if (!Main) return;
            execDepth++;
            execImmediate = __0;
            viewPosOk = false;
            try
            {
                Camera cam = Camera.main;
                if (cam != null) { viewPos = cam.transform.position; viewPosOk = true; }
            }
            catch { }
        }

        private static float DistanceTo(NewRoom room)
        {
            if (!viewPosOk) return 0f;
            Vector3 p = room.middleRoomPosition;
            if (p == Vector3.zero) p = room.worldPos;
            if (p == Vector3.zero) return 0f;
            return Vector3.Distance(p, viewPos);
        }

        public static void ExecPostfix()
        {
            if (Main && execDepth > 0) execDepth--;
        }

        public static bool VisPrefix(NewRoom __instance, bool __0, bool __1, bool __2, bool __3)
        {
            if (!Main) return true;
            bool run = true;
            try
            {
                if (EnabledNow && !processing && __instance != null)
                {
                    IntPtr p = __instance.Pointer;
                    if (__0)
                    {
                        // Wanted visible again before we hid it: it is still visible, so the game's call is a cheap no-op.
                        if (pending.Remove(p)) Cancelled++;
                        else if (ShowEnabledNow && execDepth > 0 && !execImmediate && !__1 && !__instance.isVisible)
                        {
                            float d = DistanceTo(__instance);
                            if (d > PerfConfig.ShowNearDistance.Value)
                            {
                                if (!pendingShow.ContainsKey(p))
                                    pendingShow[p] = new Pending { Room = __instance, Key = p, Force = __1, Immediate = __2, ImmediateStuff = __3, Since = Time.realtimeSinceStartup, Dist = d };
                                ShowDeferred++;
                                run = false;
                            }
                        }
                    }
                    else if (pendingShow.Remove(p))
                    {
                        ShowCancelled++;   // no longer wanted before we showed it: it is still hidden, the game's call is a no-op
                    }
                    else if (execDepth > 0 && !execImmediate && !__1 && __instance.isVisible)
                    {
                        if (!pending.ContainsKey(p)) order.Add(p);
                        pending[p] = new Pending { Room = __instance, Force = __1, Immediate = __2, ImmediateStuff = __3, Since = Time.realtimeSinceStartup };
                        Deferred++;
                        run = false;
                    }
                }
            }
            catch { }
            lastSkipped = !run;
            if (run && visDepth++ == 0)
            {
                visStart = Stopwatch.GetTimestamp();
                visVal = __0;
            }
            return run;
        }

        public static void VisPostfix()
        {
            if (!Main) return;
            if (lastSkipped) { lastSkipped = false; return; }
            if (visDepth == 0) return;
            if (--visDepth == 0)
            {
                double ms = (Stopwatch.GetTimestamp() - visStart) * 1000.0 / Stopwatch.Frequency;
                int i = visVal ? 1 : 0;
                SetVisibleMs[i] += ms;
                SetVisibleCalls[i]++;
                if (visVal) { if (ms > WorstShowMs) WorstShowMs = ms; }
                else if (ms > WorstHideMs) WorstHideMs = ms;
            }
        }

        /// <summary>Hides queued rooms within the per-frame budget (called from the runner every frame).</summary>
        internal static void Tick()
        {
            TickShows();
            QueueNow = pending.Count;
            if (QueueNow > QueueMax) QueueMax = QueueNow;
            if (order.Count == 0) { if (pending.Count == 0) return; }
            if (execDepth > 0) return;
            double budget = Math.Max(0.5, PerfConfig.HideBudgetMs.Value);
            if (!EnabledNow || pending.Count > 400) budget = 1000;   // turned off or a huge backlog: finish now
            long t0 = Stopwatch.GetTimestamp();
            long deadline = t0 + (long)(budget * Stopwatch.Frequency / 1000);
            int done = 0;
            processing = true;
            try
            {
                int idx = 0;
                while (idx < order.Count && (done == 0 || Stopwatch.GetTimestamp() < deadline))
                {
                    IntPtr p = order[idx++];
                    if (!pending.TryGetValue(p, out Pending e)) continue;   // cancelled or duplicate
                    pending.Remove(p);
                    NewRoom room = e.Room;
                    if (room == null || !room.isVisible) continue;
                    room.SetVisible(false, e.Force, e.Immediate, e.ImmediateStuff);
                    HiddenLater++;
                    done++;
                }
                order.RemoveRange(0, idx);
                if (pending.Count == 0) order.Clear();
            }
            catch (Exception ex)
            {
                pending.Clear();
                order.Clear();
                Runner.ReportError("room-hide", ex);
            }
            finally
            {
                processing = false;
            }
            if (done > 0)
            {
                LastFrameMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (LastFrameMs > WorstFrameMs) WorstFrameMs = LastFrameMs;
            }
        }

        private static void TickShows()
        {
            if (pendingShow.Count == 0 || execDepth > 0) return;
            if (pendingShow.Count > ShowQueueMax) ShowQueueMax = pendingShow.Count;
            float now = Time.realtimeSinceStartup;
            float maxDelay = Math.Max(0.05f, PerfConfig.ShowMaxDelayMs.Value / 1000f);
            double budget = Math.Max(1.0, PerfConfig.ShowBudgetMs.Value);
            bool flushAll = !ShowEnabledNow;
            showBatch.Clear();
            foreach (var kv in pendingShow) showBatch.Add(kv.Value);
            // rooms past the delay limit first, then nearest first
            showBatch.Sort((a, b) =>
            {
                bool oa = now - a.Since >= maxDelay, ob = now - b.Since >= maxDelay;
                if (oa != ob) return oa ? -1 : 1;
                return a.Dist.CompareTo(b.Dist);
            });
            long t0 = Stopwatch.GetTimestamp();
            long deadline = t0 + (long)(budget * Stopwatch.Frequency / 1000);
            long overdueDeadline = t0 + (long)(budget * 3 * Stopwatch.Frequency / 1000);   // late rooms get more time, but not unlimited
            int done = 0;
            processing = true;
            try
            {
                foreach (var e in showBatch)
                {
                    bool overdue = now - e.Since >= maxDelay;
                    long ts = Stopwatch.GetTimestamp();
                    if (!flushAll && done > 0 && ts >= (overdue ? overdueDeadline : deadline)) continue;
                    NewRoom room = e.Room;
                    pendingShow.Remove(e.Key);
                    if (room == null || room.isVisible) continue;
                    double waitedMs = (now - e.Since) * 1000.0;
                    if (waitedMs > MaxShowDelayMs) MaxShowDelayMs = waitedMs;
                    if (overdue) ShowForcedLate++;
                    room.SetVisible(true, e.Force, e.Immediate, e.ImmediateStuff);
                    ShownLater++;
                    done++;
                }
            }
            catch (Exception ex)
            {
                // Something unexpected: show everything that is left right away (never leave a room hidden).
                foreach (var kv in pendingShow) { try { if (kv.Value.Room != null && !kv.Value.Room.isVisible) kv.Value.Room.SetVisible(true, kv.Value.Force, kv.Value.Immediate, kv.Value.ImmediateStuff); } catch { } }
                pendingShow.Clear();
                Runner.ReportError("room-show", ex);
            }
            finally
            {
                processing = false;
            }
            showBatch.Clear();   // do not keep room wrappers (GC handles) between frames
            if (done > 0)
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ms > WorstShowFrameMs) WorstShowFrameMs = ms;
            }
        }

        /// <summary>New city / load: forget queued rooms (they belong to the old scene).</summary>
        internal static void Reset()
        {
            pending.Clear();
            order.Clear();
            pendingShow.Clear();
        }
    }

    /// <summary>
    /// After a room change the game loads the contents of every newly visible room, one whole room per frame
    /// (ObjectPoolingController.LateUpdate -> NewRoom.LoadRoomStuff). Its furniture alone takes 20-30 ms per room, so
    /// moving around gives a run of 50-60 ms frames: the micro-stutter. This takes over that queue: it picks the
    /// nearest queued room, loads its furniture clusters a few at a time within a small per-frame budget, and lets the
    /// game finish the room (LoadRoomStuff, now cheap) once all its furniture is in. The game's own loop is paused
    /// (roomsLoadedPerFrame = 0) only while this runs, and restored when it is off.
    /// </summary>
    internal static class RoomContentLoader
    {
        internal static bool EnabledNow, Broken;
        internal static long RoomsLoaded, ClustersLoaded;
        internal static double WorstClusterMs, WorstFrameMs, WorstFinishMs, TotalMs;
        internal static int QueueMax;
        internal static string Status = "-";

        private static IntPtr controllerPtr = IntPtr.Zero;
        private static int originalPerFrame = -1;
        private static bool holding;
        private static readonly System.Collections.Generic.List<NewRoom> queued = new System.Collections.Generic.List<NewRoom>();

        internal static void Tick()
        {
            ObjectPoolingController p = null;
            try
            {
                p = ObjectPoolingController.Instance;
                if (p == null) { holding = false; controllerPtr = IntPtr.Zero; return; }
                if (p.Pointer != controllerPtr)
                {
                    controllerPtr = p.Pointer;
                    holding = false;
                    originalPerFrame = p.roomsLoadedPerFrame;
                }
                if (!EnabledNow || Broken || !p.allowGradualRoomLoading) { Release(p); Status = EnabledNow ? "game loads rooms itself (gradual loading off)" : "off"; return; }

                // Take over: stop the game's whole-room-per-frame loop while this runs.
                if (!holding) { originalPerFrame = p.roomsLoadedPerFrame; holding = true; }
                p.roomsLoadedPerFrame = 0;

                var set = p.roomStuffQueuedToLoad;
                if (set == null || set.Count == 0) { Status = "idle"; return; }
                queued.Clear();
                foreach (NewRoom r in set) queued.Add(r);
                if (queued.Count > QueueMax) QueueMax = queued.Count;

                Vector3 cam = Vector3.zero;
                bool camOk = false;
                try { Camera c = Camera.main; if (c != null) { cam = c.transform.position; camOk = true; } } catch { }

                double budget = Math.Max(1.0, queued.Count > 40 ? PerfConfig.ContentBudgetBusyMs.Value : PerfConfig.ContentBudgetMs.Value);
                long t0 = Stopwatch.GetTimestamp();
                long deadline = t0 + (long)(budget * Stopwatch.Frequency / 1000);
                int work = 0;

                while (queued.Count > 0 && (work == 0 || Stopwatch.GetTimestamp() < deadline))
                {
                    // nearest queued room first
                    int best = 0;
                    if (camOk)
                    {
                        float bestD = float.MaxValue;
                        for (int i = 0; i < queued.Count; i++)
                        {
                            NewRoom q = queued[i];
                            if (q == null) { best = i; bestD = -1; break; }
                            Vector3 pos = q.middleRoomPosition;
                            if (pos == Vector3.zero) pos = q.worldPos;
                            float d = (pos - cam).sqrMagnitude;
                            if (d < bestD) { bestD = d; best = i; }
                        }
                    }
                    NewRoom room = queued[best];
                    queued.RemoveAt(best);
                    if (room == null) continue;

                    // Furniture first, one cluster at a time.
                    bool allFurniture = true;
                    var clusters = room.furniture;
                    if (clusters != null)
                    {
                        for (int i = 0; i < clusters.Count; i++)
                        {
                            var cl = clusters[i];
                            if (cl == null || cl.loadedGeometry) continue;
                            if (work > 0 && Stopwatch.GetTimestamp() >= deadline) { allFurniture = false; break; }
                            long c0 = Stopwatch.GetTimestamp();
                            cl.LoadFurnitureToWorld(false);
                            double ms = (Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency;
                            if (ms > WorstClusterMs) WorstClusterMs = ms;
                            ClustersLoaded++;
                            work++;
                        }
                    }
                    if (!allFurniture) break;   // continue with this room next frame

                    // Everything else in the room (objects, lights, static batching): the game's own call; it also
                    // removes the room from the queue.
                    if (work > 0 && Stopwatch.GetTimestamp() >= deadline) break;
                    long f0 = Stopwatch.GetTimestamp();
                    room.LoadRoomStuff(false);
                    double fms = (Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency;
                    if (fms > WorstFinishMs) WorstFinishMs = fms;
                    if (set.Contains(room)) set.Remove(room);
                    RoomsLoaded++;
                    work++;
                }
                double frameMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                TotalMs += frameMs;
                if (frameMs > WorstFrameMs) WorstFrameMs = frameMs;
                Status = $"queue {set.Count}";
                queued.Clear();   // do not keep room wrappers (GC handles) between frames
            }
            catch (Exception e)
            {
                Broken = true;   // never leave the game's loader paused after an error: hand it back for this session
                Status = "error: handed back to the game";
                try { if (p != null) Release(p); } catch { }
                Runner.ReportError("room-content-loader", e);
            }
        }

        private static void Release(ObjectPoolingController p)
        {
            if (!holding) return;
            holding = false;
            if (originalPerFrame > 0) p.roomsLoadedPerFrame = originalPerFrame;
        }
    }

    /// <summary>
    /// Garbage collections freeze the game for ~150-230 ms with a 5-6 GB heap in a big city, about 5 times a minute.
    /// A collection while the game is paused (menu) or fast-forwarding (sleeping/waiting) is not noticeable and
    /// resets the collector's allocation counter, so the next automatic one comes later. Also measures how fast the
    /// game allocates memory, which decides what else can be done about GC.
    /// </summary>
    internal static class QuietGc
    {
        internal static int Collections;
        internal static double LastMs, WorstMs;
        internal static double AllocMbMeasured, AllocSeconds;
        internal static long UsedAfterGcMb = -1;

        private static bool doneThisPause;
        private static float pausedSince = -1f, lastQuietGc = -100f;
        private static long lastUsed = -1;
        private static int lastCount = -1;
        private static float lastSampleAt;

        internal static void Tick(bool optimizationsOn, bool hasCity, bool playing, bool fastForward, float now)
        {
            if (!optimizationsOn || !PerfConfig.QuietGc.Value || !hasCity || GcStats.Broken) { pausedSince = -1f; return; }
            bool paused = !playing;
            if (paused)
            {
                if (pausedSince < 0f) pausedSince = now;
                if (!doneThisPause && now - pausedSince >= 0.75f)
                {
                    doneThisPause = true;
                    Collect(now);
                }
            }
            else
            {
                pausedSince = -1f;
                doneThisPause = false;
                if (fastForward && now - lastQuietGc >= 20f) Collect(now);
            }
        }

        private static void Collect(float now)
        {
            try
            {
                lastQuietGc = now;
                long t0 = Stopwatch.GetTimestamp();
                Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_collect(0);
                LastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (LastMs > WorstMs) WorstMs = LastMs;
                Collections++;
            }
            catch (Exception e)
            {
                Runner.ReportError("quiet-gc", e);
            }
        }

        /// <summary>Called every 0.5 s while measuring: estimates the allocation rate from heap growth between collections.</summary>
        internal static void Sample(bool measuring, float now)
        {
            try
            {
                long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024);
                int count = Il2CppSystem.GC.CollectionCount(0);
                if (lastCount >= 0 && count != lastCount) UsedAfterGcMb = used;
                if (measuring && lastUsed >= 0 && count == lastCount && used >= lastUsed)
                {
                    AllocMbMeasured += used - lastUsed;
                    AllocSeconds += now - lastSampleAt;
                }
                lastUsed = used;
                lastCount = count;
                lastSampleAt = now;
            }
            catch { }
        }

        internal static string Rate => AllocSeconds < 5 ? "-" : $"{AllocMbMeasured / AllocSeconds:0.0} MB/s";
    }

    /// <summary>Reads the game's own object/room streaming settings (ObjectPoolingController) for the report.</summary>
    internal static class StreamingProbe
    {
        internal static string Describe()
        {
            try
            {
                ObjectPoolingController p = ObjectPoolingController.Instance;
                if (p == null) return "ObjectPoolingController not found";
                return $"gradual object spawning {p.useGradualSpawning} (new {p.loadNewObjectPerFrame}/frame, pooled {p.loadPooledObjectPerFrame}/frame), " +
                       $"pooling {p.usePooling} (cache {p.maximumInteractablePoolCache}, pooled now {p.interactablesCurrentlyPooled}), " +
                       $"range checks {p.useRange} ({p.maximumRangeCheckingPerFrame}/frame), " +
                       $"gradual room loading {p.allowGradualRoomLoading} ({p.roomsLoadedPerFrame} room/frame, queued now {p.roomStuffToLoad}), " +
                       $"room cache limit {p.roomCacheLimit} ({p.maxRoomCache}), objects waiting to spawn {p.interactablesToLoadCount}";
            }
            catch (Exception e)
            {
                return "unavailable: " + e.GetType().Name;
            }
        }
    }
}
