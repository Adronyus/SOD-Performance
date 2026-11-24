using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using HarmonyLib;

namespace SODPerformance
{
    /// <summary>
    /// Measures city loading: wall time from the first loading-screen text to the moment the game is playable,
    /// the loading-screen phase timeline (CityConstructor.SetLoadingText), and how much room-visibility (culling tree)
    /// generation happened during load vs during play, on the main thread vs worker threads.
    /// </summary>
    internal static class LoadProfiler
    {
        internal static int MainThreadId;
        internal static bool Loading;
        private static long loadStart;
        internal static double LastLoadSeconds = -1;
        internal static string LastLoadKind = "";
        internal static readonly List<(double t, string text)> Timeline = new List<(double, string)>();
        internal static readonly List<(double t, string text)> LastTimeline = new List<(double, string)>();

        // Culling tree generation stats: [0] during load, [1] during play; main thread / worker threads.
        internal static readonly long[] GenMainCount = new long[2], GenWorkerCount = new long[2], GenMainTicks = new long[2], GenWorkerTicks = new long[2];
        internal static readonly long[] GenMainMaxTicks = new long[2];

        [ThreadStatic] private static long tStart;
        [ThreadStatic] private static int tDepth;

        internal static bool OnMainThread => Environment.CurrentManagedThreadId == MainThreadId;

        internal static void Install(Harmony harmony)
        {
            TryPatch(harmony, typeof(CityConstructor), "SetLoadingText", nameof(SetLoadingTextPrefix), null);
            foreach (var m in new[] { "StartLoading", "LoadSaveGame", "LoadCityStartNewGame", "GenerateNewCity" })
                TryPatch(harmony, typeof(CityConstructor), m, nameof(LoadStartPrefix), null);
        }

        internal static void TryPatch(Harmony harmony, Type type, string method, string prefix, string postfix, Type hookType = null, int priority = -1)
        {
            try
            {
                var target = AccessTools.GetDeclaredMethods(type).FirstOrDefault(x => x.Name == method && !x.IsGenericMethodDefinition);
                if (target == null) { Plugin.L.LogWarning($"Load profiler: {type.Name}.{method} not found."); return; }
                hookType ??= typeof(LoadProfiler);
                HarmonyMethod pre = prefix != null ? new HarmonyMethod(AccessTools.Method(hookType, prefix)) : null;
                HarmonyMethod post = postfix != null ? new HarmonyMethod(AccessTools.Method(hookType, postfix)) : null;
                if (priority >= 0) { if (pre != null) pre.priority = priority; if (post != null) post.priority = priority; }
                harmony.Patch(target, prefix: pre, postfix: post);
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Could not hook {type.Name}.{method}: {e.GetType().Name}: {e.Message}");
            }
        }

        internal static int LoadsThisGame;

        private static void Begin(string kind)
        {
            if (Loading) return;
            Loading = true;
            LoadsThisGame++;
            // Per-load numbers: the culling-tree counters and every statistic in the report start again for this city.
            for (int i = 0; i < 2; i++) { GenMainCount[i] = 0; GenMainTicks[i] = 0; GenMainMaxTicks[i] = 0; GenWorkerCount[i] = 0; GenWorkerTicks[i] = 0; }
            try { Runner.ResetSessionStats(); } catch { }
            CullingCache.ForgetCity();
            LastLoadKind = kind;
            loadStart = Stopwatch.GetTimestamp();
            Timeline.Clear();
            lastState = null;
        }

        // The loading screen text is not set through SetLoadingText in this build, so follow CityConstructor.loadState
        // every frame instead: when each step starts, how many frames it took and its longest frame.
        private static string lastState;
        private static int stateFrames;
        private static double stateWorstMs, stateStart;

        private static void PollState()
        {
            try
            {
                CityConstructor cc = CityConstructor.Instance;
                if (cc == null) return;
                string st = cc.loadState.ToString();
                double now = Elapsed();
                double frameMs = UnityEngine.Time.unscaledDeltaTime * 1000.0;
                if (st != lastState)
                {
                    CloseState(now);
                    lastState = st;
                    stateFrames = 0;
                    stateWorstMs = 0;
                    stateStart = now;
                    if (Timeline.Count < 400) Timeline.Add((now, st));
                }
                else
                {
                    stateFrames++;
                    if (frameMs > stateWorstMs) stateWorstMs = frameMs;
                }
            }
            catch { }
        }

