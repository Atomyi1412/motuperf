using System.Collections.Generic;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class MetricStatisticsTests
    {
        [Fact]
        public void FpsStatisticsUseOnlyFreshSamples()
        {
            PerfSample fresh = new PerfSample { HasFps = true, Fps = 60, FpsUpdated = true, ElapsedSec = 1 };
            PerfSample stale = new PerfSample { HasFps = true, Fps = 0, FpsUpdated = false, ElapsedSec = 2 };

            FpsStatistics result = MetricStatistics.ComputeFps(new List<PerfSample> { fresh, stale });

            Assert.True(result.HasData);
            Assert.Equal(1, result.SampleCount);
            Assert.Equal(60, result.Average, 5);
        }

        [Fact]
        public void FrameTimeProjectionRejectsStateSnapshots()
        {
            PerfSample frame = new PerfSample { HasFrameTimeMax = true, FpsUpdated = true, FrameTimeMaxMs = 16.7 };
            PerfSample state = new PerfSample { HasFrameTimeMax = true, FpsUpdated = false, FrameTimeMaxMs = 16.7 };

            Assert.True(FrameTimeChartProjection.IsChartSample(frame));
            Assert.False(FrameTimeChartProjection.IsChartSample(state));
        }
    }
}
