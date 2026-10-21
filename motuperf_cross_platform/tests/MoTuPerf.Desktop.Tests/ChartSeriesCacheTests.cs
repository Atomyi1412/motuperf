using System;
using System.Collections.Generic;
using System.Linq;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class ChartSeriesCacheTests
    {
        private static PerfSample Point(double time, double value = 60) => new PerfSample
        { ElapsedSec = time, HasFps = true, Fps = value, FrameSource = "display", FpsScope = "display" };

        [Fact]
        public void MixedCollectorSnapshotsDoNotCreateFrameSourceBreaksOrDuplicateJank()
        {
            var buffer = new List<PerfSample>();
            for (int i = 0; i < 20; i++)
            {
                var frame = Point(i, i == 10 ? 12 : 60);
                frame.HasFreshnessMetadata = true; frame.FpsUpdated = true;
                frame.Source = "ios-fps"; frame.HasJank = true; frame.Jank = i == 10 ? 2 : 0;
                frame.HasFrameSourceSequence = true; frame.FrameSourceSequence = i;
                buffer.Add(frame);
                foreach (string source in new[] { "ios-fps", "battery-temperature", "thermal-state" })
                {
                    var state = Point(i + 0.1 + buffer.Count % 3 * 0.1, frame.Fps);
                    state.HasFreshnessMetadata = true; state.FpsUpdated = false;
                    state.Source = source; state.HasJank = true; state.Jank = frame.Jank;
                    buffer.Add(state);
                }
            }
            var cache = new ChartDataCache();
            cache.Update(new SampleSnapshot(buffer), "fps");
            foreach (string key in new[] { "FPS", "Jank", "BigJank" })
            {
                Assert.Equal(20, cache.Get(key).Samples.Count);
                Assert.Empty(cache.Get(key).Discontinuities(0, 20));
                Assert.Single(cache.Get(key).Project(0, 20, 300));
            }
            Assert.Equal(12, cache.Get("FPS").Min);
            Assert.Equal(2, cache.Get("Jank").Samples.Sum(s => s.Jank));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FilteringSnapshotsStillPreservesRealFrameBreaks(bool changeSource)
        {
            var first = Point(0); first.FpsUpdated = true; first.HasFreshnessMetadata = true;
            first.HasFrameSourceSequence = true; first.FrameSourceSequence = 1;
            var state = Point(0.5); state.HasFreshnessMetadata = true; state.Source = "thermal-state";
            var next = Point(1); next.FpsUpdated = true; next.HasFreshnessMetadata = true;
            next.HasFrameSourceSequence = true; next.FrameSourceSequence = changeSource ? 2 : 3;
            if (changeSource) next.FrameSource = "fallback";
            var cache = new ChartDataCache();
            cache.Update(new[] { first, state, next }, "fps");
            Assert.Equal(new[] { 1d }, cache.Get("FPS").Discontinuities(0, 2));
            Assert.Equal(2, cache.Get("FPS").Project(0, 2, 100).Count);
        }

        [Fact]
        public void LegacySamplesWithoutFreshnessMetadataStillRender()
        {
            var cache = new ChartDataCache();
            cache.Update(new[] { Point(0), Point(1) }, "fps");
            Assert.Equal(2, cache.Get("FPS").Samples.Count);
            Assert.Single(cache.Get("FPS").Project(0, 2, 100));
        }

        [Fact]
        public void AppendInspectsOnlyNewSamplesAndNewSessionResetsExtrema()
        {
            var buffer = Enumerable.Range(0, 10000).Select(i => Point(i, i % 100)).ToList();
            var cache = new ChartDataCache();
            var before = new SampleSnapshot(buffer);
            cache.Update(before, "fps");
            buffer.Add(Point(10000, 144));
            var after = new SampleSnapshot(buffer, before);
            cache.Update(after, "fps");
            Assert.Equal(10001, cache.InspectedSamples);
            Assert.Equal(144, cache.Get("FPS").Max);
            cache.Update(after, "fps");
            Assert.Equal(10001, cache.InspectedSamples);
            cache.Update(new SampleSnapshot(new List<PerfSample> { Point(0, 30) }), "fps");
            Assert.Single(cache.Get("FPS").Samples);
            Assert.Equal(30, cache.Get("FPS").Max);
        }

        [Fact]
        public void LateSamplesAreSortedWithoutChangingSessionOrder()
        {
            var buffer = new List<PerfSample> { Point(0, 40), Point(2, 50) };
            var first = new SampleSnapshot(buffer);
            var cache = new ChartDataCache();
            cache.Update(first, "fps");
            buffer.Add(Point(1, 120));
            cache.Update(new SampleSnapshot(buffer, first), "fps");
            Assert.Equal(new[] { 0d, 1d, 2d }, cache.Get("FPS").Samples.Select(s => s.ElapsedSec));
            Assert.Equal(new[] { 0d, 2d, 1d }, buffer.Select(s => s.ElapsedSec));
            Assert.Equal(120, cache.Get("FPS").Max);
        }

        [Fact]
        public void PixelBucketsKeepNarrowSpikesAndDipsInTimeOrder()
        {
            var cache = new ChartSeriesCache(s => s.Fps, 3);
            for (int i = 0; i < 100000; i++) cache.Add(Point(i * 0.01, i == 50001 ? 1 : i == 50002 ? 120 : 60));
            var points = Assert.Single(cache.Project(0, 1000, 500));
            Assert.True(points.Count <= 2002);
            Assert.Contains(points, s => s.Fps == 1);
            Assert.Contains(points, s => s.Fps == 120);
            Assert.Equal(points.OrderBy(s => s.ElapsedSec), points);
            Assert.InRange(cache.QueryCount, 1, 500);
        }

        [Fact]
        public void SparseZoomRetainsBothOutsideNeighboursButDoesNotBridgeGap()
        {
            var cache = new ChartSeriesCache(s => s.Fps, 7);
            cache.Add(Point(0, 10)); cache.Add(Point(6, 20));
            var segment = Assert.Single(cache.Project(2, 4, 400));
            Assert.Equal(new[] { 0d, 6d }, segment.Select(s => s.ElapsedSec));
            var broken = new ChartSeriesCache(s => s.Fps, 3);
            broken.Add(Point(0)); broken.Add(Point(6));
            Assert.Empty(broken.Project(2, 4, 400));
        }

        [Fact]
        public void SourceChangeAndExplicitSequenceBreakRemainSeparateAfterDecimation()
        {
            var cache = new ChartSeriesCache(s => s.Fps, 3, true, true);
            cache.Add(Point(0));
            var next = Point(1); next.FrameSource = "fallback"; cache.Add(next);
            var last = Point(2); last.FrameSource = "fallback"; last.FrameSourceSequenceDiscontinuity = true; cache.Add(last);
            Assert.Equal(3, cache.Project(0, 3, 1).Count);
            Assert.Equal(new[] { 1d, 2d }, cache.Discontinuities(0, 3));
        }

        [Fact]
        public void SequenceFieldAloneDoesNotProveCoverageOfLongGap()
        {
            var cache = new ChartSeriesCache(s => s.Fps, 3, true, true);
            var first = Point(0); first.HasFrameSourceSequence = true; first.FrameSourceSequence = 1;
            var next = Point(10); next.HasFrameSourceSequence = true; next.FrameSourceSequence = 2;
            cache.Add(first); cache.Add(next);
            Assert.Equal(2, cache.Project(0, 10, 100).Count);
            var measured = new ChartSeriesCache(s => s.Fps, 3, true, true);
            next.HasFrameObservation = true; next.FrameObservationMs = 10000; next.Fps = 0;
            measured.Add(first); measured.Add(next);
            Assert.Single(measured.Project(0, 10, 100));
            Assert.Contains(measured.Samples, s => s.Fps == 0);
        }

        [Fact]
        public void NarrowViewQueriesOnlyVisibleTimeBuckets()
        {
            var cache = new ChartSeriesCache(s => s.Fps, 3);
            for (int i = 0; i < 200000; i++) cache.Add(Point(i * 0.1, i % 100));
            var segment = Assert.Single(cache.Project(10000, 10060, 100));
            Assert.InRange(segment[0].ElapsedSec, 9999.8, 10000);
            Assert.InRange(segment[segment.Count - 1].ElapsedSec, 10060, 10060.2);
            Assert.True(segment.Count <= 402);
            Assert.InRange(cache.QueryCount, 1, 100);
        }

        [Fact]
        public void DenseBreaksRemainDisconnectedAndHaveAPixelBoundedBudget()
        {
            var cache = new ChartSeriesCache(s => s.Fps, 3, true);
            for (int i = 0; i < 10000; i++)
            {
                var sample = Point(i * 0.1, i == 5555 ? 144 : 60);
                sample.FrameSourceSequenceDiscontinuity = i > 0;
                cache.Add(sample);
            }
            var segments = cache.Project(0, 1000, 100);
            Assert.True(cache.DenseDiscontinuities);
            Assert.True(segments.Count <= 402);
            Assert.All(segments, s => Assert.Single(s));
            Assert.Contains(segments.SelectMany(s => s), s => s.Fps == 144);
            Assert.InRange(cache.Discontinuities(0, 1000, 100).Count(), 1, 103);
        }

        [Theory]
        [InlineData(59, false)]
        [InlineData(60, true)]
        [InlineData(90, true)]
        [InlineData(120, true)]
        [InlineData(121, false)]
        public void CursorOutsideViewportIsNotClampedIntoIt(double time, bool visible)
            => Assert.Equal(visible, PerformanceChartControl.IsCursorInView(time, 60, 120));

        [Fact]
        public void MemoryKindsStaySeparateAndPreferTrueUpdatedPss()
        {
            var cache = new ChartDataCache();
            var rss = Point(0); rss.HasMemory = true; rss.MemoryMb = 400; rss.MemoryMetric = "rss"; rss.MemoryUpdated = true;
            var pss = Point(1); pss.HasMemory = true; pss.MemoryMb = 300; pss.MemoryMetric = "pss"; pss.MemoryUpdated = true;
            cache.Update(new[] { rss, pss }, "memory");
            Assert.Equal("pss", cache.PreferredMemory);
            Assert.Equal(300, cache.Get("pss").Max);
            Assert.Equal(400, cache.Get("rss").Max);
        }

        [Fact]
        public void LegacyFootprintAliasStillHasAnIndexedMemoryCurve()
        {
            var cache = new ChartDataCache();
            var sample = Point(0); sample.HasMemory = true; sample.MemoryUpdated = true;
            sample.MemoryMetric = "footprint"; sample.MemoryMb = 256;
            cache.Update(new[] { sample }, "memory");
            Assert.Equal(256, cache.Get(cache.PreferredMemory).Max);
        }
    }
}
