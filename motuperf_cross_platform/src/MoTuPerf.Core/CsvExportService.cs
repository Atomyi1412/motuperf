using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CSharpIosPerfMonitor
{
    public enum CsvExportMode
    {
        PerSecondSummary,
        RawDetail
    }

    public static class CsvExportService
    {
        public static string Build(SessionDocument document)
        {
            return Build(document, CsvExportMode.PerSecondSummary);
        }

        public static string Build(SessionDocument document, CsvExportMode mode)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            using (StringWriter writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                Write(document, mode, writer);
                return writer.ToString();
            }
        }

        public static void WriteToFile(SessionDocument document, CsvExportMode mode, string path)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            string fullPath = Path.GetFullPath(path ?? "");
            string parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    Write(document, mode, writer);
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(temporary, fullPath, true);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }
        }

        private static void Write(SessionDocument document, CsvExportMode mode, TextWriter builder)
        {
            List<PerfSample> samples = (document.Samples ?? new List<PerfSample>()).OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; }).ToList();
            List<SessionScreenshot> screenshots = document.Screenshots ?? new List<SessionScreenshot>();
            FpsStatistics fps = MetricStatistics.ComputeFps(samples);
            JankStatistics jank = MetricStatistics.ComputeJank(samples);
            MemoryStatistics memory = MetricStatistics.ComputeMemory(samples);
            List<PerfSample> cpuSamples = samples.Where(delegate(PerfSample sample) { return sample.HasCpu && sample.CpuUpdated; }).ToList();
            string memoryName = MemoryMetricName(memory.Metric, document.Device);
            DateTime startedAt = document.StartedAt != default ? document.StartedAt : samples.FirstOrDefault()?.Timestamp ?? DateTime.Now;

            builder.WriteLine(Line(startedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture), First(document.SelectedBundleId, document.App?.BundleId, "-")));
            builder.WriteLine("Device,OS,CPU,GPU,Resolution");
            builder.WriteLine(Line(DeviceName(document.Device), DeviceOs(document.Device), Value(document.Device?.CpuInfo), Value(document.Device?.GpuInfo), Value(document.Device?.Resolution)));
            builder.WriteLine("Stat");
            builder.WriteLine("FPS(avg),FPS>=18(%),FPS>=25(%),FPS(var),FPS(std),FPS(drop),FPS(min),FPS(median),FPS(medRange+-20%)[%],Jank(/10min),BigJank(/10min),Stutter(%)," + memoryName + "(avg)[MB]," + memoryName + "(peak)[MB],AppCPU(raw avg)[%],AppCPU(raw<=60%)[%],AppCPU(raw<=80%)[%],TargetPid,App,Process,ScreenshotCount,FPSStatsSourceTier,FPSStatsSampleCount,FPSStatsExcludedSampleCount,FPSStatsObservationSec,JankStatsSampleCount,JankStatsExcludedSampleCount,JankStatsObservationSec");
            builder.WriteLine(Line(
                fps.HasData ? Number(fps.Average) : "-", fps.HasData ? Number(fps.FpsGe18Percent) : "-", fps.HasData ? Number(fps.FpsGe25Percent) : "-",
                fps.HasData ? Number(fps.Variance) : "-", fps.HasData ? Number(fps.StandardDeviation) : "-", fps.HasData ? Number(fps.DropPerHour) : "-",
                fps.HasData ? Number(fps.Minimum) : "-", fps.HasData ? Number(fps.Median) : "-", fps.HasData ? Number(fps.MedianRangePercent) : "-",
                jank.HasRateData ? Number(jank.JankPer10Min) : "-", jank.HasRateData ? Number(jank.BigJankPer10Min) : "-", jank.HasStutterData ? Number(jank.StutterPercent) : "-",
                memory.HasData ? Number(memory.AverageMb) : "-", memory.HasData ? Number(memory.PeakMb) : "-",
                cpuSamples.Count > 0 ? Number(cpuSamples.Average(delegate(PerfSample sample) { return sample.CpuPercent; })) : "-",
                cpuSamples.Count > 0 ? Number(cpuSamples.Count(delegate(PerfSample sample) { return sample.CpuPercent <= 60; }) * 100.0 / cpuSamples.Count) : "-",
                cpuSamples.Count > 0 ? Number(cpuSamples.Count(delegate(PerfSample sample) { return sample.CpuPercent <= 80; }) * 100.0 / cpuSamples.Count) : "-",
                TargetPid(document, samples), AppName(document), ProcessName(document), screenshots.Count.ToString(CultureInfo.InvariantCulture),
                fps.HasData ? fps.SourceTier : "-", fps.HasData ? fps.SampleCount.ToString(CultureInfo.InvariantCulture) : "-",
                fps.HasData ? fps.ExcludedSampleCount.ToString(CultureInfo.InvariantCulture) : "-", fps.HasData ? Number(fps.TotalDurationSec) : "-",
                jank.HasRateData ? jank.SampleCount.ToString(CultureInfo.InvariantCulture) : "-", jank.HasRateData ? jank.ExcludedSampleCount.ToString(CultureInfo.InvariantCulture) : "-",
                jank.HasRateData ? Number(jank.ObservationSec) : "-"));
            builder.WriteLine();
            builder.WriteLine("Data");
            string p95Column = mode == CsvExportMode.RawDetail ? "DisplayFrameTime-P95[ms]" : "DisplayFrameTime-WindowP95Max[ms]";
            builder.WriteLine("Index,Time,FPS-FPS[frame/s],FPS-Jank[frame/s],FPS-BigJank[frame/s]," + p95Column + ",MemoryDetail-" + memoryName + " Total[KB],MemoryRSSRaw[KB],CPUUsage-Process Raw[%],CPUUsage-Process Normalized[%],CPUUsage-Device Core[%],CPU-Core-Count,Target-PID,Screenshot-Path,Source,Note,Stutter[%],DisplayFrameTime-Mean[ms],DisplayFrameTime-Max[ms],JankTime[ms],FrameObservation[ms],DisplayFrameCount,RefreshRate[Hz],MemoryMetric,MemorySource,CPU Source,FPS Scope,Frame Source,OrderedFrames,ApproximateFrameMetrics,SourceDegraded,FPS Updated,Memory Updated,CPU Updated,CPU Normalized Updated,CPU Core Updated,Temperature-All[C],TemperatureSource,TemperatureScope,Temperature Updated,ThermalState-Level,ThermalState-Name,ThermalStateSource,ThermalStateScope,ThermalStateUpdated,FrameTargetVerified,SurfaceOwnerPid,SurfaceOwnerUid,FrameSourceSequence,FrameSourceSequenceDiscontinuity,MissingFrameSourceWindows,NoPresentFrames,ResumeGap[ms]");
            string preferredMemory = MetricStatistics.PreferredMemoryMetric(samples);
            List<PerfSample> exportSamples = mode == CsvExportMode.RawDetail ? samples : PerSecondSamples(samples, screenshots, preferredMemory);
            for (int index = 0; index < exportSamples.Count; index++)
            {
                PerfSample sample = exportSamples[index];
                string screenshotPath = ScreenshotPath(screenshots, sample.ElapsedSec, mode);
                builder.WriteLine(Line(
                    index.ToString(CultureInfo.InvariantCulture), mode == CsvExportMode.RawDetail ? sample.ElapsedSec.ToString("0.000000", CultureInfo.InvariantCulture) : sample.ElapsedSec.ToString("0", CultureInfo.InvariantCulture),
                    Fresh(sample.HasFps && sample.FpsUpdated, sample.Fps), Fresh(sample.HasJank && sample.FpsUpdated, sample.Jank), Fresh(sample.HasJank && sample.FpsUpdated, sample.BigJank),
                    Fresh(sample.HasFrameTimeP95 && sample.FpsUpdated, sample.FrameTimeP95Ms),
                    Fresh(MetricStatistics.IsPreferredMemorySample(sample, preferredMemory), sample.MemoryMb * 1024), Fresh(sample.HasMemoryRss && sample.MemoryUpdated, sample.MemoryRssMb * 1024),
                    Fresh(sample.HasCpu && sample.CpuUpdated, sample.CpuPercent), Fresh(sample.HasCpuNormalized && sample.CpuNormalizedUpdated, sample.CpuNormalizedPercent),
                    sample.HasCpuCoreUsage && sample.CpuCoreUpdated ? string.Join(";", sample.CpuCorePercents.Select(delegate(double value) { return value.ToString("0.000000", CultureInfo.InvariantCulture); })) : "-",
                    sample.HasCpuCoreUsage && sample.CpuCoreUpdated ? sample.CpuCoreCount.ToString(CultureInfo.InvariantCulture) : "-", sample.TargetPid.ToString(CultureInfo.InvariantCulture), screenshotPath,
                    sample.Source, sample.Note, Fresh(sample.HasStutter && sample.FpsUpdated, sample.StutterPercent), Fresh(sample.HasFrameTimeMean && sample.FpsUpdated, sample.FrameTimeMeanMs),
                    Fresh(sample.HasFrameTimeMax && sample.FpsUpdated, sample.FrameTimeMaxMs), Fresh(sample.HasStutter && sample.FpsUpdated, sample.JankTimeMs),
                    Fresh(sample.HasFrameObservation && sample.FpsUpdated, sample.FrameObservationMs), sample.HasFrameObservation && sample.FpsUpdated ? sample.FrameCount.ToString(CultureInfo.InvariantCulture) : "-",
                    Fresh(sample.HasRefreshRate && sample.FpsUpdated, sample.RefreshRateHz), MemoryMetricLabel(sample.MemoryMetric), Value(sample.MemorySource), Value(sample.CpuSource), Value(sample.FpsScope), Value(sample.FrameSource),
                    Bool(sample.OrderedFrames), Bool(sample.ApproximateFrameMetrics), Bool(sample.SourceDegraded), Bool(sample.FpsUpdated), Bool(sample.MemoryUpdated), Bool(sample.CpuUpdated), Bool(sample.CpuNormalizedUpdated), Bool(sample.CpuCoreUpdated),
                    Temperature(sample), sample.HasTemperature && sample.TemperatureUpdated ? Value(sample.TemperatureSource) : "-", sample.HasTemperature && sample.TemperatureUpdated ? Value(sample.TemperatureScope) : "-", Bool(sample.TemperatureUpdated),
                    sample.HasThermalState ? sample.ThermalStateLevel.ToString(CultureInfo.InvariantCulture) : "-", sample.HasThermalState ? ThermalStateName(sample, document.Device) : "-",
                    sample.HasThermalState && sample.ThermalStateUpdated ? Value(sample.ThermalStateSource) : "-", sample.HasThermalState && sample.ThermalStateUpdated ? Value(sample.ThermalStateScope) : "-", Bool(sample.ThermalStateUpdated),
                    sample.HasFrameTargetVerification ? Bool(sample.FrameTargetVerified) : "-", sample.SurfaceOwnerPid > 0 ? sample.SurfaceOwnerPid.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.SurfaceOwnerUid > 0 ? sample.SurfaceOwnerUid.ToString(CultureInfo.InvariantCulture) : "-", sample.HasFrameSourceSequence ? sample.FrameSourceSequence.ToString(CultureInfo.InvariantCulture) : "-",
                    Bool(sample.FrameSourceSequenceDiscontinuity), sample.MissingFrameSourceWindows.ToString(CultureInfo.InvariantCulture), Bool(sample.NoPresentFrames), Fresh(sample.ResumeGapMs > 0, sample.ResumeGapMs)));
            }
        }

        private static List<PerfSample> PerSecondSamples(IList<PerfSample> samples, IList<SessionScreenshot> screenshots, string preferredMemory)
        {
            double sampleEnd = samples.Count == 0 ? -1 : samples.Max(delegate(PerfSample sample) { return sample.ElapsedSec; });
            double screenshotEnd = screenshots == null || screenshots.Count == 0 ? -1 : screenshots.Where(delegate(SessionScreenshot item) { return item != null; }).Select(delegate(SessionScreenshot item) { return item.ElapsedSec; }).DefaultIfEmpty(-1).Max();
            int lastSecond = (int)Math.Floor(Math.Max(sampleEnd, screenshotEnd));
            List<PerfSample> result = new List<PerfSample>();
            if (lastSecond < 0) return result;

            ILookup<int, PerfSample> buckets = samples.Where(delegate(PerfSample sample) { return sample != null && sample.ElapsedSec >= 0; }).ToLookup(delegate(PerfSample sample) { return (int)Math.Floor(sample.ElapsedSec); });
            int lastPid = samples.Where(delegate(PerfSample sample) { return sample.TargetPid > 0; }).Select(delegate(PerfSample sample) { return sample.TargetPid; }).FirstOrDefault();
            for (int second = 0; second <= lastSecond; second++)
            {
                List<PerfSample> bucket = buckets[second].OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; }).ToList();
                PerfSample projected = AggregateSecond(second, bucket, preferredMemory, lastPid);
                if (projected.TargetPid > 0) lastPid = projected.TargetPid;
                result.Add(projected);
            }
            return result;
        }

        private static PerfSample AggregateSecond(int second, IList<PerfSample> bucket, string preferredMemory, int fallbackPid)
        {
            PerfSample result = new PerfSample
            {
                ElapsedSec = second,
                TargetPid = LastValue(bucket, delegate(PerfSample sample) { return sample.TargetPid > 0; })?.TargetPid ?? fallbackPid,
                Source = JoinDistinct(bucket.Select(delegate(PerfSample sample) { return sample.Source; }), "+"),
                Note = JoinDistinct(bucket.Select(delegate(PerfSample sample) { return sample.Note; }), " | ")
            };

            List<PerfSample> fps = bucket.Where(delegate(PerfSample sample) { return sample.FpsUpdated && sample.HasFps; }).ToList();
            if (fps.Count > 0) AggregateFrameMetrics(result, fps);

            PerfSample memory = LastValue(bucket, delegate(PerfSample sample) { return MetricStatistics.IsPreferredMemorySample(sample, preferredMemory); });
            if (memory != null)
            {
                result.HasMemory = true;
                result.MemoryUpdated = true;
                result.MemoryMb = memory.MemoryMb;
                result.MemoryMetric = memory.MemoryMetric;
                result.MemorySource = memory.MemorySource;
            }
            PerfSample rss = LastValue(bucket, delegate(PerfSample sample) { return sample.HasMemoryRss && sample.MemoryUpdated && Finite(sample.MemoryRssMb); });
            if (rss != null)
            {
                result.HasMemoryRss = true;
                result.MemoryUpdated = true;
                result.MemoryRssMb = rss.MemoryRssMb;
                if (string.IsNullOrWhiteSpace(result.MemorySource)) result.MemorySource = rss.MemorySource;
            }

            List<PerfSample> cpu = bucket.Where(delegate(PerfSample sample) { return sample.HasCpu && sample.CpuUpdated && Finite(sample.CpuPercent); }).ToList();
            if (cpu.Count > 0)
            {
                result.HasCpu = true;
                result.CpuUpdated = true;
                result.CpuPercent = cpu.Average(delegate(PerfSample sample) { return sample.CpuPercent; });
                result.CpuSource = LastNonEmpty(cpu.Select(delegate(PerfSample sample) { return sample.CpuSource; }));
            }
            List<PerfSample> normalized = bucket.Where(delegate(PerfSample sample) { return sample.HasCpuNormalized && sample.CpuNormalizedUpdated && Finite(sample.CpuNormalizedPercent); }).ToList();
            if (normalized.Count > 0)
            {
                result.HasCpuNormalized = true;
                result.CpuNormalizedUpdated = true;
                result.CpuNormalizedPercent = normalized.Average(delegate(PerfSample sample) { return sample.CpuNormalizedPercent; });
                result.CpuNormalizedSource = LastNonEmpty(normalized.Select(delegate(PerfSample sample) { return sample.CpuNormalizedSource; }));
            }
            List<PerfSample> cores = bucket.Where(delegate(PerfSample sample) { return sample.HasCpuCoreUsage && sample.CpuCoreUpdated && sample.CpuCorePercents != null && sample.CpuCorePercents.Count > 0; }).ToList();
            if (cores.Count > 0)
            {
                int count = cores.Max(delegate(PerfSample sample) { return Math.Min(sample.CpuCoreCount, sample.CpuCorePercents.Count); });
                for (int core = 0; core < count; core++)
                {
                    List<double> values = cores.Where(delegate(PerfSample sample) { return core < sample.CpuCorePercents.Count && Finite(sample.CpuCorePercents[core]); }).Select(delegate(PerfSample sample) { return sample.CpuCorePercents[core]; }).ToList();
                    result.CpuCorePercents.Add(values.Count == 0 ? 0 : values.Average());
                }
                result.HasCpuCoreUsage = count > 0;
                result.CpuCoreUpdated = count > 0;
                result.CpuCoreCount = count;
                result.CpuCoreSource = LastNonEmpty(cores.Select(delegate(PerfSample sample) { return sample.CpuCoreSource; }));
                result.CpuCoreScope = LastNonEmpty(cores.Select(delegate(PerfSample sample) { return sample.CpuCoreScope; }));
            }

            PerfSample temperature = LastValue(bucket, delegate(PerfSample sample) { return sample.HasTemperature && sample.TemperatureUpdated && sample.TemperatureCelsius != null && sample.TemperatureCelsius.Count > 0; });
            if (temperature != null)
            {
                result.HasTemperature = true;
                result.TemperatureUpdated = true;
                result.TemperatureCelsius = new Dictionary<string, double>(temperature.TemperatureCelsius, StringComparer.OrdinalIgnoreCase);
                result.TemperatureSource = temperature.TemperatureSource;
                result.TemperatureScope = temperature.TemperatureScope;
            }
            PerfSample thermal = LastValue(bucket, delegate(PerfSample sample) { return sample.HasThermalState && sample.ThermalStateUpdated; });
            if (thermal != null)
            {
                result.HasThermalState = true;
                result.ThermalStateUpdated = true;
                result.ThermalStateLevel = thermal.ThermalStateLevel;
                result.ThermalStateName = thermal.ThermalStateName;
                result.ThermalStateSource = thermal.ThermalStateSource;
                result.ThermalStateScope = thermal.ThermalStateScope;
            }
            return result;
        }

        private static void AggregateFrameMetrics(PerfSample result, IList<PerfSample> samples)
        {
            PerfSample last = samples[samples.Count - 1];
            result.HasFps = true;
            result.FpsUpdated = true;
            result.Fps = WeightedAverage(samples, delegate(PerfSample sample) { return sample.Fps; });
            List<PerfSample> jank = samples.Where(delegate(PerfSample sample) { return sample.HasJank; }).ToList();
            result.HasJank = jank.Count > 0;
            result.Jank = jank.Sum(delegate(PerfSample sample) { return sample.Jank; });
            result.BigJank = jank.Sum(delegate(PerfSample sample) { return sample.BigJank; });
            List<PerfSample> means = samples.Where(delegate(PerfSample sample) { return sample.HasFrameTimeMean && Finite(sample.FrameTimeMeanMs); }).ToList();
            result.HasFrameTimeMean = means.Count == 1 || (means.Count > 1 && means.All(sample => sample.HasFrameObservation && sample.FrameCount > 0));
            result.FrameTimeMeanMs = !result.HasFrameTimeMean ? 0 : means.Count == 1 ? means[0].FrameTimeMeanMs
                : means.Sum(sample => sample.FrameTimeMeanMs * sample.FrameCount) / means.Sum(sample => (double)sample.FrameCount);
            List<PerfSample> p95 = samples.Where(delegate(PerfSample sample) { return sample.HasFrameTimeP95 && Finite(sample.FrameTimeP95Ms); }).ToList();
            result.HasFrameTimeP95 = p95.Count > 0;
            result.FrameTimeP95Ms = p95.Count == 0 ? 0 : p95.Max(delegate(PerfSample sample) { return sample.FrameTimeP95Ms; });
            List<PerfSample> maximums = samples.Where(delegate(PerfSample sample) { return sample.HasFrameTimeMax && Finite(sample.FrameTimeMaxMs); }).ToList();
            result.HasFrameTimeMax = maximums.Count > 0;
            result.FrameTimeMaxMs = maximums.Count == 0 ? 0 : maximums.Max(delegate(PerfSample sample) { return sample.FrameTimeMaxMs; });
            List<PerfSample> observed = samples.Where(delegate(PerfSample sample) { return sample.HasFrameObservation && sample.FrameObservationMs > 0; }).ToList();
            result.HasFrameObservation = observed.Count > 0;
            result.FrameObservationMs = observed.Sum(delegate(PerfSample sample) { return sample.FrameObservationMs; });
            result.FrameCount = observed.Sum(delegate(PerfSample sample) { return sample.FrameCount; });
            List<PerfSample> stutter = samples.Where(delegate(PerfSample sample) { return sample.HasStutter; }).ToList();
            result.HasStutter = stutter.Count > 0;
            result.JankTimeMs = stutter.Sum(delegate(PerfSample sample) { return sample.JankTimeMs; });
            result.StutterPercent = result.HasFrameObservation && result.FrameObservationMs > 0
                ? result.JankTimeMs * 100.0 / result.FrameObservationMs
                : WeightedAverage(stutter, delegate(PerfSample sample) { return sample.StutterPercent; });
            result.HasRefreshRate = last.HasRefreshRate;
            result.RefreshRateHz = last.RefreshRateHz;
            result.FpsScope = last.FpsScope;
            result.FrameSource = last.FrameSource;
            result.OrderedFrames = samples.All(delegate(PerfSample sample) { return sample.OrderedFrames; });
            result.ApproximateFrameMetrics = samples.Any(delegate(PerfSample sample) { return sample.ApproximateFrameMetrics; });
            result.SourceDegraded = samples.Any(delegate(PerfSample sample) { return sample.SourceDegraded; });
            List<PerfSample> verified = samples.Where(delegate(PerfSample sample) { return sample.HasFrameTargetVerification; }).ToList();
            result.HasFrameTargetVerification = verified.Count > 0;
            result.FrameTargetVerified = verified.Count > 0 && verified.All(delegate(PerfSample sample) { return sample.FrameTargetVerified; });
            result.SurfaceOwnerPid = last.SurfaceOwnerPid;
            result.SurfaceOwnerUid = last.SurfaceOwnerUid;
            result.HasFrameSourceSequence = last.HasFrameSourceSequence;
            result.FrameSourceSequence = last.FrameSourceSequence;
            result.FrameSourceSequenceDiscontinuity = samples.Any(delegate(PerfSample sample) { return sample.FrameSourceSequenceDiscontinuity; });
            result.MissingFrameSourceWindows = samples.Sum(delegate(PerfSample sample) { return sample.MissingFrameSourceWindows; });
            result.NoPresentFrames = samples.All(delegate(PerfSample sample) { return sample.NoPresentFrames; });
            result.ResumeGapMs = samples.Select(delegate(PerfSample sample) { return sample.ResumeGapMs; }).DefaultIfEmpty(0).Max();
        }

        private static double WeightedAverage(IList<PerfSample> samples, Func<PerfSample, double> selector)
        {
            if (samples == null || samples.Count == 0) return 0;
            if (samples.Any(delegate(PerfSample sample) { return !sample.HasFrameObservation || sample.FrameObservationMs <= 0; })) return samples.Average(selector);
            double totalWeight = samples.Sum(delegate(PerfSample sample) { return sample.FrameObservationMs; });
            return totalWeight <= 0 ? samples.Average(selector) : samples.Sum(delegate(PerfSample sample) { return selector(sample) * sample.FrameObservationMs; }) / totalWeight;
        }

        private static PerfSample LastValue(IEnumerable<PerfSample> samples, Func<PerfSample, bool> predicate) { return (samples ?? Enumerable.Empty<PerfSample>()).Where(predicate).LastOrDefault(); }
        private static string LastNonEmpty(IEnumerable<string> values) { return (values ?? Enumerable.Empty<string>()).LastOrDefault(delegate(string value) { return !string.IsNullOrWhiteSpace(value); }) ?? ""; }
        private static string JoinDistinct(IEnumerable<string> values, string separator) { return string.Join(separator, (values ?? Enumerable.Empty<string>()).Where(delegate(string value) { return !string.IsNullOrWhiteSpace(value); }).Distinct(StringComparer.Ordinal)); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string ScreenshotPath(IEnumerable<SessionScreenshot> screenshots, double elapsed, CsvExportMode mode)
        {
            if (mode == CsvExportMode.PerSecondSummary)
            {
                SessionScreenshot sameSecond = (screenshots ?? Enumerable.Empty<SessionScreenshot>()).Where(delegate(SessionScreenshot item) { return item != null && (int)Math.Floor(item.ElapsedSec) == (int)Math.Floor(elapsed); }).OrderBy(delegate(SessionScreenshot item) { return item.ElapsedSec; }).LastOrDefault();
                return sameSecond?.OriginalPath ?? "";
            }
            SessionScreenshot screenshot = NearestScreenshot(screenshots, elapsed);
            return screenshot != null && Math.Abs(screenshot.ElapsedSec - elapsed) <= 1.6 ? screenshot.OriginalPath : "";
        }

        private static SessionScreenshot NearestScreenshot(IEnumerable<SessionScreenshot> screenshots, double elapsed) { return (screenshots ?? Enumerable.Empty<SessionScreenshot>()).Where(delegate(SessionScreenshot item) { return item != null; }).OrderBy(delegate(SessionScreenshot item) { return Math.Abs(item.ElapsedSec - elapsed); }).FirstOrDefault(); }
        private static string Temperature(PerfSample sample) { return sample.HasTemperature && sample.TemperatureUpdated && sample.TemperatureCelsius != null ? string.Join(";", sample.TemperatureCelsius.OrderBy(delegate(KeyValuePair<string, double> pair) { return pair.Key; }, StringComparer.OrdinalIgnoreCase).Select(delegate(KeyValuePair<string, double> pair) { return pair.Key + "=" + pair.Value.ToString("0.000000", CultureInfo.InvariantCulture); })) : "-"; }
        private static string DeviceName(DeviceInfo device) { return device == null ? "-" : First(device.MarketName, device.Name, IsAndroid(device) ? "Android Device" : "iOS Device"); }
        private static string DeviceOs(DeviceInfo device) { string platform = IsAndroid(device) ? "Android" : "iOS"; return device == null || string.IsNullOrWhiteSpace(device.ProductVersion) ? platform : platform + " " + device.ProductVersion; }
        private static string MemoryMetricName(string metric, DeviceInfo device) { string label = MemoryMetricLabel(metric); return label == "-" ? (IsAndroid(device) ? "PSS" : "Footprint") : label; }
        private static bool IsAndroid(DeviceInfo device) { return device != null && string.Equals(device.Platform, "android", StringComparison.OrdinalIgnoreCase); }
        private static string MemoryMetricLabel(string metric) { string value = (metric ?? "").Trim().ToLowerInvariant(); if (value == "physical_footprint" || value == "footprint") return "Footprint"; if (value == "pss") return "PSS"; if (value == "rss") return "RSS"; return string.IsNullOrWhiteSpace(metric) ? "-" : metric; }
        private static string AppName(SessionDocument document) { return document.App == null ? Value(document.SelectedBundleId) : First(document.App.Name, document.App.BundleId) + " / " + document.App.BundleId; }
        private static string ProcessName(SessionDocument document) { return document.Process == null ? "-" : First(document.Process.Name, document.Process.DisplayName, "-") + " / pid " + document.Process.Pid; }
        private static string TargetPid(SessionDocument document, IList<PerfSample> samples) { if (document.Process != null && document.Process.Pid > 0) return document.Process.Pid.ToString(CultureInfo.InvariantCulture); return samples.Count == 0 ? "-" : samples[0].TargetPid.ToString(CultureInfo.InvariantCulture); }
        private static string ThermalStateName(PerfSample sample, DeviceInfo device)
        {
            if (!string.IsNullOrWhiteSpace(sample.ThermalStateName)) return sample.ThermalStateName;
            int level = sample.ThermalStateLevel;
            if (IsAndroid(device))
            {
                if (level == 0) return "none";
                if (level == 1) return "light";
                if (level == 2) return "moderate";
                if (level == 3) return "severe";
                if (level == 4) return "critical";
                if (level == 5) return "emergency";
                if (level == 6) return "shutdown";
                return "-";
            }
            if (level == 0) return "nominal";
            if (level == 1) return "fair";
            if (level == 2) return "serious";
            if (level == 3) return "critical";
            return "-";
        }
        private static string Fresh(bool available, double value) { return available && !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("0.000000", CultureInfo.InvariantCulture) : "-"; }
        private static string Number(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string Bool(bool value) { return value ? "true" : "false"; }
        private static string Value(string value) { return string.IsNullOrWhiteSpace(value) ? "-" : value; }
        private static string First(params string[] values) { return values.FirstOrDefault(delegate(string value) { return !string.IsNullOrWhiteSpace(value); }) ?? ""; }
        private static string Line(params string[] values) { return string.Join(",", values.Select(Cell)); }
        private static string Cell(string value) { value = value ?? ""; return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\""; }
    }
}
