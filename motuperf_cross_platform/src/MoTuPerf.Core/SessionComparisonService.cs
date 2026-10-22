using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CSharpIosPerfMonitor
{
    public enum ComparisonMode { Average, Peak }

    public sealed class ComparisonInput
    {
        public string Name { get; set; } = "";
        public SessionDocument Document { get; set; }
    }

    public sealed class ComparisonRow
    {
        public string Label { get; set; }
        public string Unit { get; set; }
        public string Hint { get; set; }
        public bool IsDetail { get; set; }
        public string Group { get; set; } = "";
        public string SubLabel { get; set; } = "";
        public IReadOnlyList<string> Values { get; set; }
    }

    public sealed class ComparisonReport
    {
        public ComparisonMode Mode { get; set; }
        public IReadOnlyList<string> Headers { get; set; }
        public IReadOnlyList<ComparisonRow> Rows { get; set; }
    }

    public static class SessionComparisonService
    {
        public static ComparisonReport Build(IReadOnlyList<ComparisonInput> inputs, ComparisonMode mode,
            double startSec = 0, double? endSec = null)
        {
            if (inputs == null || inputs.Count < 1 || inputs.Count > 5 || inputs.Any(input => input?.Document == null))
                throw new ArgumentException("请选择 1～5 份有效现场。");
            if (!Finite(startSec) || startSec < 0 || (endSec.HasValue && (!Finite(endSec.Value) || endSec.Value <= startSec)))
                throw new ArgumentException("结束时间必须大于开始时间，时间须为非负秒数。");
            var samples = inputs.Select(input => (input.Document.Samples ?? new List<PerfSample>())
                .Where(sample => sample != null && Finite(sample.ElapsedSec) && sample.ElapsedSec >= startSec
                    && (!endSec.HasValue || sample.ElapsedSec <= endSec.Value)).ToList()).ToList();
            var rows = new List<ComparisonRow>();
            void Text(string label, Func<int, string> value, string hint = "", bool detail = false) => rows.Add(new ComparisonRow
            {
                Label = label, Unit = "", Hint = hint, IsDetail = detail,
                Values = Enumerable.Range(0, inputs.Count).Select(i => Missing(value(i))).ToArray()
            });
            void Metric(string label, string unit, Func<int, double?> value, string hint = "") => rows.Add(new ComparisonRow
            {
                Label = label, Unit = unit, Hint = hint,
                Values = Enumerable.Range(0, inputs.Count).Select(i => Number(value(i))).ToArray()
            });
            double? Aggregate(IEnumerable<double> source)
            {
                var values = source.Where(Finite).ToList();
                return values.Count == 0 ? null : mode == ComparisonMode.Peak ? values.Max() : values.Average();
            }
            Text("设备 / 系统", i => Missing(First(inputs[i].Document.Device?.MarketName, inputs[i].Document.Device?.Name))
                + " / " + DevicePlatformNames.DisplayName(inputs[i].Document.Device?.Platform) + " " + inputs[i].Document.Device?.ProductVersion);
            Text("应用", i => First(inputs[i].Document.App?.Name, inputs[i].Document.Process?.Name));
            Text("包名", i => First(inputs[i].Document.SelectedBundleId, inputs[i].Document.App?.BundleId, inputs[i].Document.Process?.BundleId));
            Text("分辨率", i => inputs[i].Document.Device?.Resolution);
            Metric("屏幕刷新率", "Hz", i => Aggregate(samples[i].Where(s => s.HasRefreshRate && s.FpsUpdated).Select(s => s.RefreshRateHz)));
            Text("测试时间", i => inputs[i].Document.StartedAt == default ? null : inputs[i].Document.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Text("样本范围（秒）/ 数量", i => samples[i].Count == 0 ? null : Number(samples[i].Min(s => s.ElapsedSec)) + " ～ " + Number(samples[i].Max(s => s.ElapsedSec)) + " / " + samples[i].Count,
                "以各记录相对起点的秒数筛选；帧窗口按结束时间归属，不拆分窗口或补齐缺测。");
            var fps = samples.Select(MetricStatistics.ComputeFps).ToList();
            var frames = samples.Select(s => MetricStatistics.ComputeFrameTime(s, includePercentile: false)).ToList();
            Metric("FPS", "帧/s", i => fps[i].HasData ? mode == ComparisonMode.Peak ? fps[i].Maximum : fps[i].Average : (double?)null,
                "均值按可信观测时长加权；峰值是有效窗口最高 FPS，不是越高就一定更流畅。");
            Metric("Smooth", "指数", _ => null,
                "当前采集模型没有 Smooth 稳帧指数；保持缺测，不从 FPS、Jank 或帧耗时推算。");
            Metric("FPS 1% Low", "帧/s", i => frames[i].OnePercentLowFps,
                "取完整逐帧数据中耗时最高的 1% 帧，按平均帧耗时换算 FPS；窗口汇总数据不推算。");
            Metric("FPS Std", "帧/s", i => fps[i].HasData ? fps[i].StandardDeviation : (double?)null,
                "可信 FPS 观测按时长加权的总体标准差。");
            Metric("FPS Var", "(帧/s)^2", i => fps[i].HasData ? fps[i].Variance : (double?)null,
                "可信 FPS 观测按时长加权的总体方差。");
            Metric("FPS >=18%", "%", i => fps[i].HasData ? fps[i].FpsGe18Percent : (double?)null,
                "可信 FPS 观测时长中，FPS 大于等于 18 的占比。");
            Metric("FPS >=25%", "%", i => fps[i].HasData ? fps[i].FpsGe25Percent : (double?)null,
                "可信 FPS 观测时长中，FPS 大于等于 25 的占比。");
            Metric("FPS Drop", "次/h", i => fps[i].HasData ? fps[i].DropPerHour : (double?)null,
                "相邻可信 FPS 点下降大于 8 帧的次数，按小时折算。");
            Metric("FPS Min", "帧/s", i => fps[i].HasData ? fps[i].Minimum : (double?)null,
                "可信 FPS 观测窗口中的最低值。");
            Metric("FPS Median", "帧/s", i => fps[i].HasData ? fps[i].Median : (double?)null,
                "可信 FPS 观测按时长加权的 FPS 中位数。");
            Metric("FPS MedRange", "%", i => fps[i].HasData ? fps[i].MedianRangePercent : (double?)null,
                "FPS 位于中位数 80%～120% 范围内的可信观测时长占比。");
            Metric("FrameTime", "ms", i => mode == ComparisonMode.Peak ? frames[i].MaximumMs : frames[i].AverageMs,
                "完整逐帧数据使用实际帧间隔；旧现场按帧数加权均值/测得最大值，不能从窗口最大值算逐帧均值。");
            Metric("FrameTime Max", "ms", i => frames[i].MaximumMs,
                "完整逐帧数据取实际最大间隔；旧现场仅在逐窗口最大值可验证时汇总。");
            Metric("FrameTime P95", "ms", i => Aggregate(samples[i]
                    .Where(s => s.HasFrameTimeP95 && s.FrameTimeP95Ms > 0 && s.FrameTimeP95Ms <= 60000)
                    .Select(s => s.FrameTimeP95Ms)),
                "按当前模式汇总各窗口 P95；与 CSV Stat 的全程池化 P95 不是同一统计层级。");
            Metric("FrameTime Std", "ms", i => frames[i].StandardDeviationMs,
                "完整逐帧间隔的总体标准差；窗口汇总数据保持 --。");
            Metric("FrameTime Var", "ms^2", i => frames[i].VarianceMs2,
                "完整逐帧间隔的总体方差；窗口汇总数据保持 --。");
            Metric("FrameTime >=100ms", "%", i => frames[i].FrameTimeGe100MsPercent,
                "完整逐帧间隔中大于等于 100ms 的帧占比；窗口汇总数据保持 --。");
            Metric("FrameTime Delta>100ms/h", "次/h", i => frames[i].FrameTimeDeltaOver100PerHour,
                "相邻帧耗时增加超过 100ms 的次数，按有效逐帧观测时长折算为每小时。");
            Metric("Jank", "次/窗", i => Aggregate(samples[i].Where(s => s.FpsUpdated && s.HasJank && MetricStatistics.IsReliableJankSample(s)).Select(s => s.Jank)), "包含 BigJank；均值/峰值描述采样窗口，窗口长度不同时请同时参考每 10 分钟次数。");
            Metric("BigJank", "次/窗", i => Aggregate(samples[i].Where(s => s.FpsUpdated && s.HasJank && MetricStatistics.IsReliableJankSample(s)).Select(s => s.BigJank)));
            var jank = samples.Select(MetricStatistics.ComputeJank).ToList();
            Metric("TinyJank 发生率", "次/10min", _ => null, "当前采集模型没有 TinyJank 字段；保持缺测，不用 0 或其他 Jank 推导。");
            Metric("SmallJank 发生率", "次/10min", _ => null, "当前采集模型没有 SmallJank 字段；保持缺测，不用 0 或其他 Jank 推导。");
            Metric("Jank 发生率（不含 BigJank）", "次/10min",
                i => jank[i].HasRateData ? Math.Max(0, jank[i].JankPer10Min - jank[i].BigJankPer10Min) : (double?)null,
                "与 PerfDog 的 Jank 概览列对齐：从 MoTuPerf 含 BigJank 的 Jank 总数中扣除 BigJank后按 10 分钟折算。");
            Metric("Jank 发生率", "次/10min", i => jank[i].HasRateData ? jank[i].JankPer10Min : (double?)null,
                "包含 BigJank 的总 Jank 频率，按有效观测时长计算，不随峰值/均值模式变化。");
            Metric("BigJank 发生率", "次/10min", i => jank[i].HasRateData ? jank[i].BigJankPer10Min : (double?)null,
                "BigJank 每 10 分钟发生次数，按有效观测时长计算。");
            Metric("卡顿时长占比", "%", i => jank[i].HasStutterData ? jank[i].StutterPercent : (double?)null, "有效卡顿时间 / 有效观测时间，不随峰值/均值模式变化。");
            var memories = samples.Select(MetricStatistics.PreferredMemoryMetric).ToList();
            foreach (string memory in memories.Distinct(StringComparer.OrdinalIgnoreCase).Where(v => !string.IsNullOrWhiteSpace(v)))
                Metric("进程内存 / " + memory, "MB", i => string.Equals(memories[i], memory, StringComparison.OrdinalIgnoreCase)
                    ? Aggregate(samples[i].Where(s => MetricStatistics.IsPreferredMemorySample(s, memory)).Select(s => s.MemoryMb)) : null,
                    "PSS、Physical Footprint 与 RSS 不混为相同口径；只统计真实内存更新。");
            Metric("进程 CPU Raw", "%", i => Aggregate(samples[i].Where(s => s.HasCpu && s.CpuUpdated).Select(s => s.CpuPercent)));
            Metric("进程 CPU Normalized", "%", i => Aggregate(samples[i].Where(s => s.HasCpuNormalized && s.CpuNormalizedUpdated).Select(s => s.CpuNormalizedPercent)),
                "MoTuPerf 按在线核数归一化；不等同 Gears 的 CPU 频率加权。旧现场请核对来源。");
            int cores = Math.Min(256, samples.SelectMany(s => s).Where(s => s.HasCpuCoreUsage && s.CpuCoreUpdated)
                .Select(s => s.CpuCorePercents?.Count ?? 0).DefaultIfEmpty(0).Max());
            for (int core = 0; core < cores; core++)
            {
                int index = core;
                Metric("设备 CPU " + index, "%", i => Aggregate(samples[i].Where(s => s.HasCpuCoreUsage && s.CpuCoreUpdated
                    && s.CpuCorePercents != null && s.CpuCorePercents.Count > index).Select(s => s.CpuCorePercents[index])), "设备逐核负载，不是应用逐核占用；不同芯片相同编号不保证性能等级相同。");
            }
            var sensors = samples.SelectMany(s => s).Where(s => s.HasTemperature && s.TemperatureUpdated && s.TemperatureCelsius != null)
                .SelectMany(s => s.TemperatureCelsius.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            foreach (string sensor in sensors)
                Metric("温度 / " + sensor, "°C", i => Aggregate(samples[i].Where(s => s.HasTemperature && s.TemperatureUpdated
                    && s.TemperatureCelsius != null && s.TemperatureCelsius.ContainsKey(sensor)).Select(s => s.TemperatureCelsius[sensor])
                    .Where(v => v >= -20 && v <= 120)), "同名传感器在不同机型上位置/含义可能不同；结合温度来源与范围判断。");
            foreach (string platform in inputs.Select(i => (i.Document.Device?.Platform ?? "未知").ToLowerInvariant()).Distinct())
                Metric("热状态 / " + platform + "（最高）", "级别", i => string.Equals(inputs[i].Document.Device?.Platform ?? "未知", platform, StringComparison.OrdinalIgnoreCase)
                    ? MaxThermal(samples[i], platform) : null, "仅展示最高等级；iOS 0～3、Android 0～6，不跨平台直接比较；鸿蒙未接入时缺失。");
            Text("FPS 范围", i => Join(samples[i].Where(s => s.FpsUpdated && s.HasFps).Select(s => s.FpsScope)), detail: true);
            Text("帧来源", i => Join(samples[i].Where(s => s.FpsUpdated && s.HasFps).Select(s => s.FrameSource)), "不同范围、来源和刷新率的 FPS 不代表完全相同的性能口径。", detail: true);
            Text("FPS 统计来源", i => fps[i].HasData ? fps[i].SourceTier : null, detail: true);
            Text("FrameTime 统计口径", i => frames[i].SampleCount == 0 ? null : frames[i].HasCompleteRawFrames ? "完整逐帧" : "窗口汇总 / " + frames[i].SourceTier, detail: true);
            Text("CPU 归一化来源", i => Join(samples[i].Where(s => s.HasCpuNormalized && s.CpuNormalizedUpdated).Select(s => s.CpuNormalizedSource)), detail: true);
            Text("温度来源 / 范围", i => Join(samples[i].Where(s => s.HasTemperature && s.TemperatureUpdated).Select(s => s.TemperatureSource + " / " + s.TemperatureScope)), detail: true);
            foreach (var row in rows.Where(row => !row.IsDetail))
            {
                if (row.Label == "FPS" || row.Label == "Smooth" || row.Label.StartsWith("FPS ", StringComparison.Ordinal)
                    || row.Label == "Jank" || row.Label == "BigJank" || row.Label.EndsWith("发生率")
                    || row.Label == "卡顿时长占比")
                    row.Group = "帧率 / 卡顿";
                else if (row.Label == "FrameTime" || row.Label.StartsWith("FrameTime ", StringComparison.Ordinal)) row.Group = "帧耗时";
                else if (row.Label.StartsWith("进程内存 / ")) { row.Group = "进程内存"; row.SubLabel = row.Label.Substring("进程内存 / ".Length); }
                else if (row.Label.StartsWith("进程 CPU ")) { row.Group = "进程 CPU"; row.SubLabel = row.Label.Substring("进程 CPU ".Length); }
                else if (row.Label.StartsWith("设备 CPU ")) { row.Group = "设备逐核负载"; row.SubLabel = row.Label.Substring("设备 ".Length); }
                else if (row.Label.StartsWith("温度 / ")) { row.Group = "温度"; row.SubLabel = row.Label.Substring("温度 / ".Length); }
                else if (row.Label.StartsWith("热状态 / ")) { row.Group = "系统热状态"; row.SubLabel = row.Label.Substring("热状态 / ".Length); }
                if (row.Group == "帧率 / 卡顿")
                {
                    switch (row.Label)
                    {
                        case "FPS": row.SubLabel = "Avg(FPS)"; break;
                        case "Smooth": row.SubLabel = "Smooth(稳帧指数)"; break;
                        case "FPS 1% Low": row.SubLabel = "1% Low(FPS)"; break;
                        case "FPS Std": row.SubLabel = "Std(FPS)"; break;
                        case "FPS Var": row.SubLabel = "Var(FPS)"; break;
                        case "FPS >=18%": row.SubLabel = "FPS >= 18 [%]"; break;
                        case "FPS >=25%": row.SubLabel = "FPS >= 25 [%]"; break;
                        case "FPS Drop": row.SubLabel = "Drop(FPS)[/h]"; break;
                        case "FPS Min": row.SubLabel = "Min(FPS)"; break;
                        case "FPS Median": row.SubLabel = "Median(FPS)"; break;
                        case "FPS MedRange": row.SubLabel = "MedRange(FPS)[%]"; break;
                        case "TinyJank 发生率": row.SubLabel = "TinyJank(/10min)"; break;
                        case "SmallJank 发生率": row.SubLabel = "SmallJank(/10min)"; break;
                        case "Jank 发生率（不含 BigJank）": row.SubLabel = "Jank(/10min)"; break;
                        case "Jank 发生率": row.SubLabel = "Jank(/10min, 含 BigJank)"; break;
                        case "BigJank 发生率": row.SubLabel = "BigJank(/10min)"; break;
                        case "卡顿时长占比": row.SubLabel = "Stutter[%]"; break;
                        default: row.SubLabel = row.Label; break;
                    }
                }
                else if (row.Group == "帧耗时")
                {
                    switch (row.Label)
                    {
                        case "FrameTime": row.SubLabel = "Avg(FrameTime)[ms]"; break;
                        case "FrameTime Max": row.SubLabel = "Max(FrameTime)[ms]"; break;
                        case "FrameTime P95": row.SubLabel = "Window P95(FrameTime)[ms]"; break;
                        case "FrameTime Std": row.SubLabel = "Std(FrameTime)[ms]"; break;
                        case "FrameTime Var": row.SubLabel = "Var(FrameTime)[ms^2]"; break;
                        case "FrameTime >=100ms": row.SubLabel = "FrameTime >= 100ms [%]"; break;
                        case "FrameTime Delta>100ms/h": row.SubLabel = "Delta(FrameTime)>100ms/h"; break;
                        default: row.SubLabel = row.Label; break;
                    }
                }
                else if (!string.IsNullOrEmpty(row.Group) && string.IsNullOrEmpty(row.SubLabel)) row.SubLabel = row.Label;
            }
            return new ComparisonReport { Mode = mode, Headers = inputs.Select(i => Missing(i.Name)).ToArray(), Rows = rows };
        }

        private static double? MaxThermal(IEnumerable<PerfSample> samples, string platform)
        {
            var values = samples.Where(s => s.HasThermalState && s.ThermalStateUpdated && s.ThermalStateLevel >= 0
                && s.ThermalStateLevel <= (platform == "ios" ? 3 : 6)).Select(s => (double)s.ThermalStateLevel).ToList();
            return values.Count == 0 ? null : values.Max();
        }

        public static void WriteCsv(ComparisonReport report, string path)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            string fullPath = Path.GetFullPath(path);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true)))
                {
                    Write(writer, new[] { "MoTuPerf 对比分析", report.Mode == ComparisonMode.Peak ? "峰值" : "均值" });
                    Write(writer, new[] { "测试项", "单位", "口径说明" }.Concat(report.Headers));
                    foreach (ComparisonRow row in report.Rows)
                        Write(writer, new[] { row.Label, row.Unit, row.Hint }.Concat(row.Values));
                }
                File.Move(temporary, fullPath, true);
            }
            catch { try { File.Delete(temporary); } catch { } throw; }
        }

        private static void Write(TextWriter writer, IEnumerable<string> values) => writer.WriteLine(string.Join(",", values.Select(Cell)));
        private static string Cell(string value)
        {
            value = value ?? "";
            string trimmed = value.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) && value != "--"
                && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static string Number(double? value) => value.HasValue && Finite(value.Value) ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "--";
        private static string Missing(string value) => string.IsNullOrWhiteSpace(value) ? "--" : value;
        private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        private static string Join(IEnumerable<string> values) => string.Join(" / ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct());
    }
}
