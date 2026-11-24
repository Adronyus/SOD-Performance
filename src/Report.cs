using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SODPerformance
{
    /// <summary>
    /// Writes a plain-text report: system info, settings, game state and an A/B table
    /// (optimizations OFF vs ON) of frame times and per-system script cost.
    /// </summary>
    internal static class Report
    {
        internal static void Write(bool manual)
        {
            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "SODPerformance");
                Directory.CreateDirectory(dir);
                string file = manual ? $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt" : "report-latest.txt";
                string path = Path.Combine(dir, file);
                File.WriteAllText(path, Build(manual));
                Runner.LastReportPath = "BepInEx/SODPerformance/" + file;
                if (manual) Plugin.L.LogInfo($"Report written: {path}");
            }
            catch (Exception e)
            {
                Runner.ReportError("report", e);
            }
        }

        private static string Build(bool manual)
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine($"SOD Performance {Plugin.Version} report — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({(manual ? "manual" : "auto")})");
            sb.AppendLine(new string('=', 78));

            if (LoadProfiler.LoadsThisGame > 1)
                sb.AppendLine($"NOTE: city load #{LoadProfiler.LoadsThisGame} since the game started. Statistics cover this city only, but the game keeps memory " +
                              "from earlier cities (bigger heap, longer GC pauses, slower loading). Restart the game before loading another city for a clean test.\n");
            Section(sb, "System", () =>
            {
                sb.AppendLine($"CPU      {SystemInfo.processorType} ({SystemInfo.processorCount} threads, {SystemInfo.processorFrequency} MHz)");
                sb.AppendLine($"RAM      {SystemInfo.systemMemorySize} MB");
                sb.AppendLine($"GPU      {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB, {SystemInfo.graphicsDeviceType})");
                sb.AppendLine($"Screen   {Screen.width}x{Screen.height} {Screen.fullScreenMode}, vSync {QualitySettings.vSyncCount}, targetFrameRate {Application.targetFrameRate}");
                sb.AppendLine($"Unity    {Application.unityVersion}, game {Application.version}, quality level {QualitySettings.GetQualityLevel()}");
                sb.AppendLine($"Time     fixedDeltaTime {Time.fixedDeltaTime:0.####} s, maximumDeltaTime {Time.maximumDeltaTime:0.###} s");
            });

            Section(sb, "Mod settings", () =>
            {
                sb.AppendLine($"Optimizations now {(Runner.OptimizationsOn ? "ON" : "OFF")}; AI mode {PerfConfig.AiMode.Value}, target {PerfConfig.TargetFps.Value} fps, " +
                              $"budget {PerfConfig.MinBudgetPercent.Value}-{PerfConfig.MaxBudgetPercent.Value}% of vanilla, fixed {PerfConfig.FixedBudget.Value}, max AI delay {PerfConfig.MaxAiDelaySeconds.Value} s");
                sb.AppendLine($"Strip log stack traces {PerfConfig.StripLogStackTraces.Value}, strip error stack traces {PerfConfig.StripErrorStackTraces.Value}, light checks override {PerfConfig.LightChecksPerFrame.Value}");
                sb.AppendLine($"Profiler {(Profiler.Installed ? "on" : "off")}, per-instance hooks {PerfConfig.ProfilePerInstanceUpdates.Value}, AI spike catcher {PerfConfig.CatchAiSpikes.Value} (>{PerfConfig.AiSpikeMs.Value} ms), spike threshold {PerfConfig.SpikeThresholdMs.Value} ms");
                sb.AppendLine($"Auto A/B {(Runner.AutoAB ? "running" : "off")} (every {PerfConfig.AutoABSeconds.Value} s), switches so far {Runner.ABSwitches}");
            });

            Section(sb, "What the fixes did", () =>
            {
                double minOff = FrameStats.SumMs[0] / 60000.0, minOn = FrameStats.SumMs[1] / 60000.0;
                sb.AppendLine($"AI time budget: {(AiTimeBudget.Installed ? PerfConfig.AiFrameBudgetMs.Value + " ms/frame" : "not installed")}; " +
                              $"frames where it stopped the AI queue early: {AiTimeBudget.SesCuts[1]} ({(minOn > 0 ? AiTimeBudget.SesCuts[1] / minOn : 0):0.0}/min while ON)");
                sb.AppendLine($"Shadow distance: game multiplier {ShadowTuning.GameMult:0.###}, applied now {ShadowTuning.AppliedMult:0.###} (scale {PerfConfig.ShadowDistanceScale.Value} when ON); " +
                              $"last light refresh {ShadowTuning.LastRefreshCount} lights in {ShadowTuning.LastRefreshMs:0.0} ms");
                sb.AppendLine($"Shadow updates per frame: game {ShadowTuning.GameMaxUpdates}, applied now {ShadowTuning.AppliedMaxUpdates} (override {PerfConfig.MaxShadowUpdatesPerFrame.Value})");
                sb.AppendLine($"Movement stagger: {(MovementStagger.Installed ? "installed" : "not installed")}; last second {MovementStagger.UpdatesLastSecond} staggered citizen updates in {MovementStagger.TicksLastSecond} ticks, slowest tick {MovementStagger.MaxMsLastSecond:0.0} ms");
                sb.AppendLine($"Other lights' shadow cap {PerfConfig.OtherLightShadowDistance.Value} m: tracking {OtherLightShadows.Tracked} shadow-casting lights, {OtherLightShadows.Capped} capped now; last scan {OtherLightShadows.LastScanMs:0.0} ms");
                sb.AppendLine($"Trash trickle: {TrashTrickle.Calls} small cleanups, {TrashTrickle.TotalRemoved} items removed, last {TrashTrickle.LastMs:0.0} ms, worst {TrashTrickle.WorstMs:0.0} ms");
                sb.AppendLine($"Incremental GC slice: game {GcTuning.Original / 1000} µs, now {GcStats.SliceNs / 1000} µs (setting {PerfConfig.GcSliceMs.Value} ms when ON)");
                sb.AppendLine($"Fast-forward (sleep/wait) excluded from all statistics: {Runner.FastForwardSeconds / 60:0.0} min, {Runner.FastForwardFrames} frames");
                sb.AppendLine($"Sound occlusion moved off room-change frames: {(SoundOcclusionDefer.Installed ? "installed" : "not installed")}, " +
                              $"deferred {SoundOcclusionDefer.Deferred}x, run later {SoundOcclusionDefer.RunLater}x (last {SoundOcclusionDefer.LastMs:0.0} ms, worst {SoundOcclusionDefer.WorstMs:0.0} ms)");
                sb.AppendLine($"Room hiding spread over frames: {(RoomHideDefer.Installed ? (PerfConfig.DeferRoomHiding.Value ? "on" : "off (measuring only)") : "not installed")}, " +
                              $"deferred {RoomHideDefer.Deferred}, hidden later {RoomHideDefer.HiddenLater}, visible again before hiding {RoomHideDefer.Cancelled}, " +
                              $"queue now {RoomHideDefer.QueueNow} (max {RoomHideDefer.QueueMax}), slowest hiding frame {RoomHideDefer.WorstFrameMs:0.0} ms");
                sb.AppendLine($"Far-room showing spread over frames (> {PerfConfig.ShowNearDistance.Value:0} m): {(PerfConfig.SpreadFarRoomShowing.Value ? "on" : "off")}, deferred {RoomHideDefer.ShowDeferred}, shown later {RoomHideDefer.ShownLater}, " +
                              $"no longer needed {RoomHideDefer.ShowCancelled}, shown at the {PerfConfig.ShowMaxDelayMs.Value:0} ms limit {RoomHideDefer.ShowForcedLate}, longest wait {RoomHideDefer.MaxShowDelayMs:0} ms, " +
                              $"queue max {RoomHideDefer.ShowQueueMax}, slowest showing frame {RoomHideDefer.WorstShowFrameMs:0.0} ms");
                sb.AppendLine($"Room contents loaded nearest-first in small steps: {(PerfConfig.SmoothRoomContentLoading.Value ? "on" : "off")} ({RoomContentLoader.Status}); rooms {RoomContentLoader.RoomsLoaded}, furniture clusters {RoomContentLoader.ClustersLoaded}, " +
                              $"total {RoomContentLoader.TotalMs / 1000:0.0} s; slowest cluster {RoomContentLoader.WorstClusterMs:0.0} ms, slowest room finish {RoomContentLoader.WorstFinishMs:0.0} ms, slowest frame {RoomContentLoader.WorstFrameMs:0.0} ms, queue max {RoomContentLoader.QueueMax}");
                sb.AppendLine($"Fast preset compare (SoCustomComparison.Equals): {FastPresetCompare.Status}, {FastPresetCompare.Calls} comparisons so far");
                sb.AppendLine($"Breaker lookup index: {(BreakerLookup.Installed ? (BreakerLookup.Broken ? "off after an error" : "on") : "not installed")}, answered {BreakerLookup.Answered}, no breaker {BreakerLookup.NotFound}, handed back to the game {BreakerLookup.HandedBack}, " +
                              $"index rebuilds {BreakerLookup.Rebuilds} (last {BreakerLookup.LastRebuildMs:0.0} ms), appends {BreakerLookup.Appends}");
                sb.AppendLine($"NewRoom.SetVisible time: show {RoomHideDefer.SetVisibleMs[1] / 1000:0.0} s in {RoomHideDefer.SetVisibleCalls[1]} calls (worst {RoomHideDefer.WorstShowMs:0} ms), " +
                              $"hide {RoomHideDefer.SetVisibleMs[0] / 1000:0.0} s in {RoomHideDefer.SetVisibleCalls[0]} calls (worst {RoomHideDefer.WorstHideMs:0} ms)");
                if (QuietGc.Collections > 0) sb.AppendLine($"GC while paused/fast-forwarding: {QuietGc.Collections} collections (last {QuietGc.LastMs:0} ms, worst {QuietGc.WorstMs:0} ms)");
            });

            Section(sb, "CPU placement", () =>
            {
                sb.AppendLine($"Layout: {CpuPlacement.Layout}");
                sb.AppendLine($"Setting PreferVCacheCores {PerfConfig.PreferVCacheCores.Value}; now: {CpuPlacement.Status}; switches {CpuPlacement.Switches}");
                if (CpuPlacement.HasVCache)
                {
                    sb.AppendLine($"Main thread on V-Cache cores while NOT pinned (Windows' choice; start-up + OFF periods): {CpuPlacement.Percent(0)}");
                    sb.AppendLine($"Main thread on V-Cache cores while pinned: {CpuPlacement.Percent(1)}");
                }
            });

            Section(sb, "Loading", () =>
            {
                double tk = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                sb.AppendLine(LoadProfiler.LastLoadSeconds >= 0
                    ? $"Last city load: {LoadProfiler.LastLoadSeconds:0.0} s (from the first loading-screen text until playable)"
                    : (LoadProfiler.Loading ? $"Loading in progress: {LoadProfiler.Elapsed():0.0} s so far" : "No load measured yet in this game session."));
                sb.AppendLine($"Room visibility trees calculated by the game  during load: main thread {LoadProfiler.GenMainCount[0]} ({LoadProfiler.GenMainTicks[0] * tk / 1000:0.0} s, slowest {LoadProfiler.GenMainMaxTicks[0] * tk:0} ms), worker threads {LoadProfiler.GenWorkerCount[0]} ({LoadProfiler.GenWorkerTicks[0] * tk / 1000:0.0} s CPU)");
                sb.AppendLine($"                                               during play: main thread {LoadProfiler.GenMainCount[1]} ({LoadProfiler.GenMainTicks[1] * tk / 1000:0.0} s, slowest {LoadProfiler.GenMainMaxTicks[1] * tk:0} ms), worker threads {LoadProfiler.GenWorkerCount[1]} ({LoadProfiler.GenWorkerTicks[1] * tk / 1000:0.0} s CPU)");
                sb.AppendLine($"Room visibility cache ({(CullingCache.Enabled ? "on" : "off")}, city key {CullingCache.Key}): {CullingCache.Count} rooms cached ({CullingCache.Loaded} loaded from disk, {CullingCache.Captured} captured this session)");
                sb.AppendLine($"  used instead of calculating: {CullingCache.Hits} (during load {CullingCache.HitsDuringLoad}), pre-filled {CullingCache.Prefilled}, misses {CullingCache.Misses}, " +
                              $"skipped (room not found) {CullingCache.NotReady}, room map {CullingCache.IdMapSize} (resolved outside roomDictionary {CullingCache.ResolvedViaMap}), errors {CullingCache.ApplyErrors}/{CullingCache.CaptureErrors}");
                int applied = CullingCache.Hits + CullingCache.Prefilled;
                sb.AppendLine($"  average apply {(applied > 0 ? CullingCache.ApplyTicks * tk / applied : 0):0.00} ms per room; self-check after apply: {CullingCache.SelfCheckOk} identical, {CullingCache.SelfCheckBad} different; saved {CullingCache.Saves}x (last {CullingCache.LastSaveMs:0} ms)");
                var tl = LoadProfiler.LastLoadSeconds >= 0 ? LoadProfiler.LastTimeline : LoadProfiler.Timeline;
                if (tl.Count > 0)
                {
                    sb.AppendLine("Loading steps (CityConstructor.loadState; start time, step, duration, frames, longest frame):");
                    var shown = tl.Count <= 60 ? tl : tl.Take(30).Concat(tl.Skip(tl.Count - 30)).ToList();
                    foreach (var (t, text) in shown) sb.AppendLine($"  {t,7:0.0}  {Trunc(text.Replace('\n', ' '), 110)}");
                    if (tl.Count > 60) sb.AppendLine($"  ({tl.Count - 60} lines in the middle not shown)");
                }
            });

            Section(sb, "Game state (latest sample)", () =>
            {
                sb.AppendLine($"Citizens {GameProbe.Citizens}, visible {GameProbe.VisibleHumans}, time speed {GameProbe.TimeSpeed} (x{GameProbe.TimeMult:0.##})");
                sb.AppendLine($"AI tiers  veryHigh {GameProbe.VeryHigh}, high {GameProbe.High}, medium {GameProbe.Medium}, low {GameProbe.Low}, veryLow {GameProbe.VeryLow}");
                sb.AppendLine($"AI budget applied {AiGovernor.Applied} (vanilla {AiGovernor.Vanilla}), ran {GameProbe.Executed} last frame, queue {GameProbe.Queue}, backlog {GameProbe.Backlog}");
                sb.AppendLine($"Governor: {AiGovernor.Reason}; estimated AI queue delay {AiGovernor.AiDelaySeconds:0.00} s; one AI decision {(Profiler.AiPerTickMs >= 0 ? (Profiler.AiPerTickMs * 1000).ToString("0") + " µs" : "n/a")}");
                PathFinder pf = PathFinder.Instance;
                if (pf != null)
                    sb.AppendLine($"Pathfinder: {pf.totalPathCalls} path calls, room routes calculated {pf.calculatedRoomRoutes}, reused from cache {pf.returnedCachedRoomRoutes}, internal routes calculated {pf.calculatedInternalRoutes}");
                GameplayControls gc = GameplayControls.Instance;
                if (gc != null)
                    sb.AppendLine($"GameplayControls: routineUpdateFrequency {gc.routineUpdateFrequency} s, gameWorldUpdateFrequency {gc.gameWorldUpdateFrequency} s (both divided by game speed)");
                sb.AppendLine($"Lights tracked {GameProbe.LightsTracked}, unculled {GameProbe.LightsNotCulled}, culled {GameProbe.LightsCulled}, checks/frame {GameProbe.LightChecks}");
                sb.AppendLine($"Shadow map queue {GameProbe.ShadowQueue}, shadows updated last frame {GameProbe.LastShadowsUpdated}, max per frame {GameProbe.MaxShadowUpdates}, shadow fade multiplier {GameProbe.ShadowFadeMult:0.###}");
                Game g = Game.Instance;
                if (g != null)
                {
                    sb.Append("Game render settings:");
                    foreach (var name in RenderSettingNames)
                    {
                        try
                        {
                            var prop = typeof(Game).GetProperty(name);
                            if (prop != null) sb.Append($" {name}={prop.GetValue(g)}");
                        }
                        catch { sb.Append($" {name}=?"); }
                    }
                    sb.AppendLine();
                }
            });

            Section(sb, "Frame times while playing (A/B)", () =>
            {
                sb.AppendLine("                     optimizations OFF     optimizations ON");
                Row(sb, "frames", FrameStats.Frames[0].ToString(), FrameStats.Frames[1].ToString());
                Row(sb, "avg fps", Fps(FrameStats.AvgFor(0)), Fps(FrameStats.AvgFor(1)));
                Row(sb, "avg frame ms", Ms(FrameStats.AvgFor(0)), Ms(FrameStats.AvgFor(1)));
                Row(sb, "median ms", Ms(FrameStats.Percentile(0, 0.5)), Ms(FrameStats.Percentile(1, 0.5)));
                Row(sb, "95th pct ms", Ms(FrameStats.Percentile(0, 0.95)), Ms(FrameStats.Percentile(1, 0.95)));
                Row(sb, "99th pct ms (1% low)", Ms(FrameStats.Percentile(0, 0.99)), Ms(FrameStats.Percentile(1, 0.99)));
                Row(sb, "worst ms", Ms(FrameStats.WorstMs[0]), Ms(FrameStats.WorstMs[1]));
                sb.AppendLine("Note: compare only runs in similar places (same district, time of day, weather).");
            });

            Section(sb, "Where the frame time goes (avg ms per frame while playing)", () =>
            {
                sb.AppendLine("                          optimizations OFF     optimizations ON");
                for (int i = 0; i < Phases.Count; i++)
                    Row(sb, Phases.Names[i], Ms(Phases.SesAvg(0, i)), Ms(Phases.SesAvg(1, i)));
                Row(sb, "physics steps / frame",
                    Phases.SesFrames[0] == 0 ? "-" : ((double)Phases.SesFixedSteps[0] / Phases.SesFrames[0]).ToString("0.00"),
                    Phases.SesFrames[1] == 0 ? "-" : ((double)Phases.SesFixedSteps[1] / Phases.SesFrames[1]).ToString("0.00"));
                sb.AppendLine("wait = present/GPU sync/frame cap; physics = FixedUpdate steps; scripts = Update scripts + animation;");
                sb.AppendLine("render = LateUpdate scripts + HDRP rendering on the main thread. Borders are approximate (script order).");
            });

            Section(sb, "Garbage collector (game heap)", () =>
            {
                double minOff = FrameStats.SumMs[0] / 60000.0, minOn = FrameStats.SumMs[1] / 60000.0;
                Row(sb, "collections", GcStats.SesCollections[0].ToString(), GcStats.SesCollections[1].ToString());
                Row(sb, "per minute", minOff > 0 ? (GcStats.SesCollections[0] / minOff).ToString("0.0") : "-", minOn > 0 ? (GcStats.SesCollections[1] / minOn).ToString("0.0") : "-");
                sb.AppendLine($"Heap used {GcStats.HeapUsedMb} MB of {GcStats.HeapSizeMb} MB; mode {GcStats.Mode}; incremental {GcStats.Incremental} (slice {GcStats.SliceNs / 1000} µs)");
                sb.AppendLine($"Allocation rate while playing: {QuietGc.Rate}; heap used right after the last collection: {(QuietGc.UsedAfterGcMb >= 0 ? QuietGc.UsedAfterGcMb + " MB" : "-")}");
            });

            Section(sb, "Where the main thread's time goes (sampling profiler)", () => Sampler.AppendReport(sb));

            Section(sb, "Slow seconds (seconds that averaged under 45 fps; latest 40)", () =>
            {
                sb.AppendLine($"{SlowSeconds.Total} slow seconds this session. Columns: fps, wait/physics/scripts/render ms per frame, AI veryHigh tier count, visible citizens, top costs (ms per frame).");
                foreach (var e in SlowSeconds.Recent)
                    sb.AppendLine($"  {e.Time:HH:mm:ss} {e.Fps,5:0.0} fps | {e.Phases[0]:0} / {e.Phases[1]:0} / {e.Phases[2]:0} / {e.Phases[3]:0} | vH {e.VeryHigh}, vis {e.Visible} | {e.Top}");
            });

            Section(sb, $"Stutters (frames over {PerfConfig.SpikeThresholdMs.Value:0} ms)", () =>
            {
                double minOff = FrameStats.SumMs[0] / 60000.0, minOn = FrameStats.SumMs[1] / 60000.0;
                Row(sb, "count", Spikes.SesCount[0].ToString(), Spikes.SesCount[1].ToString());
                Row(sb, "per minute", minOff > 0 ? (Spikes.SesCount[0] / minOff).ToString("0.0") : "-", minOn > 0 ? (Spikes.SesCount[1] / minOn).ToString("0.0") : "-");
                sb.AppendLine();
                sb.AppendLine($"{"cause",-40} {"OFF n",6} {"avg ms",7}   {"ON n",6} {"avg ms",7}");
                var causes = Spikes.ByCause[0].Keys.Union(Spikes.ByCause[1].Keys)
                    .OrderByDescending(k => (Spikes.ByCause[0].TryGetValue(k, out var a) ? a.count : 0) + (Spikes.ByCause[1].TryGetValue(k, out var b) ? b.count : 0));
                foreach (var c in causes)
                {
                    Spikes.ByCause[0].TryGetValue(c, out var off);
                    Spikes.ByCause[1].TryGetValue(c, out var on);
                    sb.AppendLine($"{c,-40} {off.count,6} {(off.count > 0 ? off.totalMs / off.count : 0),7:0}   {on.count,6} {(on.count > 0 ? on.totalMs / on.count : 0),7:0}");
                }
                sb.AppendLine();
                sb.AppendLine("Most recent stutters (wait / physics / scripts / render in ms):");
                foreach (var sp in Enumerable.Reverse(Spikes.Recent).Take(40)) sb.AppendLine("  " + Spikes.Describe(sp));
            });

            SpikeTable(sb, AiSpikes.Tick, "Slow AI decisions (one NewAIController.AITick)");
            SpikeTable(sb, AiSpikes.Move, "Slow movement updates (one NewAIController.FrequentUpdate)");

            if (Profiler.Installed)
            {
                Section(sb, "Script cost per frame while playing (avg ms / worst ms / calls per frame)", () =>
                {
                    sb.AppendLine($"{"system",-30} {"target",-44} {"OFF avg",8} {"worst",7} {"calls",6}   {"ON avg",8} {"worst",7} {"calls",6}");
                    foreach (var s in Profiler.Active.OrderByDescending(x => Math.Max(Avg(x, 0), Avg(x, 1))))
                    {
                        sb.AppendLine($"{s.Label,-30} {s.Target,-44} {Avg(s, 0),8:0.000} {Worst(s, 0),7:0.0} {Calls(s, 0),6:0.#}   {Avg(s, 1),8:0.000} {Worst(s, 1),7:0.0} {Calls(s, 1),6:0.#}");
                    }
                    double off = Profiler.Active.Where(x => !x.Nested).Sum(x => Avg(x, 0));
                    double on = Profiler.Active.Where(x => !x.Nested).Sum(x => Avg(x, 1));
                    sb.AppendLine($"{"profiled total (top-level)",-75} {off,8:0.000} {"",14}   {on,8:0.000}");
                    sb.AppendLine("Indented rows run inside another row and are not added to the total.");
                    var failed = Profiler.Sections.Take(Profiler.SectionCount).Where(x => x != null && !x.Installed).Select(x => x.Target).ToList();
                    if (failed.Count > 0) sb.AppendLine("Not hooked: " + string.Join(", ", failed));
                });
            }

            if (manual)
            {
                Section(sb, "Scene object counts (manual reports only)", () =>
                {
                    CountLights(sb);
                    sb.AppendLine($"LightController          {Count<LightController>()}");
                    sb.AppendLine($"InteractableController   {Count<InteractableController>()}");
                    sb.AppendLine($"NewAIController          {Count<NewAIController>()}");
                    sb.AppendLine($"SpeechController         {Count<SpeechController>()}");
                    sb.AppendLine($"NeonSignController       {Count<NeonSignController>()}");
                    sb.AppendLine($"SecuritySystem           {Count<SecuritySystem>()}");
                    CountRigidbodies(sb);
                    CountShadowModes(sb);
                    LightOwners(sb);
                    sb.AppendLine("Non-LightController shadow lights by group (count, average original shadow fade distance m):");
                    foreach (var g in OtherLightShadows.Groups().Take(12)) sb.AppendLine($"  {Trunc(g.group, 70),-70} {g.count,5} {g.avgOriginal,10:0}");
                    GeometryCullingController gcc = GeometryCullingController.Instance;
                    if (gcc != null)
                        sb.AppendLine($"Room culling: backgroundCachingEnabled {gcc.backgroundCachingEnabled}, rooms calculated {gcc.roomsCalculated}, " +
                                      $"to calculate {(gcc.toCalculateList != null ? gcc.toCalculateList.Count : -1)}, async active {gcc.asyncCullingTreeActive}");
                    sb.AppendLine("Object/room streaming (ObjectPoolingController): " + StreamingProbe.Describe());
                    CountRenderers(sb);
                });
            }

            return sb.ToString();
        }

        private static readonly string[] RenderSettingNames =
        {
            "lightFadeDistanceMultiplier", "shadowFadeDistanceMultiplier", "sunShadowUpdateFrequency", "overrideLightControllerShadowMode",
            "shadowModeOverride", "dynamicShadowUpdateFrames", "maxUpdateDynamicShadowsPerFrame", "enableCustomLightCulling",
            "noShadowsWhenPlayerIsInDifferentGoundmapLocation", "shadowsOnCitizenLOD", "flickeringLights",
            "roomWallShadowMode", "roomFloorShadowMode", "roomCeilingShadowMode", "airDuctShadowMode",
        };

        private static void SpikeTable(StringBuilder sb, SpikeCatcher c, string title)
        {
            if (c.Total == 0) return;
            Section(sb, $"{title} over {PerfConfig.AiSpikeMs.Value:0} ms, by goal / action", () =>
            {
                sb.AppendLine($"{c.Total} recorded; where they ran: " + string.Join(", ", c.ByContext.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
                sb.AppendLine($"{"goal / action",-60} {"count",6} {"worst ms",9} {"avg ms",7}");
                foreach (var kv in c.ByAction.OrderByDescending(k => k.Value.totalMs).Take(25))
                    sb.AppendLine($"{Trunc(kv.Key, 60),-60} {kv.Value.count,6} {kv.Value.worstMs,9:0.0} {kv.Value.totalMs / kv.Value.count,7:0.0}");
            });
        }

        private static void CountShadowModes(StringBuilder sb)
        {
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LightController>());
            if (arr == null) return;
            var modes = new System.Collections.Generic.Dictionary<string, int>();
            int withShadows = 0, near = 0;
            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            for (int i = 0; i < arr.Length; i++)
            {
                LightController lc = arr[i]?.TryCast<LightController>();
                if (lc == null || !lc.useShadows) continue;
                withShadows++;
                var hd = lc.hdrpLightData;
                if (hd == null) continue;
                string m = hd.shadowUpdateMode.ToString();
                modes.TryGetValue(m, out int n);
                modes[m] = n + 1;
                if (cam != null && Vector3.Distance(camPos, lc.transform.position) <= hd.shadowFadeDistance) near++;
            }
            sb.AppendLine($"LightControllers with shadows {withShadows}; within their shadow fade distance of the camera: {near}; update modes: " +
                          string.Join(", ", modes.Select(k => $"{k.Key} {k.Value}")));
        }

        /// <summary>What the shadow-casting Unity lights are: LightController lights vs everything else, grouped by object name.</summary>
        private static void LightOwners(StringBuilder sb)
        {
            var owned = new System.Collections.Generic.HashSet<IntPtr>();
            var lcs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LightController>());
            if (lcs != null)
                for (int i = 0; i < lcs.Length; i++)
                {
                    var lc = lcs[i]?.TryCast<LightController>();
                    if (lc != null && lc.lightComponent != null) owned.Add(lc.lightComponent.Pointer);
                }

            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            var groups = new System.Collections.Generic.Dictionary<string, int[]>(); // total, shadows, inShadowRange, everyFrame
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>());
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                Light l = arr[i]?.TryCast<Light>();
                if (l == null || !l.isActiveAndEnabled) continue;
                string key;
                if (owned.Contains(l.Pointer)) key = "LightController";
                else
                {
                    string n = l.gameObject.name ?? "?";
                    int cut = n.IndexOf('(');
                    if (cut > 0) n = n.Substring(0, cut);
                    key = $"{l.type} '{n.Trim()}'";
                }
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new int[4];
                g[0]++;
                if (l.shadows == LightShadows.None) continue;
                g[1]++;
                var hd = l.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
                if (hd == null) continue;
                if (cam != null && Vector3.Distance(camPos, l.transform.position) <= hd.shadowFadeDistance) g[2]++;
                if (hd.shadowUpdateMode == UnityEngine.Rendering.HighDefinition.ShadowUpdateMode.EveryFrame) g[3]++;
            }
            sb.AppendLine($"{"active lights by owner",-50} {"total",6} {"shadows",8} {"in shadow range",16} {"every-frame",12}");
            foreach (var kv in groups.OrderByDescending(k => k.Value[1]).ThenByDescending(k => k.Value[0]).Take(15))
                sb.AppendLine($"{Trunc(kv.Key, 50),-50} {kv.Value[0],6} {kv.Value[1],8} {kv.Value[2],16} {kv.Value[3],12}");
        }

        private static void CountRenderers(StringBuilder sb)
        {
            void Count<T>(string label) where T : Renderer
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>());
                int total = arr == null ? 0 : arr.Length, enabled = 0, visible = 0, shadowCasters = 0;
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        T r = arr[i]?.TryCast<T>();
                        if (r == null || !r.enabled) continue;
                        enabled++;
                        if (r.isVisible) visible++;
                        if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) shadowCasters++;
                    }
                sb.AppendLine($"{label,-24} {total} (enabled {enabled}, visible to a camera last frame {visible}, cast shadows {shadowCasters})");
            }
            Count<MeshRenderer>("MeshRenderers");
            Count<SkinnedMeshRenderer>("SkinnedMeshRenderers");
        }

        private static void Section(StringBuilder sb, string title, Action body)
        {
            sb.AppendLine();
            sb.AppendLine("## " + title);
            try { body(); }
            catch (Exception e) { sb.AppendLine($"(failed: {e.GetType().Name}: {e.Message})"); }
        }

        private static void Row(StringBuilder sb, string name, string off, string on) => sb.AppendLine($"{name,-24} {off,16} {on,20}");
        private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        private static string Ms(double v) => v <= 0 ? "-" : v.ToString("0.00");
        private static string Fps(double ms) => ms <= 0 ? "-" : (1000.0 / ms).ToString("0.0");

        private static double Avg(Profiler.Section s, int st) => Profiler.SesFrames[st] == 0 ? 0 : s.SesTicks[st] * Profiler.TicksToMs / Profiler.SesFrames[st];
        private static double Worst(Profiler.Section s, int st) => s.SesMaxFrame[st] * Profiler.TicksToMs;
        private static double Calls(Profiler.Section s, int st) => Profiler.SesFrames[st] == 0 ? 0 : (double)s.SesCalls[st] / Profiler.SesFrames[st];

        private static int Count<T>() where T : UnityEngine.Object
        {
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>());
            return arr == null ? 0 : arr.Length;
        }

        private static void CountLights(StringBuilder sb)
        {
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>());
            int total = arr == null ? 0 : arr.Length, enabled = 0, shadowed = 0;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Light l = arr[i]?.TryCast<Light>();
                    if (l == null || !l.isActiveAndEnabled) continue;
                    enabled++;
                    if (l.shadows != LightShadows.None) shadowed++;
                }
            }
            sb.AppendLine($"Unity lights             {total} (active {enabled}, casting shadows {shadowed})");
        }

        private static void CountRigidbodies(StringBuilder sb)
        {
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Rigidbody>());
            int total = arr == null ? 0 : arr.Length, awake = 0;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Rigidbody rb = arr[i]?.TryCast<Rigidbody>();
                    if (rb != null && !rb.IsSleeping()) awake++;
                }
            }
            sb.AppendLine($"Rigidbodies              {total} (awake {awake})");
        }
    }
}
