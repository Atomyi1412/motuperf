using System;
using System.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class FrameTimeStatisticsTests
    {
        internal static PerfSample Window(params double[] intervals)
        {
            return new PerfSample
            {
                HasFps = true, FpsUpdated = true, OrderedFrames = true,
                HasFrameObservation = true, FrameCount = intervals.Length, FrameObservationMs = intervals.Sum(),
                Fps = intervals.Length * 1000.0 / intervals.Sum(), FrameIntervalsMs = intervals,
                HasFrameTimeMean = true, FrameTimeMeanMs = intervals.Average(),
                HasFrameTimeMax = true, FrameTimeMaxMs = intervals.Max(),
                HasFrameTimeP95 = true, FrameTimeP95Ms = intervals.OrderBy(x => x).ElementAt((int)Math.Ceiling(intervals.Length * 0.95) - 1),
                Source = "ios-coreprofile", FpsScope = "screen", FrameSource = "iomfb-swap-on-glass"
            };
        }

        [Fact]
        public void PooledFramesAreNotAnAverageOfWindowMaximaOrPercentiles()
        {
            PerfSample first = Window(Enumerable.Repeat(1000.0 / 60, 59).Append(100).ToArray());
            PerfSample second = Window(Enumerable.Repeat(1000.0 / 60, 60).ToArray());
            FrameTimeStatistics result = MetricStatistics.ComputeFrameTime(new[] { first, second });
            Assert.True(result.HasCompleteRawFrames);
            Assert.Equal(120, result.FrameCount);
            Assert.Equal(17.361111111, result.AverageMs.Value, 6);
            Assert.Equal(1000.0 / 60, result.MinimumMs.Value, 8);
            Assert.Equal(1000.0 / 60, result.P95Ms.Value, 8);
            Assert.Equal(100, result.MaximumMs.Value);
        }

        [Fact]
        public void LegacyOrPartiallyRawFilesUseFrameWeightsWithoutInventingMinimumOrP95()
        {
            PerfSample first = Window(10, 10, 10);
            PerfSample second = Window(100);
            second.FrameIntervalsMs = null;
            FrameTimeStatistics mixed = MetricStatistics.ComputeFrameTime(new[] { first, second });
            Assert.False(mixed.HasCompleteRawFrames);
            Assert.Equal(32.5, mixed.AverageMs);
            Assert.Equal(100, mixed.MaximumMs);
            Assert.Null(mixed.MinimumMs);
            Assert.Null(mixed.P95Ms);
            first.FrameIntervalsMs = null;
            Assert.Equal(mixed.AverageMs, MetricStatistics.ComputeFrameTime(new[] { first, second }).AverageMs);
        }

        [Fact]
        public void CarriedAndCorruptAndUnverifiedFramesNeverEnterSummary()
        {
            PerfSample valid = Window(20, 20);
            PerfSample carried = Window(1000);
            carried.FpsUpdated = false;
            PerfSample corrupt = Window(1000);
            corrupt.OutOfOrderFrameTimestamps = 1;
            PerfSample wrongTarget = Window(1000);
            wrongTarget.HasFrameTargetVerification = true;
            wrongTarget.FrameTargetVerified = false;
            FrameTimeStatistics result = MetricStatistics.ComputeFrameTime(new[] { valid, carried, corrupt, wrongTarget });
            Assert.Equal(1, result.SampleCount);
            Assert.Equal(20, result.AverageMs);
        }

        [Fact]
        public void SummaryOnlySourcesRemainAvailableButNeverMixWithOrderedFrames()
        {
            PerfSample histogram = Window(100);
            histogram.OrderedFrames = false;
            histogram.FrameIntervalsMs = null;
            var summary = MetricStatistics.ComputeFrameTime(new[] { histogram });
            Assert.Equal("fallback_summaries", summary.SourceTier);
            Assert.Equal(100, summary.AverageMs);
            Assert.Null(summary.MinimumMs);
            var preferred = MetricStatistics.ComputeFrameTime(new[] { histogram, Window(20) });
            Assert.Equal("exact_ordered", preferred.SourceTier);
            Assert.Equal(20, preferred.AverageMs);
            Assert.True(preferred.HasCompleteRawFrames);
        }

        [Fact]
        public void FpsUsesDurationWeightingAndExcludesStartupFallbackAndCarriedValues()
        {
            PerfSample first = Window(Enumerable.Repeat(1000.0 / 60, 60).ToArray());
            PerfSample second = Window(Enumerable.Repeat(1000.0 / 30, 300).ToArray());
            var fallback = new PerfSample { HasFps = true, FpsUpdated = true, Fps = 120, Source = "graphics" };
            var stale = new PerfSample { HasFps = true, FpsUpdated = false, Fps = 1 };
            FpsStatistics result = MetricStatistics.ComputeFps(new[] { first, second, fallback, stale });
            Assert.Equal("exact_ordered", result.SourceTier);
            Assert.Equal(360.0 / 11, result.Average, 8);
            Assert.Equal(60, result.Maximum, 8);
            Assert.Equal(30, result.Minimum, 8);
            Assert.Equal(2, result.SampleCount);
            Assert.Equal(1, result.ExcludedSampleCount);
        }

        [Fact]
        public void RawFrameStatisticsMatchPerfDogOnePercentLowThresholdAndDeltaSemantics()
        {
            FrameTimeStatistics result = MetricStatistics.ComputeFrameTime(
                new[] { Window(10, 10, 10, 111, 111) }, includePercentile: false);

            Assert.Equal(5, result.FrameCount);
            Assert.Equal(1000.0 / 111.0, result.OnePercentLowFps.Value, 8);
            Assert.Equal(2448.24, result.VarianceMs2.Value, 8);
            Assert.Equal(Math.Sqrt(2448.24), result.StandardDeviationMs.Value, 8);
            Assert.Equal(40.0, result.FrameTimeGe100MsPercent.Value, 8);
            Assert.Equal(3600.0 / 0.252, result.FrameTimeDeltaOver100PerHour.Value, 6);
            Assert.Null(result.P95Ms);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(60001)]
        public void InvalidIntervalsAreRejected(double value)
        {
            Assert.False(FrameIntervalData.IsValid(new[] { value }, 1, 100));
        }

        [Fact]
        public void IntervalBoundsCountAndObservationAreValidated()
        {
            Assert.False(FrameIntervalData.IsValid(new[] { 20.0, 20 }, 3, 40));
            Assert.False(FrameIntervalData.IsValid(new[] { 20.0, 20 }, 2, 1000));
            Assert.False(FrameIntervalData.IsValid(new double[FrameIntervalData.MaximumIntervalsPerWindow + 1], FrameIntervalData.MaximumIntervalsPerWindow + 1, 1000));
            Assert.True(FrameIntervalData.IsValid(new[] { 1000.0 / 60, 1000.0 / 60 }, 2, 1000.0 / 30));
        }
    }
}
