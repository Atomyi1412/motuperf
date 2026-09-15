using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CSharpIosPerfMonitor
{
    public sealed class ChartCanvas : FrameworkElement
    {
        private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(34, 35, 48));
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(58, 255, 255, 255));
        private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(211, 213, 226));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(136, 139, 156));
        private static readonly Brush[] CoreCpuBrushes = new Brush[]
        {
            Brushes.Gold,
            Brushes.LightGreen,
            Brushes.DeepSkyBlue,
            Brushes.HotPink,
            Brushes.Orange,
            Brushes.MediumPurple,
            Brushes.Cyan,
            Brushes.YellowGreen,
            Brushes.Coral,
            Brushes.CornflowerBlue,
            Brushes.Violet,
            Brushes.SpringGreen,
            Brushes.Tomato,
            Brushes.MediumTurquoise,
            Brushes.Khaki,
            Brushes.LightSteelBlue
        };
        private static readonly Brush[] TemperatureBrushes = new Brush[]
        {
            Brushes.OrangeRed,
            Brushes.DeepSkyBlue,
            Brushes.Gold,
            Brushes.MediumPurple,
            Brushes.LightGreen,
            Brushes.Cyan,
            Brushes.HotPink,
            Brushes.Coral
        };
        private readonly HashSet<string> _hiddenSeries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ChartCanvas()
        {
            Samples = new List<PerfSample>();
            Screenshots = new List<ScreenshotInfo>();
            Mode = "fps";
            Cursor = Cursors.Cross;
        }

        public List<PerfSample> Samples { get; set; }
        public List<ScreenshotInfo> Screenshots { get; set; }
        public double? SelectedTime { get; set; }
        public string Mode { get; set; }
        public event Action<double> TimeSelected;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            Rect rect = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(BackgroundBrush, null, rect);
            if (ActualWidth < 160 || ActualHeight < 78) return;

            Rect area = PlotArea();
            double maxTime = MaxTime();

            if (Samples.Count == 0 && Mode != "shots")
            {
                DrawGrid(dc, area, 0, 60, UnitForMode(), maxTime);
                DrawText(dc, "等待真实数据", area.Left + area.Width / 2 - 44, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                return;
            }

            if (Mode == "fps")
            {
                bool showFps = IsSeriesVisible("FPS");
                bool showJank = IsSeriesVisible("Jank");
                bool showBigJank = IsSeriesVisible("BigJank");
                IEnumerable<double> visibleValues = Enumerable.Empty<double>();
                if (showFps)
                {
                    visibleValues = visibleValues.Concat(Samples.Where(delegate(PerfSample s) { return s.HasFps; }).Select(delegate(PerfSample s) { return s.Fps; }));
                }
                if (showJank)
                {
                    visibleValues = visibleValues.Concat(Samples.Where(delegate(PerfSample s) { return s.HasJank; }).Select(delegate(PerfSample s) { return s.Jank; }));
                }
                if (showBigJank)
                {
                    visibleValues = visibleValues.Concat(Samples.Where(delegate(PerfSample s) { return s.HasJank; }).Select(delegate(PerfSample s) { return s.BigJank; }));
                }
                AxisRange fpsRange = AxisRange.FromValues(visibleValues, 0, 60, 5);
                DrawGrid(dc, area, fpsRange.Min, fpsRange.Max, "帧/s", maxTime);
                if (showFps)
                {
                    DrawLine(dc, area, maxTime, delegate(PerfSample s) { return s.Fps; }, delegate(PerfSample s) { return s.HasFps; }, Brushes.HotPink, fpsRange.Min, fpsRange.Max, 2, 3.0, null, AllowMeasuredFpsGap);
                }
                if (showJank)
                {
                    DrawLine(dc, area, maxTime, delegate(PerfSample s) { return s.Jank; }, delegate(PerfSample s) { return s.HasJank; }, Brushes.DeepSkyBlue, fpsRange.Min, fpsRange.Max, 1.2, 3.0, null, AllowMeasuredFpsGap);
                }
                if (showBigJank)
                {
                    DrawLine(dc, area, maxTime, delegate(PerfSample s) { return s.BigJank; }, delegate(PerfSample s) { return s.HasJank; }, Brushes.LightGreen, fpsRange.Min, fpsRange.Max, 1.2, 3.0, null, AllowMeasuredFpsGap);
                }
                bool anySeriesVisible = showFps || showJank || showBigJank;
                bool anyVisibleData = (showFps && Samples.Any(delegate(PerfSample s) { return s.HasFps; }))
                    || ((showJank || showBigJank) && Samples.Any(delegate(PerfSample s) { return s.HasJank; }));
                if (anySeriesVisible && !anyVisibleData)
                {
                    DrawText(dc, "等待已显示指标数据", area.Left + area.Width / 2 - 60, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                }
                DrawText(dc, "FPS / Jank / BigJank", area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "frametime")
            {
                Func<PerfSample, bool> hasMax = delegate(PerfSample sample)
                {
                    return FrameTimeChartProjection.IsChartSample(sample);
                };
                IEnumerable<double> values = Samples.Where(hasMax).Select(delegate(PerfSample s) { return FrameTimeMaxMs(s); });
                AxisRange range = AxisRange.FromValues(values, 0, 35, 3);
                DrawGrid(dc, area, range.Min, range.Max, "ms", maxTime);
                DrawLine(dc, area, maxTime, delegate(PerfSample s) { return FrameTimeMaxMs(s); }, hasMax, Brushes.HotPink, range.Min, range.Max, 2, 3.0, BreaksFrameContinuity);
                if (!Samples.Any(hasMax))
                {
                    DrawText(dc, "等待 Display FrameTime 数据", area.Left + area.Width / 2 - 78, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                }
                DrawText(dc, "Display FrameTime", area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "memory")
            {
                string preferredMetric = MetricStatistics.PreferredMemoryMetric(Samples);
                Func<PerfSample, bool> hasPreferredMemory = delegate(PerfSample sample)
                {
                    return sample != null
                        && sample.HasMemory
                        && sample.MemoryMb > 0
                        && MemoryMetricMatches(sample.MemoryMetric, preferredMetric);
                };
                AxisRange range = AxisRange.FromValues(Samples.Where(hasPreferredMemory).Select(delegate(PerfSample s) { return s.MemoryMb; }), 0, 100, 50);
                DrawGrid(dc, area, range.Min, range.Max, "MB", maxTime);
                DrawLine(dc, area, maxTime, delegate(PerfSample s) { return s.MemoryMb; }, hasPreferredMemory, Brushes.Gold, range.Min, range.Max, 2, 7.0);
                string memoryLabel = MemoryMetricLabel(preferredMetric);
                DrawText(dc, "Process Memory" + (string.IsNullOrWhiteSpace(memoryLabel) ? "" : " " + memoryLabel), area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "cpu")
            {
                AxisRange range = AxisRange.FromValues(Samples.Where(delegate(PerfSample s) { return s.HasCpu; }).Select(delegate(PerfSample s) { return s.CpuPercent; }), 0, 100, 5);
                DrawGrid(dc, area, range.Min, range.Max, "%", maxTime);
                DrawLine(dc, area, maxTime, delegate(PerfSample s) { return s.CpuPercent; }, delegate(PerfSample s) { return s.HasCpu; }, Brushes.LightGreen, range.Min, range.Max, 2, 7.0);
                DrawText(dc, "Process CPU Raw", area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "cpunormalized")
            {
                AxisRange range = AxisRange.FromValues(Samples.Where(delegate(PerfSample s) { return s.HasCpuNormalized; }).Select(delegate(PerfSample s) { return s.CpuNormalizedPercent; }), 0, 100, 5);
                DrawGrid(dc, area, range.Min, range.Max, "%", maxTime);
                DrawLine(dc, area, maxTime, delegate(PerfSample s) { return s.CpuNormalizedPercent; }, delegate(PerfSample s) { return s.HasCpuNormalized; }, Brushes.DeepSkyBlue, range.Min, range.Max, 2, 7.0);
                if (!Samples.Any(delegate(PerfSample s) { return s.HasCpuNormalized; }))
                {
                    DrawText(dc, "等待归一化 CPU 数据", area.Left + area.Width / 2 - 70, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                }
                DrawText(dc, "Process CPU Normalized (raw / online cores)", area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "corecpu")
            {
                int coreCount = Samples
                    .Where(delegate(PerfSample s) { return s.HasCpuCoreUsage; })
                    .Select(delegate(PerfSample s) { return s.CpuCoreCount; })
                    .DefaultIfEmpty(0)
                    .Max();
                List<double> coreValues = new List<double>();
                for (int coreIndex = 0; coreIndex < coreCount; coreIndex++)
                {
                    if (!IsSeriesVisible(CoreSeriesName(coreIndex))) continue;
                    int capturedIndex = coreIndex;
                    coreValues.AddRange(Samples
                        .Where(delegate(PerfSample s)
                        {
                            return s.HasCpuCoreUsage
                                && s.CpuCorePercents != null
                                && s.CpuCorePercents.Count > capturedIndex;
                        })
                        .Select(delegate(PerfSample s) { return s.CpuCorePercents[capturedIndex]; }));
                }
                AxisRange range = AxisRange.FromValues(coreValues, 0, 100, 5);
                DrawGrid(dc, area, range.Min, range.Max, "%", maxTime);
                for (int coreIndex = 0; coreIndex < coreCount; coreIndex++)
                {
                    if (!IsSeriesVisible(CoreSeriesName(coreIndex))) continue;
                    int capturedIndex = coreIndex;
                    DrawLine(
                        dc,
                        area,
                        maxTime,
                        delegate(PerfSample s) { return s.CpuCorePercents[capturedIndex]; },
                        delegate(PerfSample s) { return s.HasCpuCoreUsage && s.CpuCorePercents != null && s.CpuCorePercents.Count > capturedIndex; },
                        CoreCpuBrush(capturedIndex),
                        range.Min,
                        range.Max,
                        1.4,
                        7.0);
                }
                if (coreCount == 0)
                {
                    DrawText(dc, "等待设备逐核 CPU 数据", area.Left + area.Width / 2 - 72, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                }
                DrawText(dc, "Device Core CPU (device-wide, each core)", area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "temperature")
            {
                List<string> sensors = TemperatureSensorNames(Samples);
                List<double> visibleValues = new List<double>();
                foreach (string sensor in sensors)
                {
                    if (!IsSeriesVisible(TemperatureSeriesName(sensor))) continue;
                    string capturedSensor = sensor;
                    List<PerfSample> displaySamples = TemperatureChartProjection.ForSensor(Samples, capturedSensor, maxTime);
                    visibleValues.AddRange(displaySamples
                        .Where(delegate(PerfSample sample) { return HasTemperatureValue(sample, capturedSensor); })
                        .Select(delegate(PerfSample sample) { return TemperatureValue(sample, capturedSensor); }));
                }
                AxisRange range = AxisRange.FromTemperatureValues(visibleValues);
                DrawGrid(dc, area, range.Min, range.Max, "\u2103", maxTime);
                foreach (string sensor in sensors)
                {
                    if (!IsSeriesVisible(TemperatureSeriesName(sensor))) continue;
                    string capturedSensor = sensor;
                    List<PerfSample> displaySamples = TemperatureChartProjection.ForSensor(Samples, capturedSensor, maxTime);
                    DrawLine(
                        dc,
                        area,
                        maxTime,
                        delegate(PerfSample sample) { return TemperatureValue(sample, capturedSensor); },
                        delegate(PerfSample sample) { return HasTemperatureValue(sample, capturedSensor); },
                        TemperatureBrush(sensors.IndexOf(sensor)),
                        range.Min,
                        range.Max,
                        1.7,
                        12.0,
                        null,
                        null,
                        displaySamples);
                }
                if (sensors.Count == 0)
                {
                    DrawText(dc, "Waiting for device temperature", area.Left + area.Width / 2 - 82, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                }
                DrawText(dc, "Device Temperature", area.Left, 6, 13, TextBrush);
            }
            else if (Mode == "thermalstate")
            {
                List<PerfSample> displaySamples = ThermalStateChartProjection.ForChart(Samples, maxTime);
                bool androidStatus = displaySamples.Any(IsAndroidThermalStatus);
                int thermalMaximum = androidStatus ? 6 : 3;
                string thermalTitle = androidStatus ? "Thermal Status" : "Thermal State";
                string thermalLegend = androidStatus
                    ? "0正常 / 1轻微 / 2中度 / 3严重 / 4临界 / 5紧急 / 6关机"
                    : "0正常 / 1升温 / 2严重 / 3临界";
                DrawGrid(dc, area, 0, thermalMaximum, "级别", maxTime);
                DrawLine(
                    dc,
                    area,
                    maxTime,
                    delegate(PerfSample sample) { return sample.ThermalStateLevel; },
                    delegate(PerfSample sample) { return sample.HasThermalState; },
                    Brushes.OrangeRed,
                    0,
                    thermalMaximum,
                    2,
                    3.0,
                    null,
                    null,
                    displaySamples);
                if (displaySamples.Count == 0)
                {
                    DrawText(dc, "等待系统热状态数据", area.Left + area.Width / 2 - 65, area.Top + area.Height / 2 - 8, 13, MutedBrush);
                }
                DrawText(dc, thermalTitle + "  " + thermalLegend, area.Left, 6, 13, TextBrush);
            }
            else
            {
                DrawGrid(dc, area, 0, 1, "截图", maxTime);
                DrawScreenshots(dc, area, maxTime);
                DrawText(dc, "Screenshot Timeline", area.Left, 6, 13, TextBrush);
            }
            DrawCursor(dc, area, maxTime);
        }

        internal static Brush CoreCpuBrush(int coreIndex)
        {
            int normalizedIndex = Math.Max(0, coreIndex) % CoreCpuBrushes.Length;
            return CoreCpuBrushes[normalizedIndex];
        }

        private static bool IsAndroidThermalStatus(PerfSample sample)
        {
            if (sample == null || !sample.HasThermalState) return false;
            string name = (sample.ThermalStateName ?? "").Trim().ToLowerInvariant();
            return sample.ThermalStateLevel > 3
                || name == "none"
                || name == "light"
                || name == "moderate"
                || name == "severe"
                || name == "emergency"
                || name == "shutdown";
        }

        internal static string CoreSeriesName(int coreIndex)
        {
            return "CPU" + Math.Max(0, coreIndex).ToString(CultureInfo.InvariantCulture);
        }

        internal static Brush TemperatureBrush(int sensorIndex)
        {
            int normalizedIndex = Math.Max(0, sensorIndex) % TemperatureBrushes.Length;
            return TemperatureBrushes[normalizedIndex];
        }

        internal static string TemperatureSeriesName(string sensorName)
        {
            return "Temperature " + (sensorName ?? "").Trim();
        }

        internal static List<string> TemperatureSensorNames(IEnumerable<PerfSample> samples)
        {
            string[] preferredOrder = new[] { "CPU", "GPU", "Battery", "Skin", "NPU", "USB", "Power Amplifier" };
            IEnumerable<string> names = (samples ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample) { return sample != null && sample.HasTemperature && sample.TemperatureCelsius != null; })
                .SelectMany(delegate(PerfSample sample) { return sample.TemperatureCelsius.Keys; })
                .Where(delegate(string name) { return !string.IsNullOrWhiteSpace(name); })
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return names
                .OrderBy(delegate(string name)
                {
                    int index = Array.FindIndex(preferredOrder, delegate(string preferred) { return string.Equals(preferred, name, StringComparison.OrdinalIgnoreCase); });
                    return index < 0 ? int.MaxValue : index;
                })
                .ThenBy(delegate(string name) { return name; }, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool IsSeriesVisible(string seriesName)
        {
            string key = (seriesName ?? "").Trim();
            return string.IsNullOrEmpty(key) || !_hiddenSeries.Contains(key);
        }

        public bool ToggleSeries(string seriesName)
        {
            string key = (seriesName ?? "").Trim();
            if (string.IsNullOrEmpty(key)) return true;
            bool visible;
            if (_hiddenSeries.Contains(key))
            {
                _hiddenSeries.Remove(key);
                visible = true;
            }
            else
            {
                _hiddenSeries.Add(key);
                visible = false;
            }
            InvalidateVisual();
            return visible;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            CaptureMouse();
            SelectTimeAt(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            {
                SelectTimeAt(e.GetPosition(this));
                e.Handled = true;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SelectTimeAt(Point point)
        {
            if (Samples.Count == 0) return;
            Rect area = PlotArea();
            if (point.Y < area.Top - 18 || point.Y > area.Bottom + 28) return;
            double x = Math.Max(area.Left, Math.Min(area.Right, point.X));
            double elapsed = (x - area.Left) / Math.Max(1, area.Width) * MaxTime();
            Action<double> handler = TimeSelected;
            if (handler != null) handler(elapsed);
        }

        private Rect PlotArea()
        {
            return new Rect(60, 28, Math.Max(10, ActualWidth - 84), Math.Max(10, ActualHeight - 54));
        }

        private double MaxTime()
        {
            return Samples.Count > 0 ? Math.Max(1, Samples.Max(delegate(PerfSample sample) { return sample.ElapsedSec; })) : 60;
        }

        private static void DrawGrid(DrawingContext dc, Rect area, double min, double max, string unit, double maxTime)
        {
            Pen gridPen = new Pen(GridBrush, 1);
            int i;
            for (i = 0; i <= 6; i++)
            {
                double x = area.Left + area.Width * i / 6.0;
                dc.DrawLine(gridPen, new Point(x, area.Top), new Point(x, area.Bottom));
                double seconds = maxTime * i / 6.0;
                DrawText(dc, FormatSeconds(seconds), x - 10, area.Bottom + 5, 11, MutedBrush);
            }
            for (i = 0; i <= 4; i++)
            {
                double y = area.Top + area.Height * i / 4.0;
                dc.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
                double value = max - (max - min) * i / 4.0;
                DrawText(dc, FormatValue(value), 7, y - 8, 11, MutedBrush);
            }
            DrawTextRight(dc, unit, area.Left - 8, area.Top - 19, 11, MutedBrush);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1), area);
        }

        private void DrawLine(DrawingContext dc, Rect area, double maxTime, Func<PerfSample, double> value, Func<PerfSample, bool> hasValue, Brush brush, double min, double max, double width, double maxGapSeconds, Func<PerfSample, PerfSample, bool> breakBefore = null, Func<PerfSample, PerfSample, bool> allowLongGap = null, IEnumerable<PerfSample> sourceSamples = null)
        {
            Pen pen = new Pen(brush, width);
            StreamGeometry geometry = new StreamGeometry();
            List<PerfSample> orderedSamples = ChronologicalSamples(Samples);
            if (sourceSamples != null)
            {
                orderedSamples = ChronologicalSamples(sourceSamples.ToList());
            }
            List<Point> isolatedPoints = new List<Point>();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                bool started = false;
                bool segmentHasLine = false;
                double? previousElapsed = null;
                PerfSample previousSample = null;
                Point segmentStart = new Point();
                foreach (PerfSample sample in Decimate(orderedSamples, 2400, value, hasValue))
                {
                    if (hasValue != null && !hasValue(sample))
                    {
                        continue;
                    }
                    bool longGap = previousElapsed.HasValue && sample.ElapsedSec - previousElapsed.Value > maxGapSeconds;
                    bool measuredLongGap = longGap && allowLongGap != null && allowLongGap(previousSample, sample);
                    if ((breakBefore != null && breakBefore(previousSample, sample))
                        || (longGap && !measuredLongGap))
                    {
                        AddIsolatedPoint(isolatedPoints, started, segmentHasLine, segmentStart);
                        started = false;
                        segmentHasLine = false;
                    }
                    double x = area.Left + Math.Max(0, Math.Min(maxTime, sample.ElapsedSec)) / maxTime * area.Width;
                    double normal = (value(sample) - min) / Math.Max(0.0001, max - min);
                    double y = area.Bottom - Math.Max(0, Math.Min(1, normal)) * area.Height;
                    if (!started)
                    {
                        segmentStart = new Point(x, y);
                        ctx.BeginFigure(segmentStart, false, false);
                        started = true;
                        segmentHasLine = false;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y), true, false);
                        segmentHasLine = true;
                    }
                    previousElapsed = sample.ElapsedSec;
                    previousSample = sample;
                }
                AddIsolatedPoint(isolatedPoints, started, segmentHasLine, segmentStart);
            }
            geometry.Freeze();
            dc.PushClip(new RectangleGeometry(area));
            dc.DrawGeometry(null, pen, geometry);
            double markerRadius = Math.Max(2.4, width * 1.6);
            foreach (Point point in isolatedPoints)
            {
                Point marker = KeepMarkerInsidePlot(point, area, markerRadius);
                dc.DrawEllipse(brush, null, marker, markerRadius, markerRadius);
            }
            dc.Pop();
        }

        private static void AddIsolatedPoint(List<Point> points, bool started, bool segmentHasLine, Point segmentStart)
        {
            if (started && !segmentHasLine) points.Add(segmentStart);
        }

        private static Point KeepMarkerInsidePlot(Point point, Rect area, double radius)
        {
            double x = Math.Max(area.Left + radius, Math.Min(area.Right - radius, point.X));
            double y = Math.Max(area.Top + radius, Math.Min(area.Bottom - radius, point.Y));
            return new Point(x, y);
        }

        private static List<PerfSample> ChronologicalSamples(List<PerfSample> samples)
        {
            for (int index = 1; index < samples.Count; index++)
            {
                if (samples[index - 1].ElapsedSec > samples[index].ElapsedSec)
                {
                    return samples.OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; }).ToList();
                }
            }
            return samples;
        }

        private void DrawScreenshots(DrawingContext dc, Rect area, double maxTime)
        {
            Pen pen = new Pen(Brushes.DeepSkyBlue, 1.5);
            foreach (ScreenshotInfo shot in Screenshots)
            {
                double x = area.Left + Math.Max(0, Math.Min(maxTime, shot.ElapsedSec)) / maxTime * area.Width;
                dc.DrawLine(pen, new Point(x, area.Top + 8), new Point(x, area.Bottom - 8));
            }
        }

        private void DrawCursor(DrawingContext dc, Rect area, double maxTime)
        {
            if (!SelectedTime.HasValue) return;
            double selected = Math.Max(0, Math.Min(maxTime, SelectedTime.Value));
            double x = area.Left + selected / maxTime * area.Width;
            Pen pen = new Pen(Brushes.White, 1);
            pen.DashStyle = DashStyles.Dash;
            dc.DrawLine(pen, new Point(x, area.Top), new Point(x, area.Bottom));
            DrawText(dc, FormatSeconds(selected), x + 6, area.Top + 6, 12, Brushes.White);
        }

        private static IEnumerable<PerfSample> Decimate(List<PerfSample> samples, int maxPoints, Func<PerfSample, double> value, Func<PerfSample, bool> hasValue)
        {
            if (samples.Count <= maxPoints) return samples;
            int bucketSize = Math.Max(1, (int)Math.Ceiling(samples.Count / Math.Max(1, maxPoints / 4.0)));
            List<Tuple<int, PerfSample>> picked = new List<Tuple<int, PerfSample>>();
            HashSet<int> seen = new HashSet<int>();
            Action<int> add = delegate(int index)
            {
                if (index >= 0 && index < samples.Count && seen.Add(index))
                {
                    picked.Add(Tuple.Create(index, samples[index]));
                }
            };
            for (int start = 0; start < samples.Count; start += bucketSize)
            {
                int end = Math.Min(samples.Count - 1, start + bucketSize - 1);
                add(start);
                add(end);

                int minIndex = -1;
                int maxIndex = -1;
                double minValue = double.MaxValue;
                double maxValue = double.MinValue;
                for (int i = start; i <= end; i++)
                {
                    PerfSample sample = samples[i];
                    if (sample.FrameSourceSequenceDiscontinuity)
                    {
                        add(i - 1);
                        add(i);
                    }
                    if (i > start && !MetricStatistics.IsSameFrameSeries(samples[i - 1], sample))
                    {
                        add(i - 1);
                        add(i);
                    }
                    if (hasValue != null && !hasValue(sample)) continue;
                    double current = value(sample);
                    if (double.IsNaN(current) || double.IsInfinity(current)) continue;
                    if (current < minValue)
                    {
                        minValue = current;
                        minIndex = i;
                    }
                    if (current > maxValue)
                    {
                        maxValue = current;
                        maxIndex = i;
                    }
                }
                add(minIndex);
                add(maxIndex);
            }
            return picked.OrderBy(delegate(Tuple<int, PerfSample> item) { return item.Item1; }).Select(delegate(Tuple<int, PerfSample> item) { return item.Item2; }).ToList();
        }

        private static bool BreaksFrameContinuity(PerfSample previous, PerfSample current)
        {
            return current != null
                && (current.FrameSourceSequenceDiscontinuity
                    || (previous != null && !MetricStatistics.IsSameFrameSeries(previous, current)));
        }

        private static bool AllowMeasuredFpsGap(PerfSample previous, PerfSample current)
        {
            if (previous == null || current == null || !previous.HasFps || !current.HasFps) return false;

            // A zero-FPS idle window or a verified resume gap is a real observation,
            // even when the source reports that window less frequently than the
            // normal chart cadence. Keep that measured drop connected visually.
            return previous.NoPresentFrames
                || current.NoPresentFrames
                || previous.ResumeGapMs > 0
                || current.ResumeGapMs > 0
                || previous.HasFrameSourceSequence
                || current.HasFrameSourceSequence;
        }

        private static double FrameTimeMaxMs(PerfSample sample)
        {
            return sample.HasFrameTimeMax ? sample.FrameTimeMaxMs : 0;
        }

        private static string MemoryMetricLabel(string metric)
        {
            string normalized = (metric ?? "").Trim().ToLowerInvariant();
            if (normalized == "physical_footprint" || normalized == "footprint") return "Footprint";
            if (normalized == "pss") return "PSS";
            if (normalized == "rss") return "RSS";
            return string.IsNullOrWhiteSpace(metric) ? "" : metric;
        }

        private static bool MemoryMetricMatches(string sampleMetric, string preferredMetric)
        {
            string sample = (sampleMetric ?? "").Trim().ToLowerInvariant();
            string preferred = (preferredMetric ?? "").Trim().ToLowerInvariant();
            if (sample == "footprint") sample = "physical_footprint";
            if (preferred == "footprint") preferred = "physical_footprint";
            return sample == preferred;
        }

        private static bool HasTemperatureValue(PerfSample sample, string sensorName)
        {
            if (sample == null || !sample.HasTemperature || sample.TemperatureCelsius == null || string.IsNullOrWhiteSpace(sensorName)) return false;
            double value;
            return sample.TemperatureCelsius.TryGetValue(sensorName, out value)
                && !double.IsNaN(value)
                && !double.IsInfinity(value)
                && value >= -20.0
                && value <= 120.0;
        }

        private static double TemperatureValue(PerfSample sample, string sensorName)
        {
            double value;
            return sample != null && sample.TemperatureCelsius != null && sample.TemperatureCelsius.TryGetValue(sensorName, out value)
                ? value
                : 0;
        }

        private string UnitForMode()
        {
            if (Mode == "memory") return "MB";
            if (Mode == "cpu" || Mode == "cpunormalized" || Mode == "corecpu") return "%";
            if (Mode == "frametime") return "ms";
            if (Mode == "temperature") return "\u2103";
            if (Mode == "thermalstate") return "级别";
            if (Mode == "shots") return "截图";
            return "帧/s";
        }

        private static string FormatSeconds(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalMinutes >= 1) return string.Format("{0}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
            return string.Format("{0:0}s", seconds);
        }

        private static string FormatValue(double value)
        {
            if (Math.Abs(value) >= 100) return string.Format("{0:0}", value);
            if (Math.Abs(value) >= 10) return string.Format("{0:0.0}", value);
            return string.Format("{0:0.##}", value);
        }

        private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
        {
            FormattedText formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                size,
                brush,
                VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
            dc.DrawText(formatted, new Point(x, y));
        }

        private static void DrawTextRight(DrawingContext dc, string text, double right, double y, double size, Brush brush)
        {
            FormattedText formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                size,
                brush,
                VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
            dc.DrawText(formatted, new Point(right - formatted.Width, y));
        }

        private struct AxisRange
        {
            public double Min;
            public double Max;

            public static AxisRange FromValues(IEnumerable<double> values, double floor, double defaultMax, double padding)
            {
                List<double> list = values.Where(delegate(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }).ToList();
                if (list.Count == 0)
                {
                    return new AxisRange { Min = floor, Max = defaultMax };
                }

                double min = Math.Min(list.Min(), floor);
                double max = Math.Max(list.Max(), defaultMax);
                if (Math.Abs(max - min) < 0.001)
                {
                    max = min + Math.Max(1, padding);
                }
                else
                {
                    double extra = Math.Max(padding, (max - min) * 0.12);
                    min = Math.Max(floor, min - extra);
                    max = max + extra;
                }
                return new AxisRange { Min = min, Max = max };
            }

            public static AxisRange FromTemperatureValues(IEnumerable<double> values)
            {
                List<double> list = values
                    .Where(delegate(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= -20.0 && value <= 120.0; })
                    .ToList();
                if (list.Count == 0)
                {
                    return new AxisRange { Min = 20.0, Max = 50.0 };
                }

                double min = list.Min();
                double max = list.Max();
                double span = max - min;
                double padding = Math.Max(2.0, span * 0.15);
                min -= padding;
                max += padding;
                return new AxisRange
                {
                    Min = Math.Max(-20.0, min),
                    Max = Math.Min(120.0, Math.Max(min + 0.1, max))
                };
            }
        }
    }
}
