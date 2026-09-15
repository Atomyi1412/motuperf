using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpIosPerfMonitor
{
    internal static class ThermalStateChartProjection
    {
        internal const double DisplayFreshSeconds = 2.5;
        internal const double AndroidDisplayFreshSeconds = 7.5;

        internal static List<PerfSample> ForChart(IEnumerable<PerfSample> samples, double latestElapsedSec)
        {
            List<PerfSample> realSamples = (samples ?? Enumerable.Empty<PerfSample>())
                .Where(HasValue)
                .OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .ToList();
            List<PerfSample> projected = new List<PerfSample>();
            PerfSample previous = null;
            foreach (PerfSample sample in realSamples)
            {
                if (previous != null && previous.ThermalStateLevel != sample.ThermalStateLevel)
                {
                    projected.Add(PresentationPoint(sample.ElapsedSec, previous));
                }
                projected.Add(sample);
                previous = sample;
            }
            if (previous == null || double.IsNaN(latestElapsedSec) || double.IsInfinity(latestElapsedSec))
            {
                return projected;
            }
            double freshness = IsAndroidStatus(previous) ? AndroidDisplayFreshSeconds : DisplayFreshSeconds;
            double extensionEnd = Math.Min(latestElapsedSec, previous.ElapsedSec + freshness);
            if (extensionEnd > previous.ElapsedSec + 0.000001)
            {
                projected.Add(PresentationPoint(extensionEnd, previous));
            }
            return projected;
        }

        private static PerfSample PresentationPoint(double elapsedSec, PerfSample source)
        {
            return new PerfSample
            {
                ElapsedSec = elapsedSec,
                HasThermalState = true,
                ThermalStateLevel = source.ThermalStateLevel,
                ThermalStateName = source.ThermalStateName,
                ThermalStateUpdated = false
            };
        }

        private static bool HasValue(PerfSample sample)
        {
            return sample != null
                && sample.HasThermalState
                && sample.ThermalStateLevel >= 0
                && sample.ThermalStateLevel <= 6;
        }

        private static bool IsAndroidStatus(PerfSample sample)
        {
            string name = (sample == null ? "" : sample.ThermalStateName ?? "").Trim().ToLowerInvariant();
            return name == "none"
                || name == "light"
                || name == "moderate"
                || name == "severe"
                || name == "emergency"
                || name == "shutdown"
                || (sample != null && sample.ThermalStateLevel > 3);
        }
    }
}
