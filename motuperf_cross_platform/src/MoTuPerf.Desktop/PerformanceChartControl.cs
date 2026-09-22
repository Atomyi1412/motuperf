using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public sealed class PerformanceChartControl : Control
    {
        public static readonly StyledProperty<IReadOnlyList<PerfSample>> SamplesProperty =
            AvaloniaProperty.Register<PerformanceChartControl, IReadOnlyList<PerfSample>>(nameof(Samples));
        public static readonly StyledProperty<string> ModeProperty =
            AvaloniaProperty.Register<PerformanceChartControl, string>(nameof(Mode), "fps");
        public static readonly StyledProperty<double?> SelectedTimeProperty =
            AvaloniaProperty.Register<PerformanceChartControl, double?>(nameof(SelectedTime));
        public static readonly StyledProperty<bool> ShowFpsProperty =
            AvaloniaProperty.Register<PerformanceChartControl, bool>(nameof(ShowFps), true);
        public static readonly StyledProperty<bool> ShowJankProperty =
            AvaloniaProperty.Register<PerformanceChartControl, bool>(nameof(ShowJank), true);
        public static readonly StyledProperty<bool> ShowBigJankProperty =
            AvaloniaProperty.Register<PerformanceChartControl, bool>(nameof(ShowBigJank), true);
        public static readonly StyledProperty<double> ViewStartTimeProperty =
            AvaloniaProperty.Register<PerformanceChartControl, double>(nameof(ViewStartTime));
        public static readonly StyledProperty<double> ViewEndTimeProperty =
            AvaloniaProperty.Register<PerformanceChartControl, double>(nameof(ViewEndTime));
        public static readonly StyledProperty<bool> ZoomEnabledProperty =
            AvaloniaProperty.Register<PerformanceChartControl, bool>(nameof(ZoomEnabled));
        public static readonly StyledProperty<double> ThermalStateMaxProperty =
            AvaloniaProperty.Register<PerformanceChartControl, double>(nameof(ThermalStateMax), 3);
        public static readonly StyledProperty<string> ThermalStateTitleProperty =
            AvaloniaProperty.Register<PerformanceChartControl, string>(nameof(ThermalStateTitle), "Thermal State");
        public static readonly StyledProperty<string> ThermalStateLegendProperty =
            AvaloniaProperty.Register<PerformanceChartControl, string>(nameof(ThermalStateLegend), "0正常 / 1升温 / 2严重 / 3临界");
        public static readonly StyledProperty<string> ThermalStateWaitingTextProperty =
            AvaloniaProperty.Register<PerformanceChartControl, string>(nameof(ThermalStateWaitingText), "等待 Thermal State 数据");

        private static IBrush ThemeResourceBrush(string key, string fallback)
        {
            object value;
            if (Application.Current != null && Application.Current.TryFindResource(key, out value) && value is IBrush brush) return brush;
            return Brush.Parse(fallback);
        }

        private static IBrush BackgroundBrush { get { return ThemeResourceBrush("Theme.ChartBackground", "#161720"); } }
        private static IBrush GridBrush { get { return ThemeResourceBrush("Theme.ChartGrid", "#383A4A"); } }
        private static IBrush TextBrush { get { return ThemeResourceBrush("Theme.TextPrimary", "#D3D5E2"); } }
        private static IBrush MutedBrush { get { return ThemeResourceBrush("Theme.TextMuted", "#888B9C"); } }
        private static readonly IBrush[] CoreBrushes = CreateBrushes("#F3C44F", "#91DB72", "#4DB5E0", "#FF6B9A", "#F08A60", "#B78DEB", "#51D6CE", "#A7CF5A");
        private static readonly IBrush[] TemperatureBrushes = CreateBrushes("#F05A3C", "#4DB5E0", "#F3C44F", "#B78DEB", "#91DB72", "#51D6CE", "#FF6B9A", "#F08A60");
        private static readonly List<WeakReference<PerformanceChartControl>> Instances = new List<WeakReference<PerformanceChartControl>>();
        private readonly HashSet<string> _hiddenSeries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private double _viewStartTime;
        private double _viewEndTime;
        private Point? _dragStartPoint;
        private Point? _dragCurrentPoint;
        private bool _isRangeSelecting;
        private IReadOnlyList<PerfSample> _orderedSource;
        private IReadOnlyList<PerfSample> _orderedSamples = Array.Empty<PerfSample>();
        private double _cachedMaxTime = 60;
        private DrawingGroup _seriesDrawing;
        internal int SeriesRenderCount { get; private set; }

        public PerformanceChartControl()
        {
            Instances.Add(new WeakReference<PerformanceChartControl>(this));
        }

        static PerformanceChartControl()
        {
            AffectsRender<PerformanceChartControl>(SamplesProperty, ModeProperty, SelectedTimeProperty, ShowFpsProperty, ShowJankProperty, ShowBigJankProperty, ViewStartTimeProperty, ViewEndTimeProperty, ZoomEnabledProperty, ThermalStateMaxProperty, ThermalStateTitleProperty, ThermalStateLegendProperty, ThermalStateWaitingTextProperty);
        }

        public static void RefreshThemeResources()
        {
            for (int i = Instances.Count - 1; i >= 0; i--)
            {
                PerformanceChartControl chart;
                if (Instances[i].TryGetTarget(out chart)) { chart._seriesDrawing = null; chart.InvalidateVisual(); }
                else Instances.RemoveAt(i);
            }
        }

        public IReadOnlyList<PerfSample> Samples
        {
            get { return GetValue(SamplesProperty) ?? Array.Empty<PerfSample>(); }
            set { SetValue(SamplesProperty, value); }
        }
        public string Mode
        {
            get { return GetValue(ModeProperty); }
            set { SetValue(ModeProperty, value); }
        }
        public double? SelectedTime
        {
            get { return GetValue(SelectedTimeProperty); }
            set { SetValue(SelectedTimeProperty, value); }
        }
        public bool ShowFps
        {
            get { return GetValue(ShowFpsProperty); }
            set { SetValue(ShowFpsProperty, value); }
        }
        public bool ShowJank
        {
            get { return GetValue(ShowJankProperty); }
            set { SetValue(ShowJankProperty, value); }
        }
        public bool ShowBigJank
        {
            get { return GetValue(ShowBigJankProperty); }
            set { SetValue(ShowBigJankProperty, value); }
        }
        public double ViewStartTime
        {
            get { return GetValue(ViewStartTimeProperty); }
            set { SetValue(ViewStartTimeProperty, value); }
        }
        public double ViewEndTime
        {
            get { return GetValue(ViewEndTimeProperty); }
            set { SetValue(ViewEndTimeProperty, value); }
        }
        public bool ZoomEnabled
        {
            get { return GetValue(ZoomEnabledProperty); }
            set { SetValue(ZoomEnabledProperty, value); }
        }
        public double ThermalStateMax
        {
            get { return GetValue(ThermalStateMaxProperty); }
            set { SetValue(ThermalStateMaxProperty, value); }
        }
        public string ThermalStateTitle
        {
            get { return GetValue(ThermalStateTitleProperty); }
            set { SetValue(ThermalStateTitleProperty, value); }
        }
        public string ThermalStateLegend
        {
            get { return GetValue(ThermalStateLegendProperty); }
            set { SetValue(ThermalStateLegendProperty, value); }
        }
        public string ThermalStateWaitingText
        {
            get { return GetValue(ThermalStateWaitingTextProperty); }
            set { SetValue(ThermalStateWaitingTextProperty, value); }
        }

        public event Action<double> TimeSelected;
        public event Action<double, double> ZoomRangeSelected;
        public event Action<string, bool> SeriesVisibilityChanged;

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
            _seriesDrawing = null;
            InvalidateVisual();
            SeriesVisibilityChanged?.Invoke(key, visible);
            return visible;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Rect bounds = new Rect(Bounds.Size);
            context.DrawRectangle(BackgroundBrush, null, bounds);
            if (bounds.Width < 180 || bounds.Height < 90) return;

            Rect area = new Rect(60, 28, Math.Max(10, bounds.Width - 84), Math.Max(10, bounds.Height - 56));
            IReadOnlyList<PerfSample> samples = Samples;
            if (!ReferenceEquals(_orderedSource, samples))
            {
                _orderedSource = samples;
                _orderedSamples = (samples as SampleSnapshot)?.Ordered ?? Chronological(samples);
                _cachedMaxTime = _orderedSamples.Count == 0
                    ? 60
                    : Math.Max(1, _orderedSamples[_orderedSamples.Count - 1].ElapsedSec);
            }
            double maxTime = _cachedMaxTime;
            _viewStartTime = Math.Max(0, Math.Min(Math.Max(0, maxTime - 0.01), ViewStartTime));
            _viewEndTime = ViewEndTime > _viewStartTime + 0.01
                ? Math.Min(maxTime, ViewEndTime)
                : maxTime;
            if (_viewEndTime <= _viewStartTime) _viewStartTime = 0;
            string mode = (Mode ?? "fps").Trim().ToLowerInvariant();

            if (_seriesDrawing == null)
            {
                DrawingGroup drawing = new DrawingGroup();
                using (DrawingContext seriesContext = drawing.Open())
                {
                    DrawSeries(seriesContext, area, samples, maxTime, mode);
                }
                _seriesDrawing = drawing;
                SeriesRenderCount++;
            }
            _seriesDrawing.Draw(context);

            if (_isRangeSelecting) DrawRangeSelection(context, area);
            DrawCursor(context, area, maxTime);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != SelectedTimeProperty && change.Property != ZoomEnabledProperty)
                _seriesDrawing = null;
        }

        private void DrawSeries(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime, string mode)
        {
            if (mode == "fps") DrawFps(context, area, samples, maxTime);
            else if (mode == "frametime") DrawFrameTime(context, area, samples, maxTime);
            else if (mode == "memory") DrawMemory(context, area, samples, maxTime);
            else if (mode == "cpu") DrawSingle(context, area, samples, maxTime, "Process CPU Raw", "%", "#91DB72", 0, 100, 5, delegate(PerfSample sample) { return sample.CpuPercent; }, delegate(PerfSample sample) { return sample.HasCpu; }, 7);
            else if (mode == "cpunormalized") DrawSingle(context, area, samples, maxTime, "Process CPU Normalized", "%", "#4DB5E0", 0, 100, 5, delegate(PerfSample sample) { return sample.CpuNormalizedPercent; }, delegate(PerfSample sample) { return sample.HasCpuNormalized; }, 7);
            else if (mode == "corecpu") DrawCoreCpu(context, area, samples, maxTime);
            else if (mode == "temperature") DrawTemperature(context, area, samples, maxTime);
            else if (mode == "thermalstate") DrawThermalState(context, area, samples, maxTime);
            else DrawGrid(context, area, 0, 1, "", maxTime);

        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Point point = e.GetPosition(this);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (ZoomEnabled && !GetPlotArea().Contains(point)) return;
            e.Pointer.Capture(this);
            _dragStartPoint = point;
            _dragCurrentPoint = point;
            _isRangeSelecting = false;
            if (!ZoomEnabled) SelectTimeAt(point);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured == this && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                Point point = e.GetPosition(this);
                if (ZoomEnabled)
                {
                    _dragCurrentPoint = point;
                    if (!_isRangeSelecting && _dragStartPoint.HasValue && Math.Abs(point.X - _dragStartPoint.Value.X) >= 6)
                    {
                        _isRangeSelecting = true;
                    }
                }
                else SelectTimeAt(point);
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            Point point = e.GetPosition(this);
            bool wasRangeSelecting = _isRangeSelecting;
            Point? startPoint = _dragStartPoint;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            _dragStartPoint = null;
            _dragCurrentPoint = null;
            _isRangeSelecting = false;
            if (ZoomEnabled && wasRangeSelecting && startPoint.HasValue)
            {
                double start = TimeAtPoint(new Point(Math.Min(startPoint.Value.X, point.X), point.Y));
                double end = TimeAtPoint(new Point(Math.Max(startPoint.Value.X, point.X), point.Y));
                if (end > start + 0.01) ZoomRangeSelected?.Invoke(start, end);
            }
            else if (ZoomEnabled) SelectTimeAt(point);
            InvalidateVisual();
            e.Handled = true;
        }

        private void DrawFps(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            bool showFps = ShowFps && IsSeriesVisible("FPS");
            bool showJank = ShowJank && IsSeriesVisible("Jank");
            bool showBigJank = ShowBigJank && IsSeriesVisible("BigJank");
            IReadOnlyList<PerfSample> all = OrderedFor(samples);
            IReadOnlyList<PerfSample> visible = VisibleSamples(samples);
            List<double> values = showFps ? all.Where(delegate(PerfSample sample) { return sample.HasFps; }).Select(delegate(PerfSample sample) { return sample.Fps; }).ToList() : new List<double>();
            if (showJank) values.AddRange(all.Where(delegate(PerfSample sample) { return sample.HasJank; }).Select(delegate(PerfSample sample) { return sample.Jank; }));
            if (showBigJank) values.AddRange(all.Where(delegate(PerfSample sample) { return sample.HasJank; }).Select(delegate(PerfSample sample) { return sample.BigJank; }));
            AxisRange range = AxisRange.FromValues(values, 0, 60, 5);
            DrawGrid(context, area, range.Min, range.Max, "帧/s", maxTime);
            if (showFps) DrawLine(context, area, samples, maxTime, range, Brush.Parse("#FF6B9A"), 2, 3, delegate(PerfSample sample) { return sample.Fps; }, delegate(PerfSample sample) { return sample.HasFps; }, false, true);
            if (showJank) DrawLine(context, area, samples, maxTime, range, Brush.Parse("#4DB5E0"), 1.4, 3, delegate(PerfSample sample) { return sample.Jank; }, delegate(PerfSample sample) { return sample.HasJank; }, false, true);
            if (showBigJank) DrawLine(context, area, samples, maxTime, range, Brush.Parse("#91DB72"), 1.4, 3, delegate(PerfSample sample) { return sample.BigJank; }, delegate(PerfSample sample) { return sample.HasJank; }, false, true);
            DrawText(context, "FPS / Jank / BigJank", area.Left, 6, 13, TextBrush);
            if (values.Count == 0) DrawWaiting(context, area, showFps || showJank || showBigJank ? "等待 FPS / Jank 数据" : "已隐藏全部曲线");
        }

        private void DrawFrameTime(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            Func<PerfSample, bool> has = delegate(PerfSample sample) { return FrameTimeChartProjection.IsChartSample(sample); };
            IReadOnlyList<PerfSample> all = OrderedFor(samples);
            IReadOnlyList<PerfSample> visible = VisibleSamples(samples);
            AxisRange range = AxisRange.FromValues(all.Where(has).Select(delegate(PerfSample sample) { return sample.FrameTimeMaxMs; }), 0, 35, 3);
            DrawGrid(context, area, range.Min, range.Max, "ms", maxTime);
            DrawLine(context, area, samples, maxTime, range, Brush.Parse("#FF6B9A"), 2, 3, delegate(PerfSample sample) { return sample.FrameTimeMaxMs; }, has, true, false);
            DrawText(context, "Display FrameTime (Max)", area.Left, 6, 13, TextBrush);
            if (!visible.Any(has)) DrawWaiting(context, area, "等待 Display FrameTime 数据");
        }

        private void DrawMemory(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            string preferred = MetricStatistics.PreferredMemoryMetric(samples);
            Func<PerfSample, bool> has = delegate(PerfSample sample)
            {
                return sample != null && sample.HasMemory && sample.MemoryMb > 0 && MemoryMetricMatches(sample.MemoryMetric, preferred);
            };
            IReadOnlyList<PerfSample> all = OrderedFor(samples);
            IReadOnlyList<PerfSample> visible = VisibleSamples(samples);
            AxisRange range = AxisRange.FromValues(all.Where(has).Select(delegate(PerfSample sample) { return sample.MemoryMb; }), 0, 100, 50);
            DrawGrid(context, area, range.Min, range.Max, "MB", maxTime);
            DrawLine(context, area, samples, maxTime, range, Brush.Parse("#F3C44F"), 2, 7, delegate(PerfSample sample) { return sample.MemoryMb; }, has, false, false);
            DrawText(context, "Process Memory" + (string.IsNullOrWhiteSpace(preferred) ? "" : " · " + preferred), area.Left, 6, 13, TextBrush);
            if (!visible.Any(has)) DrawWaiting(context, area, "等待进程内存数据");
        }

        private void DrawSingle(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime, string title, string unit, string color, double floor, double defaultMax, double padding, Func<PerfSample, double> value, Func<PerfSample, bool> has, double maxGap)
        {
            IReadOnlyList<PerfSample> all = OrderedFor(samples);
            IReadOnlyList<PerfSample> visible = VisibleSamples(samples);
            AxisRange range = AxisRange.FromValues(all.Where(has).Select(value), floor, defaultMax, padding);
            DrawGrid(context, area, range.Min, range.Max, unit, maxTime);
            DrawLine(context, area, samples, maxTime, range, Brush.Parse(color), 2, maxGap, value, has, false, false);
            DrawText(context, title, area.Left, 6, 13, TextBrush);
            if (!visible.Any(has)) DrawWaiting(context, area, "等待" + title + "数据");
        }

        private void DrawCoreCpu(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            IReadOnlyList<PerfSample> all = OrderedFor(samples);
            int count = all.Where(delegate(PerfSample sample) { return sample.HasCpuCoreUsage; }).Select(delegate(PerfSample sample) { return sample.CpuCoreCount; }).DefaultIfEmpty(0).Max();
            IReadOnlyList<PerfSample> visible = VisibleSamples(samples);
            List<double> values = new List<double>();
            for (int index = 0; index < count; index++)
            {
                if (!IsSeriesVisible(CoreSeriesName(index))) continue;
                int core = index;
                values.AddRange(all.Where(delegate(PerfSample sample) { return sample.HasCpuCoreUsage && sample.CpuCorePercents != null && sample.CpuCorePercents.Count > core; }).Select(delegate(PerfSample sample) { return sample.CpuCorePercents[core]; }));
            }
            AxisRange range = AxisRange.FromValues(values, 0, 100, 5);
            DrawGrid(context, area, range.Min, range.Max, "%", maxTime);
            for (int index = 0; index < count; index++)
            {
                if (!IsSeriesVisible(CoreSeriesName(index))) continue;
                int core = index;
                DrawLine(context, area, samples, maxTime, range, CoreBrushes[core % CoreBrushes.Length], 1.3, 7,
                    delegate(PerfSample sample) { return sample.CpuCorePercents[core]; },
                    delegate(PerfSample sample) { return sample.HasCpuCoreUsage && sample.CpuCorePercents != null && sample.CpuCorePercents.Count > core; }, false, false);
            }
            DrawText(context, "Device Core CPU" + (count > 0 ? " · " + count + " cores" : ""), area.Left, 6, 13, TextBrush);
            if (values.Count == 0) DrawWaiting(context, area, count == 0 ? "等待设备逐核 CPU 数据" : "已隐藏全部曲线");
        }

        private void DrawTemperature(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            List<string> sensors = TemperatureSensorNames(samples);
            IReadOnlyList<PerfSample> all = OrderedFor(samples);
            IReadOnlyList<PerfSample> visible = VisibleSamples(samples);
            List<double> values = new List<double>();
            foreach (string sensor in sensors)
            {
                if (!IsSeriesVisible(TemperatureSeriesName(sensor))) continue;
                values.AddRange(all.Where(delegate(PerfSample sample) { return HasTemperatureValue(sample, sensor); }).Select(delegate(PerfSample sample) { return sample.TemperatureCelsius[sensor]; }));
            }
            AxisRange range = AxisRange.FromTemperatureValues(values);
            DrawGrid(context, area, range.Min, range.Max, "°C", maxTime);
            for (int index = 0; index < sensors.Count; index++)
            {
                string sensor = sensors[index];
                if (!IsSeriesVisible(TemperatureSeriesName(sensor))) continue;
                IReadOnlyList<PerfSample> projected = TemperatureChartProjection.ForSensor(samples, sensor, maxTime);
                DrawLine(context, area, projected, maxTime, range, TemperatureBrushes[index % TemperatureBrushes.Length], 1.7, 12,
                    delegate(PerfSample sample) { return sample.TemperatureCelsius[sensor]; },
                    delegate(PerfSample sample) { return HasTemperatureValue(sample, sensor); }, false, false);
            }
            DrawText(context, "Device Temperature" + (sensors.Count > 0 ? " · " + string.Join(" / ", sensors) : ""), area.Left, 6, 13, TextBrush);
            if (values.Count == 0) DrawWaiting(context, area, sensors.Count == 0 ? "等待设备温度数据" : "已隐藏全部曲线");
        }

        private void DrawThermalState(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            IReadOnlyList<PerfSample> projected = ThermalStateChartProjection.ForChart(samples, maxTime);
            double thermalMax = Math.Max(1, ThermalStateMax);
            AxisRange range = new AxisRange { Min = 0, Max = thermalMax };
            DrawGrid(context, area, 0, thermalMax, "级别", maxTime);
            DrawLine(context, area, projected, maxTime, range, Brush.Parse("#F08A60"), 2, 7,
                delegate(PerfSample sample) { return sample.ThermalStateLevel; },
                delegate(PerfSample sample) { return sample.HasThermalState; }, false, false, true);
            DrawText(context, ThermalStateTitle + " · " + ThermalStateLegend, area.Left, 6, 13, TextBrush);
            if (!VisibleSamples(projected).Any(delegate(PerfSample sample) { return sample.HasThermalState; })) DrawWaiting(context, area, ThermalStateWaitingText);
        }

        private void DrawLine(DrawingContext context, Rect area, IReadOnlyList<PerfSample> source, double maxTime, AxisRange range, IBrush brush, double width, double maxGap, Func<PerfSample, double> value, Func<PerfSample, bool> has, bool breakFrameSeries, bool allowMeasuredFpsGap, bool step = false)
        {
            List<PerfSample> samples = OrderedFor(source)
                .Where(has)
                .Where(delegate(PerfSample sample) { return sample.ElapsedSec >= _viewStartTime && sample.ElapsedSec <= _viewEndTime; })
                .ToList();
            if (samples.Count == 0) return;
            Pen pen = new Pen(brush, width);
            IReadOnlyList<IReadOnlyList<PerfSample>> segments = ChartSeriesProjection.BuildSegments(
                samples, 2400, value, maxGap, breakFrameSeries, allowMeasuredFpsGap, true);
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                foreach (IReadOnlyList<PerfSample> segment in segments)
                {
                    Point? previousPoint = null;
                    foreach (PerfSample sample in segment)
                    {
                        Point point = ToPoint(area, maxTime, range, sample.ElapsedSec, value(sample));
                        if (previousPoint.HasValue)
                        {
                            if (step) path.LineTo(new Point(point.X, previousPoint.Value.Y));
                            path.LineTo(point);
                        }
                        else
                        {
                            path.BeginFigure(point, false);
                            context.DrawEllipse(brush, null, point, 2.4, 2.4);
                        }
                        previousPoint = point;
                    }
                    path.EndFigure(false);
                }
            }
            context.DrawGeometry(null, pen, geometry);
        }

        private IReadOnlyList<PerfSample> VisibleSamples(IReadOnlyList<PerfSample> samples)
        {
            return OrderedFor(samples)
                .Where(delegate(PerfSample sample) { return sample.ElapsedSec >= _viewStartTime && sample.ElapsedSec <= _viewEndTime; })
                .ToList();
        }

        private IReadOnlyList<PerfSample> OrderedFor(IReadOnlyList<PerfSample> samples)
        {
            return ReferenceEquals(samples, Samples) ? _orderedSamples : Chronological(samples);
        }

        private Rect GetPlotArea()
        {
            return new Rect(60, 28, Math.Max(10, Bounds.Width - 84), Math.Max(10, Bounds.Height - 56));
        }

        private void DrawRangeSelection(DrawingContext context, Rect area)
        {
            if (!_dragStartPoint.HasValue || !_dragCurrentPoint.HasValue) return;
            double left = Math.Max(area.Left, Math.Min(area.Right, Math.Min(_dragStartPoint.Value.X, _dragCurrentPoint.Value.X)));
            double right = Math.Max(area.Left, Math.Min(area.Right, Math.Max(_dragStartPoint.Value.X, _dragCurrentPoint.Value.X)));
            if (right <= left) return;
            IBrush accent = ThemeResourceBrush("Theme.Accent", "#4DB5E0");
            SolidColorBrush fill = new SolidColorBrush(Color.FromArgb(48, 77, 181, 224));
            SolidColorBrush solidAccent = accent as SolidColorBrush;
            if (solidAccent != null)
            {
                fill = new SolidColorBrush(Color.FromArgb(48, solidAccent.Color.R, solidAccent.Color.G, solidAccent.Color.B));
            }
            context.DrawRectangle(fill, new Pen(accent, 1), new Rect(left, area.Top, right - left, area.Height));
        }

        private void DrawGrid(DrawingContext context, Rect area, double min, double max, string unit, double maxTime)
        {
            Pen grid = new Pen(GridBrush, 1);
            for (int index = 0; index <= 6; index++)
            {
                double x = area.Left + area.Width * index / 6.0;
                context.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
                DrawText(context, FormatSeconds(_viewStartTime + (_viewEndTime - _viewStartTime) * index / 6.0), x - 10, area.Bottom + 5, 11, MutedBrush);
            }
            for (int index = 0; index <= 4; index++)
            {
                double y = area.Top + area.Height * index / 4.0;
                context.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
                DrawText(context, FormatValue(max - (max - min) * index / 4.0), 7, y - 8, 11, MutedBrush);
            }
            DrawTextRight(context, unit, area.Left - 8, area.Top - 19, 11, MutedBrush);
            context.DrawRectangle(null, new Pen(ThemeResourceBrush("Theme.BorderStrong", "#686B7B"), 1), area);
        }

        internal static string CoreSeriesName(int coreIndex) { return "CPU" + Math.Max(0, coreIndex).ToString(CultureInfo.InvariantCulture); }
        internal static IBrush CoreCpuBrush(int coreIndex) { return CoreBrushes[Math.Max(0, coreIndex) % CoreBrushes.Length]; }
        internal static string TemperatureSeriesName(string sensorName) { return "Temperature " + (sensorName ?? "").Trim(); }
        internal static IBrush TemperatureBrush(int sensorIndex) { return TemperatureBrushes[Math.Max(0, sensorIndex) % TemperatureBrushes.Length]; }

        private void DrawCursor(DrawingContext context, Rect area, double maxTime)
        {
            if (!SelectedTime.HasValue) return;
            double selected = Math.Max(_viewStartTime, Math.Min(_viewEndTime, SelectedTime.Value));
            double x = area.Left + (selected - _viewStartTime) / Math.Max(0.01, _viewEndTime - _viewStartTime) * area.Width;
            IBrush cursorBrush = ThemeResourceBrush("Theme.Cursor", "#FFFFFF");
            context.DrawLine(new Pen(cursorBrush, 1), new Point(x, area.Top), new Point(x, area.Bottom));
            DrawText(context, FormatSeconds(selected), x + 6, area.Top + 6, 12, cursorBrush);
        }

        private void SelectTimeAt(Point point)
        {
            if (Samples.Count == 0) return;
            Rect area = new Rect(60, 28, Math.Max(10, Bounds.Width - 84), Math.Max(10, Bounds.Height - 56));
            if (point.Y < area.Top - 18 || point.Y > area.Bottom + 28) return;
            double x = Math.Max(area.Left, Math.Min(area.Right, point.X));
            TimeSelected?.Invoke(TimeAtPoint(point));
        }

        private Point ToPoint(Rect area, double maxTime, AxisRange range, double elapsed, double value)
        {
            double x = area.Left + (Math.Max(_viewStartTime, Math.Min(_viewEndTime, elapsed)) - _viewStartTime) / Math.Max(0.01, _viewEndTime - _viewStartTime) * area.Width;
            double ratio = (value - range.Min) / Math.Max(0.0001, range.Max - range.Min);
            double y = area.Bottom - Math.Max(0, Math.Min(1, ratio)) * area.Height;
            return new Point(x, y);
        }

        private double TimeAtPoint(Point point)
        {
            Rect area = new Rect(60, 28, Math.Max(10, Bounds.Width - 84), Math.Max(10, Bounds.Height - 56));
            double ratio = (Math.Max(area.Left, Math.Min(area.Right, point.X)) - area.Left) / Math.Max(1, area.Width);
            return Math.Max(_viewStartTime, Math.Min(_viewEndTime, _viewStartTime + ratio * (_viewEndTime - _viewStartTime)));
        }

        private static List<PerfSample> Chronological(IReadOnlyList<PerfSample> samples)
        {
            List<PerfSample> list = samples == null ? new List<PerfSample>() : samples.Where(delegate(PerfSample sample) { return sample != null; }).ToList();
            return list.OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; }).ToList();
        }

        private static bool MemoryMetricMatches(string sampleMetric, string preferredMetric)
        {
            string sample = (sampleMetric ?? "").Trim().ToLowerInvariant();
            string preferred = (preferredMetric ?? "").Trim().ToLowerInvariant();
            if (sample == "footprint") sample = "physical_footprint";
            if (preferred == "footprint") preferred = "physical_footprint";
            return sample == preferred;
        }

        internal static List<string> TemperatureSensorNames(IEnumerable<PerfSample> samples)
        {
            string[] preferred = { "CPU", "GPU", "Battery", "Skin", "NPU", "USB", "Power Amplifier" };
            return (samples ?? Enumerable.Empty<PerfSample>()).Where(delegate(PerfSample sample) { return sample.HasTemperature && sample.TemperatureCelsius != null; }).SelectMany(delegate(PerfSample sample) { return sample.TemperatureCelsius.Keys; }).Where(delegate(string name) { return !string.IsNullOrWhiteSpace(name); }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(delegate(string name)
            {
                int index = Array.FindIndex(preferred, delegate(string value) { return string.Equals(value, name, StringComparison.OrdinalIgnoreCase); });
                return index < 0 ? int.MaxValue : index;
            }).ThenBy(delegate(string name) { return name; }, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool HasTemperatureValue(PerfSample sample, string sensor)
        {
            double value;
            return sample != null && sample.HasTemperature && sample.TemperatureCelsius != null && sample.TemperatureCelsius.TryGetValue(sensor, out value) && !double.IsNaN(value) && !double.IsInfinity(value) && value >= -20 && value <= 120;
        }

        private static void DrawWaiting(DrawingContext context, Rect area, string text) { DrawText(context, text, area.Left + area.Width / 2 - 70, area.Top + area.Height / 2 - 8, 13, MutedBrush); }
        private static void DrawText(DrawingContext context, string text, double x, double y, double size, IBrush brush) { context.DrawText(new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Inter, Microsoft YaHei UI"), size, brush), new Point(x, y)); }
        private static void DrawTextRight(DrawingContext context, string text, double right, double y, double size, IBrush brush)
        {
            FormattedText formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Inter, Microsoft YaHei UI"), size, brush);
            context.DrawText(formatted, new Point(right - formatted.Width, y));
        }
        private static string FormatSeconds(double seconds) { TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds)); return span.TotalMinutes >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)span.TotalMinutes, span.Seconds) : string.Format(CultureInfo.InvariantCulture, "{0:0}s", seconds); }
        private static string FormatValue(double value) { return Math.Abs(value) >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : Math.Abs(value) >= 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture); }
        private static IBrush[] CreateBrushes(params string[] colors) { return colors.Select(Brush.Parse).ToArray(); }

        private struct AxisRange
        {
            public double Min;
            public double Max;
            public static AxisRange FromValues(IEnumerable<double> values, double floor, double defaultMax, double padding)
            {
                List<double> list = (values ?? Enumerable.Empty<double>()).Where(delegate(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }).ToList();
                if (list.Count == 0) return new AxisRange { Min = floor, Max = defaultMax };
                double min = Math.Min(list.Min(), floor);
                double max = Math.Max(list.Max(), defaultMax);
                double extra = Math.Abs(max - min) < 0.001 ? Math.Max(1, padding) : Math.Max(padding, (max - min) * 0.12);
                return new AxisRange { Min = Math.Max(floor, min - extra), Max = max + extra };
            }
            public static AxisRange FromTemperatureValues(IEnumerable<double> values)
            {
                List<double> list = (values ?? Enumerable.Empty<double>()).Where(delegate(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= -20 && value <= 120; }).ToList();
                if (list.Count == 0) return new AxisRange { Min = 20, Max = 50 };
                double min = list.Min();
                double max = list.Max();
                double padding = Math.Max(2, (max - min) * 0.15);
                return new AxisRange { Min = Math.Max(-20, min - padding), Max = Math.Min(120, Math.Max(min + 0.1, max + padding)) };
            }
        }
    }

    internal static class ChartSeriesProjection
    {
        public static IReadOnlyList<IReadOnlyList<PerfSample>> BuildSegments(
            IEnumerable<PerfSample> source,
            int maxPoints,
            Func<PerfSample, double> value,
            double maxGap,
            bool breakFrameSeries,
            bool allowMeasuredFpsGap,
            bool alreadyOrdered = false)
        {
            List<PerfSample> samples = (source ?? Enumerable.Empty<PerfSample>())
                .Where(delegate(PerfSample sample) { return sample != null; })
                .ToList();
            if (!alreadyOrdered) samples = samples.OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; }).ToList();
            List<List<PerfSample>> rawSegments = new List<List<PerfSample>>();
            foreach (PerfSample sample in samples)
            {
                List<PerfSample> current = rawSegments.Count == 0 ? null : rawSegments[rawSegments.Count - 1];
                PerfSample previous = current == null || current.Count == 0 ? null : current[current.Count - 1];
                if (previous == null || ShouldBreak(previous, sample, maxGap, breakFrameSeries, allowMeasuredFpsGap))
                {
                    current = new List<PerfSample>();
                    rawSegments.Add(current);
                }
                current.Add(sample);
            }

            int perSegmentLimit = rawSegments.Count == 0 ? maxPoints : Math.Max(4, maxPoints / rawSegments.Count);
            return rawSegments
                .Select(delegate(List<PerfSample> segment) { return (IReadOnlyList<PerfSample>)Decimate(segment, perSegmentLimit, value); })
                .ToList();
        }

        private static bool ShouldBreak(PerfSample previous, PerfSample current, double maxGap, bool breakFrameSeries, bool allowMeasuredFpsGap)
        {
            // FPS callers deliberately pass breakFrameSeries=false: valid values stay visually
            // continuous while source discontinuities remain available to statistics/export.
            if (breakFrameSeries && (current.FrameSourceSequenceDiscontinuity || !MetricStatistics.IsSameFrameSeries(previous, current))) return true;
            if (current.ElapsedSec - previous.ElapsedSec <= maxGap) return false;
            return !(allowMeasuredFpsGap && AllowsMeasuredGap(previous, current));
        }

        private static bool AllowsMeasuredGap(PerfSample previous, PerfSample current)
        {
            return previous.NoPresentFrames || current.NoPresentFrames || previous.ResumeGapMs > 0 || current.ResumeGapMs > 0 || previous.HasFrameSourceSequence || current.HasFrameSourceSequence;
        }

        private static List<PerfSample> Decimate(List<PerfSample> samples, int maxPoints, Func<PerfSample, double> value)
        {
            if (samples.Count <= maxPoints) return samples;
            int bucketSize = Math.Max(1, (int)Math.Ceiling(samples.Count / Math.Max(2.0, maxPoints / 2.0)));
            List<PerfSample> result = new List<PerfSample> { samples[0] };
            for (int start = 1; start < samples.Count - 1; start += bucketSize)
            {
                int end = Math.Min(start + bucketSize, samples.Count - 1);
                int min = start, max = start;
                for (int i = start + 1; i < end; i++)
                {
                    if (value(samples[i]) < value(samples[min])) min = i;
                    if (value(samples[i]) > value(samples[max])) max = i;
                }
                result.Add(samples[Math.Min(min, max)]);
                if (min != max) result.Add(samples[Math.Max(min, max)]);
            }
            result.Add(samples[samples.Count - 1]);
            return result;
        }
    }
}