        private static void CloseState(double now)
        {
            if (lastState == null || Timeline.Count == 0) return;
            int i = Timeline.Count - 1;
            if (Timeline[i].text != lastState) return;
            Timeline[i] = (Timeline[i].t, $"{lastState,-28} {now - stateStart,6:0.0} s, {stateFrames} frames, longest frame {stateWorstMs:0} ms");
        }

        public static void LoadStartPrefix() => Begin("load");

        // SetLoadingText() takes no arguments in this game version: it shows CityConstructor.loadState.
        public static void SetLoadingTextPrefix(CityConstructor __instance)
        {
            try
            {
                Begin("load");
                if (Timeline.Count < 400 && __instance != null)
                {
                    string t = __instance.loadState.ToString();
                    if (Timeline.Count == 0 || Timeline[Timeline.Count - 1].text != t) Timeline.Add((Elapsed(), t));
                }
            }
            catch { }
        }

        internal static double Elapsed() => (Stopwatch.GetTimestamp() - loadStart) / (double)Stopwatch.Frequency;

        /// <summary>Called every frame from the runner.</summary>
        internal static void Tick(bool playing)
        {
            if (Loading && !playing) PollState();
            if (Loading && playing)
            {
                LastLoadSeconds = Elapsed();
                CloseState(LastLoadSeconds);
                Runner.ToastUntil = UnityEngine.Time.realtimeSinceStartup + 12f;
                Loading = false;
                LastTimeline.Clear();
                LastTimeline.AddRange(Timeline);
                Plugin.L.LogInfo($"City load took {LastLoadSeconds:0.0} s (culling trees generated during load: main thread {GenMainCount[0]} in {GenMainTicks[0] * 1000.0 / Stopwatch.Frequency / 1000:0.0} s, worker threads {GenWorkerCount[0]}; from cache {CullingCache.HitsDuringLoad}).");
            }
        }

        // --- NewRoom.GenerateCullingTree timing (any thread) ---
        internal static void GenBegin()
        {
            if (tDepth++ == 0) tStart = Stopwatch.GetTimestamp();
        }

        internal static void GenEnd()
        {
            if (--tDepth > 0) return;
            tDepth = 0;
            long d = Stopwatch.GetTimestamp() - tStart;
            int phase = Loading ? 0 : 1;
            if (OnMainThread)
            {
                Interlocked.Increment(ref GenMainCount[phase]);
                Interlocked.Add(ref GenMainTicks[phase], d);
                if (d > GenMainMaxTicks[phase]) GenMainMaxTicks[phase] = d;
            }
            else
            {
                Interlocked.Increment(ref GenWorkerCount[phase]);
                Interlocked.Add(ref GenWorkerTicks[phase], d);
            }
        }
    }

    /// <summary>
    /// Room visibility (culling tree) cache, per city, on disk.
    ///
    /// With Game.generateCullingInGame on (as in the current game version), NewRoom.LoadCullingTree ignores any saved trees
    /// and GeometryCullingController recalculates every room's tree each session: synchronously on the main thread during loading
    /// (GeometryCullingController.Update -> NewRoom.GenerateCullingTree) and later in the background; a room you enter before it is
    /// done is calculated on the spot (UpdateCullingForRoom), which is the 100-300 ms room-entry freeze.
    ///
    /// A generated tree consists of exactly: cullingTree (Dictionary room -> list of CullTreeEntry{requiredOpenDoors}), doorCheckSet,
    /// nonAudioOccludedRooms and completedTreeCull. We capture those as rooms complete, store them per city, and on the next session
    /// hand a room its stored data in the game's own save format (ct / occ) and call the game's own NewRoom.LoadCullingTree
    /// (with the two flags it checks temporarily off), then restore doorCheckSet exactly and set completedTreeCull.
    /// Only on the main thread; worker-thread generation is left alone.
    /// </summary>
    internal static class CullingCache
    {
        internal sealed class RoomRec
        {
            public int FloorId;
            public int[] Targets;
            public int[][] Doors;
            public int[] Occ;
            public int[] DoorCheck;
        }

        private const int FileVersion = 1;
        private static readonly Dictionary<int, RoomRec> cache = new Dictionary<int, RoomRec>();
        private static readonly HashSet<int> captured = new HashSet<int>();   // rooms whose current tree is in the cache (captured or applied)
        private static string key;
        private static string identity;
        private static string path;
        private static bool dirty;
        private static float nextSave;
        private static int captureCursor, prefillCursor;
        private static volatile bool saving;
        private static int selfChecks;

