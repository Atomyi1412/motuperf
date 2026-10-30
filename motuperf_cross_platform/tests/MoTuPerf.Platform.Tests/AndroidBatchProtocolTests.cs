using System.Collections.Generic;
using System.Reflection;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class AndroidBatchProtocolTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void Parse(PerfCollector collector, string line)
        {
            typeof(PerfCollector).GetField("_captureGeneration", Flags).SetValue(collector, 1);
            typeof(PerfCollector).GetMethod("ParseLine", Flags).Invoke(collector, new object[] { line, 1, "android" });
        }

        private static Queue<PerfSample> FrameSamples(PerfCollector collector)
        {
            return (Queue<PerfSample>)typeof(PerfCollector).GetField("_pendingFrameSamples", Flags).GetValue(collector);
        }

        [Fact]
        public void BatchV1ExpandsFrameEventsInOrderAndKeepsSingleEventCompatibility()
        {
            using var collector = new PerfCollector();
            const string batch = "batch {\"version\":1,\"events\":[" +
                "{\"kind\":\"fps\",\"payload\":{\"fps\":60,\"frame_count\":1,\"window_sec\":0.016,\"source\":\"first\"}}," +
                "{\"kind\":\"fps\",\"payload\":{\"fps\":59,\"frame_count\":1,\"window_sec\":0.016,\"source\":\"second\"}}]}";

            Parse(collector, batch);
            Parse(collector, "fps {\"fps\":58,\"frame_count\":1,\"window_sec\":0.016,\"source\":\"legacy\"}");

            Queue<PerfSample> samples = FrameSamples(collector);
            Assert.Equal(3, samples.Count);
            Assert.Equal(60, samples.Dequeue().Fps);
            Assert.Equal(59, samples.Dequeue().Fps);
            Assert.Equal(58, samples.Dequeue().Fps);
        }

        [Fact]
        public void InvalidBatchVersionAndMalformedEventsAreIgnored()
        {
            using var collector = new PerfCollector();
            Parse(collector, "batch {\"version\":2,\"events\":[{\"kind\":\"fps\",\"payload\":{\"fps\":60,\"frame_count\":1,\"window_sec\":0.016}}]}");
            Parse(collector, "batch {\"version\":1,\"events\":[{\"kind\":\"fps\"},{\"kind\":\"unknown\",\"payload\":{\"fps\":60}},null]}");

            Assert.Empty(FrameSamples(collector));
        }

        [Fact]
        public void FatalInsideBatchStopsRemainingEvents()
        {
            using var collector = new PerfCollector();
            int failureCount = 0;
            collector.Failed += _ => failureCount++;
            const string batch = "batch {\"version\":1,\"events\":[" +
                "{\"kind\":\"fatal\",\"payload\":{\"code\":\"first\",\"message\":\"first failure\"}}," +
                "{\"kind\":\"fatal\",\"payload\":{\"code\":\"second\",\"message\":\"second failure\"}}]}";

            Parse(collector, batch);

            Assert.Equal(1, failureCount);
            Assert.Empty(FrameSamples(collector));
        }
    }
}
