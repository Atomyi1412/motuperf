using System.Collections.Generic;
using System.Linq;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class PerformanceChartProjectionTests
    {
        [Fact]
        public void ZeroFpsRemainsInsideContinuousMeasuredSeries()
        {
            List<PerfSample> samples = new List<PerfSample>
            {
                Frame(0, 60, 1),
                Frame(1, 0, 2),
                Frame(2, 60, 3)
            };

            IReadOnlyList<IReadOnlyList<PerfSample>> segments = ChartSeriesProjection.BuildSegments(
                samples, 2400, delegate(PerfSample sample) { return sample.Fps; }, 3, false, true);

            Assert.Single(segments);
            Assert.Equal(new[] { 60d, 0d, 60d }, segments[0].Select(delegate(PerfSample sample) { return sample.Fps; }).ToArray());
        }

        [Fact]
        public void OrderedFrameMetricDiscontinuityStartsANewSegment()
        {
            PerfSample first = Frame(0, 16.7, 1);
            PerfSample second = Frame(1, 33.4, 3);
            second.FrameSourceSequenceDiscontinuity = true;

            IReadOnlyList<IReadOnlyList<PerfSample>> segments = ChartSeriesProjection.BuildSegments(
                new[] { first, second }, 2400, delegate(PerfSample sample) { return sample.Fps; }, 3, true, false);

            Assert.Equal(2, segments.Count);
            Assert.Single(segments[0]);
            Assert.Single(segments[1]);
        }

        [Fact]
        public void ValidFpsStaysContinuousAcrossSourceAndSequenceChanges()
        {
            PerfSample first = Frame(0, 60, 1);
            PerfSample second = Frame(1, 0, 3);
            second.FrameSource = "fallback-display";
            second.FrameSourceSequenceDiscontinuity = true;
            PerfSample third = Frame(2, 58, 4);

            IReadOnlyList<IReadOnlyList<PerfSample>> segments = ChartSeriesProjection.BuildSegments(
                new[] { first, second, third }, 2400, delegate(PerfSample sample) { return sample.Fps; }, 3, false, true);

            Assert.Single(segments);
            Assert.Equal(new[] { 60d, 0d, 58d }, segments[0].Select(delegate(PerfSample sample) { return sample.Fps; }).ToArray());
        }

        [Fact]
        public void MissingFpsLeavesAGapInsteadOfBecomingZero()
        {
            PerfSample first = Frame(0, 60, 1);
            PerfSample second = Frame(5, 58, 2);
            first.HasFrameSourceSequence = false;
            second.HasFrameSourceSequence = false;
            IReadOnlyList<IReadOnlyList<PerfSample>> segments = ChartSeriesProjection.BuildSegments(
                new[] { first, second }, 2400,
                delegate(PerfSample sample) { return sample.Fps; }, 3, false, true);

            Assert.Equal(2, segments.Count);
            Assert.DoesNotContain(segments.SelectMany(segment => segment), sample => sample.Fps == 0);
        }

        [Fact]
        public void DecimationKeepsEndpointsAndInstantLowValue()
        {
            List<PerfSample> samples = Enumerable.Range(0, 100)
                .Select(delegate(int index) { return Frame(index, index == 52 ? 4 : 60, index + 1); })
                .ToList();

            IReadOnlyList<IReadOnlyList<PerfSample>> segments = ChartSeriesProjection.BuildSegments(
                samples, 20, delegate(PerfSample sample) { return sample.Fps; }, 3, false, true);

            Assert.Single(segments);
            Assert.Equal(0, segments[0][0].ElapsedSec);
            Assert.Equal(99, segments[0][segments[0].Count - 1].ElapsedSec);
            Assert.Contains(segments[0], delegate(PerfSample sample) { return sample.Fps == 4; });
        }

        [Fact]
        public void LegendToggleOnlyChangesRequestedSeries()
        {
            PerformanceChartControl chart = new PerformanceChartControl();

            Assert.True(chart.IsSeriesVisible("FPS"));
            Assert.True(chart.IsSeriesVisible("Jank"));
            Assert.False(chart.ToggleSeries("Jank"));
            Assert.True(chart.IsSeriesVisible("FPS"));
            Assert.False(chart.IsSeriesVisible("Jank"));
            Assert.True(chart.IsSeriesVisible("BigJank"));
            Assert.True(chart.ToggleSeries("Jank"));
            Assert.True(chart.IsSeriesVisible("Jank"));
        }

        [Fact]
        public void DynamicSeriesKeysRemainIndependent()
        {
            PerformanceChartControl chart = new PerformanceChartControl();
            string cpu0 = PerformanceChartControl.CoreSeriesName(0);
            string cpu1 = PerformanceChartControl.CoreSeriesName(1);
            string cpuTemperature = PerformanceChartControl.TemperatureSeriesName("CPU");
            string gpuTemperature = PerformanceChartControl.TemperatureSeriesName("GPU");

            chart.ToggleSeries(cpu0);
            chart.ToggleSeries(cpuTemperature);

            Assert.False(chart.IsSeriesVisible(cpu0));
            Assert.True(chart.IsSeriesVisible(cpu1));
            Assert.False(chart.IsSeriesVisible(cpuTemperature));
            Assert.True(chart.IsSeriesVisible(gpuTemperature));
        }

        private static PerfSample Frame(double elapsed, double fps, long sequence)
        {
            return new PerfSample
            {
                ElapsedSec = elapsed,
                HasFps = true,
                FpsUpdated = true,
                Fps = fps,
                FrameSource = "test",
                FpsScope = "display",
                HasFrameSourceSequence = true,
                FrameSourceSequence = sequence
            };
        }
    }
}