        internal static int Loaded, Captured, Hits, HitsDuringLoad, Misses, Prefilled, ApplyErrors, SelfCheckOk, SelfCheckBad, Saves, CaptureErrors, NotReady;
        internal static double LastSaveMs, ApplyTicks;
        internal static string Status = "waiting for city";
        internal static bool Enabled => PerfConfig.CullingCache.Value;
        internal static int Count => cache.Count;
        internal static string Key => key ?? "-";

        internal static void Install(Harmony harmony)
        {
            // Priority.First: the cache prefix decides before anything else whether the original runs.
            LoadProfiler.TryPatch(harmony, typeof(NewRoom), "GenerateCullingTree", nameof(GenPrefix), nameof(GenPostfix), typeof(CullingCache), Priority.First);
        }

        // ---------------- hooks ----------------

        public static bool GenPrefix(NewRoom __instance, bool __0)
        {
            LoadProfiler.GenBegin();
            if (!Enabled || __0 || !LoadProfiler.OnMainThread) return true;
            try
            {
                EnsureKey();
                if (key == null || __instance == null) return true;
                if (!cache.TryGetValue(__instance.roomID, out var rec) || rec.FloorId != __instance.roomFloorID)
                {
                    Misses++;
                    return true;
                }
                long t0 = Stopwatch.GetTimestamp();
                bool ok = Apply(__instance, rec);
                ApplyTicks += Stopwatch.GetTimestamp() - t0;
                if (!ok) return true;
                Hits++;
                if (LoadProfiler.Loading) HitsDuringLoad++;
                return false;   // skip the game's generation: same result, already in place
            }
            catch (Exception e)
            {
                ApplyErrors++;
                Runner.ReportError("culling-cache", e);
                return true;
            }
        }

        public static void GenPostfix() => LoadProfiler.GenEnd();

        // ---------------- key / file ----------------

