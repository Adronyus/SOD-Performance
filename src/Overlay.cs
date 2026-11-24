using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SODPerformance
{
    /// <summary>IMGUI overlay. Text is rebuilt twice per second; OnGUI only draws the cached string.</summary>
    internal static class Overlay
    {
        private static string text = "";
        private static int lineCount = 1;
        private static GUIStyle style;
        private static readonly StringBuilder sb = new StringBuilder(2048);

        internal static void Rebuild()
        {
            sb.Clear();
            string on = Runner.OptimizationsOn ? "<color=#7CFC00>ON</color>" : "<color=#FF6060>OFF</color>";
            sb.Append("<b>SOD Performance ").Append(Plugin.Version).Append("</b>   optimizations ").Append(on);
            if (Runner.AutoAB) sb.Append(" <color=#AAAAAA>(auto A/B, switch in ").Append(Math.Max(0, Runner.ABRemaining).ToString("0")).Append(" s)</color>");
            if (Runner.FastForwardNow) sb.Append(" <color=#FFD060>fast-forward: not measured</color>");
            sb.Append("   <color=#AAAAAA>").Append(PerfConfig.OverlayKey.Value).Append(" hide · ")
              .Append(PerfConfig.ReportKey.Value).Append(" report · ")
              .Append(PerfConfig.ToggleKey.Value).Append(" A/B</color>\n");

            float avg = FrameStats.AvgMs;
            sb.Append("FPS <b>").Append(avg > 0 ? (1000f / avg).ToString("0.0") : "-").Append("</b>   ")
              .Append(avg.ToString("0.0")).Append(" ms avg   1% low ")
              .Append(FrameStats.P99Ms > 0 ? (1000f / FrameStats.P99Ms).ToString("0") : "-").Append(" fps   worst ")
              .Append(FrameStats.WorstWindowMs.ToString("0")).Append(" ms  <color=#AAAAAA>(last ~10 s)</color>\n");
            sb.Append("Frame: scripts ").Append(Phases.WindowAvg[Phases.Scripts].ToString("0.0"))
              .Append(" · render ").Append(Phases.WindowAvg[Phases.Render].ToString("0.0"))
              .Append(" · physics ").Append(Phases.WindowAvg[Phases.Physics].ToString("0.0"))
              .Append(" · wait ").Append(Phases.WindowAvg[Phases.Wait].ToString("0.0")).Append(" ms\n");
            sb.Append("GC ").Append(GcStats.PerMinute).Append("/min · heap ").Append(GcStats.HeapUsedMb).Append('/').Append(GcStats.HeapSizeMb)
              .Append(" MB · incremental ").Append(GcStats.Incremental ? "yes" : "no")
              .Append("   Stutters >").Append(PerfConfig.SpikeThresholdMs.Value.ToString("0")).Append(" ms: OFF ").Append(Spikes.SesCount[0])
              .Append(" / ON ").Append(Spikes.SesCount[1]);
            if (Spikes.LastCause.Length > 0) sb.Append("  <color=#AAAAAA>last: ").Append(Spikes.LastCause).Append("</color>");
            sb.Append('\n');

            if (CpuPlacement.HasVCache)
                sb.Append("CPU: ").Append(CpuPlacement.Applied ? "<color=#7CFC00>on V-Cache cores</color>" : "all cores")
                  .Append(" <color=#AAAAAA>(main thread on V-Cache: before ").Append(CpuPlacement.Percent(0)).Append(")</color>\n");
            sb.Append("Room cache: ").Append(CullingCache.Enabled ? CullingCache.Status : "off");
            if (LoadProfiler.LastLoadSeconds >= 0) sb.Append("  ·  last load ").Append(LoadProfiler.LastLoadSeconds.ToString("0")).Append(" s");
            sb.Append('\n');
            if (GameProbe.HasCity)
            {
                sb.Append("AI budget <b>").Append(AiGovernor.Applied).Append("</b> / vanilla ").Append(AiGovernor.Vanilla)
                  .Append("  ·  ran ").Append(GameProbe.Executed).Append("/frame  ·  queue ").Append(GameProbe.Backlog)
                  .Append("  ·  time x").Append(GameProbe.TimeMult.ToString("0.#")).Append('\n');
                sb.Append("   tiers vH/H/M/L/vL ").Append(GameProbe.VeryHigh).Append('/').Append(GameProbe.High).Append('/')
                  .Append(GameProbe.Medium).Append('/').Append(GameProbe.Low).Append('/').Append(GameProbe.VeryLow)
                  .Append("  ·  citizens ").Append(GameProbe.Citizens).Append("  ·  visible ").Append(GameProbe.VisibleHumans).Append('\n');
                if (AiTimeBudget.Installed)
                    sb.Append("   AI time budget ").Append(PerfConfig.AiFrameBudgetMs.Value.ToString("0.#")).Append(" ms: ")
                      .Append(AiTimeBudget.Status)
                      .Append(", cut short ").Append(Runner.CutsPerSecond).Append("x last second\n");
                if (MovementStagger.Installed)
                    sb.Append("   far-citizen movement stagger: ").Append(MovementStagger.EnabledNow ? "on" : "off")
                      .Append(", ").Append(MovementStagger.UpdatesLastSecond).Append(" moves/s, slowest tick ")
                      .Append(MovementStagger.MaxMsLastSecond.ToString("0.0")).Append(" ms\n");
                sb.Append("   other lights: ").Append(OtherLightShadows.Capped).Append('/').Append(OtherLightShadows.Tracked)
                  .Append(" shadows capped at ").Append(PerfConfig.OtherLightShadowDistance.Value.ToString("0")).Append(" m · trash cleanups ")
                  .Append(TrashTrickle.Calls).Append('\n');
                sb.Append("   shadows: fade x").Append(GameProbe.ShadowFadeMult.ToString("0.##")).Append(" (game ").Append(ShadowTuning.GameMult.ToString("0.##"))
                  .Append(") · queue ").Append(GameProbe.ShadowQueue).Append(" · updates/frame max ").Append(GameProbe.MaxShadowUpdates).Append('\n');
                double perTick = Profiler.AiPerTickMs;
                if (perTick >= 0) sb.Append("   one AI decision ≈ ").Append((perTick * 1000).ToString("0")).Append(" µs\n");
                sb.Append("   <color=#AAAAAA>governor (").Append(PerfConfig.AiMode.Value).Append(", target ")
                  .Append(PerfConfig.TargetFps.Value).Append(" fps): ").Append(AiGovernor.Reason).Append("</color>\n");
                if (GameProbe.LightsTracked > 0)
                    sb.Append("Lights tracked ").Append(GameProbe.LightsTracked).Append("  unculled ").Append(GameProbe.LightsNotCulled)
                      .Append("  culled ").Append(GameProbe.LightsCulled).Append("  checks/frame ").Append(GameProbe.LightChecks).Append('\n');
            }
            else
            {
                sb.Append("<color=#AAAAAA>Waiting for a city to load...</color>\n");
            }

            if (Profiler.Installed)
            {
                sb.Append("<b>Script time</b> <color=#AAAAAA>ms/frame avg · worst · calls</color>\n");
                foreach (var s in Profiler.Active.OrderByDescending(x => x.AvgMs).Take(9))
                {
                    string color = s.AvgMs >= 2 ? "#FF8080" : s.AvgMs >= 0.75 ? "#FFD060" : "#FFFFFF";
                    sb.Append("  <color=").Append(color).Append('>').Append(s.Label.Trim()).Append("</color>  ")
                      .Append(s.AvgMs.ToString("0.00")).Append(" · ").Append(s.MaxMs.ToString("0.0"))
                      .Append(" · ").Append(s.CallsPerFrame.ToString("0.#")).Append('\n');
                }
                double total = Profiler.Active.Where(x => !x.Nested).Sum(x => x.AvgMs);
                sb.Append("Profiled total ").Append(total.ToString("0.0")).Append(" ms");
                if (avg > 0) sb.Append(" = ").Append((total / avg * 100).ToString("0")).Append("% of frame");
                sb.Append('\n');
            }

            if (!string.IsNullOrEmpty(Runner.LastReportPath))
                sb.Append("<color=#AAAAAA>Report: ").Append(Runner.LastReportPath).Append("</color>\n");

            text = sb.ToString().TrimEnd('\n');
            lineCount = text.Count(c => c == '\n') + 1;
        }

        private static GUIStyle toastStyle;

        internal static void Draw()
        {
            Event ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            if (!Runner.OverlayVisible)
            {
                if (Time.realtimeSinceStartup < Runner.ToastUntil) DrawToast();
                return;
            }

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    richText = true,
                    wordWrap = false,
                };
                style.normal.textColor = Color.white;
            }

            float h = lineCount * 17f + 14f;
            DrawBox(h);
        }

        private static void DrawToast()
        {
            if (toastStyle == null)
            {
                toastStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = false };
                toastStyle.normal.textColor = Color.white;
            }
            string t = $"<b>SOD Performance {Plugin.Version}</b> active  <color=#AAAAAA>{PerfConfig.OverlayKey.Value} stats · {PerfConfig.ReportKey.Value} report</color>";
            var rect = new Rect(8f, 8f, 420f, 28f);
            GUI.Box(rect, "");
            GUI.Box(rect, "");
            GUI.Label(new Rect(16f, 12f, 410f, 22f), t, toastStyle);
        }

        private static void DrawBox(float h)
        {
            var rect = new Rect(8f, 8f, 500f, h);
            GUI.Box(rect, "");
            GUI.Box(rect, "");   // drawn twice: the default skin box is quite transparent
            GUI.Label(new Rect(16f, 13f, 488f, h), text, style);
        }
    }
}
