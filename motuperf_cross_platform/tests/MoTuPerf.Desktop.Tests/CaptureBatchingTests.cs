using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class CaptureBatchingTests
    {
        [Fact]
        public void StopFlushesAcceptedSamplesAndRejectsOldGenerationAfterRestart()
        {
            CaptureUiBuffer queue = new CaptureUiBuffer();
            long first = queue.Begin();
            for (int i = 0; i < 2000; i++) Assert.True(queue.Enqueue(first, new PerfSample { ElapsedSec = i }));
            Assert.Equal(512, queue.Drain(first).Count);
            Assert.Equal(1488, queue.Drain(first, int.MaxValue, close: true).Count);
            Assert.False(queue.IsCurrent(first));
            Assert.False(queue.Enqueue(first, new PerfSample()));
            long second = queue.Begin();
            Assert.False(queue.Enqueue(first, new PerfSample { Fps = 99 }));
            Assert.True(queue.Enqueue(second, new PerfSample { Fps = 17 }));
            Assert.Empty(queue.Drain(first, int.MaxValue, close: true));
            Assert.True(queue.IsCurrent(second));
            Assert.Equal(17, Assert.Single(queue.Drain(second)).Fps);
        }

        [Fact]
        public async Task ConcurrentStopPreservesExactlyTheAcceptedSamples()
        {
            CaptureUiBuffer queue = new CaptureUiBuffer();
            long generation = queue.Begin();
            int accepted = 0;
            Task writer = Task.Run(() =>
            {
                for (int i = 0; i < 20000; i++)
                    if (queue.Enqueue(generation, new PerfSample { ElapsedSec = i })) accepted++;
            });
            IReadOnlyList<PerfSample> saved = queue.Drain(generation, int.MaxValue, close: true);
            await writer;
            Assert.Equal(accepted, saved.Count);
            Assert.Empty(queue.Drain(generation));
        }

        [Fact]
        public void SnapshotPreservesPublishedPrefixAndMergesLateSamplesStably()
        {
            List<PerfSample> raw = new List<PerfSample> { new PerfSample { ElapsedSec = 1 }, new PerfSample { ElapsedSec = 3 } };
            SampleSnapshot first = new SampleSnapshot(raw);
            raw.Add(new PerfSample { ElapsedSec = 2 });
            SampleSnapshot second = new SampleSnapshot(raw, first);
            raw.Add(new PerfSample { ElapsedSec = 4 });
            SampleSnapshot third = new SampleSnapshot(raw, second);
            Assert.Equal(new[] { 1d, 3d }, first.Select(s => s.ElapsedSec));
            Assert.Equal(new[] { 1d, 2d, 3d }, second.Ordered.Select(s => s.ElapsedSec));
            Assert.Equal(new[] { 1d, 2d, 3d, 4d }, third.Ordered.Select(s => s.ElapsedSec));
            Assert.Equal(new[] { 1d, 3d, 2d, 4d }, third.Select(s => s.ElapsedSec));
            Assert.Throws<ArgumentOutOfRangeException>(() => first[2]);
        }

        [Fact]
        public void BatchedRefreshKeepsAllSamplesExtremaSelectionAndExportData()
        {
            using MainWindowViewModel model = new MainWindowViewModel();
            model.ApplySample(new PerfSample { ElapsedSec = 0, HasFps = true, Fps = 60 });
            model.SelectTimeFromChart(0);
            int notifications = 0;
            model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Samples)) notifications++; };
            PerfSample[] batch = Enumerable.Range(1, 10000).Select(i => new PerfSample
            {
                ElapsedSec = i, HasFps = i != 51, Fps = i == 50 ? 0 : 60,
                HasCpu = true, CpuPercent = i == 100 ? 750 : 100
            }).ToArray();
            model.ApplySamples(batch);
            Assert.Equal(1, notifications);
            Assert.Equal(0, model.SelectedTime);
            Assert.Equal(10001, model.Samples.Count);
            SessionDocument saved = model.BuildSessionDocument();
            Assert.Equal(10001, saved.Samples.Count);
            Assert.Same(batch[49], saved.Samples[50]);
            Assert.False(saved.Samples[51].HasFps);
            Assert.Equal(750, saved.Samples.Max(s => s.CpuPercent));
        }

        [Fact]
        public void IndexedLookupHonorsMissingWindowAndZeroValue()
        {
            PerfSample[] data = { new PerfSample { ElapsedSec = 0, HasFps = true, Fps = 0 }, new PerfSample { ElapsedSec = 10 } };
            Assert.Equal(0, SampleSnapshot.Nearest(data, 2, s => s.HasFps, 3).Fps);
            Assert.Null(SampleSnapshot.Nearest(data, 5, s => s.HasFps, 3));
            Assert.Same(data[1], SampleSnapshot.Nearest(data, 20, _ => true, double.PositiveInfinity));
        }
    }
}
