using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class CsvExportServiceTests
    {
        [Fact]
        public void ExportKeepsMeasuredZeroFpsAndDeviceMetadata()
        {
            SessionDocument document = new SessionDocument
            {
                Format = "motuperf-session",
                Version = 5,
                StartedAt = new DateTime(2026, 8, 21, 12, 0, 0),
                SelectedBundleId = "com.example.game",
                Device = new DeviceInfo { MarketName = "Test Phone", Platform = "android", ProductVersion = "17", CpuInfo = "CPU-X", GpuInfo = "GPU-Y", Resolution = "1080x2400" },
                Process = new ProcessInfo { Pid = 123, Name = "game" }
            };
            document.Samples.Add(new PerfSample
            {
                ElapsedSec = 1,
                TargetPid = 123,
                HasFps = true,
                FpsUpdated = true,
                Fps = 0,
                Source = "adb",
                FpsScope = "display",
                HasThermalState = true,
                ThermalStateUpdated = true,
                ThermalStateLevel = 5,
                ThermalStateName = "emergency",
                ThermalStateSource = "adb-dumpsys-thermalservice",
                ThermalStateScope = "device"
            });

            string csv = CsvExportService.Build(document);

            Assert.Contains("Test Phone,Android 17,CPU-X,GPU-Y,1080x2400", csv);
            Assert.Contains("1,1,0.000000", csv);
            Assert.Contains("ThermalState-Level", csv);
            Assert.Contains(",5,emergency,adb-dumpsys-thermalservice,device,true,", csv);
        }

        [Fact]
        public void DefaultExportProducesOneRowPerSecondWithMetricSpecificAggregation()
        {
            SessionDocument document = CreateDocument();
            document.Samples.Add(CpuSample(0.10, 100, 20, 10, 30));
            document.Samples.Add(new PerfSample { ElapsedSec = 0.20, TargetPid = 123, HasMemory = true, MemoryUpdated = true, MemoryMb = 100, MemoryMetric = "PSS", MemorySource = "mem-a" });
            document.Samples.Add(CpuSample(0.30, 200, 40, 30, 50));
            document.Samples.Add(new PerfSample
            {
                ElapsedSec = 0.40,
                TargetPid = 123,
                HasFps = true,
                FpsUpdated = true,
                Fps = 60,
                HasJank = true,
                Jank = 2,
                BigJank = 1,
                HasFrameTimeP95 = true,
                FrameTimeP95Ms = 21,
                HasFrameTimeMax = true,
                FrameTimeMaxMs = 48,
                HasFrameObservation = true,
                FrameObservationMs = 1000,
                FrameCount = 60,
                FpsScope = "display",
                FrameSource = "ordered-test",
                OrderedFrames = true
            });
            document.Samples.Add(new PerfSample { ElapsedSec = 0.80, TargetPid = 123, HasMemory = true, MemoryUpdated = true, MemoryMb = 120, MemoryMetric = "PSS", MemorySource = "mem-b" });
            document.Samples.Add(new PerfSample
            {
                ElapsedSec = 0.90,
                TargetPid = 123,
                HasTemperature = true,
                TemperatureUpdated = true,
                TemperatureCelsius = new Dictionary<string, double> { { "CPU", 45 } },
                TemperatureSource = "thermal",
                TemperatureScope = "device",
                HasThermalState = true,
                ThermalStateUpdated = true,
                ThermalStateLevel = 2,
                ThermalStateName = "moderate",
                ThermalStateSource = "thermal-state",
                ThermalStateScope = "device"
            });
            document.Samples.Add(new PerfSample { ElapsedSec = 1.10, TargetPid = 123, HasFps = true, FpsUpdated = true, Fps = 30, HasJank = true, Jank = 4, BigJank = 2 });
            document.Screenshots.Add(new SessionScreenshot { ElapsedSec = 0.70, OriginalPath = "shot-0.png" });

            CsvTable table = ParseData(CsvExportService.Build(document));

            Assert.Equal(2, table.Rows.Count);
            Assert.Equal("0", table.Value(0, "Time"));
            Assert.Equal("1", table.Value(1, "Time"));
            Assert.Equal("60.000000", table.Value(0, "FPS-FPS[frame/s]"));
            Assert.Equal("2.000000", table.Value(0, "FPS-Jank[frame/s]"));
            Assert.Equal("1.000000", table.Value(0, "FPS-BigJank[frame/s]"));
            Assert.Equal("150.000000", table.Value(0, "CPUUsage-Process Raw[%]"));
            Assert.Equal("30.000000", table.Value(0, "CPUUsage-Process Normalized[%]"));
            Assert.Equal("20.000000;40.000000", table.Value(0, "CPUUsage-Device Core[%]"));
            Assert.Equal("122880.000000", table.Value(0, "MemoryDetail-PSS Total[KB]"));
            Assert.Equal("shot-0.png", table.Value(0, "Screenshot-Path"));
            Assert.Equal("CPU=45.000000", table.Value(0, "Temperature-All[C]"));
            Assert.Equal("2", table.Value(0, "ThermalState-Level"));
            Assert.Equal("30.000000", table.Value(1, "FPS-FPS[frame/s]"));
            Assert.Equal("-", table.Value(1, "Temperature-All[C]"));
            Assert.Equal("-", table.Value(1, "ThermalState-Level"));
        }

        [Fact]
        public void RawDetailExportKeepsEveryOriginalTimestampAndRow()
        {
            SessionDocument document = CreateDocument();
            document.Samples.Add(CpuSample(0.123456, 100, 20, 10, 30));
            document.Samples.Add(new PerfSample { ElapsedSec = 0.234567, TargetPid = 123, HasMemory = true, MemoryUpdated = true, MemoryMb = 100, MemoryMetric = "PSS" });
            document.Samples.Add(new PerfSample { ElapsedSec = 0.345678, TargetPid = 123, HasFps = true, FpsUpdated = true, Fps = 58 });

            CsvTable table = ParseData(CsvExportService.Build(document, CsvExportMode.RawDetail));

            Assert.Equal(3, table.Rows.Count);
            Assert.Equal(new[] { "0.123456", "0.234567", "0.345678" }, table.Rows.Select(delegate(string[] row) { return row[table.Columns["Time"]]; }).ToArray());
        }

        [Fact]
        public void SummaryAndRawModesUseTheSameOriginalSamplesForStatistics()
        {
            SessionDocument document = CreateDocument();
            for (int second = 0; second < 20; second++)
            {
                document.Samples.Add(CpuSample(second + 0.10, 100 + second, 20 + second, 10 + second, 30 + second));
                document.Samples.Add(new PerfSample { ElapsedSec = second + 0.25, TargetPid = 123, HasMemory = true, MemoryUpdated = true, MemoryMb = 100 + second, MemoryMetric = "PSS" });
                document.Samples.Add(new PerfSample { ElapsedSec = second + 0.50, TargetPid = 123, HasFps = true, FpsUpdated = true, Fps = 60 - second, HasJank = true, Jank = second % 3, BigJank = second % 2 });
            }

            string summary = CsvExportService.Build(document, CsvExportMode.PerSecondSummary);
            string raw = CsvExportService.Build(document, CsvExportMode.RawDetail);

            Assert.Equal(summary.Substring(0, summary.IndexOf("Data", StringComparison.Ordinal)), raw.Substring(0, raw.IndexOf("Data", StringComparison.Ordinal)));
            Assert.Equal(20, ParseData(summary).Rows.Count);
            Assert.Equal(60, ParseData(raw).Rows.Count);
        }

        [Fact]
        public void MultipleFrameWindowsInOneSecondPreserveCountsAndWorstFrameTime()
        {
            SessionDocument document = CreateDocument();
            document.Samples.Add(FrameSample(0.10, 60, 1, 0, 20, 30, 500));
            document.Samples.Add(FrameSample(0.80, 20, 2, 1, 80, 120, 500));

            CsvTable table = ParseData(CsvExportService.Build(document));

            Assert.Single(table.Rows);
            Assert.Equal("40.000000", table.Value(0, "FPS-FPS[frame/s]"));
            Assert.Equal("3.000000", table.Value(0, "FPS-Jank[frame/s]"));
            Assert.Equal("1.000000", table.Value(0, "FPS-BigJank[frame/s]"));
            Assert.Equal("80.000000", table.Value(0, "DisplayFrameTime-WindowP95Max[ms]"));
            Assert.Equal("120.000000", table.Value(0, "DisplayFrameTime-Max[ms]"));
            Assert.Equal("1000.000000", table.Value(0, "FrameObservation[ms]"));
        }

        [Fact]
        public void FrameTimeMeanUsesFrameCountAndSummaryP95IsExplicitlyWindowMaximum()
        {
            SessionDocument document = CreateDocument();
            PerfSample fast = FrameSample(0.1, 62.5, 0, 0, 16, 16, 960);
            fast.HasFrameTimeMean = true;
            fast.FrameTimeMeanMs = 16;
            PerfSample slow = FrameSample(0.9, 10, 1, 1, 100, 100, 100);
            slow.HasFrameTimeMean = true;
            slow.FrameTimeMeanMs = 100;
            document.Samples.Add(fast);
            document.Samples.Add(slow);

            CsvTable summary = ParseData(CsvExportService.Build(document));
            Assert.Equal("17.377049", summary.Value(0, "DisplayFrameTime-Mean[ms]"));
            Assert.Equal("100.000000", summary.Value(0, "DisplayFrameTime-WindowP95Max[ms]"));
            Assert.False(summary.Columns.ContainsKey("DisplayFrameTime-P95[ms]"));
            CsvTable raw = ParseData(CsvExportService.Build(document, CsvExportMode.RawDetail));
            Assert.Equal("16.000000", raw.Value(0, "DisplayFrameTime-P95[ms]"));
            Assert.Equal("100.000000", raw.Value(1, "DisplayFrameTime-Mean[ms]"));
            slow.FrameCount = 0;
            Assert.Equal("-", ParseData(CsvExportService.Build(document)).Value(0, "DisplayFrameTime-Mean[ms]"));
        }

        private static SessionDocument CreateDocument()
        {
            return new SessionDocument
            {
                StartedAt = new DateTime(2026, 8, 28, 9, 0, 0),
                SelectedBundleId = "com.example.game",
                Device = new DeviceInfo { Platform = "android", MarketName = "Test Phone" },
                Process = new ProcessInfo { Pid = 123, Name = "game" }
            };
        }

        private static PerfSample CpuSample(double elapsed, double raw, double normalized, double core0, double core1)
        {
            return new PerfSample
            {
                ElapsedSec = elapsed,
                TargetPid = 123,
                HasCpu = true,
                CpuUpdated = true,
                CpuPercent = raw,
                CpuSource = "cpu",
                HasCpuNormalized = true,
                CpuNormalizedUpdated = true,
                CpuNormalizedPercent = normalized,
                HasCpuCoreUsage = true,
                CpuCoreUpdated = true,
                CpuCoreCount = 2,
                CpuCorePercents = new List<double> { core0, core1 }
            };
        }

        private static PerfSample FrameSample(double elapsed, double fps, double jank, double bigJank, double p95, double maximum, double observationMs)
        {
            return new PerfSample
            {
                ElapsedSec = elapsed,
                TargetPid = 123,
                HasFps = true,
                FpsUpdated = true,
                Fps = fps,
                HasJank = true,
                Jank = jank,
                BigJank = bigJank,
                HasFrameTimeP95 = true,
                FrameTimeP95Ms = p95,
                HasFrameTimeMax = true,
                FrameTimeMaxMs = maximum,
                HasFrameObservation = true,
                FrameObservationMs = observationMs,
                FrameCount = (int)Math.Round(fps * observationMs / 1000.0)
            };
        }

        private static CsvTable ParseData(string csv)
        {
            string[] lines = csv.Replace("\r\n", "\n").Split('\n');
            int data = Array.FindIndex(lines, delegate(string line) { return line == "Data"; });
            string[] headers = ParseLine(lines[data + 1]).ToArray();
            List<string[]> rows = lines.Skip(data + 2).Where(delegate(string line) { return !string.IsNullOrWhiteSpace(line); }).Select(delegate(string line) { return ParseLine(line).ToArray(); }).ToList();
            return new CsvTable(headers, rows);
        }

        private static IEnumerable<string> ParseLine(string line)
        {
            StringBuilder cell = new StringBuilder();
            bool quoted = false;
            for (int index = 0; index < line.Length; index++)
            {
                char value = line[index];
                if (value == '"')
                {
                    if (quoted && index + 1 < line.Length && line[index + 1] == '"') { cell.Append('"'); index++; }
                    else quoted = !quoted;
                }
                else if (value == ',' && !quoted) { yield return cell.ToString(); cell.Clear(); }
                else cell.Append(value);
            }
            yield return cell.ToString();
        }

        private sealed class CsvTable
        {
            public CsvTable(string[] headers, List<string[]> rows)
            {
                Rows = rows;
                Columns = headers.Select(delegate(string name, int index) { return new KeyValuePair<string, int>(name, index); }).ToDictionary(delegate(KeyValuePair<string, int> item) { return item.Key; }, delegate(KeyValuePair<string, int> item) { return item.Value; });
            }

            public Dictionary<string, int> Columns { get; private set; }
            public List<string[]> Rows { get; private set; }
            public string Value(int row, string column) { return Rows[row][Columns[column]]; }
        }
    }
}
