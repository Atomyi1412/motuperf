using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MoTuPerf.Desktop
{
    public sealed class ComparisonChartPoint
    {
        public double TimeSec { get; set; }
        public double Value { get; set; }
    }

    public sealed class ComparisonChartSeries
    {
        public string Name { get; set; } = "";
        public string Color { get; set; } = "#4DB5E0";
        public double DurationSec { get; set; }
        public IReadOnlyList<ComparisonChartPoint> Points { get; set; } = Array.Empty<ComparisonChartPoint>();
    }

    public sealed class ComparisonChartControl : Control
    {
        public static readonly StyledProperty<IReadOnlyList<ComparisonChartSeries>> SeriesProperty =
            AvaloniaProperty.Register<ComparisonChartControl, IReadOnlyList<ComparisonChartSeries>>(nameof(Series));
        public static readonly StyledProperty<string> UnitProperty =
            AvaloniaProperty.Register<ComparisonChartControl, string>(nameof(Unit), "");
        public static readonly StyledProperty<string> EmptyTextProperty =
            AvaloniaProperty.Register<ComparisonChartControl, string>(nameof(EmptyText), "当前区间没有可比较的数据");
        public static readonly StyledProperty<double?> ThresholdProperty =
            AvaloniaProperty.Register<ComparisonChartControl, double?>(nameof(Threshold));
        public static readonly StyledProperty<double?> MinimumProperty =
            AvaloniaProperty.Register<ComparisonChartControl, double?>(nameof(Minimum));
        public static readonly StyledProperty<double?> MaximumProperty =
            AvaloniaProperty.Register<ComparisonChartControl, double?>(nameof(Maximum));
        public static readonly StyledProperty<bool> IntegerAxisProperty =
            AvaloniaProperty.Register<ComparisonChartControl, bool>(nameof(IntegerAxis));
        public static readonly StyledProperty<bool> AlignTimeTrendsProperty =
            AvaloniaProperty.Register<ComparisonChartControl, bool>(nameof(AlignTimeTrends));

        private static readonly List<WeakReference<ComparisonChartControl>> Instances = new List<WeakReference<ComparisonChartControl>>();
        private const double LegendRowHeight = 22;
        private const double RangeHeight = 10;
        private readonly HashSet<string> _hiddenSeries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<(Rect Bounds, string Name)> _legendAreas = new List<(Rect, string)>();
        private double _rangeStartFraction;
        private double _rangeEndFraction = 1;
        private Rect _rangeTrackBounds;
        private Rect _rangeSelectionBounds;
        private Rect _rangeStartHandleBounds;
        private Rect _rangeEndHandleBounds;
        private Rect _rangeResetBounds;
        private RangeDragMode _rangeDragMode;
        private double _rangeDragAnchorFraction;
        private double _rangeDragOriginStart;
        private double _rangeDragOriginEnd;
        private Point? _hoverPoint;

        public ComparisonChartControl()
        {
            Instances.Add(new WeakReference<ComparisonChartControl>(this));
            ClipToBounds = true;
        }

        public IReadOnlyList<ComparisonChartSeries> Series
        {
            get => GetValue(SeriesProperty);
            set => SetValue(SeriesProperty, value);
        }

        public string Unit
        {
            get => GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public string EmptyText
        {
            get => GetValue(EmptyTextProperty);
            set => SetValue(EmptyTextProperty, value);
        }

        public double? Threshold
        {
            get => GetValue(ThresholdProperty);
            set => SetValue(ThresholdProperty, value);
        }

        public double? Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double? Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public bool IntegerAxis
        {
            get => GetValue(IntegerAxisProperty);
            set => SetValue(IntegerAxisProperty, value);
        }

        public bool AlignTimeTrends
        {
            get => GetValue(AlignTimeTrendsProperty);
            set => SetValue(AlignTimeTrendsProperty, value);
        }

        internal double VisibleStartFractionForTest => _rangeStartFraction;
        internal double VisibleEndFractionForTest => _rangeEndFraction;
        internal Rect RangeTrackBoundsForTest => _rangeTrackBounds;
        internal Rect RangeSelectionBoundsForTest => _rangeSelectionBounds;
        internal Rect RangeStartHandleBoundsForTest => _rangeStartHandleBounds;
        internal Rect RangeEndHandleBoundsForTest => _rangeEndHandleBounds;
        internal Rect RangeResetBoundsForTest => _rangeResetBounds;
        internal bool IsSeriesVisibleForTest(string name) => !_hiddenSeries.Contains(name ?? "");
        internal double DisplayedSeriesEndTimeForTest(string name)
        {
            var allData = (Series ?? Array.Empty<ComparisonChartSeries>())
                .Where(series => series != null && !string.IsNullOrWhiteSpace(series.Name))
                .Select(series => new VisibleSeries(series, PointsFor(series)))
                .Where(item => item.Points.Count > 0)
                .ToList();
            if (allData.Count == 0) return double.NaN;
            if (AlignTimeTrends) allData = AlignSeriesDurations(allData);
            VisibleSeries item = allData.FirstOrDefault(series =>
                string.Equals(series.Series.Name, name ?? "", StringComparison.OrdinalIgnoreCase));
            return item == null ? double.NaN : item.Points[item.Points.Count - 1].TimeSec;
        }

        internal Rect LegendBoundsForTest(string name)
        {
            foreach (var area in _legendAreas)
            {
                if (string.Equals(area.Name, name, StringComparison.Ordinal)) return area.Bounds;
            }
            return default;
        }

        internal void ResetVisibleRangeForTest() => ResetVisibleRange();

        public void SetSeriesVisible(string name, bool visible)
        {
            string key = name ?? "";
            if (string.IsNullOrWhiteSpace(key)) return;
            if (visible) _hiddenSeries.Remove(key);
            else _hiddenSeries.Add(key);
            InvalidateVisual();
        }

        public bool ToggleSeries(string name)
        {
            bool visible = _hiddenSeries.Contains(name ?? "");
            SetSeriesVisible(name, visible);
            return visible;
        }

        public static void RefreshThemeResources()
        {
            for (int i = Instances.Count - 1; i >= 0; i--)
            {
                if (!Instances[i].TryGetTarget(out ComparisonChartControl control))
                {
                    Instances.RemoveAt(i);
                    continue;
                }
                control.InvalidateVisual();
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SeriesProperty) ResetVisibleRange(false);
            else if (change.Property == AlignTimeTrendsProperty) ResetVisibleRange();
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point point = e.GetPosition(this);
            if (e.Pointer.Captured == this && _rangeDragMode != RangeDragMode.None)
            {
                UpdateRangeDrag(point);
                _hoverPoint = null;
                e.Handled = true;
                InvalidateVisual();
                return;
            }

            _hoverPoint = point;
            UpdateRangeCursor(point);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverPoint = null;
            if (_rangeDragMode == RangeDragMode.None) Cursor = new Cursor(StandardCursorType.Arrow);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Point point = e.GetPosition(this);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            foreach (var area in _legendAreas)
            {
                if (!area.Bounds.Contains(point)) continue;
                ToggleSeries(area.Name);
                e.Handled = true;
                return;
            }

            if (_rangeResetBounds.Contains(point))
            {
                ResetVisibleRange();
                e.Handled = true;
                return;
            }

            Rect rangeHitBounds = Inflate(_rangeTrackBounds, 4, 7);
            if (rangeHitBounds.Contains(point))
            {
                if (e.ClickCount >= 2)
                {
                    ResetVisibleRange();
                    e.Handled = true;
                    return;
                }

                double fraction = RangeFractionAt(point.X);
                double handleDistance = Math.Max(8, Math.Min(14, _rangeTrackBounds.Width * 0.02));
                if (Math.Abs(point.X - _rangeSelectionBounds.Left) <= handleDistance)
                    _rangeDragMode = RangeDragMode.Start;
                else if (Math.Abs(point.X - _rangeSelectionBounds.Right) <= handleDistance)
                    _rangeDragMode = RangeDragMode.End;
                else
                    _rangeDragMode = RangeDragMode.Pan;

                _rangeDragAnchorFraction = fraction;
                _rangeDragOriginStart = _rangeStartFraction;
                _rangeDragOriginEnd = _rangeEndFraction;
                e.Pointer.Capture(this);
                _hoverPoint = null;
                UpdateRangeCursor(point);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            _hoverPoint = point;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.Pointer.Captured == this && _rangeDragMode != RangeDragMode.None)
            {
                UpdateRangeDrag(e.GetPosition(this));
                e.Pointer.Capture(null);
                _rangeDragMode = RangeDragMode.None;
                UpdateRangeCursor(e.GetPosition(this));
                e.Handled = true;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Rect bounds = new Rect(Bounds.Size);
            context.DrawRectangle(ResourceBrush("Theme.ChartBackground", "#171A20"), null, bounds);
            if (bounds.Width < 220 || bounds.Height < 120) return;

            double plotLeft = 58;
            double plotRight = Math.Max(plotLeft + 20, bounds.Width - 20);
            var allSeries = Series ?? Array.Empty<ComparisonChartSeries>();
            LegendPlan legendPlan = BuildLegendPlan(bounds.Width - 32, allSeries);
            double legendHeight = Math.Max(LegendRowHeight, legendPlan.Rows.Count * LegendRowHeight);
            double plotTop = 42;
            double plotBottom = Math.Max(plotTop + 80, bounds.Height - 72 - legendHeight);
            Rect plot = new Rect(plotLeft, plotTop, Math.Max(1, plotRight - plotLeft), Math.Max(1, plotBottom - plotTop));
            double rangeTop = plot.Bottom + 34;
            double legendTop = rangeTop + RangeHeight + 20;

            var allData = allSeries
                .Where(series => series != null && !string.IsNullOrWhiteSpace(series.Name))
                .Select(series => new VisibleSeries(series, PointsFor(series)))
                .Where(item => item.Points.Count > 0)
                .ToList();
            if (AlignTimeTrends) allData = AlignSeriesDurations(allData);
            double maxTime = allData.Count == 0
                ? 60
                : Math.Max(1, allData.Max(item => item.Points[item.Points.Count - 1].TimeSec));
            var visible = allData.Where(item => !_hiddenSeries.Contains(item.Series.Name)).ToList();

            if (visible.Count == 0)
            {
                DrawGrid(context, plot, 0, 1, 0, maxTime, true);
                DrawCenteredText(context, plot, EmptyText ?? "当前区间没有可比较的数据", 13, ResourceBrush("Theme.TextMuted", "#8A8F9A"));
                DrawRangeBar(context, plot, maxTime, rangeTop);
                DrawLegend(context, legendPlan, legendTop);
                return;
            }

            GetVisibleTimeRange(maxTime, out double startTime, out double endTime);
            double minValue = Minimum ?? 0;
            double maxValue = Maximum ?? visible.SelectMany(item => item.Points).Max(point => point.Value);
            if (Threshold.HasValue) maxValue = Math.Max(maxValue, Threshold.Value);
            if (IntegerAxis)
            {
                minValue = Math.Floor(minValue);
                maxValue = Math.Ceiling(Math.Max(minValue + 1, maxValue));
            }
            else
            {
                maxValue = NiceMaximum(maxValue, minValue);
            }
            if (!double.IsFinite(minValue)) minValue = 0;
            if (!double.IsFinite(maxValue) || maxValue <= minValue) maxValue = minValue + 1;

            DrawGrid(context, plot, minValue, maxValue, startTime, endTime, false);
            DrawThreshold(context, plot, minValue, maxValue);

            foreach (var item in visible)
            {
                DrawSeriesLine(context, plot, minValue, maxValue, startTime, endTime, item);
            }

            DrawHover(context, plot, visible, minValue, maxValue, startTime, endTime);
            DrawRangeBar(context, plot, maxTime, rangeTop);
            DrawLegend(context, legendPlan, legendTop);
            if (_rangeDragMode != RangeDragMode.None)
            {
                string rangeLabel = FormatRangeTime(startTime) + " - " + FormatRangeTime(endTime);
                Rect labelArea = new Rect(plot.Left, 10, plot.Width, 22);
                DrawCenteredText(context, labelArea, rangeLabel, 11, ResourceBrush("Theme.TextPrimary", "#E4E6EC"));
            }
        }

        private IReadOnlyList<ComparisonChartPoint> PointsFor(ComparisonChartSeries series)
        {
            return (series.Points ?? Array.Empty<ComparisonChartPoint>())
                .Where(point => point != null && double.IsFinite(point.TimeSec) && double.IsFinite(point.Value))
                .OrderBy(point => point.TimeSec)
                .ToList();
        }

        private static List<VisibleSeries> AlignSeriesDurations(IReadOnlyList<VisibleSeries> series)
        {
            double targetDuration = series.Count == 0 ? 0 : series.Max(SeriesDuration);
            if (!double.IsFinite(targetDuration) || targetDuration <= 0) return series.ToList();

            return series.Select(item =>
            {
                double duration = SeriesDuration(item);
                if (!double.IsFinite(duration) || duration <= 0 || Math.Abs(duration - targetDuration) < 0.000001)
                    return item;
                double scale = targetDuration / duration;
                var points = item.Points.Select(point => new ComparisonChartPoint
                {
                    TimeSec = point.TimeSec * scale,
                    Value = point.Value
                }).ToList();
                return new VisibleSeries(item.Series, points);
            }).ToList();
        }

        private static double SeriesDuration(VisibleSeries item)
        {
            if (item.Series.DurationSec > 0 && double.IsFinite(item.Series.DurationSec)) return item.Series.DurationSec;
            return item.Points.Count == 0 ? 0 : item.Points[item.Points.Count - 1].TimeSec;
        }

        private LegendPlan BuildLegendPlan(double availableWidth, IReadOnlyList<ComparisonChartSeries> series)
        {
            var plan = new LegendPlan();
            var row = new List<LegendItem>();
            double rowWidth = 0;
            const double gap = 18;
            foreach (ComparisonChartSeries item in series.Where(value => value != null && !string.IsNullOrWhiteSpace(value.Name)))
            {
                string label = TrimToWidth(item.Name, 190, 11);
                double width = MeasureText(label, 11) + 28;
                if (row.Count > 0 && rowWidth + gap + width > availableWidth)
                {
                    plan.Rows.Add(row);
                    row = new List<LegendItem>();
                    rowWidth = 0;
                }
                row.Add(new LegendItem(item, label, width));
                rowWidth += (row.Count > 1 ? gap : 0) + width;
            }
            if (row.Count > 0) plan.Rows.Add(row);
            return plan;
        }

        private void DrawLegend(DrawingContext context, LegendPlan plan, double top)
        {
            _legendAreas.Clear();
            const double gap = 18;
            for (int rowIndex = 0; rowIndex < plan.Rows.Count; rowIndex++)
            {
                List<LegendItem> row = plan.Rows[rowIndex];
                double rowWidth = row.Sum(item => item.Width) + Math.Max(0, row.Count - 1) * gap;
                double x = Math.Max(16, (Bounds.Width - rowWidth) / 2);
                double y = top + rowIndex * LegendRowHeight;
                foreach (LegendItem legendItem in row)
                {
                    bool visible = !_hiddenSeries.Contains(legendItem.Series.Name);
                    IBrush color = visible ? Brush.Parse(legendItem.Series.Color ?? "#4DB5E0") : ResourceBrush("Theme.TextMuted", "#8A8F9A");
                    context.DrawRectangle(color, null, new Rect(x, y + 5, 10, 3), 1.5, 1.5);
                    IBrush textBrush = visible ? ResourceBrush("Theme.TextPrimary", "#D7D9DF") : ResourceBrush("Theme.TextMuted", "#8A8F9A");
                    context.DrawText(Text(legendItem.Label, 11, textBrush), new Point(x + 15, y));
                    _legendAreas.Add((new Rect(x - 5, y - 4, legendItem.Width, 22), legendItem.Series.Name));
                    x += legendItem.Width + gap;
                }
            }
        }

        private void DrawRangeBar(DrawingContext context, Rect plot, double maxTime, double top)
        {
            double resetWidth = 42;
            double trackRight = Math.Max(plot.Left + 20, plot.Right - resetWidth - 12);
            _rangeTrackBounds = new Rect(plot.Left, top, Math.Max(20, trackRight - plot.Left), RangeHeight);
            IBrush trackBrush = ResourceBrush("Theme.SelectedBackground", "#2A2C3C");
            IBrush accent = ResourceBrush("Theme.Accent", "#1476FF");
            context.DrawRectangle(trackBrush, new Pen(ResourceBrush("Theme.BorderStrong", "#424456"), 1), _rangeTrackBounds, 5, 5);

            GetVisibleTimeRange(maxTime, out double startTime, out double endTime);
            double startX = _rangeTrackBounds.Left + _rangeStartFraction * _rangeTrackBounds.Width;
            double endX = _rangeTrackBounds.Left + _rangeEndFraction * _rangeTrackBounds.Width;
            _rangeSelectionBounds = new Rect(startX, top, Math.Max(1, endX - startX), RangeHeight);
            context.DrawRectangle(accent, null, _rangeSelectionBounds, 4, 4);

            _rangeStartHandleBounds = new Rect(startX - 4, top - 4, 9, RangeHeight + 8);
            _rangeEndHandleBounds = new Rect(endX - 5, top - 4, 9, RangeHeight + 8);
            IBrush handleFill = ResourceBrush("Theme.TextPrimary", "#EBECF4");
            context.DrawRectangle(handleFill, new Pen(accent, 1), _rangeStartHandleBounds, 3, 3);
            context.DrawRectangle(handleFill, new Pen(accent, 1), _rangeEndHandleBounds, 3, 3);

            Pen handleLine = new Pen(ResourceBrush("Theme.TextMuted", "#777A8C"), 1);
            context.DrawLine(handleLine, new Point(startX - 1, top + 2), new Point(startX - 1, top + RangeHeight - 2));
            context.DrawLine(handleLine, new Point(endX + 1, top + 2), new Point(endX + 1, top + RangeHeight - 2));

            bool isZoomed = _rangeStartFraction > 0.0001 || _rangeEndFraction < 0.9999;
            string rangeLabel = FormatRangeTime(startTime) + " - " + FormatRangeTime(endTime);
            context.DrawText(Text(rangeLabel, 10, ResourceBrush("Theme.TextMuted", "#8A8F9A")),
                new Point(plot.Right - MeasureText(rangeLabel, 10), 10));

            _rangeResetBounds = new Rect(trackRight + 8, top - 5, resetWidth, RangeHeight + 10);
            IBrush resetBrush = isZoomed ? accent : ResourceBrush("Theme.TextMuted", "#777A8C");
            context.DrawText(Text("还原", 11, resetBrush),
                new Point(_rangeResetBounds.X + Math.Max(0, (resetWidth - MeasureText("还原", 11)) / 2), top - 2));
        }

        private void DrawGrid(DrawingContext context, Rect plot, double minValue, double maxValue, double startTime, double endTime, bool empty)
        {
            Pen gridPen = new Pen(ResourceBrush("Theme.ChartGrid", "#343842"), 1);
            IBrush textBrush = ResourceBrush("Theme.TextMuted", "#8A8F9A");
            for (int i = 0; i <= 4; i++)
            {
                double ratio = i / 4.0;
                double y = plot.Bottom - ratio * plot.Height;
                context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                double value = minValue + ratio * (maxValue - minValue);
                string label = FormatValue(value);
                context.DrawText(Text(label, 10, textBrush), new Point(Math.Max(4, plot.Left - MeasureText(label, 10) - 8), y - 7));
            }
            for (int i = 0; i <= 6; i++)
            {
                double ratio = i / 6.0;
                double x = plot.Left + ratio * plot.Width;
                context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                string label = FormatTime(startTime + ratio * (endTime - startTime));
                context.DrawText(Text(label, 10, textBrush), new Point(x - MeasureText(label, 10) / 2, plot.Bottom + 9));
            }
            if (!empty && !string.IsNullOrWhiteSpace(Unit))
            {
                context.DrawText(Text(Unit, 10, textBrush), new Point(8, plot.Top - 20));
            }
        }

        private void DrawThreshold(DrawingContext context, Rect plot, double minValue, double maxValue)
        {
            if (!Threshold.HasValue || Threshold.Value < minValue || Threshold.Value > maxValue) return;
            double y = plot.Bottom - (Threshold.Value - minValue) / Math.Max(0.0001, maxValue - minValue) * plot.Height;
            Pen pen = new Pen(ResourceBrush("Theme.Warning", "#E9A23B"), 1, new DashStyle(new[] { 4.0, 4.0 }, 0));
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            string label = "阈值 " + FormatValue(Threshold.Value);
            context.DrawText(Text(label, 10, ResourceBrush("Theme.Warning", "#E9A23B")), new Point(plot.Right - MeasureText(label, 10) - 4, y - 14));
        }

        private void DrawSeriesLine(DrawingContext context, Rect plot, double minValue, double maxValue,
            double startTime, double endTime, VisibleSeries item)
        {
            IReadOnlyList<ComparisonChartPoint> points = Compact(PointsInView(item.Points, startTime, endTime),
                (int)Math.Max(200, plot.Width * 1.5));
            if (points.Count == 0) return;
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext geometryContext = geometry.Open())
            {
                bool started = false;
                double previousTime = 0;
                double typicalGap = TypicalGap(points);
                foreach (ComparisonChartPoint point in points)
                {
                    Point location = MapPoint(plot, minValue, maxValue, startTime, endTime, point.TimeSec, point.Value);
                    bool breakLine = started && typicalGap > 0 && point.TimeSec - previousTime > Math.Max(2.2, typicalGap * 3.0);
                    if (!started || breakLine) geometryContext.BeginFigure(location, false);
                    else geometryContext.LineTo(location);
                    started = true;
                    previousTime = point.TimeSec;
                }
            }
            using (context.PushClip(plot))
            {
                context.DrawGeometry(null, new Pen(Brush.Parse(item.Series.Color ?? "#4DB5E0"), 1.65), geometry);
            }
        }

        private void DrawHover(DrawingContext context, Rect plot, IReadOnlyList<VisibleSeries> visible,
            double minValue, double maxValue, double startTime, double endTime)
        {
            if (!_hoverPoint.HasValue || !plot.Contains(_hoverPoint.Value)) return;
            double ratio = Math.Max(0, Math.Min(1, (_hoverPoint.Value.X - plot.Left) / plot.Width));
            double hoverTime = startTime + ratio * (endTime - startTime);
            double x = plot.Left + ratio * plot.Width;
            context.DrawLine(new Pen(ResourceBrush("Theme.Cursor", "#E8EAF0"), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));

            var readings = visible.Select(item => new { item.Series, Point = Nearest(item.Points, hoverTime, startTime, endTime) })
                .Where(value => value.Point != null).OrderByDescending(value => value.Point.Value).ToList();
            if (readings.Count == 0) return;
            string title = FormatTime(hoverTime);
            double boxWidth = 170;
            double boxHeight = 26 + readings.Count * 20;
            double boxX = x + 12 + boxWidth <= plot.Right ? x + 12 : x - boxWidth - 12;
            Rect box = new Rect(Math.Max(plot.Left + 4, boxX), plot.Top + 10, boxWidth, boxHeight);
            context.DrawRectangle(ResourceBrush("Theme.OverlayBackground", "#E8292D35"),
                new Pen(ResourceBrush("Theme.BorderStrong", "#5A6070"), 1), box, 6, 6);
            context.DrawText(Text(title, 11, ResourceBrush("Theme.TextSecondary", "#B9BDC8")), new Point(box.X + 10, box.Y + 7));
            double y = box.Y + 27;
            foreach (var reading in readings)
            {
                context.DrawRectangle(Brush.Parse(reading.Series.Color ?? "#4DB5E0"), null, new Rect(box.X + 10, y + 5, 7, 7), 1, 1);
                string name = TrimToWidth(reading.Series.Name, 92, 10);
                context.DrawText(Text(name, 10, ResourceBrush("Theme.TextPrimary", "#E4E6EC")), new Point(box.X + 22, y));
                string value = FormatValue(reading.Point.Value) + (string.IsNullOrWhiteSpace(Unit) ? "" : " " + Unit);
                context.DrawText(Text(value, 10, ResourceBrush("Theme.TextPrimary", "#E4E6EC")), new Point(box.Right - MeasureText(value, 10) - 10, y));
                y += 20;
            }
        }

        private void ResetVisibleRange(bool invalidate = true)
        {
            _rangeStartFraction = 0;
            _rangeEndFraction = 1;
            _rangeDragMode = RangeDragMode.None;
            if (invalidate) InvalidateVisual();
        }

        private void GetVisibleTimeRange(double maxTime, out double startTime, out double endTime)
        {
            maxTime = Math.Max(0.01, maxTime);
            startTime = _rangeStartFraction * maxTime;
            endTime = _rangeEndFraction * maxTime;
            if (endTime <= startTime)
            {
                startTime = 0;
                endTime = maxTime;
            }
        }

        private void UpdateRangeDrag(Point point)
        {
            if (_rangeDragMode == RangeDragMode.None) return;
            double fraction = RangeFractionAt(point.X);
            double minRange = MinRangeFraction();
            if (_rangeDragMode == RangeDragMode.Start)
            {
                _rangeStartFraction = Math.Max(0, Math.Min(fraction, _rangeEndFraction - minRange));
            }
            else if (_rangeDragMode == RangeDragMode.End)
            {
                _rangeEndFraction = Math.Min(1, Math.Max(fraction, _rangeStartFraction + minRange));
            }
            else
            {
                double width = Math.Max(minRange, _rangeDragOriginEnd - _rangeDragOriginStart);
                double start = _rangeDragOriginStart + (fraction - _rangeDragAnchorFraction);
                start = Math.Max(0, Math.Min(1 - width, start));
                _rangeStartFraction = start;
                _rangeEndFraction = start + width;
            }
        }

        private void UpdateRangeCursor(Point point)
        {
            if (_rangeResetBounds.Contains(point) || _legendAreas.Any(area => area.Bounds.Contains(point)))
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            }
            if (Inflate(_rangeTrackBounds, 4, 7).Contains(point))
            {
                double handleDistance = Math.Max(8, Math.Min(14, _rangeTrackBounds.Width * 0.02));
                if (Math.Abs(point.X - _rangeSelectionBounds.Left) <= handleDistance
                    || Math.Abs(point.X - _rangeSelectionBounds.Right) <= handleDistance)
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                else
                    Cursor = new Cursor(StandardCursorType.SizeAll);
                return;
            }
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        private double RangeFractionAt(double x)
        {
            if (_rangeTrackBounds.Width <= 0) return 0;
            return Math.Max(0, Math.Min(1, (x - _rangeTrackBounds.Left) / _rangeTrackBounds.Width));
        }

        private double MinRangeFraction()
        {
            return Math.Min(0.5, Math.Max(0.008, 18 / Math.Max(1, _rangeTrackBounds.Width)));
        }

        private static Rect Inflate(Rect rect, double horizontal, double vertical)
        {
            return new Rect(rect.Left - horizontal, rect.Top - vertical,
                rect.Width + horizontal * 2, rect.Height + vertical * 2);
        }

        private static IReadOnlyList<ComparisonChartPoint> PointsInView(IReadOnlyList<ComparisonChartPoint> points,
            double startTime, double endTime)
        {
            if (points == null || points.Count == 0) return Array.Empty<ComparisonChartPoint>();
            int first = LowerBound(points, startTime);
            int last = LowerBound(points, endTime + 0.000001) - 1;
            if (first < points.Count && points[first].TimeSec <= endTime) last = Math.Max(first, last);
            if (last < first) return Array.Empty<ComparisonChartPoint>();
            if (first > 0) first--;
            if (last < points.Count - 1) last++;
            return new PointRange(points, first, last - first + 1);
        }

        private static Point MapPoint(Rect plot, double minValue, double maxValue,
            double startTime, double endTime, double time, double value)
        {
            double x = plot.Left + (time - startTime) / Math.Max(0.0001, endTime - startTime) * plot.Width;
            double yRatio = Math.Max(0, Math.Min(1, (value - minValue) / Math.Max(0.0001, maxValue - minValue)));
            return new Point(x, plot.Bottom - yRatio * plot.Height);
        }

        private static ComparisonChartPoint Nearest(IReadOnlyList<ComparisonChartPoint> points, double time,
            double startTime, double endTime)
        {
            if (points == null || points.Count == 0) return null;
            int low = LowerBound(points, time);
            if (low >= points.Count) low = points.Count - 1;
            ComparisonChartPoint right = points[low];
            ComparisonChartPoint left = points[Math.Max(0, low - 1)];
            ComparisonChartPoint nearest = Math.Abs(left.TimeSec - time) <= Math.Abs(right.TimeSec - time) ? left : right;
            return nearest.TimeSec >= startTime - 0.000001 && nearest.TimeSec <= endTime + 0.000001 ? nearest : null;
        }

        private static int LowerBound(IReadOnlyList<ComparisonChartPoint> points, double time)
        {
            int low = 0;
            int high = points.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (points[mid].TimeSec < time) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private static IReadOnlyList<ComparisonChartPoint> Compact(IReadOnlyList<ComparisonChartPoint> points, int maxPoints)
        {
            if (points.Count <= maxPoints || maxPoints < 4) return points;
            int buckets = Math.Max(1, maxPoints / 4);
            double bucketSize = (double)points.Count / buckets;
            var compact = new List<ComparisonChartPoint>(Math.Min(maxPoints, points.Count));
            for (int bucket = 0; bucket < buckets; bucket++)
            {
                int start = (int)Math.Floor(bucket * bucketSize);
                int end = Math.Min(points.Count - 1, (int)Math.Ceiling((bucket + 1) * bucketSize) - 1);
                if (end < start) continue;
                int min = start;
                int max = start;
                for (int i = start + 1; i <= end; i++)
                {
                    if (points[i].Value < points[min].Value) min = i;
                    if (points[i].Value > points[max].Value) max = i;
                }
                foreach (int index in new[] { start, Math.Min(min, max), Math.Max(min, max), end }.Distinct().OrderBy(value => value))
                {
                    AddUnique(compact, points[index]);
                }
            }
            return compact;
        }

        internal static IReadOnlyList<ComparisonChartPoint> CompactForTest(IReadOnlyList<ComparisonChartPoint> points, int maxPoints) =>
            Compact(points, maxPoints);

        private static void AddUnique(List<ComparisonChartPoint> points, ComparisonChartPoint candidate)
        {
            if (points.Count == 0 || !ReferenceEquals(points[points.Count - 1], candidate)) points.Add(candidate);
        }

        private static double TypicalGap(IReadOnlyList<ComparisonChartPoint> points)
        {
            if (points.Count < 3) return 0;
            var gaps = new List<double>();
            for (int i = 1; i < points.Count && gaps.Count < 20; i++)
            {
                double gap = points[i].TimeSec - points[i - 1].TimeSec;
                if (gap > 0) gaps.Add(gap);
            }
            return gaps.Count == 0 ? 0 : gaps.OrderBy(value => value).ElementAt(gaps.Count / 2);
        }

        private static double NiceMaximum(double maxValue, double minValue)
        {
            if (maxValue <= minValue) return minValue + 1;
            double span = maxValue - minValue;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(0.01, span))));
            double step = magnitude;
            while (span / step > 5) step *= 2;
            return minValue + Math.Ceiling(span / step) * step;
        }

        private string FormatValue(double value)
        {
            if (IntegerAxis) return value.ToString("0", CultureInfo.InvariantCulture);
            if (Math.Abs(value) >= 1000) return value.ToString("0", CultureInfo.InvariantCulture);
            if (Math.Abs(value) >= 100) return value.ToString("0.0", CultureInfo.InvariantCulture);
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatTime(double seconds)
        {
            seconds = Math.Max(0, seconds);
            int minutes = (int)(seconds / 60);
            double remain = seconds - minutes * 60;
            return minutes > 0 ? minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + remain.ToString("00", CultureInfo.InvariantCulture)
                : remain.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        private static string FormatRangeTime(double seconds)
        {
            int totalSeconds = Math.Max(0, (int)Math.Round(seconds));
            int hours = totalSeconds / 3600;
            int minutes = totalSeconds / 60 % 60;
            int remain = totalSeconds % 60;
            return hours > 0
                ? hours.ToString("00", CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + remain.ToString("00", CultureInfo.InvariantCulture)
                : minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + remain.ToString("00", CultureInfo.InvariantCulture);
        }

        private static FormattedText Text(string value, double size, IBrush brush)
        {
            return new FormattedText(value ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Inter, Microsoft YaHei UI"), size, brush) { MaxTextWidth = 360, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        }

        private static double MeasureText(string value, double size) => Text(value, size, Brushes.White).Width;

        private static string TrimToWidth(string value, double width, double size)
        {
            string text = value ?? "";
            if (MeasureText(text, size) <= width) return text;
            while (text.Length > 1 && MeasureText(text + "...", size) > width) text = text.Substring(0, text.Length - 1);
            return text + "...";
        }

        private static void DrawCenteredText(DrawingContext context, Rect area, string value, double size, IBrush brush)
        {
            FormattedText text = new FormattedText(value ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Inter, Microsoft YaHei UI"), size, brush)
            { MaxTextWidth = area.Width, MaxLineCount = 2, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
            context.DrawText(text, new Point(area.Left, area.Top + Math.Max(0, area.Height / 2 - text.Height / 2)));
        }

        private static IBrush ResourceBrush(string key, string fallback)
        {
            object value;
            if (Application.Current != null && Application.Current.TryFindResource(key, out value) && value is IBrush brush) return brush;
            return Brush.Parse(fallback);
        }

        private sealed class VisibleSeries
        {
            public VisibleSeries(ComparisonChartSeries series, IReadOnlyList<ComparisonChartPoint> points)
            {
                Series = series;
                Points = points;
            }

            public ComparisonChartSeries Series { get; }
            public IReadOnlyList<ComparisonChartPoint> Points { get; }
        }

        private sealed class LegendPlan
        {
            public List<List<LegendItem>> Rows { get; } = new List<List<LegendItem>>();
        }

        private sealed class LegendItem
        {
            public LegendItem(ComparisonChartSeries series, string label, double width)
            {
                Series = series;
                Label = label;
                Width = width;
            }

            public ComparisonChartSeries Series { get; }
            public string Label { get; }
            public double Width { get; }
        }

        private sealed class PointRange : IReadOnlyList<ComparisonChartPoint>
        {
            private readonly IReadOnlyList<ComparisonChartPoint> _source;
            private readonly int _start;

            public PointRange(IReadOnlyList<ComparisonChartPoint> source, int start, int count)
            {
                _source = source;
                _start = start;
                Count = count;
            }

            public int Count { get; }
            public ComparisonChartPoint this[int index] => _source[_start + index];
            public IEnumerator<ComparisonChartPoint> GetEnumerator()
            {
                for (int index = 0; index < Count; index++) yield return this[index];
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private enum RangeDragMode
        {
            None,
            Start,
            End,
            Pan
        }
    }
}
