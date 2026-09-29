using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyFrameTimelineTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 25, 12, 0, 0);

        private static PerfSample Frame(string generation, double elapsed, double received)
        {
            return new PerfSample
            {
                Source = "hdc-renderservice-surface-fps", FrameSource = generation,
                HasFrameSourceElapsed = true, FrameSourceElapsedSec = elapsed,
                Timestamp = Start.AddSeconds(received)
            };
        }

        private static void Apply(PerfCollector collector, params PerfSample[] samples)
        {
            typeof(PerfCollector).GetField("_startedAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(collector, Start);
            typeof(PerfCollector).GetMethod("ApplyFrameTimeline", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(collector, new object[] { new List<PerfSample>(samples), samples[samples.Length - 1].Timestamp });
        }

        [Fact]
        public void HarmonyFrameJsonSurvivesCollectorArchiveAndCsv()
        {
            using var collector = new PerfCollector();
            typeof(PerfCollector).GetField("_captureGeneration", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(collector, 1);
            typeof(PerfCollector).GetMethod("ParseLine", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(collector, new object[] {
                    "fps {\"platform\":\"harmony\",\"pid\":42,\"fps\":50,\"frame_count\":50,\"window_sec\":1," +
                    "\"source\":\"hdc-renderservice-surface-fps\",\"scope\":\"surface\",\"ordered_frames\":true," +
                    "\"frame_source\":\"harmony-present/node-180388626439/generation-2\",\"source_elapsed_sec\":1," +
                    "\"source_sequence\":3,\"source_lag_sec\":2,\"target_verified\":true,\"surface_owner_pid\":42," +
                    "\"frame_time_mean_ms\":20,\"frame_time_p95_ms\":20,\"frame_time_max_ms\":20,\"jank\":0,\"big_jank\":0}", 1, "harmony" });
            var queue = (Queue<PerfSample>)typeof(PerfCollector)
                .GetField("_pendingFrameSamples", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(collector);
            var sample = Assert.Single(queue);
            var received = (DateTime)typeof(PerfCollector)
                .GetField("_latestFpsAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(collector);
            Assert.Equal(received.AddSeconds(-2), sample.Timestamp);
            Apply(collector, sample);
            Assert.Equal(received.AddSeconds(-2), sample.Timestamp);
            string directory = Directory.CreateTempSubdirectory("motuperf-frame-").FullName;
            try
            {
                var document = new SessionDocument
                {
                    Format = "motuperf-session", Version = 5,
                    Device = new DeviceInfo { Platform = "harmony", Udid = "test-hdc" },
                    Process = new ProcessInfo { Platform = "harmony", Pid = 42, Name = "test-game" }
                };
                document.Samples.Add(sample);
                string archive = Path.Combine(directory, "frame.motuperf");
                SessionArchiveService.Save(archive, document);
                var loaded = SessionArchiveService.Load(archive, Path.Combine(directory, "opened"));
                var saved = Assert.Single(loaded.Samples);
                Assert.True(saved.HasFps && saved.OrderedFrames && saved.HasFrameTargetVerification && saved.FrameTargetVerified);
                Assert.True(saved.HasFrameTimeMean && saved.HasFrameTimeMax && saved.HasJank);
                Assert.Equal(50, saved.Fps);
                Assert.Equal(20, saved.FrameTimeMaxMs);
                Assert.Equal(42, saved.SurfaceOwnerPid);
                Assert.Equal(3, saved.FrameSourceSequence);
                Assert.Equal(1, saved.FrameSourceElapsedSec);
                Assert.False(saved.HasThermalState);
                string csv = CsvExportService.Build(loaded);
                Assert.Contains("hdc-renderservice-surface-fps", csv);
                Assert.Contains("harmony-present/node-180388626439/generation-2", csv);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void SameElapsedAfterGenerationChangeAnchorsAtRecovery()
        {
            using var collector = new PerfCollector();
            var first = Frame("node-1/generation-1", 1, 5);
            Apply(collector, first);
            var recovered = Frame("node-1/generation-2", 1, 20);
            Apply(collector, recovered);
            Assert.Equal(Start.AddSeconds(20), recovered.Timestamp);
        }

        [Fact]
        public void MixedQueuePreservesPreviousGenerationAndAnchorsNewOne()
        {
            using var collector = new PerfCollector();
            Apply(collector, Frame("node-1/generation-1", 1, 5));
            var previous = Frame("node-1/generation-1", 2, 10);
            var recovered = Frame("node-2/generation-2", 1, 20);
            Apply(collector, previous, recovered);
            Assert.Equal(Start.AddSeconds(6), previous.Timestamp);
            Assert.Equal(Start.AddSeconds(20), recovered.Timestamp);
        }

        [Theory]
        [InlineData("hdc-renderservice-surface-fps")]
        [InlineData("pymobiledevice3-coreprofile-display")]
        [InlineData("surfaceflinger-latency")]
        public void HostDeliveryJitterDoesNotChangeContinuousSourceSpacing(string source)
        {
            using var collector = new PerfCollector();
            var first = Frame("continuous", 1, 5);
            first.Source = source;
            Apply(collector, first);
            var second = Frame("continuous", 2, 15);
            second.Source = source;
            Apply(collector, second);
            Assert.Equal(1, (second.Timestamp - first.Timestamp).TotalSeconds);
        }
    }
}
