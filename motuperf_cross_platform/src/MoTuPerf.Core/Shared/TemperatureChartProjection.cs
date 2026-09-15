using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpIosPerfMonitor
{
    internal static class TemperatureChartProjection
    {
        // The collector samples temperature every five seconds. Keep the last real
        // value visible until the next sample, but stop after a bounded stale window.
        internal const double DisplayFreshSeconds = 7.5;

        internal static List<PerfSample> ForSensor(
            IEnumerable<PerfSample> samples,
            string sensorName,
            double latestElapsedSec)
        {
            List<PerfSample> realSamples = (samples ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample) { return HasValue(sample, sensorName); })
                .OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .ToList();
            if (realSamples.Count == 0 || double.IsNaN(latestElapsedSec) || double.IsInfinity(latestElapsedSec))
            {
                return realSamples;
            }

            PerfSample latestRealSample = realSamples[realSamples.Count - 1];
            double extensionEnd = Math.Min(latestElapsedSec, latestRealSample.ElapsedSec + DisplayFreshSeconds);
            if (extensionEnd <= latestRealSample.ElapsedSec + 0.000001)
            {
                return realSamples;
            }

            double value = latestRealSample.TemperatureCelsius[sensorName];
            PerfSample displaySample = new PerfSample
            {
                ElapsedSec = extensionEnd,
                HasTemperature = true,
                TemperatureCelsius = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    { sensorName, value }
                },
                // This point is presentation-only. It must never be exported as a
                // fresh device reading or written back into the session samples.
                TemperatureUpdated = false
            };
            realSamples.Add(displaySample);
            return realSamples;
        }

        private static bool HasValue(PerfSample sample, string sensorName)
        {
            if (sample == null || !sample.HasTemperature || sample.TemperatureCelsius == null || string.IsNullOrWhiteSpace(sensorName))
            {
                return false;
            }

            double value;
            return sample.TemperatureCelsius.TryGetValue(sensorName, out value)
                && !double.IsNaN(value)
                && !double.IsInfinity(value)
                && value >= -20.0
                && value <= 120.0;
        }
    }
}
