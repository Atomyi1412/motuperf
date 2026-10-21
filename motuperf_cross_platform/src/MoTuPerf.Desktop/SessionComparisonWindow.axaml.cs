using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public partial class SessionComparisonWindow : Window
    {
        private readonly ComparisonInput[] _slots = new ComparisonInput[5];
        private readonly Button[] _fileButtons = new Button[5];
        private readonly List<ComparisonInput> _knownInputs = new List<ComparisonInput>();
        private readonly SessionDocument _current;
        private ComparisonReport _report;
        private bool _ready;
        private bool _busy;
        private double _start;
        private double? _end;
        private ComparisonMode _mode;
        private static readonly string[] Colors = { "#E46C63", "#C66B98", "#9874D0", "#399AC9", "#3DA68C" };

        public SessionComparisonWindow() : this(null) { }

        public SessionComparisonWindow(SessionDocument current)
        {
            _current = current;
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            ContentDialogSizing.Attach(this);
            CreateSlots();
            _ready = true;
            Closing += (_, e) => { if (_busy) e.Cancel = true; };
            Opened += async (_, _) =>
            {
                if (_current != null && _slots.All(slot => slot == null)) await AddCurrentAsync();
                else if (_report == null) Render();
            };
        }

        private void CreateSlots()
        {
            var panel = this.FindControl<Panel>("SlotsPanel");
            for (int index = 0; index < 5; index++)
            {
                int slot = index;
                var label = new Border { Width = 58, Height = 28, CornerRadius = new CornerRadius(4), Background = Brush.Parse(Colors[index]),
                    HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = ((char)('A' + index)).ToString(), FontSize = 18,
                    FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto"), HorizontalAlignment = HorizontalAlignment.Center };
                heading.Children.Add(label);
                var buttonContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14") };
                buttonContent.Children.Add(new TextBlock { Text = "请选择对比数据", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var arrow = new PathIcon { Data = Geometry.Parse("M0,0 L10,0 L5,6 Z"), Width = 10, Height = 6, VerticalAlignment = VerticalAlignment.Center };
                arrow.Bind(PathIcon.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.TextSecondary"));
                Grid.SetColumn(arrow, 1); buttonContent.Children.Add(arrow);
                var button = new Button { Name = "Slot" + (char)('A' + index), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, MinWidth = 0, Height = 32, Content = buttonContent };
                button.Classes.Add("comparisonAction");
                button.Click += (_, _) => OpenSlotMenu(slot);
                var stack = new StackPanel { Spacing = 6, Margin = new Thickness(index == 0 ? 0 : 6, 0, index == 4 ? 0 : 6, 0) };
                stack.Children.Add(heading);
                stack.Children.Add(button);
                panel.Children.Add(stack);
                _fileButtons[index] = button;
            }
        }

        private void OpenSlotMenu(int slot)
        {
            if (_busy) return;
            var items = new List<Control>();
            foreach (var input in _knownInputs.Where(input => input.Document != _current))
            {
                var item = new MenuItem { Header = new TextBlock { Text = input.Name, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis } };
                ToolTip.SetTip(item, input.Name);
                item.Click += async (_, _) => await ReplaceAsync(slot, input);
                items.Add(item);
            }
            if (_current != null)
            {
                if (items.Count > 0) items.Add(new Separator());
                var current = new MenuItem { Header = "当前现场" };
                current.Click += async (_, _) => await ReplaceAsync(slot, new ComparisonInput { Name = "当前现场", Document = _current });
                items.Add(current);
            }
            if (items.Count > 0) items.Add(new Separator());
            var import = new MenuItem { Header = "导入其他路径文件…" };
            import.Click += async (_, _) => await PickFileAsync(slot);
            items.Add(import);
            if (_slots[slot] != null)
            {
                items.Add(new Separator());
                var clear = new MenuItem { Header = "移除此现场" };
                clear.Click += async (_, _) => await ReplaceAsync(slot, null);
                items.Add(clear);
            }
            var menu = new ContextMenu { ItemsSource = items };
            menu.Classes.Add("comparisonMenu");
            _fileButtons[slot].ContextMenu = menu;
            menu.Open(_fileButtons[slot]);
        }

        internal Button SlotButtonForTest(int slot) => _fileButtons[slot];

        private async Task PickFileAsync(int slot)
        {
            if (_busy) return;
            SetBusy(true, "正在选择现场…");
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择对比现场", AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("MoTuPerf 现场") { Patterns = new[] { "*.motuperf" } } }
                });
                string path = files.FirstOrDefault()?.TryGetLocalPath();
                if (path == null) return;
                await LoadFileCoreAsync(slot, path);
            }
            catch (Exception ex) { Status("读取失败：" + ex.Message); }
            finally { SetBusy(false); }
        }

        internal async Task LoadFileAsync(int slot, string path)
        {
            if (_busy) return;
            SetBusy(true);
            try { await LoadFileCoreAsync(slot, path); }
            catch (Exception ex) { Status("读取失败：" + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task LoadFileCoreAsync(int slot, string path)
        {
            this.FindControl<TextBlock>("BusyText").Text = "正在读取现场与计算统计…";
            var document = await Task.Run(() => SessionArchiveService.LoadForAnalysis(path));
            await ReplaceCoreAsync(slot, new ComparisonInput { Name = Path.GetFileNameWithoutExtension(path), Document = document });
        }

        internal async Task ReplaceAsync(int slot, ComparisonInput input)
        {
            if (_busy) return;
            SetBusy(true);
            try { await ReplaceCoreAsync(slot, input); }
            catch (Exception ex) { Status("处理失败：" + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task ReplaceCoreAsync(int slot, ComparisonInput input)
        {
            if (slot < 0 || slot >= _slots.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            var proposed = (ComparisonInput[])_slots.Clone();
            proposed[slot] = input;
            var report = await ComputeAsync(proposed, _mode, _start, _end);
            _slots[slot] = input;
            if (input != null && !_knownInputs.Any(known => ReferenceEquals(known.Document, input.Document)))
            {
                _knownInputs.Add(input);
                if (_knownInputs.Count > 20) _knownInputs.RemoveAt(0);
            }
            _report = report;
            Render();
        }

        private static Task<ComparisonReport> ComputeAsync(ComparisonInput[] slots, ComparisonMode mode, double start, double? end)
        {
            var inputs = slots.Select((input, index) => input == null ? null : new ComparisonInput
            {
                Name = (char)('A' + index) + " · " + input.Name, Document = input.Document
            }).Where(input => input != null).ToArray();
            return Task.Run(() => inputs.Length == 0 ? null : SessionComparisonService.Build(inputs, mode, start, end));
        }

        private async Task AddCurrentAsync()
        {
            if (_current == null || _busy) return;
            int index = Array.FindIndex(_slots, slot => slot == null);
            if (index < 0) { Status("最多对比五份现场，请先移除或替换一份。"); return; }
            await ReplaceAsync(index, new ComparisonInput { Name = "当前现场", Document = _current });
        }

        private async void ApplyRange(object sender, RoutedEventArgs e) => await UpdateReportAsync();
        private async void ResetRange(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            this.FindControl<TextBox>("StartTimeInput").Text = "0";
            this.FindControl<TextBox>("EndTimeInput").Text = "";
            await UpdateReportAsync();
        }
        private async void ModeChanged(object sender, RoutedEventArgs e)
        {
            if (_ready && !_busy && sender is RadioButton { IsChecked: true } button)
                await UpdateReportAsync(button.Name == "PeakMode" ? ComparisonMode.Peak : ComparisonMode.Average);
        }

        private async Task UpdateReportAsync(ComparisonMode? requestedMode = null)
        {
            if (_busy) return;
            this.FindControl<Button>("ExportButton").IsEnabled = false;
            string startText = this.FindControl<TextBox>("StartTimeInput").Text ?? "";
            string endText = this.FindControl<TextBox>("EndTimeInput").Text ?? "";
            if (!double.TryParse(startText, NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
                || (!string.IsNullOrWhiteSpace(endText) && !double.TryParse(endText, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            { Status("请输入有效秒数，例如 60 或 90.5；结束留空表示记录末尾。"); return; }
            double? end = string.IsNullOrWhiteSpace(endText) ? null : double.Parse(endText, CultureInfo.InvariantCulture);
            if (!double.IsFinite(start) || start < 0 || (end.HasValue && (!double.IsFinite(end.Value) || end <= start)))
            { Status("结束时间须大于开始时间，且时间必须为非负有限秒数。"); return; }
            // The other radio can still be checked while the group processes this event.
            var mode = requestedMode ?? (this.FindControl<RadioButton>("PeakMode").IsChecked == true ? ComparisonMode.Peak : ComparisonMode.Average);
            SetBusy(true, "正在计算区间统计…");
            try
            {
                var report = await ComputeAsync(_slots, mode, start, end);
                _start = start; _end = end; _mode = mode; _report = report;
                Render();
            }
            catch (Exception ex) { Status("统计失败：" + ex.Message); }
            finally { SetBusy(false); }
        }

        private void Render()
        {
            this.FindControl<TextBox>("StartTimeInput").Text = _start.ToString("G", CultureInfo.InvariantCulture);
            this.FindControl<TextBox>("EndTimeInput").Text = _end?.ToString("G", CultureInfo.InvariantCulture) ?? "";
            this.FindControl<RadioButton>("PeakMode").IsChecked = _mode == ComparisonMode.Peak;
            this.FindControl<RadioButton>("AverageMode").IsChecked = _mode == ComparisonMode.Average;
            for (int i = 0; i < 5; i++)
            {
                ((TextBlock)((Grid)_fileButtons[i].Content).Children[0]).Text = _slots[i]?.Name ?? "请选择对比数据";
                ToolTip.SetTip(_fileButtons[i], _slots[i]?.Name ?? "选择 .motuperf 现场文件");
            }
            var header = this.FindControl<Grid>("TableHeader");
            var body = this.FindControl<StackPanel>("TableRows");
            bool detailsExpanded = body.Children.OfType<Expander>().FirstOrDefault()?.IsExpanded == true;
            header.Children.Clear(); body.Children.Clear();
            int count = _report?.Headers.Count ?? 0;
            string columns = "120,120" + string.Concat(Enumerable.Repeat(",*", Math.Max(1, count)));
            header.ColumnDefinitions = new ColumnDefinitions(columns);
            AddCell(header, 0, "测试项", "", true, columnSpan: 2);
            var occupiedSlots = Enumerable.Range(0, 5).Where(i => _slots[i] != null).ToArray();
            for (int i = 0; i < count; i++)
            {
                var cell = AddCell(header, i + 2, _report.Headers[i], _report.Headers[i], true);
                ((TextBlock)cell.Child).Foreground = Brush.Parse(Colors[occupiedSlots[i]]);
            }
            if (_report != null)
            {
                var detailRows = new StackPanel();
                foreach (var group in _report.Rows.Where(row => !row.IsDetail).GroupBy(row => string.IsNullOrEmpty(row.Group) ? row.Label : row.Group))
                {
                    var entries = group.ToArray();
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns),
                        RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("30", entries.Length))) };
                    bool grouped = !string.IsNullOrEmpty(entries[0].Group);
                    if (grouped) AddCell(grid, 0, group.Key, string.Join("\n", entries.Select(row => row.Hint).Where(hint => !string.IsNullOrWhiteSpace(hint))), rowSpan: entries.Length);
                    for (int r = 0; r < entries.Length; r++)
                    {
                        var row = entries[r];
                        var label = AddCell(grid, grouped ? 1 : 0, (grouped ? row.SubLabel : row.Label)
                            + (string.IsNullOrWhiteSpace(row.Unit) ? "" : " (" + row.Unit + ")"), row.Hint,
                            row: r, columnSpan: grouped ? 1 : 2);
                        Avalonia.Automation.AutomationProperties.SetAutomationId(label.Child, row.Label);
                        for (int i = 0; i < row.Values.Count; i++) AddCell(grid, i + 2, row.Values[i], row.Values[i], row: r);
                    }
                    body.Children.Add(grid);
                }
                foreach (var row in _report.Rows.Where(row => row.IsDetail))
                {
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
                    AddCell(grid, 0, row.Label, row.Hint, columnSpan: 2);
                    for (int i = 0; i < row.Values.Count; i++) AddCell(grid, i + 2, row.Values[i], row.Values[i]);
                    detailRows.Children.Add(grid);
                }
                if (detailRows.Children.Count > 0)
                {
                    var expander = new Expander
                    {
                        Name = "ComparisonDetails", Header = "口径详情", Content = detailRows,
                        Padding = new Thickness(0), BorderThickness = new Thickness(0),
                        IsExpanded = detailsExpanded, HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    };
                    expander.Classes.Add("comparisonDetails");
                    expander.Bind(Expander.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.TextPrimary"));
                    body.Children.Add(expander);
                }
            }
            else body.Children.Add(new TextBlock { Text = "选择两份或更多现场文件开始对比", Margin = new Thickness(20, 40),
                HorizontalAlignment = HorizontalAlignment.Center });
            Status(count < 2 ? "选择至少两份现场进行对比；缺测显示 --。" :
                $"已加载 {count} 份现场 · {(_mode == ComparisonMode.Peak ? "峰值" : "均值")} · {_start:0.##} 秒至 {(_end.HasValue ? _end.Value.ToString("0.##", CultureInfo.InvariantCulture) + " 秒" : "各自末尾")} · 跨平台请核对指标口径");
            this.FindControl<Button>("ExportButton").IsEnabled = count >= 2;
        }

        private static Border AddCell(Grid grid, int column, string value, string hint, bool heading = false,
            int row = 0, int rowSpan = 1, int columnSpan = 1)
        {
            var text = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(8, 4) };
            text.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.TextPrimary"));
            ToolTip.SetTip(text, string.IsNullOrWhiteSpace(hint) ? value : value + "\n" + hint);
            var border = new Border { Child = text, BorderThickness = new Thickness(0, 0, 1, 1), ClipToBounds = true, MinHeight = 30 };
            border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.DialogSeparator"));
            Grid.SetColumn(border, column); Grid.SetRow(border, row); Grid.SetRowSpan(border, rowSpan);
            Grid.SetColumnSpan(border, columnSpan); grid.Children.Add(border);
            return border;
        }

        private void SetBusy(bool busy, string title = "正在读取现场与计算统计…")
        {
            _busy = busy;
            this.FindControl<Border>("BusyOverlay").IsVisible = busy;
            this.FindControl<TextBlock>("BusyText").Text = title;
        }
        private void Status(string text)
        {
            var status = this.FindControl<TextBlock>("StatusText"); status.Text = text; ToolTip.SetTip(status, text);
        }

        private void RangeEdited(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _busy) return;
            if (this.FindControl<TextBox>("StartTimeInput").Text == _start.ToString("G", CultureInfo.InvariantCulture)
                && (this.FindControl<TextBox>("EndTimeInput").Text ?? "") == (_end?.ToString("G", CultureInfo.InvariantCulture) ?? "")) return;
            this.FindControl<Button>("ExportButton").IsEnabled = false;
            Status("区间尚未应用，请点击“应用”更新统计后导出。");
        }

        internal Func<Task<string>> ExportPathPicker { get; set; }

        private async void ExportComparison(object sender, RoutedEventArgs e)
        {
            if (_busy || _report == null || _report.Headers.Count < 2 || !this.FindControl<Button>("ExportButton").IsEnabled) return;
            SetBusy(true, "正在导出对比表…");
            try
            {
                string path;
                if (ExportPathPicker != null) path = await ExportPathPicker();
                else
                {
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "导出对比分析", SuggestedFileName = "motuperf-comparison-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv",
                        DefaultExtension = "csv", FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } }
                    });
                    if (file == null) return;
                    path = file.TryGetLocalPath();
                }
                if (path == null) return;
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("请选择本地保存目录。");
                var report = _report;
                await Task.Run(() => SessionComparisonService.WriteCsv(report, path));
                Status("已导出：" + Path.GetFileName(path));
            }
            catch (Exception ex) { Status("导出失败：" + ex.Message); }
            finally { SetBusy(false); }
        }
        private void MoveTitle(object sender, PointerPressedEventArgs e)
        {
            if (!_busy && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }
        private void CloseWindow(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
    }
}
