using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class SessionComparisonTests
    {
        private static ComparisonInput Input(string name = "A", string platform = "android", params PerfSample[] samples) =>
            new ComparisonInput { Name = name, Document = new SessionDocument { Device = new DeviceInfo { Platform = platform }, Samples = samples.ToList() } };
        private static string Value(ComparisonReport report, string label, int column = 0) => report.Rows.Single(row => row.Label == label).Values[column];

        [Fact]
        public void UnequalWindowsUseSharedFrameStatisticsAndIgnoreCarriedCpu()
        {
            var first = FrameTimeStatisticsTests.Window(10, 10, 10);
            first.ElapsedSec = 1;
            first.HasCpu = true; first.CpuUpdated = true; first.CpuPercent = 40;
            var second = FrameTimeStatisticsTests.Window(100);
            second.ElapsedSec = 2;
            var carried = new PerfSample { ElapsedSec = 2.5, HasCpu = true, CpuUpdated = false, CpuPercent = 1000, HasFps = true, FpsUpdated = false, Fps = 240 };
            var input = Input("A", "ios", first, second, carried);
            var mean = SessionComparisonService.Build(new[] { input }, ComparisonMode.Average);
            var peak = SessionComparisonService.Build(new[] { input }, ComparisonMode.Peak);
            Assert.Equal("32.50", Value(mean, "FrameTime"));
            Assert.Equal("100.00", Value(peak, "FrameTime"));
            Assert.Equal("40.00", Value(mean, "进程 CPU Raw"));
            Assert.Equal(MetricStatistics.ComputeFps(input.Document.Samples).Average.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), Value(mean, "FPS"));
        }

        [Fact]
        public void RangeUsesWindowEndpointsAndNeverMutatesOriginalData()
        {
            var first = FrameTimeStatisticsTests.Window(20);
            first.ElapsedSec = 1;
            var second = FrameTimeStatisticsTests.Window(100);
            second.ElapsedSec = 2;
            var input = Input("A", "ios", first, second);
            var report = SessionComparisonService.Build(new[] { input }, ComparisonMode.Average, 1.5, 2);
            Assert.Equal("100.00", Value(report, "FrameTime"));
            Assert.Equal(2, input.Document.Samples.Count);
            Assert.Equal(new[] { 20.0 }, first.FrameIntervalsMs);
            Assert.Equal("--", Value(SessionComparisonService.Build(new[] { input }, ComparisonMode.Average, 5, 6), "FrameTime"));
            Assert.Throws<ArgumentException>(() => SessionComparisonService.Build(new[] { input }, ComparisonMode.Peak, 5, 4));
            Assert.Throws<ArgumentException>(() => SessionComparisonService.Build(new[] { input }, ComparisonMode.Peak, double.NaN));
            Assert.Throws<ArgumentException>(() => SessionComparisonService.Build(Enumerable.Repeat(input, 6).ToArray(), ComparisonMode.Peak));
        }

        [Fact]
        public void PlatformMemoryAndThermalSemanticsStaySeparateAndZeroIsReal()
        {
            var android = Input("A", "android", new PerfSample
            {
                HasMemory = true, MemoryUpdated = true, MemoryMetric = "pss", MemoryMb = 100,
                HasThermalState = true, ThermalStateUpdated = true, ThermalStateLevel = 0,
                HasCpuCoreUsage = true, CpuCoreUpdated = true, CpuCoreCount = 2, CpuCorePercents = new List<double> { 0, 60 },
                HasTemperature = true, TemperatureUpdated = true, TemperatureCelsius = new Dictionary<string, double> { ["Battery"] = 35 }
            });
            var ios = Input("B", "ios", new PerfSample { HasMemory = true, MemoryUpdated = true, MemoryMetric = "physical_footprint", MemoryMb = 200 });
            var report = SessionComparisonService.Build(new[] { android, ios }, ComparisonMode.Average);
            Assert.Equal("100.00", Value(report, "进程内存 / pss"));
            Assert.Equal("--", Value(report, "进程内存 / pss", 1));
            Assert.Equal("200.00", Value(report, "进程内存 / physical_footprint", 1));
            Assert.Equal("0.00", Value(report, "热状态 / android（最高）"));
            Assert.Equal("--", Value(report, "热状态 / ios（最高）", 1));
            Assert.Equal("0.00", Value(report, "设备 CPU 0"));
            Assert.Equal("35.00", Value(report, "温度 / Battery"));
        }

        [Fact]
        public void JankPeaksUseReliableFreshWindowsAndRatesKeepTheirMeaning()
        {
            var first = FrameTimeStatisticsTests.Window(1000);
            first.HasJank = true; first.Jank = 1; first.BigJank = 1;
            var second = FrameTimeStatisticsTests.Window(1000);
            second.ElapsedSec = 2; second.HasJank = true; second.Jank = 3; second.BigJank = 2;
            var carried = new PerfSample { HasJank = true, Jank = 900, BigJank = 900, FpsUpdated = false };
            var inputs = new[] { Input("A", "ios", first, second, carried) };
            var mean = SessionComparisonService.Build(inputs, ComparisonMode.Average);
            var peak = SessionComparisonService.Build(inputs, ComparisonMode.Peak);
            Assert.Equal("2.00", Value(mean, "Jank"));
            Assert.Equal("3.00", Value(peak, "Jank"));
            Assert.Equal("1.50", Value(mean, "BigJank"));
            Assert.Equal(Value(mean, "Jank 发生率"), Value(peak, "Jank 发生率"));
            JankStatistics statistics = MetricStatistics.ComputeJank(inputs[0].Document.Samples);
            Assert.Equal((statistics.JankPer10Min - statistics.BigJankPer10Min).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Value(mean, "Jank 发生率（不含 BigJank）"));
            Assert.Equal("--", Value(mean, "TinyJank 发生率"));
            Assert.Equal("--", Value(mean, "SmallJank 发生率"));
        }

        [Fact]
        public void PerfDogStyleOverviewStatisticsUseRawFramesAndHonestMissingValues()
        {
            PerfSample sample = FrameTimeStatisticsTests.Window(10, 10, 10, 111, 111);
            sample.HasJank = true;
            sample.Jank = 4;
            sample.BigJank = 1;
            ComparisonReport report = SessionComparisonService.Build(
                new[] { Input("A", "ios", sample) }, ComparisonMode.Average);

            Assert.Equal("9.01", Value(report, "FPS 1% Low"));
            Assert.Equal("--", Value(report, "Smooth"));
            Assert.Equal("111.00", Value(report, "FrameTime Max"));
            Assert.Equal("111.00", Value(report, "FrameTime P95"));
            Assert.Equal("49.48", Value(report, "FrameTime Std"));
            Assert.Equal("2448.24", Value(report, "FrameTime Var"));
            Assert.Equal("40.00", Value(report, "FrameTime >=100ms"));
            Assert.Equal("14285.71", Value(report, "FrameTime Delta>100ms/h"));
            JankStatistics statistics = MetricStatistics.ComputeJank(new[] { sample });
            Assert.Equal((statistics.JankPer10Min - statistics.BigJankPer10Min).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Value(report, "Jank 发生率（不含 BigJank）"));
            Assert.Equal("--", Value(report, "TinyJank 发生率"));
            Assert.Equal("--", Value(report, "SmallJank 发生率"));
            Assert.Equal("1% Low(FPS)", report.Rows.Single(row => row.Label == "FPS 1% Low").SubLabel);
            Assert.Equal("Smooth(稳帧指数)", report.Rows.Single(row => row.Label == "Smooth").SubLabel);
            Assert.Equal("Max(FrameTime)[ms]", report.Rows.Single(row => row.Label == "FrameTime Max").SubLabel);
            Assert.Equal("Window P95(FrameTime)[ms]", report.Rows.Single(row => row.Label == "FrameTime P95").SubLabel);
            Assert.Equal("FrameTime >= 100ms [%]", report.Rows.Single(row => row.Label == "FrameTime >=100ms").SubLabel);
        }

        [Fact]
        public void ProvenanceIsDetailAndRefreshRateStaysWithDeviceInformation()
        {
            var sample = new PerfSample { ElapsedSec = 1, FpsUpdated = true, HasFps = true,
                Fps = 60, FpsScope = "surface", FrameSource = "ordered-layer-present", HasRefreshRate = true, RefreshRateHz = 60,
                HasCpu = true, CpuUpdated = true, CpuPercent = 35.5 };
            var report = SessionComparisonService.Build(new[] { Input("A", "android", sample) }, ComparisonMode.Average);
            Assert.Equal(new[] { "FPS 范围", "帧来源", "FPS 统计来源", "FrameTime 统计口径", "CPU 归一化来源", "温度来源 / 范围" },
                report.Rows.Where(row => row.IsDetail).Select(row => row.Label));
            int resolution = report.Rows.ToList().FindIndex(row => row.Label == "分辨率");
            Assert.Equal("屏幕刷新率", report.Rows[resolution + 1].Label);
            Assert.False(report.Rows[resolution + 1].IsDetail);
            Assert.Equal("60.00", Value(report, "屏幕刷新率"));
            var fps = report.Rows.Single(row => row.Label == "FPS");
            Assert.Equal("帧率 / 卡顿", fps.Group);
            Assert.Equal("Avg(FPS)", fps.SubLabel);
            Assert.Equal("帧耗时", report.Rows.Single(row => row.Label == "FrameTime").Group);
            var cpu = report.Rows.Single(row => row.Label == "进程 CPU Raw");
            Assert.Equal("进程 CPU", cpu.Group);
            Assert.Equal("Raw", cpu.SubLabel);
            Assert.Equal("", report.Rows.Single(row => row.Label == "分辨率").Group);
        }

        [Fact]
        public void CsvIsExcelReadableAndProtectsTextFormulas()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motuperf-compare-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var input = Input("=HYPERLINK(\"bad\")");
                input.Document.App = new AppInfo { Name = "@SUM(1+1)" };
                var report = SessionComparisonService.Build(new[] { input }, ComparisonMode.Average);
                string path = Path.Combine(directory, "compare.csv");
                File.WriteAllText(path, "old");
                SessionComparisonService.WriteCsv(report, path);
                Assert.True(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
                string csv = File.ReadAllText(path);
                Assert.Contains("'=HYPERLINK", csv);
                Assert.Contains("'@SUM", csv);
                Assert.Contains("\"--\"", csv);
                Assert.Contains("\"帧来源\"", csv);
                Assert.Single(Directory.GetFiles(directory));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void AnalysisLoadRetainsValidationWithoutExtractingScreenshots()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motuperf-compare-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string png = Path.Combine(directory, "image.png");
                File.WriteAllBytes(png, new byte[] { 1, 2, 3 });
                string path = Path.Combine(directory, "sample.motuperf");
                var document = Input().Document;
                document.Format = "motuperf-session"; document.Version = 5;
                document.Screenshots.Add(new SessionScreenshot { ArchivePath = "screenshots/image.png", OriginalPath = png });
                SessionArchiveService.Save(path, document);
                var result = SessionArchiveService.LoadForAnalysis(path);
                Assert.Single(result.Screenshots);
                Assert.True(string.IsNullOrEmpty(result.Screenshots[0].OriginalPath));
                Assert.Equal(2, Directory.GetFiles(directory).Length);
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.GetEntry("screenshots/image.png").Delete();
                Assert.Throws<InvalidDataException>(() => SessionArchiveService.LoadForAnalysis(path));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
