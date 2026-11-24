using System;

namespace SODPerformance
{
    /// <summary>Frame-time statistics: a short ring buffer for the overlay, plus per-state histograms for A/B reports.</summary>
    internal static class FrameStats
    {
        private const int RingSize = 600;          // ~10 s at 60 fps
        private const float BinMs = 0.25f;         // histogram resolution
        private const int Bins = 2000;             // 0..500 ms, last bin = overflow

        private static readonly float[] ring = new float[RingSize];
        private static readonly float[] sortBuf = new float[RingSize];
        private static int ringCount;
        private static int ringPos;

        // [state] 0 = optimizations OFF, 1 = ON
        private static readonly int[][] hist = { new int[Bins], new int[Bins] };
        internal static readonly long[] Frames = new long[2];
        internal static readonly double[] SumMs = new double[2];
        internal static readonly float[] WorstMs = new float[2];

        // Last computed window values (overlay).
        internal static float AvgMs, P99Ms, WorstWindowMs;

        internal static void ResetSession()
        {
            for (int i = 0; i < 2; i++) { Frames[i] = 0; SumMs[i] = 0; WorstMs[i] = 0; Array.Clear(hist[i], 0, hist[i].Length); }
        }

        internal static void Add(float ms, bool measuring, int state)
        {
            if (ms <= 0f || float.IsNaN(ms)) return;
            ring[ringPos] = ms;
            ringPos = (ringPos + 1) % RingSize;
            if (ringCount < RingSize) ringCount++;

            if (!measuring) return;
            Frames[state]++;
            SumMs[state] += ms;
            if (ms > WorstMs[state]) WorstMs[state] = ms;
            int bin = (int)(ms / BinMs);
            if (bin >= Bins) bin = Bins - 1;
            hist[state][bin]++;
        }

        /// <summary>Recomputes overlay numbers from the ring buffer (call a couple of times per second).</summary>
        internal static void ComputeWindow()
        {
            if (ringCount == 0) return;
            double sum = 0;
            float worst = 0;
            for (int i = 0; i < ringCount; i++)
            {
                float v = ring[i];
                sortBuf[i] = v;
                sum += v;
                if (v > worst) worst = v;
            }
            Array.Sort(sortBuf, 0, ringCount);
            AvgMs = (float)(sum / ringCount);
            P99Ms = sortBuf[Math.Min(ringCount - 1, (int)(ringCount * 0.99f))];
            WorstWindowMs = worst;
        }

        /// <summary>Frame time at the given percentile (0..1) of all measured frames in a state.</summary>
        internal static float Percentile(int state, double p)
        {
            long total = Frames[state];
            if (total == 0) return 0f;
            long target = (long)Math.Ceiling(total * p);
            long acc = 0;
            int[] h = hist[state];
            for (int i = 0; i < Bins; i++)
            {
                acc += h[i];
                if (acc >= target) return (i + 0.5f) * BinMs;
            }
            return Bins * BinMs;
        }

        internal static double AvgFor(int state) => Frames[state] == 0 ? 0 : SumMs[state] / Frames[state];
    }
}
