using System;
using UnityEngine;

namespace SODPerformance
{
    /// <summary>
    /// Controls CitizenBehaviour.AITicksPerFrame.
    ///
    /// Every frame the game takes citizens off its AI update list and calls NewAIController.AITick on each, until a
    /// per-frame count limit (AITicksPerFrame, scaled by the game speed) is reached; the rest of the queue waits.
    /// The queue is refilled by detail tier (far-away citizens less often), skipping citizens already queued.
    /// The constructor default is 20 and nothing in the game rewrites it, so this is a clean, reversible budget knob:
    /// fewer AI decisions per frame = less CPU per frame; the cost is a longer queue (citizens decide a little later).
    /// </summary>
    internal static class AiGovernor
    {
        internal static int Vanilla = -1;
        internal static int Applied = -1;
        internal static string Reason = "waiting for city";

        private static int adaptive = -1;
        private static float emaMs = 16.7f;
        private static float nextAdjust;

        internal static void Tick(bool optimizationsOn, float frameMs, float now)
        {
            CitizenBehaviour cb = CitizenBehaviour.Instance;
            if (cb == null)
            {
                Reason = "waiting for city";
                return;
            }

            int current = cb.AITicksPerFrame;
            if (Vanilla < 0)
            {
                Vanilla = current > 0 ? current : 20;
                adaptive = Vanilla;
                Plugin.L.LogInfo($"AI governor: vanilla AITicksPerFrame = {Vanilla}.");
            }

            if (frameMs > 0f && frameMs < 1000f) emaMs = emaMs * 0.9f + frameMs * 0.1f;

            // Queue drain time at the budget currently in effect: backlog / ticks-per-frame frames x smoothed frame time.
            float effectiveTicks = Math.Max(1f, current * Math.Max(0.01f, SessionData.Instance != null ? SessionData.Instance.currentTimeMultiplier : 1f));
            AiDelaySeconds = cb.aiTickBacklog / effectiveTicks * emaMs / 1000f;

            int desired = Vanilla;
            if (!optimizationsOn)
            {
                Reason = "optimizations off";
            }
            else
            {
                SessionData sd = SessionData.Instance;
                bool fastForward = PerfConfig.VanillaWhileFastForwarding.Value && sd != null && sd.currentTimeMultiplier > 1.01f;
                switch (PerfConfig.AiMode.Value)
                {
                    case AiBudgetMode.Vanilla:
                        Reason = "mode Vanilla";
                        break;
                    case AiBudgetMode.Fixed:
                        desired = Math.Max(1, PerfConfig.FixedBudget.Value);
                        Reason = "mode Fixed";
                        break;
                    default:
                        if (fastForward)
                        {
                            adaptive = Vanilla;
                            Reason = "fast-forward: vanilla";
                        }
                        else
                        {
                            desired = AdaptiveStep(cb, now);
                        }
                        break;
                }
            }

            if (desired != current)
            {
                cb.AITicksPerFrame = desired;
            }
            Applied = desired;
        }

        /// <summary>Estimated seconds for the current AI queue to drain at the applied budget.</summary>
        internal static float AiDelaySeconds;

        private static int AdaptiveStep(CitizenBehaviour cb, float now)
        {
            int min = Math.Max(1, (int)Math.Round(Vanilla * PerfConfig.MinBudgetPercent.Value / 100f));
            int max = Math.Max(min, (int)Math.Round(Vanilla * PerfConfig.MaxBudgetPercent.Value / 100f));

            int floor = min;
            bool guard = AiDelaySeconds > PerfConfig.MaxAiDelaySeconds.Value;
            if (guard) floor = Math.Min(max, Math.Max(min, Vanilla));

            if (now >= nextAdjust)
            {
                nextAdjust = now + 0.25f;
                float target = 1000f / Math.Max(20, PerfConfig.TargetFps.Value);

                // Cost of one AI decision, independent of the budget. If even the full vanilla budget would cost
                // under 0.5 ms per frame, starving the AI cannot buy meaningful FPS: drift back up instead.
                double perTick = Profiler.AiPerTickMs;
                bool aiIsCheap = perTick >= 0 && perTick * Vanilla < 0.5;

                if (emaMs > target * 1.03f && !aiIsCheap)
                {
                    adaptive -= emaMs > target * 1.5f ? 2 : 1;
                    Reason = "below target fps: saving CPU";
                }
                else if (emaMs < target * 0.92f || aiIsCheap)
                {
                    adaptive += 1;
                    Reason = aiIsCheap ? "AI is cheap here: restoring" : "fps headroom: restoring";
                }
                else
                {
                    Reason = "holding";
                }
                if (guard) Reason = $"AI queue {AiDelaySeconds:0.0} s > {PerfConfig.MaxAiDelaySeconds.Value:0.#} s: protecting AI";
            }

            adaptive = Math.Max(floor, Math.Min(max, adaptive));
            return adaptive;
        }

        internal static void ResetForNewSession()
        {
            // CitizenBehaviour lives for the whole game session; if a new one appears we re-read the vanilla value.
            Vanilla = -1;
            Applied = -1;
            adaptive = -1;
        }
    }

    /// <summary>Optional override of LightCullingController.lightsToCheckPerFrame (game default 10).</summary>
    internal static class LightBudget
    {
        internal static int Vanilla = -1;

        internal static void Tick(bool optimizationsOn)
        {
            LightCullingController lc = LightCullingController.Instance;
            if (lc == null) return;

            int current = lc.lightsToCheckPerFrame;
            if (Vanilla < 0) Vanilla = current > 0 ? current : 10;

            int wanted = PerfConfig.LightChecksPerFrame.Value;
            int desired = optimizationsOn && wanted >= 1 ? wanted : Vanilla;
            if (desired != current) lc.lightsToCheckPerFrame = desired;
        }

        internal static void ResetForNewSession() => Vanilla = -1;
    }

    /// <summary>
    /// Turns off Unity's per-message stack trace capture. Unity grabs a managed stack trace for every
    /// Debug.Log/LogWarning by default, which is expensive in IL2CPP; the game emits many warnings
    /// (negative-scale BoxColliders, missing _Stencil properties, ...).
    /// </summary>
    internal static class LogTweaks
    {
        private static readonly LogType[] Types = { LogType.Log, LogType.Warning, LogType.Assert, LogType.Error, LogType.Exception };
        private static StackTraceLogType[] original;
        private static int appliedKey = -1;

        internal static void Tick(bool optimizationsOn)
        {
            bool stripLog = optimizationsOn && PerfConfig.StripLogStackTraces.Value;
            bool stripErr = optimizationsOn && PerfConfig.StripErrorStackTraces.Value;
            int key = (stripLog ? 1 : 0) | (stripErr ? 2 : 0);
            if (key == appliedKey) return;

            if (original == null)
            {
                original = new StackTraceLogType[Types.Length];
                for (int i = 0; i < Types.Length; i++) original[i] = Application.GetStackTraceLogType(Types[i]);
            }

            for (int i = 0; i < Types.Length; i++)
            {
                bool isError = Types[i] == LogType.Error || Types[i] == LogType.Exception;
                bool strip = isError ? stripErr : stripLog;
                Application.SetStackTraceLogType(Types[i], strip ? StackTraceLogType.None : original[i]);
            }
            appliedKey = key;
        }
    }
}
