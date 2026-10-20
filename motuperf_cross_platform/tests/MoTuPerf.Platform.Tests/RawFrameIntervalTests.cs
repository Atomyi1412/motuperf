using System.Collections.Generic;
using System.Reflection;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class RawFrameIntervalTests
    {
        [Theory]
        [InlineData("[10,20,30]", 0, true)]
        [InlineData("[10,20]", 0, false)]
        [InlineData("[10,-20,70]", 0, false)]
        [InlineData("[10,20,100]", 0, false)]
        [InlineData("[10,20,30]", 1, false)]
        public void OnlyValidatedIntervalsReachFreshQueuedWindows(string intervals, int invalidCount, bool retained)
        {
            using var collector = new PerfCollector();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PerfCollector).GetField("_captureGeneration", flags).SetValue(collector, 1);
            string json = "fps {\"fps\":50,\"frame_count\":3,\"window_sec\":0.06,\"ordered_frames\":true," +
                "\"scope\":\"screen\",\"frame_time_mean_ms\":20,\"frame_time_max_ms\":30," +
                "\"invalid_frame_intervals\":" + invalidCount + ",\"frame_intervals_ms\":" + intervals + "}";
            typeof(PerfCollector).GetMethod("ParseLine", flags).Invoke(collector, new object[] { json, 1, "ios" });
            var samples = (Queue<PerfSample>)typeof(PerfCollector).GetField("_pendingFrameSamples", flags).GetValue(collector);
            PerfSample sample = Assert.Single(samples);
            Assert.True(sample.FpsUpdated);
            if (retained) Assert.Equal(new[] { 10.0, 20, 30 }, sample.FrameIntervalsMs);
            else Assert.Null(sample.FrameIntervalsMs);
            typeof(PerfCollector).GetMethod("ParseLine", flags).Invoke(collector,
                new object[] { "cpu {\"value\":25,\"unit\":\"%\"}", 1, "ios" });
            var process = (Queue<PerfSample>)typeof(PerfCollector).GetField("_pendingProcessSamples", flags).GetValue(collector);
            PerfSample processSnapshot = Assert.Single(process);
            Assert.False(processSnapshot.FpsUpdated);
            Assert.Null(processSnapshot.FrameIntervalsMs);
        }
    }
}
