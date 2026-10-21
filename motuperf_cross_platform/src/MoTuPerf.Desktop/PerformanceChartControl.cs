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
        private readonly ChartDataCache _data = new ChartDataCache();
        private bool _hasDiscontinuity;
        private bool _denseDiscontinuities;
        internal int SeriesRenderCount { get; private set; }
        internal int IndexedSampleCount => _data.InspectedSamples;

        internal List<string> IndexedSeriesKeys(IReadOnlyList<PerfSample> samples, string mode)
        {
            _data.Update(samples ?? Array.Empty<PerfSample>(), mode);
            return _data.Keys.ToList();
        }

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

            Rect area = GetPlotArea();
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
            _data.Update(samples, mode);

            if (_seriesDrawing == null)
            {
                _hasDiscontinuity = false;
                _denseDiscontinuities = false;
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
            AxisRange range = RangeFor(new[] { _data.Get("FPS") }, 0, 60, 5);
            AxisRange counts = RangeFor(new[] { _data.Get("Jank"), _data.Get("BigJank") }, 0, 1, 0);
            counts.Max = Math.Max(1, Math.Ceiling(counts.Max));
            DrawGrid(context, area, range.Min, range.Max, "帧/s", maxTime);
            if (showJank || showBigJank) DrawCountAxis(context, area, counts.Max);
            if (showFps) DrawIndexed(context, area, "FPS", range, Brush.Parse("#FF6B9A"), 2, x => x.Fps, frames: true);
            if (showJank) DrawIndexed(context, area, "Jank", counts, Brush.Parse("#4DB5E0"), 1.4, x => x.Jank, frames: true);
            if (showBigJank) DrawIndexed(context, area, "BigJank", counts, Brush.Parse("#91DB72"), 1.4, x => x.BigJank, frames: true);
            DrawTitle(context, area, "FPS（左轴） / Jank、BigJank（右轴：次/窗口）");
            if (!showFps && !showJank && !showBigJank) DrawWaiting(context, area, "已隐藏全部曲线");
            else if (!(showFps && HasVisible("FPS")) && !(showJank && HasVisible("Jank")) && !(showBigJank && HasVisible("BigJank"))) DrawWaiting(context, area, "当前区间无 FPS / Jank 数据");
        }

        private void DrawFrameTime(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            AxisRange range = RangeFor(new[] { _data.Get("FrameTime") }, 0, 35, 3);
            DrawGrid(context, area, range.Min, range.Max, "ms", maxTime);
            DrawIndexed(context, area, "FrameTime", range, Brush.Parse("#FF6B9A"), 2, x => x.FrameTimeMaxMs, frames: true);
            DrawTitle(context, area, "Display FrameTime (Max)");
            if (!HasVisible("FrameTime")) DrawWaiting(context, area, "当前区间无 Display FrameTime 数据");
        }

        private void DrawMemory(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            string preferred = _data.PreferredMemory;
            AxisRange range = RangeFor(new[] { _data.Get(preferred) }, 0, 100, 50);
            DrawGrid(context, area, range.Min, range.Max, "MB", maxTime);
            DrawIndexed(context, area, preferred, range, Brush.Parse("#F3C44F"), 2, x => x.MemoryMb);
            DrawTitle(context, area, "Process Memory" + (string.IsNullOrWhiteSpace(preferred) ? "" : " · " + preferred));
            if (!HasVisible(preferred)) DrawWaiting(context, area, "当前区间无进程内存数据");
        }

        private void DrawSingle(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime, string title, string unit, string color, double floor, double defaultMax, double padding, Func<PerfSample, double> value, Func<PerfSample, bool> has, double maxGap)
        {
            AxisRange range = RangeFor(new[] { _data.Get("CPU") }, floor, defaultMax, padding);
            DrawGrid(context, area, range.Min, range.Max, unit, maxTime);
            DrawIndexed(context, area, "CPU", range, Brush.Parse(color), 2, value);
            DrawTitle(context, area, title);
            if (!HasVisible("CPU")) DrawWaiting(context, area, "当前区间无" + title + "数据");
        }

        private void DrawCoreCpu(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            int count = _data.Keys.Count();
            AxisRange range = RangeFor(_data.Keys.Select(_data.Get), 0, 100, 5);
            DrawGrid(context, area, range.Min, range.Max, "%", maxTime);
            for (int index = 0; index < count; index++)
            {
                if (!IsSeriesVisible(CoreSeriesName(index))) continue;
                int core = index;
                DrawIndexed(context, area, CoreSeriesName(core), range, CoreBrushes[core % CoreBrushes.Length], 1.3, x => x.CpuCorePercents[core]);
            }
            DrawTitle(context, area, "Device Core CPU" + (count > 0 ? " · " + count + " cores" : ""));
            if (!_data.Keys.Any(IsSeriesVisible)) DrawWaiting(context, area, count == 0 ? "等待设备逐核 CPU 数据" : "已隐藏全部曲线");
        }

        private void DrawTemperature(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            List<string> sensors = SortTemperatureSensorNames(_data.Keys);
            AxisRange range = AxisRange.FromTemperatureValues(Extrema(_data.Keys.Select(_data.Get)));
            DrawGrid(context, area, range.Min, range.Max, "°C", maxTime);
            for (int index = 0; index < sensors.Count; index++)
            {
                string sensor = sensors[index];
                if (!IsSeriesVisible(TemperatureSeriesName(sensor))) continue;
                DrawIndexed(context, area, sensor, range, TemperatureBrushes[index % TemperatureBrushes.Length], 1.7,
                    x => ChartDataCache.TemperatureValue(x, sensor), project: (points, end) => TemperatureChartProjection.ForSensor(points, sensor, end));
            }
            DrawTitle(context, area, "Device Temperature" + (sensors.Count > 0 ? " · " + string.Join(" / ", sensors) : ""));
            if (!sensors.Any(x => IsSeriesVisible(TemperatureSeriesName(x)))) DrawWaiting(context, area, sensors.Count == 0 ? "等待设备温度数据" : "已隐藏全部曲线");
        }

        private void DrawThermalState(DrawingContext context, Rect area, IReadOnlyList<PerfSample> samples, double maxTime)
        {
            double thermalMax = Math.Max(1, ThermalStateMax);
            AxisRange range = new AxisRange { Min = 0, Max = thermalMax };
            DrawGrid(context, area, 0, thermalMax, "级别", maxTime, true);
            DrawIndexed(context, area, "Thermal", range, Brush.Parse("#F08A60"), 2, x => x.ThermalStateLevel,
                step: true, project: (points, end) => ThermalStateChartProjection.ForChart(points, end));
            DrawTitle(context, area, ThermalStateTitle + " · " + ThermalStateLegend);
            if (!HasVisible("Thermal", ThermalStateMax > 3 ? ThermalStateChartProjection.AndroidDisplayFreshSeconds : ThermalStateChartProjection.DisplayFreshSeconds)) DrawWaiting(context, area, ThermalStateWaitingText);
        }

        private static IEnumerable<double> Extrema(IEnumerable<ChartSeriesCache> series)
        {
            foreach (var entry in series)
                if (entry != null && entry.Samples.Count > 0) { yield return entry.Min; yield return entry.Max; }
        }

        private static AxisRange RangeFor(IEnumerable<ChartSeriesCache> series, double floor, double defaultMax, double padding)
            => AxisRange.FromValues(Extrema(series), floor, defaultMax, padding);

        private bool HasVisible(string key, double freshness = 0)
        {
            var entry = _data.Get(key);
            if (entry == null) return false;
            int index = ChartSeriesCache.LowerBound(entry.Samples, _viewStartTime);
            return (index < entry.Samples.Count && entry.Samples[index].ElapsedSec <= _viewEndTime)
                || (freshness > 0 && entry.Samples.Count > 0 && entry.Samples[entry.Samples.Count - 1].ElapsedSec <= _viewEndTime
                    && entry.Samples[entry.Samples.Count - 1].ElapsedSec + freshness >= _viewStartTime);
        }

        private void DrawIndexed(DrawingContext context, Rect area, string key, AxisRange range, IBrush brush, double width,
            Func<PerfSample, double> value, bool frames = false, bool step = false,
            Func<IEnumerable<PerfSample>, double, List<PerfSample>> project = null)
        {
            var entry = _data.Get(key);
            if (entry == null || entry.Samples.Count == 0) return;
            IReadOnlyList<IReadOnlyList<PerfSample>> segments = entry.Project(project == null ? _viewStartTime : Math.Max(0, _viewStartTime - 12), _viewEndTime, (int)Math.Ceiling(area.Width));
            _denseDiscontinuities |= entry.DenseDiscontinuities;
            StreamGeometry geometry = new StreamGeometry();
            using var clip = context.PushClip(area);
            using (StreamGeometryContext path = geometry.Open())
            {
                foreach (IReadOnlyList<PerfSample> realSegment in segments)
                {
                    IReadOnlyList<PerfSample> segment = realSegment;
                    if (project != null)
                    {
                        bool tail = ReferenceEquals(realSegment[realSegment.Count - 1], entry.Samples[entry.Samples.Count - 1]);
                        segment = project(realSegment, tail ? _cachedMaxTime : realSegment[realSegment.Count - 1].ElapsedSec);
                    }
                    Point? previousPoint = null;
                    foreach (PerfSample sample in segment)
                    {
                        Point point = ToPoint(area, _cachedMaxTime, range, sample.ElapsedSec, value(sample));
                        if (previousPoint.HasValue)
                        {
                            if (step) path.LineTo(new Point(point.X, previousPoint.Value.Y));
                            path.LineTo(point);
                        }
                        else
                        {
                            path.BeginFigure(point, false);
                            if (segment.Count == 1) context.DrawEllipse(brush, null, point, 2.4, 2.4);
                        }
                        previousPoint = point;
                    }
                    path.EndFigure(false);
                }
            }
            context.DrawGeometry(null, new Pen(brush, width), geometry);
            if (frames)
            {
                var seen = new HashSet<int>();
                foreach (double time in entry.Discontinuities(_viewStartTime, _viewEndTime, (int)Math.Ceiling(area.Width)))
                {
                    double x = area.Left + (time - _viewStartTime) / Math.Max(0.01, _viewEndTime - _viewStartTime) * area.Width;
                    if (!seen.Add((int)x)) continue;
                    _hasDiscontinuity = true;
                    context.DrawLine(new Pen(MutedBrush, 1, DashStyle.Dash), new Point(x, area.Top), new Point(x, area.Bottom));
                }
            }
        }

        private Rect GetPlotArea()
        {
            return new Rect(60, 28, Math.Max(10, Bounds.Width - 112), Math.Max(10, Bounds.Height - 56));
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

        private void DrawGrid(DrawingContext context, Rect area, double min, double max, string unit, double maxTime, bool integerTicks = false)
        {
            Pen grid = new Pen(GridBrush, 1);
            for (int index = 0; index <= 6; index++)
            {
                double x = area.Left + area.Width * index / 6.0;
                context.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
                string label = FormatSeconds(_viewStartTime + (_viewEndTime - _viewStartTime) * index / 6.0);
                DrawTextWithin(context, label, x - 10, area.Bottom + 5, 11, MutedBrush);
            }
            int ticks = integerTicks ? Math.Max(1, (int)Math.Round(max - min)) : 4;
            for (int index = 0; index <= ticks; index++)
            {
                double y = area.Top + area.Height * index / ticks;
                context.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
                DrawText(context, FormatValue(max - (max - min) * index / ticks), 7, y - 8, 11, MutedBrush);
            }
            DrawTextRight(context, unit, area.Left - 8, area.Top - 19, 11, MutedBrush);
            context.DrawRectangle(null, new Pen(ThemeResourceBrush("Theme.BorderStrong", "#686B7B"), 1), area);
        }

        internal static string CoreSeriesName(int coreIndex) { return "CPU" + Math.Max(0, coreIndex).ToString(CultureInfo.InvariantCulture); }
        internal static IBrush CoreCpuBrush(int coreIndex) { return CoreBrushes[Math.Max(0, coreIndex) % CoreBrushes.Length]; }
        internal static string TemperatureSeriesName(string sensorName) { return "Temperature " + (sensorName ?? "").Trim(); }
        internal static IBrush TemperatureBrush(int sensorIndex) { return TemperatureBrushes[Math.Max(0, sensorIndex) % TemperatureBrushes.Length]; }

        private void DrawCountAxis(DrawingContext context, Rect area, double max)
        {
            int interval = Math.Max(1, (int)Math.Ceiling(max / 4));
            for (int value = 0; value <= max; value += interval)
            {
                double y = area.Bottom - area.Height * value / max;
                DrawTextWithin(context, value.ToString("0", CultureInfo.InvariantCulture), area.Right + 5, y - 8, 11, MutedBrush);
            }
        }

        private void DrawTitle(DrawingContext context, Rect area, string title)
        {
            string full = title + (_hasDiscontinuity ? " · 虚线：来源切换/采集缺口" : "")
                + (_denseDiscontinuities ? " · 断点密集，仅显示峰谷点，请放大查看" : "");
            ToolTip.SetTip(this, full);
            var text = new FormattedText(full, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Inter, Microsoft YaHei UI"), 13, TextBrush)
            { MaxTextWidth = area.Width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            context.DrawText(text, new Point(area.Left, 6));
        }

        private void DrawTextWithin(DrawingContext context, string text, double x, double y, double size, IBrush brush)
        {
            var formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Inter, Microsoft YaHei UI"), size, brush);
            context.DrawText(formatted, new Point(Math.Max(2, Math.Min(Bounds.Width - formatted.Width - 2, x)), y));
        }

        private void DrawCursor(DrawingContext context, Rect area, double maxTime)
        {
            if (!IsCursorInView(SelectedTime, _viewStartTime, _viewEndTime)) return;
            double selected = SelectedTime.Value;
            double x = area.Left + (selected - _viewStartTime) / Math.Max(0.01, _viewEndTime - _viewStartTime) * area.Width;
            IBrush cursorBrush = ThemeResourceBrush("Theme.Cursor", "#FFFFFF");
            context.DrawLine(new Pen(cursorBrush, 1), new Point(x, area.Top), new Point(x, area.Bottom));
            DrawTextWithin(context, FormatSeconds(selected), x + 6, area.Top + 6, 12, cursorBrush);
        }

        internal static bool IsCursorInView(double? time, double start, double end)
            => time.HasValue && double.IsFinite(time.Value) && time.Value >= start && time.Value <= end;

        private void SelectTimeAt(Point point)
        {
            if (Samples.Count == 0) return;
            Rect area = GetPlotArea();
            if (point.Y < area.Top - 18 || point.Y > area.Bottom + 28) return;
            double x = Math.Max(area.Left, Math.Min(area.Right, point.X));
            TimeSelected?.Invoke(TimeAtPoint(point));
        }

        private Point ToPoint(Rect area, double maxTime, AxisRange range, double elapsed, double value)
        {
            double x = area.Left + (elapsed - _viewStartTime) / Math.Max(0.01, _viewEndTime - _viewStartTime) * area.Width;
            double ratio = (value - range.Min) / Math.Max(0.0001, range.Max - range.Min);
            double y = area.Bottom - Math.Max(0, Math.Min(1, ratio)) * area.Height;
            return new Point(x, y);
        }

        private double TimeAtPoint(Point point)
        {
            Rect area = GetPlotArea();
            double ratio = (Math.Max(area.Left, Math.Min(area.Right, point.X)) - area.Left) / Math.Max(1, area.Width);
            return Math.Max(_viewStartTime, Math.Min(_viewEndTime, _viewStartTime + ratio * (_viewEndTime - _viewStartTime)));
        }

        private static List<PerfSample> Chronological(IReadOnlyList<PerfSample> samples)
        {
            List<PerfSample> list = samples == null ? new List<PerfSample>() : samples.Where(delegate(PerfSample sample) { return sample != null; }).ToList();
            return list.OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; }).ToList();
        }

        internal static List<string> TemperatureSensorNames(IEnumerable<PerfSample> samples)
        {
            return SortTemperatureSensorNames((samples ?? Enumerable.Empty<PerfSample>())
                .Where(sample => sample != null && sample.HasTemperature && sample.TemperatureCelsius != null)
                .SelectMany(sample => sample.TemperatureCelsius.Keys));
        }

        internal static List<string> SortTemperatureSensorNames(IEnumerable<string> names)
        {
            string[] preferred = { "CPU", "GPU", "Battery", "Skin", "NPU", "USB", "Power Amplifier" };
            return names.Where(delegate(string name) { return !string.IsNullOrWhiteSpace(name); }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(delegate(string name)
            {
                int index = Array.FindIndex(preferred, delegate(string value) { return string.Equals(value, name, StringComparison.OrdinalIgnoreCase); });
                return index < 0 ? int.MaxValue : index;
            }).ThenBy(delegate(string name) { return name; }, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void DrawWaiting(DrawingContext context, Rect area, string text)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Inter, Microsoft YaHei UI"), 13, MutedBrush)
            { MaxTextWidth = area.Width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
            context.DrawText(formatted, new Point(area.Left, area.Top + area.Height / 2 - 8));
        }
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

}
