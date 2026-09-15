using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpIosPerfMonitor
{
    public sealed class FpsStatistics
    {
        public FpsStatistics()
        {
            SourceTier = "";
        }

        public bool HasData { get; set; }
        public bool HasEstimatedWeights { get; set; }
        public string SourceTier { get; set; }
        public int SampleCount { get; set; }
        public int ExcludedSampleCount { get; set; }
        public double Average { get; set; }
        public double Variance { get; set; }
        public double StandardDeviation { get; set; }
        public double Minimum { get; set; }
        public double Median { get; set; }
        public double MedianRangePercent { get; set; }
        public double FpsGe18Percent { get; set; }
        public double FpsGe25Percent { get; set; }
        public double DropPerHour { get; set; }
        public double TotalDurationSec { get; set; }
    }

    public sealed class MemoryStatistics
    {
        public MemoryStatistics()
        {
            Metric = "";
        }

        public bool HasData { get; set; }
        public string Metric { get; set; }
        public double AverageMb { get; set; }
        public double PeakMb { get; set; }
        public int SampleCount { get; set; }
    }

    public sealed class JankStatistics
    {
        public bool HasRateData { get; set; }
        public bool HasStutterData { get; set; }
        public int SampleCount { get; set; }
        public int ExcludedSampleCount { get; set; }
        public double ObservationSec { get; set; }
        public double JankPer10Min { get; set; }
        public double BigJankPer10Min { get; set; }
        public double StutterPercent { get; set; }
    }

    public static class MetricStatistics
    {
        private const double DefaultFallbackWeightMs = 1000.0;
        private const double MaximumInferredWeightMs = 2000.0;
        private const double DropThresholdFps = 8.0;

        public static FpsStatistics ComputeFps(IEnumerable<PerfSample> source)
        {
            List<PerfSample> availableSamples = (source ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample)
                {
                    return sample != null
                        && sample.HasFps
                        && sample.FpsUpdated
                        && IsFinite(sample.Fps)
                        && sample.Fps >= 0
                        && sample.Fps <= 240.0;
                })
                .OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .ToList();
            FpsStatistics result = new FpsStatistics();
            if (availableSamples.Count == 0) return result;

            List<PerfSample> samples = availableSamples.Where(IsExactOrderedFpsSample).ToList();
            if (samples.Count > 0)
            {
                result.SourceTier = "exact_ordered";
            }
            else
            {
                samples = availableSamples.Where(delegate(PerfSample sample)
                {
                    return sample.HasFrameTargetVerification && sample.FrameTargetVerified;
                }).ToList();
                if (samples.Count > 0)
                {
                    result.SourceTier = "target_verified";
                }
                else
                {
                    samples = availableSamples;
                    result.SourceTier = "fallback";
                }
            }
            samples = ExcludeSupersededNoPresentWindows(samples);
            result.SampleCount = samples.Count;
            result.ExcludedSampleCount = availableSamples.Count - samples.Count;

            double cadenceMs = RepresentativeCadenceMs(samples);
            List<WeightedFpsPoint> points = new List<WeightedFpsPoint>(samples.Count);
            for (int index = 0; index < samples.Count; index++)
            {
                PerfSample sample = samples[index];
                bool observed = sample.HasFrameObservation
                    && IsFinite(sample.FrameObservationMs)
                    && sample.FrameObservationMs > 0
                    && sample.FrameCount >= 0;
                double weightMs = observed
                    ? sample.FrameObservationMs
                    : InferredFallbackWeightMs(samples, index, cadenceMs);
                double fps = observed
                    ? sample.FrameCount * 1000.0 / weightMs
                    : sample.Fps;
                if (!IsFinite(fps) || fps < 0 || fps > 240.0 || weightMs <= 0) continue;
                points.Add(new WeightedFpsPoint(sample, fps, weightMs, !observed));
            }
            if (points.Count == 0) return result;

            double totalWeightMs = points.Sum(delegate(WeightedFpsPoint point) { return point.WeightMs; });
            if (!IsFinite(totalWeightMs) || totalWeightMs <= 0) return result;
            double average = points.Sum(delegate(WeightedFpsPoint point)
            {
                return point.Fps * point.WeightMs;
            }) / totalWeightMs;

            result.HasData = true;
            result.HasEstimatedWeights = points.Any(delegate(WeightedFpsPoint point) { return point.EstimatedWeight; });
            result.Average = average;
            result.Variance = points.Sum(delegate(WeightedFpsPoint point)
            {
                double delta = point.Fps - average;
                return delta * delta * point.WeightMs;
            }) / totalWeightMs;
            result.StandardDeviation = Math.Sqrt(result.Variance);
            result.Minimum = points.Min(delegate(WeightedFpsPoint point) { return point.Fps; });
            result.Median = WeightedMedian(points, totalWeightMs);
            double medianRangeLow = result.Median * 0.8;
            double medianRangeHigh = result.Median * 1.2;
            result.MedianRangePercent = points
                .Where(delegate(WeightedFpsPoint point)
                {
                    return point.Fps >= medianRangeLow && point.Fps <= medianRangeHigh;
                })
                .Sum(delegate(WeightedFpsPoint point) { return point.WeightMs; }) * 100.0 / totalWeightMs;
            result.FpsGe18Percent = points
                .Where(delegate(WeightedFpsPoint point) { return point.Fps >= 18.0; })
                .Sum(delegate(WeightedFpsPoint point) { return point.WeightMs; }) * 100.0 / totalWeightMs;
            result.FpsGe25Percent = points
                .Where(delegate(WeightedFpsPoint point) { return point.Fps >= 25.0; })
                .Sum(delegate(WeightedFpsPoint point) { return point.WeightMs; }) * 100.0 / totalWeightMs;
            result.TotalDurationSec = totalWeightMs / 1000.0;

            int dropCount = 0;
            double continuityLimitMs = Math.Min(MaximumInferredWeightMs, Math.Max(DefaultFallbackWeightMs, cadenceMs * 2.0));
            for (int index = 1; index < points.Count; index++)
            {
                if (!IsContinuousFpsPair(points[index - 1].Sample, points[index].Sample, continuityLimitMs)) continue;
                if (points[index - 1].Fps - points[index].Fps > DropThresholdFps) dropCount++;
            }
            result.DropPerHour = result.TotalDurationSec > 0
                ? dropCount * 3600.0 / result.TotalDurationSec
                : 0;
            return result;
        }

        private static bool IsContinuousFpsPair(PerfSample previous, PerfSample current, double continuityLimitMs)
        {
            if (previous == null || current == null || current.FrameSourceSequenceDiscontinuity) return false;
            double elapsedMs = (current.ElapsedSec - previous.ElapsedSec) * 1000.0;
            if (!IsFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs > continuityLimitMs) return false;
            if (!IsSameFrameSeries(previous, current)) return false;
            if (previous.HasFrameSourceSequence != current.HasFrameSourceSequence) return false;
            if (previous.HasFrameSourceSequence)
            {
                if (previous.FrameSourceSequence == long.MaxValue) return false;
                if (current.FrameSourceSequence != previous.FrameSourceSequence + 1) return false;
            }
            return true;
        }

        public static bool IsSameFrameSeries(PerfSample previous, PerfSample current)
        {
            if (previous == null || current == null) return false;
            if (!SameText(previous.Source, current.Source)
                || !SameText(previous.FpsScope, current.FpsScope)
                || !SameText(previous.FrameSource, current.FrameSource)
                || previous.OrderedFrames != current.OrderedFrames
                || previous.HasFrameTargetVerification != current.HasFrameTargetVerification
                || previous.FrameTargetVerified != current.FrameTargetVerified)
            {
                return false;
            }
            if ((previous.TargetPid > 0 || current.TargetPid > 0) && previous.TargetPid != current.TargetPid) return false;
            if ((previous.SurfaceOwnerPid > 0 || current.SurfaceOwnerPid > 0)
                && previous.SurfaceOwnerPid != current.SurfaceOwnerPid)
            {
                return false;
            }
            if ((previous.SurfaceOwnerUid > 0 || current.SurfaceOwnerUid > 0)
                && previous.SurfaceOwnerUid != current.SurfaceOwnerUid)
            {
                return false;
            }
            return true;
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExactOrderedFpsSample(PerfSample sample)
        {
            return sample != null
                && sample.OrderedFrames
                && sample.HasFrameObservation
                && IsFinite(sample.FrameObservationMs)
                && sample.FrameObservationMs > 0
                && sample.FrameCount >= 0
                && sample.OutOfOrderFrameTimestamps == 0
                && sample.InvalidFrameIntervals == 0
                && !sample.FrameRingBufferOverrun
                && (!sample.HasFrameTargetVerification || sample.FrameTargetVerified)
                && (!RequiresExplicitTargetVerification(sample)
                    || (sample.HasFrameTargetVerification && sample.FrameTargetVerified));
        }

        private static bool RequiresExplicitTargetVerification(PerfSample sample)
        {
            string source = (sample.Source ?? "").Trim();
            string scope = (sample.FpsScope ?? "").Trim();
            return source.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope, "app", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope, "surface", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope, "display", StringComparison.OrdinalIgnoreCase);
        }

        public static MemoryStatistics ComputeMemory(IEnumerable<PerfSample> source)
        {
            List<PerfSample> samples = (source ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample)
                {
                    return sample != null
                        && sample.HasMemory
                        && sample.MemoryUpdated
                        && IsFinite(sample.MemoryMb)
                        && sample.MemoryMb > 0;
                })
                .ToList();
            MemoryStatistics result = new MemoryStatistics();
            if (samples.Count == 0) return result;

            string metric = PreferredMemoryMetric(samples);
            List<PerfSample> selected = samples
                .Where(delegate(PerfSample sample)
                {
                    return string.Equals(NormalizeMemoryMetric(sample.MemoryMetric), metric, StringComparison.Ordinal);
                })
                .ToList();
            if (selected.Count == 0) return result;

            result.HasData = true;
            result.Metric = metric;
            result.AverageMb = selected.Average(delegate(PerfSample sample) { return sample.MemoryMb; });
            result.PeakMb = selected.Max(delegate(PerfSample sample) { return sample.MemoryMb; });
            result.SampleCount = selected.Count;
            return result;
        }

        public static JankStatistics ComputeJank(IEnumerable<PerfSample> source)
        {
            List<PerfSample> availableSamples = (source ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample)
                {
                    return sample != null
                        && sample.HasJank
                        && sample.FpsUpdated
                        && IsFinite(sample.Jank)
                        && sample.Jank >= 0
                        && IsFinite(sample.BigJank)
                        && sample.BigJank >= 0;
                })
                .OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .ToList();
            JankStatistics result = new JankStatistics();
            List<PerfSample> samples = availableSamples.Where(IsReliableJankSample).ToList();
            samples = ExcludeSupersededNoPresentWindows(samples);
            result.SampleCount = samples.Count;
            result.ExcludedSampleCount = availableSamples.Count - samples.Count;
            if (samples.Count == 0) return result;

            double observationMs = samples.Sum(delegate(PerfSample sample) { return sample.FrameObservationMs; });
            if (!IsFinite(observationMs) || observationMs <= 0) return result;
            result.HasRateData = true;
            result.ObservationSec = observationMs / 1000.0;
            result.JankPer10Min = samples.Sum(delegate(PerfSample sample)
            {
                return Math.Max(sample.Jank, sample.BigJank);
            }) * 600_000.0 / observationMs;
            result.BigJankPer10Min = samples.Sum(delegate(PerfSample sample)
            {
                return sample.BigJank;
            }) * 600_000.0 / observationMs;

            bool completeStutter = samples.All(delegate(PerfSample sample)
            {
                if (!sample.HasStutter || !IsFinite(sample.JankTimeMs) || sample.JankTimeMs < 0) return false;
                if (!IsFinite(sample.StutterPercent) || sample.StutterPercent < 0 || sample.StutterPercent > 100.0) return false;
                if (sample.Jank > 0 && sample.JankTimeMs <= 0) return false;
                return sample.JankTimeMs <= sample.FrameObservationMs;
            });
            if (!completeStutter) return result;

            double jankTimeMs = samples.Sum(delegate(PerfSample sample) { return sample.JankTimeMs; });
            if (!IsFinite(jankTimeMs) || jankTimeMs < 0 || jankTimeMs > observationMs) return result;
            result.HasStutterData = true;
            result.StutterPercent = jankTimeMs * 100.0 / observationMs;
            return result;
        }

        private static List<PerfSample> ExcludeSupersededNoPresentWindows(List<PerfSample> source)
        {
            if (source == null || source.Count == 0) return new List<PerfSample>();
            bool[] excluded = new bool[source.Count];
            for (int index = 0; index < source.Count; index++)
            {
                PerfSample resumed = source[index];
                if (resumed == null || !IsFinite(resumed.ResumeGapMs) || resumed.ResumeGapMs <= 0) continue;
                for (int previousIndex = index - 1; previousIndex >= 0; previousIndex--)
                {
                    PerfSample previous = source[previousIndex];
                    if (previous == null || !previous.NoPresentFrames || !IsSameFrameSeries(previous, resumed)) break;
                    excluded[previousIndex] = true;
                }
            }
            return source.Where(delegate(PerfSample sample, int index) { return !excluded[index]; }).ToList();
        }

        public static bool IsReliableJankSample(PerfSample sample)
        {
            return sample != null
                && sample.OrderedFrames
                && sample.HasFrameObservation
                && IsFinite(sample.FrameObservationMs)
                && sample.FrameObservationMs > 0
                && sample.FrameCount >= 0
                && sample.OutOfOrderFrameTimestamps == 0
                && sample.InvalidFrameIntervals == 0
                && !sample.FrameRingBufferOverrun
                && (!sample.HasFrameTargetVerification || sample.FrameTargetVerified)
                && (!RequiresExplicitTargetVerification(sample)
                    || (sample.HasFrameTargetVerification && sample.FrameTargetVerified));
        }

        public static string PreferredMemoryMetric(IEnumerable<PerfSample> source)
        {
            List<string> metrics = (source ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample)
                {
                    return sample != null
                        && sample.HasMemory
                        && sample.MemoryUpdated
                        && IsFinite(sample.MemoryMb)
                        && sample.MemoryMb > 0;
                })
                .Select(delegate(PerfSample sample) { return NormalizeMemoryMetric(sample.MemoryMetric); })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (metrics.Contains("physical_footprint")) return "physical_footprint";
            if (metrics.Contains("footprint")) return "footprint";
            if (metrics.Contains("pss")) return "pss";
            string other = metrics.FirstOrDefault(delegate(string metric)
            {
                return !string.IsNullOrWhiteSpace(metric) && metric != "rss";
            });
            if (!string.IsNullOrWhiteSpace(other)) return other;
            if (metrics.Contains("rss")) return "rss";
            return metrics.FirstOrDefault() ?? "";
        }

        public static bool IsPreferredMemorySample(PerfSample sample, string preferredMetric)
        {
            return sample != null
                && sample.HasMemory
                && sample.MemoryUpdated
                && IsFinite(sample.MemoryMb)
                && sample.MemoryMb > 0
                && string.Equals(
                    NormalizeMemoryMetric(sample.MemoryMetric),
                    NormalizeMemoryMetric(preferredMetric),
                    StringComparison.Ordinal);
        }

        public static void NormalizeFrameTargetVerification(PerfSample sample)
        {
            if (sample == null) return;
            if (!sample.HasFrameTargetVerification)
            {
                sample.FrameTargetVerified = false;
                sample.SurfaceOwnerPid = 0;
                sample.SurfaceOwnerUid = 0;
                return;
            }

            sample.SurfaceOwnerPid = Math.Max(0, sample.SurfaceOwnerPid);
            sample.SurfaceOwnerUid = Math.Max(0, sample.SurfaceOwnerUid);
            bool requiresSurfaceOwner = string.Equals(
                    sample.FrameSource ?? "",
                    "ordered-layer-present",
                    StringComparison.OrdinalIgnoreCase)
                || (sample.Source ?? "").IndexOf(
                    "surfaceflinger-layer-latency",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            if (sample.FrameTargetVerified && requiresSurfaceOwner && sample.SurfaceOwnerPid <= 0)
            {
                sample.FrameTargetVerified = false;
            }
        }

        private static double RepresentativeCadenceMs(List<PerfSample> samples)
        {
            List<double> gaps = new List<double>();
            for (int index = 1; index < samples.Count; index++)
            {
                double gapMs = (samples[index].ElapsedSec - samples[index - 1].ElapsedSec) * 1000.0;
                if (IsFinite(gapMs) && gapMs > 0 && gapMs <= MaximumInferredWeightMs)
                {
                    gaps.Add(gapMs);
                }
            }
            if (gaps.Count == 0) return DefaultFallbackWeightMs;
            gaps.Sort();
            int middle = gaps.Count / 2;
            return gaps.Count % 2 == 0
                ? (gaps[middle - 1] + gaps[middle]) / 2.0
                : gaps[middle];
        }

        private static double InferredFallbackWeightMs(List<PerfSample> samples, int index, double cadenceMs)
        {
            if (index + 1 < samples.Count)
            {
                double nextGapMs = (samples[index + 1].ElapsedSec - samples[index].ElapsedSec) * 1000.0;
                if (IsFinite(nextGapMs) && nextGapMs > 0 && nextGapMs <= MaximumInferredWeightMs)
                {
                    return nextGapMs;
                }
            }
            if (index > 0)
            {
                double previousGapMs = (samples[index].ElapsedSec - samples[index - 1].ElapsedSec) * 1000.0;
                if (IsFinite(previousGapMs) && previousGapMs > 0 && previousGapMs <= MaximumInferredWeightMs)
                {
                    return previousGapMs;
                }
            }
            return Math.Max(1.0, Math.Min(MaximumInferredWeightMs, cadenceMs));
        }

        private static double WeightedMedian(List<WeightedFpsPoint> points, double totalWeightMs)
        {
            List<WeightedFpsPoint> ordered = points
                .OrderBy(delegate(WeightedFpsPoint point) { return point.Fps; })
                .ToList();
            double threshold = totalWeightMs / 2.0;
            double cumulative = 0;
            foreach (WeightedFpsPoint point in ordered)
            {
                cumulative += point.WeightMs;
                if (cumulative >= threshold) return point.Fps;
            }
            return ordered[ordered.Count - 1].Fps;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string NormalizeMemoryMetric(string metric)
        {
            return (metric ?? "").Trim().ToLowerInvariant();
        }

        private sealed class WeightedFpsPoint
        {
            public WeightedFpsPoint(PerfSample sample, double fps, double weightMs, bool estimatedWeight)
            {
                Sample = sample;
                Fps = fps;
                WeightMs = weightMs;
                EstimatedWeight = estimatedWeight;
            }

            public PerfSample Sample { get; private set; }
            public double Fps { get; private set; }
            public double WeightMs { get; private set; }
            public bool EstimatedWeight { get; private set; }
        }
    }
}