        private static void EnsureKey()
        {
            CityData city = CityData.Instance;
            if (city == null) return;
            string name = city.cityName, seed = city.seed, built = city.cityBuiltWith;
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(seed)) return;
            string id = name + "|" + seed;
            if (key != null)
            {
                if (id == identity) return;
                ResetForNewSession();   // a different city was loaded in the same game session
            }
            identity = id;
            var size = city.citySize;
            string raw = $"{name}|{seed}|{size.x}x{size.y}|{built}|{UnityEngine.Application.version}|v{FileVersion}";
            key = Hash(raw);
            string dir = Path.Combine(Paths.CachePath, "SODPerformance");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"culling-{Safe(name)}-{key}.bin");
            LoadFile();
            Status = $"city '{name}': {cache.Count} rooms cached";
            Plugin.L.LogInfo($"Culling cache for '{name}' ({key}): {cache.Count} rooms loaded from {path}");
        }

        internal static void ResetForNewSession()
        {
            // A different city (or a reload) gets its own key on first use.
            if (dirty) SaveNow(sync: true);
            key = null;
            identity = null;
            path = null;
            cache.Clear();
            captured.Clear();
            captureCursor = prefillCursor = 0;
            Loaded = 0;
            Status = "waiting for city";
        }

        /// <summary>FNV-1a 64-bit of the city identity string (only used to name and check the cache file).</summary>
        internal static string Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        private static string Safe(string n)
        {
            if (string.IsNullOrEmpty(n)) return "city";
            var chars = n.Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(32).ToArray();
            return new string(chars);
        }

        private static void LoadFile()
        {
            cache.Clear();
            captured.Clear();
            if (path == null || !File.Exists(path)) return;
            try
            {
                using var br = new BinaryReader(File.OpenRead(path));
                if (br.ReadInt32() != 0x43444F53 /* "SODC" */ || br.ReadInt32() != FileVersion || br.ReadString() != key) return;
                int n = br.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = br.ReadInt32();
                    var r = new RoomRec { FloorId = br.ReadInt32() };
                    int t = br.ReadInt32();
                    r.Targets = new int[t];
                    r.Doors = new int[t][];
                    for (int k = 0; k < t; k++)
                    {
                        r.Targets[k] = br.ReadInt32();
                        int dn = br.ReadInt32();
                        var d = new int[dn];
                        for (int j = 0; j < dn; j++) d[j] = br.ReadInt32();
                        r.Doors[k] = d;
                    }
                    r.Occ = ReadInts(br);
                    r.DoorCheck = ReadInts(br);
                    cache[id] = r;
                }
                Loaded = cache.Count;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Culling cache file unreadable, starting fresh: {e.Message}");
                cache.Clear();
            }
        }

        private static int[] ReadInts(BinaryReader br)
        {
            int n = br.ReadInt32();
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = br.ReadInt32();
            return a;
        }

        private static void SaveNow(bool sync)
        {
            if (path == null || saving || cache.Count == 0) return;
            var snapshot = cache.ToArray();   // records are immutable once created
            string p = path, k = key;
            dirty = false;
            void Write()
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    string tmp = p + ".tmp";
                    using (var bw = new BinaryWriter(File.Create(tmp)))
                    {
                        bw.Write(0x43444F53);
                        bw.Write(FileVersion);
                        bw.Write(k);
                        bw.Write(snapshot.Length);
                        foreach (var kv in snapshot)
                        {
                            var r = kv.Value;
                            bw.Write(kv.Key);
                            bw.Write(r.FloorId);
                            bw.Write(r.Targets.Length);
                            for (int i = 0; i < r.Targets.Length; i++)
                            {
                                bw.Write(r.Targets[i]);
                                bw.Write(r.Doors[i].Length);
                                foreach (int d in r.Doors[i]) bw.Write(d);
                            }
                            WriteInts(bw, r.Occ);
                            WriteInts(bw, r.DoorCheck);
                        }
                    }
                    if (File.Exists(p)) File.Delete(p);
                    File.Move(tmp, p);
                    Saves++;
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"Culling cache save failed: {e.Message}");
                }
                finally
                {
                    LastSaveMs = sw.Elapsed.TotalMilliseconds;
                    saving = false;
                }
            }
            saving = true;
            if (sync) Write();
            else System.Threading.Tasks.Task.Run(Write);
        }

        private static void WriteInts(BinaryWriter bw, int[] a)
        {
            bw.Write(a.Length);
            foreach (int v in a) bw.Write(v);
        }

        // ---------------- apply (cache -> room, via the game's own loader) ----------------

        // Room ID -> room. CityData.roomDictionary does not hold every room a culling tree can point at (0.8 test: ~80% of
        // cached rooms referenced at least one room missing from it), so also index the room directory, every address's
        // and street's rooms and their null rooms.
        // Raw pointers, not wrappers: a wrapper holds a GC handle and would keep a previous city's rooms (and with them
        // the whole city) alive after loading another one. Cleared at the start of every load.
        private static readonly Dictionary<int, IntPtr> idMap = new Dictionary<int, IntPtr>();
        private static long idMapSig = -1;
        internal static int IdMapSize, ResolvedViaMap;

        /// <summary>Start of every load (also a reload of the same city): fresh room objects, so start over.</summary>
        internal static void ForgetCity()
        {
            idMap.Clear();
            idMapSig = -1;
            ResetForNewSession();   // saves unsaved captures, then the cache is read again for whichever city loads
            Captured = Hits = HitsDuringLoad = Misses = Prefilled = NotReady = SelfCheckOk = SelfCheckBad = ResolvedViaMap = 0;
            selfChecks = 0;
            ApplyTicks = 0;
        }

        private static void RebuildIdMapIfChanged()
        {
            CityData city = CityData.Instance;
            if (city == null) return;
            long sig = (long)(city.roomDirectory?.Count ?? 0) * 1000003L + (city.gameLocationDirectory?.Count ?? 0) * 1009L + (city.streetDirectory?.Count ?? 0);
            if (sig == idMapSig) return;
            idMapSig = sig;
            idMap.Clear();
            void AddRoom(NewRoom r) { if (r != null && !idMap.ContainsKey(r.roomID)) idMap[r.roomID] = r.Pointer; }
            var dir = city.roomDirectory;
            if (dir != null) for (int i = 0; i < dir.Count; i++) AddRoom(dir[i]);
            var locs = city.gameLocationDirectory;
            if (locs != null)
                for (int i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    if (loc == null) continue;
                    AddRoom(loc.nullRoom);
                    var rs = loc.rooms;
                    if (rs != null) for (int j = 0; j < rs.Count; j++) AddRoom(rs[j]);
                }
            var streets = city.streetDirectory;
            if (streets != null)
                for (int i = 0; i < streets.Count; i++)
                {
                    var st = streets[i];
                    if (st == null) continue;
                    AddRoom(st.nullRoom);
                    var rs = st.rooms;
                    if (rs != null) for (int j = 0; j < rs.Count; j++) AddRoom(rs[j]);
                }
            IdMapSize = idMap.Count;
        }

        private static NewRoom Resolve(int id, Il2CppSystem.Collections.Generic.Dictionary<int, NewRoom> dict)
        {
            if (dict != null && dict.ContainsKey(id))
            {
                NewRoom r = dict[id];
                if (r != null) return r;
            }
            if (idMap.TryGetValue(id, out IntPtr m) && m != IntPtr.Zero) { ResolvedViaMap++; return new NewRoom(m); }
            return null;
        }

        private static void NotFound(int id)
        {
            NotReady++;
            if (NotReady <= 5) Plugin.L.LogInfo($"Culling cache: room {id} not found yet (map {IdMapSize} rooms); the game calculates this tree itself.");
        }

        /// <summary>Builds the room's culling tree directly from the record (same result as GenerateCullingTree).</summary>
        private static bool Apply(NewRoom room, RoomRec rec)
        {
            CityData city = CityData.Instance;
            if (city == null) return false;
            var dict = city.roomDictionary;
            RebuildIdMapIfChanged();

            var targets = new NewRoom[rec.Targets.Length];
            for (int i = 0; i < targets.Length; i++)
                if ((targets[i] = Resolve(rec.Targets[i], dict)) == null) { NotFound(rec.Targets[i]); return false; }
            var occRooms = new NewRoom[rec.Occ.Length];
            for (int i = 0; i < occRooms.Length; i++)
                if ((occRooms[i] = Resolve(rec.Occ[i], dict)) == null) { NotFound(rec.Occ[i]); return false; }

            var tree = room.cullingTree;
            if (tree == null) room.cullingTree = tree = new Il2CppSystem.Collections.Generic.Dictionary<NewRoom, Il2CppSystem.Collections.Generic.List<NewRoom.CullTreeEntry>>();
            else tree.Clear();
            for (int i = 0; i < targets.Length; i++)
            {
                Il2CppSystem.Collections.Generic.List<NewRoom.CullTreeEntry> list;
                if (tree.ContainsKey(targets[i])) list = tree[targets[i]];
                else
                {
                    list = new Il2CppSystem.Collections.Generic.List<NewRoom.CullTreeEntry>();
                    tree.Add(targets[i], list);
                }
                var doors = new Il2CppSystem.Collections.Generic.List<int>();
                foreach (int d in rec.Doors[i]) doors.Add(d);
                list.Add(new NewRoom.CullTreeEntry(doors));
            }

            var nao = room.nonAudioOccludedRooms;
            if (nao == null) room.nonAudioOccludedRooms = nao = new Il2CppSystem.Collections.Generic.HashSet<NewRoom>();
            else nao.Clear();
            foreach (var r in occRooms) nao.Add(r);

            var dcs = room.doorCheckSet;
            if (dcs == null) room.doorCheckSet = dcs = new Il2CppSystem.Collections.Generic.HashSet<int>();
            else dcs.Clear();
            foreach (int d in rec.DoorCheck) dcs.Add(d);

            room.completedTreeCull = true;
            captured.Add(room.roomID);

            // Self-check on the first applies: read the room back and compare with the record.
            if (selfChecks < 40)
            {
                selfChecks++;
                var back = Read(room);
                if (back != null && Same(back, rec)) SelfCheckOk++; else SelfCheckBad++;
            }
            return true;
        }

        // ---------------- capture (room -> cache) ----------------

        private static RoomRec Read(NewRoom room)
        {
            var tree = room.cullingTree;
            if (tree == null) return null;
            var targets = new List<int>();
            var doors = new List<int[]>();
            foreach (var kv in tree)
            {
                NewRoom target = kv.Key;
                var list = kv.Value;
                if (target == null || list == null) continue;
                int tid = target.roomID;
                for (int i = 0; i < list.Count; i++)
                {
                    var entry = list[i];
                    var req = entry?.requiredOpenDoors;
                    int[] ds;
                    if (req == null) ds = Array.Empty<int>();
                    else
                    {
                        ds = new int[req.Count];
                        for (int j = 0; j < ds.Length; j++) ds[j] = req[j];
                    }
                    targets.Add(tid);
                    doors.Add(ds);
                }
            }
            var occ = new List<int>();
            var occSet = room.nonAudioOccludedRooms;
            if (occSet != null) foreach (var r in occSet) if (r != null) occ.Add(r.roomID);
            var dc = new List<int>();
            var dcSet = room.doorCheckSet;
            if (dcSet != null) foreach (int d in dcSet) dc.Add(d);
            return new RoomRec { FloorId = room.roomFloorID, Targets = targets.ToArray(), Doors = doors.ToArray(), Occ = occ.ToArray(), DoorCheck = dc.ToArray() };
        }

        private static bool Same(RoomRec a, RoomRec b)
        {
            if (a.Targets.Length != b.Targets.Length) return false;
            var sa = Enumerable.Range(0, a.Targets.Length).Select(i => a.Targets[i] + ":" + string.Join(",", a.Doors[i].OrderBy(x => x))).OrderBy(x => x);
            var sb = Enumerable.Range(0, b.Targets.Length).Select(i => b.Targets[i] + ":" + string.Join(",", b.Doors[i].OrderBy(x => x))).OrderBy(x => x);
            return sa.SequenceEqual(sb)
                && a.Occ.OrderBy(x => x).SequenceEqual(b.Occ.OrderBy(x => x))
                && a.DoorCheck.OrderBy(x => x).SequenceEqual(b.DoorCheck.OrderBy(x => x));
        }

        /// <summary>Main thread, every frame: capture finished rooms, pre-fill cached rooms, save now and then. Time-boxed.</summary>
        internal static void Tick(bool optimizationsOn, bool playing, float now)
        {
            if (!Enabled) return;
            try
            {
                EnsureKey();
                if (key == null) return;
                CityData city = CityData.Instance;
                var rooms = city?.roomDirectory;
                if (rooms == null || rooms.Count == 0) return;
                int n = rooms.Count;
                double budgetMs = LoadProfiler.Loading ? 6.0 : 1.5;
                long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000);

                // 1) Pre-fill: give rooms that are not done yet their cached tree, so neither the background caching nor a
                //    room entry has to calculate them. Skip the room the game may be calculating asynchronously right now.
                if (optimizationsOn && playing && cache.Count > 0 && prefillCursor < n)
                {
                    GeometryCullingController gcc = GeometryCullingController.Instance;
                    NewRoom busy = null;
                    if (gcc != null && gcc.asyncCullingTreeActive && gcc.toCalculateList != null && gcc.toCalculateList.Count > 0) busy = gcc.toCalculateList[0];
                    while (prefillCursor < n && Stopwatch.GetTimestamp() < deadline)
                    {
                        NewRoom room = rooms[prefillCursor++];
                        if (room == null || room.completedTreeCull) continue;
                        if (busy != null && room.Pointer == busy.Pointer) continue;
                        if (!cache.TryGetValue(room.roomID, out var rec) || rec.FloorId != room.roomFloorID) continue;
                        long t0 = Stopwatch.GetTimestamp();
                        if (Apply(room, rec)) Prefilled++;
                        ApplyTicks += Stopwatch.GetTimestamp() - t0;
                    }
                }

                // 2) Capture rooms the game finished (only while playing, so loading is not slowed down).
                if (playing)
                {
                    int scanned = 0;
                    while (scanned < n && Stopwatch.GetTimestamp() < deadline)
                    {
                        if (captureCursor >= n) captureCursor = 0;
                        NewRoom room = rooms[captureCursor++];
                        scanned++;
                        if (room == null || !room.completedTreeCull) continue;
                        int id = room.roomID;
                        if (captured.Contains(id)) continue;
                        captured.Add(id);
                        if (cache.ContainsKey(id)) continue;   // already on disk from an earlier session (city geometry does not change)
                        RoomRec rec;
                        try { rec = Read(room); }
                        catch (Exception e)
                        {
                            CaptureErrors++;
                            if (CaptureErrors <= 3) Runner.ReportError("culling-capture", e);
                            continue;
                        }
                        if (rec == null) continue;
                        cache[id] = rec;
                        Captured++;
                        dirty = true;
                    }
                }

                // 3) Save every 60 s of play when something new was captured (written on a background thread).
                if (dirty && now >= nextSave)
                {
                    nextSave = now + 60f;
                    SaveNow(sync: false);
                }
                Status = $"{cache.Count} rooms cached ({Loaded} from disk, {Captured} new), {Hits} used, {Prefilled} pre-filled";
            }
            catch (Exception e)
            {
                Runner.ReportError("culling-cache-tick", e);
            }
        }
    }
}
