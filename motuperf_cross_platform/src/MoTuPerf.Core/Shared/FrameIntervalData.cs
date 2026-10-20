using System;

namespace CSharpIosPerfMonitor
{
    public static class FrameIntervalData
    {
        public const int MaximumIntervalsPerWindow = 16384;

        public static bool IsValid(double[] intervals, int frameCount, double observationMs)
        {
            if (intervals == null || intervals.Length == 0 || intervals.Length > MaximumIntervalsPerWindow
                || intervals.Length != frameCount || !double.IsFinite(observationMs) || observationMs <= 0) return false;
            double total = 0;
            foreach (double interval in intervals)
            {
                if (!double.IsFinite(interval) || interval <= 0 || interval > 60000) return false;
                total += interval;
            }
            return double.IsFinite(total) && Math.Abs(total - observationMs) <= Math.Max(0.001, observationMs * 1e-6);
        }

        public static bool IsUsable(PerfSample sample)
        {
            return sample != null && sample.FpsUpdated && sample.HasFrameObservation
                && MetricStatistics.IsReliableJankSample(sample)
                && IsValid(sample.FrameIntervalsMs, sample.FrameCount, sample.FrameObservationMs);
        }
    }
}
