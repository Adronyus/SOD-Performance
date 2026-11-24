using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace SODPerformance
{
    /// <summary>Field offsets from the IL2CPP metadata (includes the object header, like the generated code uses).</summary>
    internal static class Il2CppFields
    {
        internal static int Offset(IntPtr klass, string field)
        {
            if (klass == IntPtr.Zero) return -1;
            for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(k, field);
                if (f != IntPtr.Zero) return (int)IL2CPP.il2cpp_field_get_offset(f);
            }
            return -1;
        }
    }

    /// <summary>Loads the mod's native helper (sodsampler.native) once.</summary>
    internal static class Native
    {
        internal static IntPtr Lib;
        internal static string Error = "";

        internal static bool Load()
        {
            if (Lib != IntPtr.Zero) return true;
            try
            {
                if (!OperatingSystem.IsWindows()) { Error = "not Windows"; return false; }
                string dir = Path.GetDirectoryName(typeof(Native).Assembly.Location) ?? "";
                // Not named .dll so BepInEx does not try to load it as a plugin assembly.
                string path = Path.Combine(dir, "sodsampler.native");
                if (!File.Exists(path)) { Error = "sodsampler.native missing next to SODPerformance.dll"; return false; }
                Lib = NativeLibrary.Load(path);
                return true;
            }
            catch (Exception e)
            {
                Error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        internal static IntPtr Export(string name) => NativeLibrary.GetExport(Lib, name);
    }

    /// <summary>
    /// SoCustomComparison.Equals (how the game compares presets) asks IL2CPP for GetType() four times per call, each under a
    /// lock. Loading does millions of these comparisons (EvidenceCreator.CreateFact): the sampling profiler put it at about
    /// half of all loading time. The native helper replaces it with an equivalent that compares the class pointers and the
    /// cached preset names directly (see native/sodsampler.c).
    /// </summary>
    internal static unsafe class FastPresetCompare
    {
        internal static bool Installed;
        internal static string Status = "off";
        private static delegate* unmanaged[Cdecl]<long> pCalls;

        internal static void Install()
        {
            try
            {
                if (!Native.Load()) { Status = "native helper not loaded: " + Native.Error; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }
                IntPtr klass = Il2CppClassPointerStore<SoCustomComparison>.NativeClassPtr;
                if (klass == IntPtr.Zero) { Status = "class not found"; return; }
                // SoCustomComparison has two Equals: Equals(SoCustomComparison) and the override Equals(object). The one the game
                // calls (and the slow one) is Equals(object); pick it by its parameter type, never by name alone.
                IntPtr eq = IntPtr.Zero, gn = IntPtr.Zero;
                IntPtr iter = IntPtr.Zero, m;
                while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    string name = IL2CPP.il2cpp_method_get_name_(m);
                    uint pc = IL2CPP.il2cpp_method_get_param_count(m);
                    if (name == "Equals" && pc == 1 && IL2CPP.il2cpp_type_get_name_(IL2CPP.il2cpp_method_get_param(m, 0)) == "System.Object") eq = m;
                    else if (name == "GetPresetName" && pc == 0) gn = m;
                }
                if (eq == IntPtr.Zero || gn == IntPtr.Zero) { Status = "methods not found"; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }
                IntPtr target = Marshal.ReadIntPtr(eq);
                IntPtr getName = Marshal.ReadIntPtr(gn);
                if (target == IntPtr.Zero || getName == IntPtr.Zero) { Status = "no native code pointer"; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }

                // Only patch code that is a whole function of its own: a real function start, and no other game method shares it
                // (the compiler merges identical functions; patching shared code would change other methods too).
                var isStart = (delegate* unmanaged[Cdecl]<IntPtr, int>)Native.Export("sod_is_function_start");
                if (isStart(target) != 1) { Status = "target is not a function start: left alone"; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }
                int sharers = CountMethodsAt(target);
                if (sharers != 1) { Status = $"code shared by {sharers} methods: left alone"; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }
                byte b0 = Marshal.ReadByte(target), b1 = Marshal.ReadByte(target, 1);
                if (b0 == 0xE9 || (b0 == 0xFF && b1 == 0x25) || (b0 == 0x48 && b1 == 0xB8))
                {
                    Status = "already hooked by something else: left alone";
                    Plugin.L.LogWarning("Fast preset compare: " + Status);
                    return;
                }
                int nameOffset = Il2CppFields.Offset(klass, "presetName");
                if (nameOffset <= 0) { Status = "presetName field not found (game changed?)"; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }
                var patch = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int>)Native.Export("sod_patch_preset_equals");
                pCalls = (delegate* unmanaged[Cdecl]<long>)Native.Export("sod_preset_equals_calls");
                int r = patch(target, getName, gn, nameOffset);
                if (r != 0) { Status = $"patch failed ({r})"; Plugin.L.LogWarning("Fast preset compare: " + Status); return; }
                Installed = true;
                Status = "installed";
                Plugin.L.LogInfo("Fast preset compare installed (SoCustomComparison.Equals).");
            }
            catch (Exception e)
            {
                Status = $"error: {e.GetType().Name}: {e.Message}";
                Plugin.L.LogWarning("Fast preset compare: " + Status);
            }
        }

        internal static long Calls => pCalls == null ? 0 : pCalls();

        /// <summary>How many methods in Assembly-CSharp have their code at this address.</summary>
        private static int CountMethodsAt(IntPtr code)
        {
            int n = 0;
            IntPtr domain = IL2CPP.il2cpp_domain_get();
            uint count = 0;
            IntPtr* asms = IL2CPP.il2cpp_domain_get_assemblies(domain, ref count);
            for (uint i = 0; i < count; i++)
            {
                IntPtr image = IL2CPP.il2cpp_assembly_get_image(asms[i]);
                if (IL2CPP.il2cpp_image_get_name_(image) != "Assembly-CSharp.dll") continue;
                uint cc = IL2CPP.il2cpp_image_get_class_count(image);
                for (uint j = 0; j < cc; j++)
                {
                    IntPtr klass = IL2CPP.il2cpp_image_get_class(image, j);
                    if (klass == IntPtr.Zero) continue;
                    IntPtr iter = IntPtr.Zero, m;
                    while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                        if (Marshal.ReadIntPtr(m) == code) n++;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// NewAddress/NewFloor.GetBreakerSecurity/Lights/Doors find the breaker box by scanning CityData.interactableDirectory
    /// (every interactable in the city) for a matching id. A found breaker is cached in a field, but an address without one
    /// scans the whole list again on every call, and every security camera/alarm that spawns asks. In slow frames this was
    /// the single biggest game function (15.6%). This answers the same question from an id index: same first-match result,
    /// same cached fields, no scan. The index follows the directory as it grows and rebuilds itself if items were removed.
    /// </summary>
    internal static unsafe class BreakerLookup
    {
        internal static bool Installed, EnabledNow, Broken;
        internal static long Answered, NotFound, Rebuilds, Appends, HandedBack;
        internal static double LastRebuildMs;

        private static readonly Dictionary<int, (IntPtr p, IntPtr klass)> index = new Dictionary<int, (IntPtr, IntPtr)>();
        private static IntPtr indexedList;
        private static int indexedSize;
        private static IntPtr lastIndexedItem;

        // Field offsets, read from the game's IL2CPP metadata at start-up (not hard-coded), so a game update that moves
        // fields cannot make this read the wrong memory. Index: 0 security, 1 doors, 2 lights.
        private static int AddrFloor = -1, InteractableId = -1, UnityCachedPtr = -1;
        private static readonly int[] AddrId = new int[3], AddrCache = new int[3], FloorId = new int[3], FloorCache = new int[3];
        private static int ListItems = -1, ListSize = -1;
        internal static string OffsetStatus = "-";

        private static bool ResolveOffsets()
        {
            IntPtr addr = Il2CppClassPointerStore<NewAddress>.NativeClassPtr;
            IntPtr floor = Il2CppClassPointerStore<NewFloor>.NativeClassPtr;
            IntPtr inter = Il2CppClassPointerStore<Interactable>.NativeClassPtr;
            IntPtr uobj = Il2CppClassPointerStore<UnityEngine.Object>.NativeClassPtr;
            string[] kinds = { "Security", "Doors", "Lights" };
            AddrFloor = Il2CppFields.Offset(addr, "floor");
            InteractableId = Il2CppFields.Offset(inter, "id");
            UnityCachedPtr = Il2CppFields.Offset(uobj, "m_CachedPtr");
            bool ok = AddrFloor > 0 && InteractableId > 0 && UnityCachedPtr > 0;
            for (int k = 0; k < 3; k++)
            {
                AddrId[k] = Il2CppFields.Offset(addr, "breaker" + kinds[k] + "ID");
                AddrCache[k] = Il2CppFields.Offset(addr, "breaker" + kinds[k]);
                FloorId[k] = Il2CppFields.Offset(floor, "breaker" + kinds[k] + "ID");
                FloorCache[k] = Il2CppFields.Offset(floor, "breaker" + kinds[k]);
                ok &= AddrId[k] > 0 && AddrCache[k] > 0 && FloorId[k] > 0 && FloorCache[k] > 0;
            }
            OffsetStatus = ok ? $"fields found (address floor 0x{AddrFloor:X}, security id 0x{AddrId[0]:X}, cache 0x{AddrCache[0]:X})" : "a field was not found (game changed?)";
            return ok;
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                if (!ResolveOffsets())
                {
                    Plugin.L.LogWarning("Breaker lookup not installed: " + OffsetStatus);
                    return;
                }
                Patch(harmony, typeof(NewAddress), "GetBreakerSecurity", nameof(AddrSecurity));
                Patch(harmony, typeof(NewAddress), "GetBreakerDoors", nameof(AddrDoors));
                Patch(harmony, typeof(NewAddress), "GetBreakerLights", nameof(AddrLights));
                Patch(harmony, typeof(NewFloor), "GetBreakerSecurity", nameof(FloorSecurity));
                Patch(harmony, typeof(NewFloor), "GetBreakerDoors", nameof(FloorDoors));
                Patch(harmony, typeof(NewFloor), "GetBreakerLights", nameof(FloorLights));
                Installed = true;
                Plugin.L.LogInfo("Breaker lookup index installed.");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Breaker lookup not installed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Patch(Harmony harmony, Type t, string method, string prefix)
        {
            var m = AccessTools.Method(t, method);
            if (m == null) throw new MissingMethodException(t.Name, method);
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(BreakerLookup), prefix) { priority = Priority.First });
        }

        public static bool AddrSecurity(NewAddress __instance, ref Interactable __result) => Address(__instance, ref __result, 0);
        public static bool AddrDoors(NewAddress __instance, ref Interactable __result) => Address(__instance, ref __result, 1);
        public static bool AddrLights(NewAddress __instance, ref Interactable __result) => Address(__instance, ref __result, 2);
        public static bool FloorSecurity(NewFloor __instance, ref Interactable __result) => Floor(__instance, ref __result, 0);
        public static bool FloorDoors(NewFloor __instance, ref Interactable __result) => Floor(__instance, ref __result, 1);
        public static bool FloorLights(NewFloor __instance, ref Interactable __result) => Floor(__instance, ref __result, 2);

        private static bool Main => Environment.CurrentManagedThreadId == LoadProfiler.MainThreadId;

        private static bool Address(NewAddress inst, ref Interactable result, int kind)
        {
            if (!EnabledNow || Broken || !Main || inst == null) return true;
            try
            {
                byte* a = (byte*)inst.Pointer;
                if (*(IntPtr*)(a + AddrCache[kind]) != IntPtr.Zero) return true;   // the game returns its cached breaker at once
                if (!Ready()) return true;
                IntPtr found = Lookup(*(int*)(a + AddrId[kind]));
                if (found != IntPtr.Zero)
                {
                    var obj = new Interactable(found);
                    SetAddr(inst, kind, obj);
                    result = obj;
                    Answered++;
                    return false;
                }
                if (!indexCurrent) { HandedBack++; return true; }   // index could not be brought up to date: the game searches itself
                // Same fallback as the game: the floor's breaker.
                byte* f = (byte*)*(IntPtr*)(a + AddrFloor);
                if (f == null || *(IntPtr*)(f + UnityCachedPtr) == IntPtr.Zero) { result = null; NotFound++; return false; }   // (game would log an error)
                return FloorCore(new NewFloor((IntPtr)f), f, ref result, kind);
            }
            catch (Exception e)
            {
                Broken = true;
                Runner.ReportError("breaker-lookup", e);
                return true;
            }
        }

        private static bool Floor(NewFloor inst, ref Interactable result, int kind)
        {
            if (!EnabledNow || Broken || !Main || inst == null) return true;
            try
            {
                byte* f = (byte*)inst.Pointer;
                if (*(IntPtr*)(f + FloorCache[kind]) != IntPtr.Zero) return true;
                if (!Ready()) return true;
                return FloorCore(inst, f, ref result, kind);
            }
            catch (Exception e)
            {
                Broken = true;
                Runner.ReportError("breaker-lookup", e);
                return true;
            }
        }

        private static bool FloorCore(NewFloor floor, byte* f, ref Interactable result, int kind)
        {
            IntPtr cached = *(IntPtr*)(f + FloorCache[kind]);
            if (cached != IntPtr.Zero) { result = new Interactable(cached); Answered++; return false; }
            IntPtr found = Lookup(*(int*)(f + FloorId[kind]));
            if (found != IntPtr.Zero)
            {
                var obj = new Interactable(found);
                SetFloor(floor, kind, obj);
                result = obj;
                Answered++;
                return false;
            }
            if (!indexCurrent) { HandedBack++; return true; }
            result = null;
            NotFound++;
            return false;
        }

        private static void SetAddr(NewAddress a, int kind, Interactable v)
        {
            if (kind == 0) a.breakerSecurity = v; else if (kind == 1) a.breakerDoors = v; else a.breakerLights = v;
        }

        private static void SetFloor(NewFloor f, int kind, Interactable v)
        {
            if (kind == 0) f.breakerSecurity = v; else if (kind == 1) f.breakerDoors = v; else f.breakerLights = v;
        }

        private static IntPtr listPtr;

        private static bool Ready()
        {
            CityData city = CityData.Instance;
            var list = city?.interactableDirectory;
            if (list == null) return false;
            listPtr = list.Pointer;
            return true;
        }

        // Field positions (List<T>._items/_size, Interactable.id, ...) come from the game's metadata at start-up (ResolveOffsets).
        // Always bring the index up to date first (O(1) when nothing changed, only the new items when the list grew), so an
        // answer is always what the game's own first-match search would return right now. If it cannot be brought up to
        // date (items were removed and a rebuild ran less than 2 s ago), indexCurrent is false and the caller lets the game
        // search itself.
        private static IntPtr Lookup(int id)
        {
            Sync();
            if (!indexCurrent) return IntPtr.Zero;
            if (index.TryGetValue(id, out var e) && Valid(e, id)) return e.p;
            return IntPtr.Zero;
        }

        // Still the same object: same class pointer and same id (guards against an entry whose item was removed since).
        private const int ArrayData = 0x20;   // IL2CPP array header size (klass, monitor, bounds, max_length): part of the runtime ABI

        private static bool Valid((IntPtr p, IntPtr klass) e, int id) =>
            e.p != IntPtr.Zero && *(IntPtr*)e.p == e.klass && *(int*)((byte*)e.p + InteractableId) == id;

        // True when the index matches the list right now (after Sync). A full rebuild is allowed at most every 2 s;
        // in between, a miss is handed back to the game's own search instead of rebuilding again and again (city
        // generation creates and removes interactables constantly).
        private static bool indexCurrent;
        private static float lastFullRebuild = -100f;

        private static void Sync()
        {
            indexCurrent = false;
            byte* list = (byte*)listPtr;
            if (ListItems < 0)
            {
                IntPtr lk = *(IntPtr*)list;   // the list object's class
                ListItems = Il2CppFields.Offset(lk, "_items");
                ListSize = Il2CppFields.Offset(lk, "_size");
                if (ListItems <= 0 || ListSize <= 0) { Broken = true; Plugin.L.LogWarning("Breaker lookup off: List<T> layout not found."); return; }
            }
            byte* items = (byte*)*(IntPtr*)(list + ListItems);
            int size = *(int*)(list + ListSize);
            if (items == null) return;
            bool sameList = listPtr == indexedList;
            bool appendOnly = sameList && size >= indexedSize &&
                              (indexedSize == 0 || *(IntPtr*)(items + ArrayData + (long)(indexedSize - 1) * 8) == lastIndexedItem);
            if (appendOnly && size == indexedSize) { indexCurrent = true; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!appendOnly)
            {
                if (now - lastFullRebuild < 2f) return;
                lastFullRebuild = now;
            }
            int start = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (appendOnly) { start = indexedSize; Appends++; }
            else { index.Clear(); Rebuilds++; }
            for (int i = start; i < size; i++)
            {
                IntPtr it = *(IntPtr*)(items + ArrayData + (long)i * 8);
                if (it == IntPtr.Zero) continue;
                int id = *(int*)((byte*)it + InteractableId);
                if (!index.ContainsKey(id)) index[id] = (it, *(IntPtr*)it);   // FirstOrDefault: the first match wins
            }
            indexedList = listPtr;
            indexedSize = size;
            lastIndexedItem = size > 0 ? *(IntPtr*)(items + ArrayData + (long)(size - 1) * 8) : IntPtr.Zero;
            if (!appendOnly) LastRebuildMs = sw.Elapsed.TotalMilliseconds;
            indexCurrent = true;
        }

        internal static void Reset()
        {
            indexCurrent = false;
            index.Clear();
            indexedList = IntPtr.Zero;
            indexedSize = 0;
            lastIndexedItem = IntPtr.Zero;
        }
    }
}
