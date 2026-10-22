using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public partial class SessionComparisonWorkspace : UserControl
    {
        private const string CurrentSessionName = "当前现场";
        private const double ReportStartSeconds = 0;
        private const ComparisonMode ReportMode = ComparisonMode.Average;
        private static readonly string[] SlotColors = { "#E46C63", "#C66B98", "#9874D0", "#399AC9", "#3DA68C" };
        private static readonly string[] Categories = { "overview", "fps", "frametime", "cpu", "memory", "temperature", "thermal" };
        private static readonly string[] CategoryLabels = { "概览", "FPS", "FrameTime / Jank", "CPU", "内存", "温度", "热状态" };

        private readonly ComparisonInput[] _slots = new ComparisonInput[5];
        private readonly Border[] _slotCards = new Border[5];
        private readonly Button[] _tabButtons = new Button[Categories.Length];
        private readonly Dictionary<string, string> _selectedMetrics = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _sectionHosts = new Dictionary<string, Border>(StringComparer.Ordinal);
        private SessionDocument _current;
        private ComparisonReport _report;
        private bool _ready;
        private bool _busy;
        private bool _isChinese;
        private bool _alignTimeTrends;
        private bool _syncingTimeTrendToggles;
        private int _currentSlotIndex = -1;
        private string _activeCategory = "overview";
        private bool _suppressScrollSync;

        public SessionComparisonWorkspace()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            CreateSessionCards();
            CreateTabs();
            _ready = true;
            Render();
        }

        public bool IsBusy { get { return _busy; } }
        internal Func<Task<string>> ImportPathPicker { get; set; }

        internal async Task InitializeAsync(SessionDocument current)
        {
            _current = current;
            int target = CurrentSlotIndex();
            if (_current == null)
            {
                if (target < 0)
                {
                    Render();
                    return;
                }
                SetBusy(true, "正在更新当前现场…");
                try { await ReplaceCoreAsync(target, null); }
                catch (Exception ex) { Status("更新当前现场失败：" + ex.Message); }
                finally { SetBusy(false); }
                return;
            }

            if (target >= 0 && ReferenceEquals(_slots[target]?.Document, _current))
            {
                _currentSlotIndex = target;
                Render();
                return;
            }

            int destination = target >= 0 ? target : Array.FindIndex(_slots, input => input == null);
            if (destination < 0)
            {
                Render();
                Status("A-E 五个槽位均已占用，请先移除一个现场后再加入当前现场。");
                return;
            }

            SetBusy(true, "正在载入当前现场…");
            try
            {
                await ReplaceCoreAsync(destination, new ComparisonInput { Name = CurrentSessionName, Document = _current });
                _currentSlotIndex = destination;
            }
            catch (Exception ex) { Status("载入当前现场失败：" + ex.Message); }
            finally { SetBusy(false); }
        }

        private int CurrentSlotIndex()
        {
            if (_currentSlotIndex >= 0 && _currentSlotIndex < _slots.Length && IsCurrentInput(_slots[_currentSlotIndex])) return _currentSlotIndex;
            return Array.FindIndex(_slots, IsCurrentInput);
        }

        private static bool IsCurrentInput(ComparisonInput input) =>
            input?.Document != null && string.Equals(input.Name, CurrentSessionName, StringComparison.Ordinal);

        private void CreateSessionCards()
        {
            UniformGrid panel = this.FindControl<UniformGrid>("SessionCardsPanel");
            for (int index = 0; index < _slots.Length; index++)
            {
                Border card = new Border
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(14, 10),
                    BorderThickness = new Thickness(0, 0, index == _slots.Length - 1 ? 0 : 1, 0)
                };
                card.Classes.Add("comparisonSessionCard");
                panel.Children.Add(card);
                _slotCards[index] = card;
            }
        }

        private void CreateTabs()
        {
            StackPanel panel = this.FindControl<StackPanel>("TabsPanel");
            for (int index = 0; index < Categories.Length; index++)
            {
                string category = Categories[index];
                Button button = new Button { Content = CategoryTabLabel(category) };
                button.Classes.Add("analysisTab");
                button.Click += delegate
                {
                    if (_busy) return;
                    NavigateToCategory(category);
                };
                panel.Children.Add(button);
                _tabButtons[index] = button;
            }
        }

        private void ToggleLanguage(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string category = _activeCategory;
            _isChinese = !_isChinese;
            UpdateLanguageToggle();
            Render();
            Dispatcher.UIThread.Post(() => NavigateToCategory(category), DispatcherPriority.Loaded);
        }

        private void UpdateLanguageToggle()
        {
            Button button = this.FindControl<Button>("LanguageToggleButton");
            if (button == null) return;
            button.Content = _isChinese ? "English" : "中文";
            ToolTip.SetTip(button, _isChinese ? "Switch metric names to English" : "切换到中文指标名称");
            TextBlock emptyState = this.FindControl<TextBlock>("EmptyStateMessage");
            if (emptyState != null)
            {
                emptyState.Text = _isChinese
                    ? "点击上方任一空卡片中的“添加现场”按钮选择 .motuperf 现场；所有指标将按概览、帧率、帧耗时、处理器、内存、温度和系统热状态依次展示。"
                    : "点击上方任一空卡片中的“添加现场”按钮选择 .motuperf 现场；所有指标将按概览、帧率、帧耗时、CPU、内存、温度和系统热状态依次展示。";
            }
        }

        private void NavigateToCategory(string category)
        {
            if (!Categories.Contains(category)) return;
            _activeCategory = category;
            UpdateTabStyles();
            Dispatcher.UIThread.Post(() => ScrollToCategory(category), DispatcherPriority.Loaded);
        }

        private void ScrollToCategory(string category)
        {
            ScrollViewer scroll = this.FindControl<ScrollViewer>("ContentScroll");
            if (scroll == null || !_sectionHosts.TryGetValue(category, out Border section) || section == null) return;
            Point? point = section.TranslatePoint(new Point(0, 0), scroll);
            if (!point.HasValue) return;
            _suppressScrollSync = true;
            double target = Math.Max(0, scroll.Offset.Y + point.Value.Y - 8);
            scroll.Offset = new Vector(0, target);
            _suppressScrollSync = false;
        }

        private void ContentScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressScrollSync || _busy || !_ready) return;
            ScrollViewer scroll = sender as ScrollViewer;
            if (scroll == null || _sectionHosts.Count == 0) return;

            string next = "overview";
            double best = double.NegativeInfinity;
            foreach (string category in Categories)
            {
                if (!_sectionHosts.TryGetValue(category, out Border section) || section == null || !section.IsVisible) continue;
                Point? point = section.TranslatePoint(new Point(0, 0), scroll);
                if (!point.HasValue || point.Value.Y > 12 || point.Value.Y <= best) continue;
                best = point.Value.Y;
                next = category;
            }
            if (string.Equals(next, _activeCategory, StringComparison.Ordinal)) return;
            _activeCategory = next;
            UpdateTabStyles();
        }

        private async Task PickFileAsync(int slot)
        {
            if (_busy) return;
            SetBusy(true, "正在选择现场…");
            try
            {
                string path;
                if (ImportPathPicker != null) path = await ImportPathPicker();
                else
                {
                    TopLevel topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel == null) return;
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = "选择曲线分析现场",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { new FilePickerFileType("MoTuPerf 现场") { Patterns = new[] { "*.motuperf" } } }
                    });
                    path = files.FirstOrDefault()?.TryGetLocalPath();
                }
                if (string.IsNullOrWhiteSpace(path)) return;
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
            SessionDocument document = await Task.Run(() => SessionArchiveService.LoadForAnalysis(path));
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
            bool currentInput = IsCurrentInput(input);
            if (slot == _currentSlotIndex && !currentInput) _currentSlotIndex = -1;
            var proposed = (ComparisonInput[])_slots.Clone();
            proposed[slot] = input;
            ComparisonReport report = await ComputeAsync(proposed, ReportMode, ReportStartSeconds, null);
            _slots[slot] = input;
            if (currentInput) _currentSlotIndex = slot;
            _report = report;
            Render();
        }

        private static Task<ComparisonReport> ComputeAsync(ComparisonInput[] slots, ComparisonMode mode, double start, double? end)
        {
            var inputs = slots.Select((input, index) => input == null ? null : new ComparisonInput
            {
                Name = (char)('A' + index) + " · " + input.Name,
                Document = input.Document
            }).Where(input => input != null).ToArray();
            return Task.Run(() => inputs.Length == 0 ? null : SessionComparisonService.Build(inputs, mode, start, end));
        }

        private void SetBusy(bool busy, string title = "正在读取现场与计算统计…")
        {
            _busy = busy;
            this.FindControl<Border>("BusyOverlay").IsVisible = busy;
            this.FindControl<TextBlock>("BusyText").Text = title;
        }

        private void Status(string text)
        {
            TextBlock status = this.FindControl<TextBlock>("StatusText");
            if (status == null) return;
            status.Text = text;
            ToolTip.SetTip(status, text);
        }

        private void Render()
        {
            if (!_ready) return;
            int count = OccupiedInputs().Count;
            UpdateLanguageToggle();
            this.FindControl<TextBlock>("SlotSummaryText").Text = count + " / 5";
            this.FindControl<Border>("EmptyState").IsVisible = count == 0;
            RenderSessionCards();
            UpdateTabStyles();
            RenderContent();
            if (count == 0) Status("点击 A-E 任一卡片添加当前现场或 .motuperf 文件；缺测显示 --。");
            else if (_report == null) Status("现场已选择，等待统计结果。");
            else Status(count < 2 ? "已加载 1 份现场；再添加一份现场即可进行多曲线对比。缺测显示 --。"
                : "已加载 " + count + " 份现场 · 全时间范围 · 缺测显示 -- · 跨平台请核对指标口径");
        }

        private void RenderSessionCards()
        {
            for (int index = 0; index < _slots.Length; index++)
            {
                _slotCards[index].Child = BuildSessionCard(index, _slots[index]);
            }
        }

        private Control BuildSessionCard(int slot, ComparisonInput input)
        {
            Color color = Color.Parse(SlotColors[slot]);
            var root = new Grid
            {
                RowDefinitions = new RowDefinitions(input?.Document == null ? "*" : "Auto,Auto,Auto,*")
            };
            var badge = new Border
            {
                Width = 24, Height = 20, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = ((char)('A' + slot)).ToString(), Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            if (input?.Document == null)
            {
                var add = new Button
                {
                    Name = "SlotAdd-" + slot,
                    Content = "添加现场",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 104
                };
                add.Classes.Add("sessionAddButton");
                add.Click += async delegate { await PickFileAsync(slot); };
                var hint = new TextBlock
                {
                    Text = "选择 .motuperf 现场文件", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center
                };
                BindForeground(hint, "Theme.TextMuted", "#8A8F9A");
                var empty = new StackPanel
                {
                    Name = "SlotAddGroup-" + slot,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 8
                };
                empty.Children.Add(add);
                empty.Children.Add(hint);
                root.Children.Add(empty);
                return root;
            }

            SessionDocument document = input.Document;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto"), ColumnSpacing = 6 };
            header.Children.Add(badge);
            string app = First(document.App?.Name, document.Process?.Name, input.Name, "未命名现场");
            string sessionName = First(input.Name, app, "未命名现场");
            var title = new TextBlock
            {
                Name = "SlotTitle-" + slot, Text = sessionName, FontSize = 13, FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            BindForeground(title, "Theme.TextPrimary", "#E4E6EC");
            ToolTip.SetTip(title, sessionName);
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            var remove = new Button
            {
                Name = "SlotRemove-" + slot,
                Content = "×",
                Width = 24,
                Height = 24,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            remove.Classes.Add("sessionRemoveButton");
            ToolTip.SetTip(remove, "移除现场");
            remove.Click += async delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                await ReplaceAsync(slot, null);
            };
            Grid.SetColumn(remove, 2);
            header.Children.Add(remove);
            root.Children.Add(header);

            string platform = DevicePlatformNames.DisplayName(document.Device?.Platform);
            string device = First(document.Device?.MarketName, document.Device?.Name, platform + " 设备");
            var sourceText = new TextBlock
            {
                Name = "SlotApplication-" + slot, Text = app, FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0)
            };
            BindForeground(sourceText, "Theme.TextSecondary", "#B9BDC8");
            ToolTip.SetTip(sourceText, app);
            Grid.SetRow(sourceText, 1);
            root.Children.Add(sourceText);

            var deviceText = new TextBlock
            {
                Text = device + " · " + platform + (string.IsNullOrWhiteSpace(document.Device?.ProductVersion) ? "" : " " + document.Device.ProductVersion),
                FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0)
            };
            BindForeground(deviceText, "Theme.TextMuted", "#8A8F9A");
            ToolTip.SetTip(deviceText, deviceText.Text);
            Grid.SetRow(deviceText, 2);
            root.Children.Add(deviceText);

            List<PerfSample> samples = (document.Samples ?? new List<PerfSample>()).Where(sample => sample != null).ToList();
            double duration = samples.Count == 0 ? 0 : samples.Max(sample => sample.ElapsedSec);
            string sampleTime = document.StartedAt == default
                ? FormatDuration(duration)
                : document.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var meta = new TextBlock
            {
                Name = "SlotMeta-" + slot,
                Text = sampleTime + " · " + samples.Count.ToString("N0", CultureInfo.InvariantCulture) + " 样本",
                FontSize = 10, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Bottom
            };
            BindForeground(meta, "Theme.TextMuted", "#8A8F9A");
            string tooltip = document.StartedAt == default
                ? "样本时长：" + FormatDuration(duration) + "\n样本数：" + samples.Count.ToString("N0", CultureInfo.InvariantCulture)
                : "样本开始：" + document.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "\n样本时长：" + FormatDuration(duration) + "\n样本数：" + samples.Count.ToString("N0", CultureInfo.InvariantCulture);
            ToolTip.SetTip(meta, tooltip);
            Grid.SetRow(meta, 3);
            root.Children.Add(meta);
            return root;
        }

        private void UpdateTabStyles()
        {
            for (int index = 0; index < Categories.Length; index++)
            {
                _tabButtons[index].Content = CategoryTabLabel(Categories[index]);
                _tabButtons[index].Classes.Set("activeAnalysisTab", string.Equals(_activeCategory, Categories[index], StringComparison.Ordinal));
            }
        }

        private void RenderContent()
        {
            StackPanel content = this.FindControl<StackPanel>("ContentHost");
            content.Children.Clear();
            _sectionHosts.Clear();
            List<(int Slot, ComparisonInput Input)> inputs = OccupiedInputs();
            if (inputs.Count == 0) return;

            content.Children.Add(BuildOverviewSection(inputs));
            foreach (string category in Categories.Where(value => !string.Equals(value, "overview", StringComparison.Ordinal)))
            {
                content.Children.Add(BuildMetricSection(inputs, category));
            }
            Dispatcher.UIThread.Post(() => ScrollToCategory(_activeCategory), DispatcherPriority.Loaded);
        }

        private Control BuildOverviewSection(List<(int Slot, ComparisonInput Input)> inputs)
        {
            IReadOnlyList<ComparisonRow> overviewRows = OverviewRows(inputs.Count);
            var items = new List<Control> { BuildReportTable(inputs, overviewRows, compactHeaders: true) };

            if (overviewRows.Count == 0) items.Add(BuildEmptyNotice("当前现场没有可统计的样本，请重新导入或重新采集。"));
            return BuildAnalysisSection(CategoryTitle("overview"), CategoryDescription("overview"), "overview", items);
        }

        private Control BuildMetricSection(List<(int Slot, ComparisonInput Input)> inputs, string category)
        {
            List<MetricOption> metrics = CreateMetrics(inputs, category);
            if (metrics.Count == 0)
            {
                return BuildAnalysisSection(CategoryTitle(category), CategoryDescription(category), category,
                    new[] { BuildEmptyNotice("该分类没有可比较的数据。MoTuPerf 不会用 0 代替缺测。") });
            }

            _selectedMetrics.TryGetValue(category, out string selectedKey);
            MetricOption selected = metrics.FirstOrDefault(option => string.Equals(option.Key, selectedKey, StringComparison.Ordinal)) ?? metrics[0];
            _selectedMetrics[category] = selected.Key;
            IReadOnlyList<ComparisonRow> metricRows = selected.RowsForMetric != null
                ? selected.RowsForMetric(SummaryRows())
                : RowsForCategory(category);
            var items = new List<Control>
            {
                BuildMetricSelector(category, metrics, selected.Key),
                BuildReportTable(inputs, metricRows, compactHeaders: true),
                BuildChartSection(selected, inputs, 360)
            };
            if (category == "thermal")
            {
                items.Add(BuildEmptyNotice(_isChinese
                    ? "苹果系统热状态为 0～3，安卓系统热状态为 0～6；平台按独立指标展示，不直接叠加比较。"
                    : "iOS Thermal State 为 0～3，Android Thermal Status 为 0～6；平台按独立指标展示，不直接叠加比较。"));
            }
            return BuildAnalysisSection(CategoryTitle(category), CategoryDescription(category), category, items);
        }

        private void SelectMetric(string category, string metricKey)
        {
            if (_busy || string.IsNullOrWhiteSpace(metricKey)
                || !_sectionHosts.TryGetValue(category, out Border section) || section == null) return;

            StackPanel content = this.FindControl<StackPanel>("ContentHost");
            int index = content.Children.IndexOf(section);
            if (index < 0) return;

            _selectedMetrics[category] = metricKey;
            _activeCategory = category;
            UpdateTabStyles();

            ScrollViewer scroll = this.FindControl<ScrollViewer>("ContentScroll");
            double offset = scroll?.Offset.Y ?? 0;
            Control replacement = BuildMetricSection(OccupiedInputs(), category);
            _suppressScrollSync = true;
            content.Children.RemoveAt(index);
            content.Children.Insert(index, replacement);
            Dispatcher.UIThread.Post(delegate
            {
                if (scroll != null) scroll.Offset = new Vector(scroll.Offset.X, offset);
                _suppressScrollSync = false;
            }, DispatcherPriority.Loaded);
        }

        private Control BuildAnalysisSection(string title, string description, string category, IEnumerable<Control> items)
        {
            var section = new Border { Tag = category };
            section.Classes.Add("analysisSection");
            var root = new Grid { RowDefinitions = new RowDefinitions("64,Auto") };
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*,Auto"), Margin = new Thickness(20, 0) };
            heading.Children.Add(new Border
            {
                Width = 4, Height = 30, Background = ResourceBrush("Theme.Accent", "#4DA3FF"),
                CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });
            var titleStack = new StackPanel { Spacing = 2, Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleText = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold };
            BindForeground(titleText, "Theme.TextPrimary", "#E4E6EC");
            titleStack.Children.Add(titleText);
            var descriptionText = new TextBlock { Text = description, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            BindForeground(descriptionText, "Theme.TextSecondary", "#B9BDC8");
            titleStack.Children.Add(descriptionText);
            Grid.SetColumn(titleStack, 1);
            heading.Children.Add(titleStack);
            var count = new TextBlock { Text = OccupiedInputs().Count + " 份现场", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
            BindForeground(count, "Theme.TextMuted", "#8A8F9A");
            Grid.SetColumn(count, 2);
            heading.Children.Add(count);
            var headingBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = heading };
            headingBorder.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.Border"));
            root.Children.Add(headingBorder);

            var body = new StackPanel();
            foreach (Control item in items)
            {
                if (item != null) body.Children.Add(item);
            }
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            section.Child = root;
            _sectionHosts[category] = section;
            return section;
        }

        private Control BuildMetricSelector(string category, IReadOnlyList<MetricOption> metrics, string selectedKey)
        {
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (MetricOption metric in metrics)
            {
                MetricOption captured = metric;
                var chip = new Button { Content = MetricLabel(captured) };
                chip.Classes.Add("metricChip");
                chip.Classes.Set("activeMetricChip", string.Equals(captured.Key, selectedKey, StringComparison.Ordinal));
                ToolTip.SetTip(chip, MetricDescription(captured));
                chip.Click += delegate
                {
                    if (_busy) return;
                    SelectMetric(category, captured.Key);
                };
                chips.Children.Add(chip);
            }
            var toggle = new CheckBox
            {
                Name = "TimeTrendToggle-" + category,
                Content = TimeTrendLabel(),
                IsChecked = _alignTimeTrends,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggle.Classes.Add("timeTrendToggle");
            ToolTip.SetTip(toggle, TimeTrendTooltip());
            toggle.IsCheckedChanged += delegate
            {
                if (_syncingTimeTrendToggles) return;
                if (_busy)
                {
                    _syncingTimeTrendToggles = true;
                    toggle.IsChecked = _alignTimeTrends;
                    _syncingTimeTrendToggles = false;
                    return;
                }
                SetAlignTimeTrends(toggle.IsChecked == true);
            };

            var selector = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
            selector.Children.Add(chips);
            Grid.SetColumn(toggle, 1);
            selector.Children.Add(toggle);
            var border = new Border { Padding = new Thickness(20, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = selector };
            border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.Border"));
            return border;
        }

        private void SetAlignTimeTrends(bool enabled)
        {
            _alignTimeTrends = enabled;
            _syncingTimeTrendToggles = true;
            StackPanel content = this.FindControl<StackPanel>("ContentHost");
            if (content != null)
            {
                foreach (CheckBox toggle in content.GetVisualDescendants().OfType<CheckBox>()
                    .Where(control => control.Name != null && control.Name.StartsWith("TimeTrendToggle-", StringComparison.Ordinal)))
                {
                    toggle.IsChecked = enabled;
                }
                foreach (ComparisonChartControl chart in content.GetVisualDescendants().OfType<ComparisonChartControl>())
                    chart.AlignTimeTrends = enabled;
            }
            _syncingTimeTrendToggles = false;
        }

        private Control BuildChartSection(MetricOption metric, List<(int Slot, ComparisonInput Input)> inputs, double height)
        {
            var stack = new StackPanel { Spacing = 8, Margin = new Thickness(20, 12, 20, 18) };
            var title = new TextBlock
            {
                Name = "ChartTitle-" + metric.Key, Text = MetricLabel(metric), FontSize = 13, FontWeight = FontWeight.SemiBold
            };
            BindForeground(title, "Theme.TextPrimary", "#E4E6EC");
            stack.Children.Add(title);

            var chart = new ComparisonChartControl
            {
                Height = height,
                Series = BuildSeries(metric, inputs),
                AlignTimeTrends = _alignTimeTrends,
                Unit = MetricUnit(metric.Unit),
                Threshold = metric.Threshold,
                Minimum = metric.Minimum,
                Maximum = metric.Maximum,
                IntegerAxis = metric.IntegerAxis,
                EmptyText = "当前区间没有 " + MetricLabel(metric) + " 数据",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            stack.Children.Add(chart);
            return stack;
        }

        private IReadOnlyList<ComparisonChartSeries> BuildSeries(MetricOption metric, List<(int Slot, ComparisonInput Input)> inputs)
        {
            return inputs.Select(entry =>
            {
                SessionDocument document = entry.Input.Document;
                IReadOnlyList<PerfSample> samples = (document.Samples ?? new List<PerfSample>())
                    .Where(sample => sample != null && sample.ElapsedSec >= ReportStartSeconds)
                    .OrderBy(sample => sample.ElapsedSec)
                    .ToList();
                return new ComparisonChartSeries
                {
                    Name = (char)('A' + entry.Slot) + " · " + entry.Input.Name,
                    Color = SlotColors[entry.Slot],
                    DurationSec = samples.Count == 0 ? 0 : Math.Max(0, samples[samples.Count - 1].ElapsedSec),
                    Points = metric.Points(samples).ToList()
                };
            }).ToList();
        }

        private Control BuildReportTable(List<(int Slot, ComparisonInput Input)> inputs, IReadOnlyList<ComparisonRow> rows,
            bool compactHeaders = false)
        {
            var border = new Border { Padding = new Thickness(0), Margin = new Thickness(20, 0, 20, 0) };
            if (rows == null || rows.Count == 0)
            {
                border.Child = BuildEmptyNotice("当前分类没有可展示的统计数据。");
                return border;
            }

            const double sessionWidth = 205;
            const double metricWidth = 120;
            double tableHeight = 36 + inputs.Count * 34;
            string rowSpecification = "36" + string.Concat(Enumerable.Repeat(",34", inputs.Count));

            var frozenColumns = new ColumnDefinitions();
            frozenColumns.Add(new ColumnDefinition { Width = new GridLength(sessionWidth), MinWidth = sessionWidth });
            var frozenTable = new Grid
            {
                Name = "FrozenSessionColumn",
                Width = sessionWidth,
                RowDefinitions = new RowDefinitions(rowSpecification),
                ColumnDefinitions = frozenColumns
            };
            Border sessionHeader = BuildTableCell("现场", true, 0, 0);
            sessionHeader.Name = "SessionHeaderCell";
            frozenTable.Children.Add(sessionHeader);

            var metricColumns = new ColumnDefinitions();
            for (int column = 0; column < rows.Count; column++)
            {
                metricColumns.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = metricWidth });
            }
            var metricTable = new Grid
            {
                Name = "MetricTableContent",
                MinWidth = rows.Count * metricWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RowDefinitions = new RowDefinitions(rowSpecification),
                ColumnDefinitions = metricColumns
            };
            for (int column = 0; column < rows.Count; column++)
            {
                ComparisonRow row = rows[column];
                string label = compactHeaders
                    ? First(row.SubLabel, row.Label)
                    : string.IsNullOrWhiteSpace(row.Group) ? row.Label : row.Group;
                if (!compactHeaders && !string.IsNullOrWhiteSpace(row.SubLabel) && !string.Equals(row.SubLabel, row.Group, StringComparison.Ordinal))
                    label += " / " + row.SubLabel;
                bool unitIncludedInCompactLabel = compactHeaders
                    && (row.Group == "帧率 / 卡顿" || row.Group == "帧耗时");
                label = DisplayRowLabel(label, row.Group);
                if (!unitIncludedInCompactLabel && !string.IsNullOrWhiteSpace(row.Unit)) label += " (" + MetricUnit(row.Unit) + ")";
                Border cell = BuildTableCell(label, true, 0, column);
                ToolTip.SetTip(cell.Child, string.IsNullOrWhiteSpace(row.Hint) ? row.Label : row.Hint);
                metricTable.Children.Add(cell);
            }
            for (int rowIndex = 0; rowIndex < inputs.Count; rowIndex++)
            {
                (int slot, ComparisonInput input) = inputs[rowIndex];
                SessionDocument document = input.Document;
                string device = First(document.Device?.MarketName, document.Device?.Name, DevicePlatformNames.DisplayName(document.Device?.Platform) + " 设备");
                string app = First(document.App?.Name, document.Process?.Name, input.Name);
                string sessionName = First(input.Name, app, "未命名现场");
                string label = (char)('A' + slot) + " · " + sessionName + "\n" + device;
                Border sessionCell = BuildTableCell(label, false, rowIndex + 1, 0, SlotColors[slot], wrap: false);
                sessionCell.Name = "SessionCell-" + slot;
                ToolTip.SetTip(sessionCell.Child, sessionName + (string.Equals(app, sessionName, StringComparison.Ordinal) ? "" : "\n应用：" + app) + "\n" + device);
                frozenTable.Children.Add(sessionCell);
                for (int column = 0; column < rows.Count; column++)
                {
                    string value = rows[column].Values != null && rowIndex < rows[column].Values.Count ? rows[column].Values[rowIndex] : "--";
                    Border data = BuildTableCell(value, false, rowIndex + 1, column);
                    Avalonia.Automation.AutomationProperties.SetAutomationId(data.Child, rows[column].Label + "-" + (char)('A' + slot));
                    metricTable.Children.Add(data);
                }
            }

            var metricScroll = new ScrollViewer
            {
                Name = "MetricTableScroll",
                Content = metricTable,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                ClipToBounds = true
            };

            var horizontalBar = new ScrollBar
            {
                Name = "MetricTableScrollBar",
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
                Minimum = 0,
                Maximum = 0,
                ViewportSize = 0,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            bool syncingScrollPosition = false;
            void SyncBarFromViewer()
            {
                double maximum = Math.Max(0, metricScroll.Extent.Width - metricScroll.Viewport.Width);
                syncingScrollPosition = true;
                horizontalBar.ViewportSize = Math.Max(0, metricScroll.Viewport.Width);
                horizontalBar.Maximum = maximum;
                horizontalBar.Value = Math.Min(maximum, Math.Max(0, metricScroll.Offset.X));
                horizontalBar.IsVisible = maximum > 0.5;
                syncingScrollPosition = false;
            }
            metricScroll.ScrollChanged += delegate { SyncBarFromViewer(); };
            metricScroll.SizeChanged += delegate { SyncBarFromViewer(); };
            horizontalBar.ValueChanged += delegate
            {
                if (syncingScrollPosition) return;
                syncingScrollPosition = true;
                metricScroll.Offset = new Vector(horizontalBar.Value, metricScroll.Offset.Y);
                syncingScrollPosition = false;
            };

            var tableViewport = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{sessionWidth},*"),
                Height = tableHeight
            };
            tableViewport.Children.Add(frozenTable);
            Grid.SetColumn(metricScroll, 1);
            tableViewport.Children.Add(metricScroll);

            var tableLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
            tableLayout.Children.Add(tableViewport);
            Grid.SetRow(horizontalBar, 1);
            tableLayout.Children.Add(horizontalBar);
            border.Child = tableLayout;
            Dispatcher.UIThread.Post(SyncBarFromViewer, DispatcherPriority.Loaded);
            return border;
        }

        private static Border BuildTableCell(string text, bool heading, int row, int column, string accent = "", bool wrap = true)
        {
            var label = new TextBlock
            {
                Text = text ?? "--", FontSize = heading ? 11 : 12, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = heading || !wrap ? TextWrapping.NoWrap : TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, MaxLines = heading ? 1 : 2
            };
            if (!string.IsNullOrWhiteSpace(accent))
            {
                label.Foreground = Brush.Parse(accent);
                label.FontWeight = FontWeight.SemiBold;
            }
            else BindForeground(label, "Theme.TextPrimary", "#E4E6EC");
            var border = new Border
            {
                Child = label, Padding = new Thickness(9, 5), MinHeight = heading ? 36 : 34,
                BorderThickness = new Thickness(0, 0, 1, 1)
            };
            border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.DialogSeparator"));
            if (heading) border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Theme.PanelHeaderBackground"));
            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            return border;
        }

        private Control BuildEmptyNotice(string text)
        {
            var border = new Border { Padding = new Thickness(20, 16) };
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12 };
            BindForeground(label, "Theme.TextSecondary", "#B9BDC8");
            border.Child = label;
            return border;
        }

        private IReadOnlyList<ComparisonRow> RowsForCategory(string category)
        {
            if (_report?.Rows == null) return Array.Empty<ComparisonRow>();
            IEnumerable<ComparisonRow> rows = _report.Rows.Where(row => !row.IsDetail);
            switch (category)
            {
                case "fps": return rows.Where(row => row.Group == "帧率 / 卡顿").ToList();
                case "frametime": return rows.Where(row => row.Group == "帧耗时").ToList();
                case "cpu": return rows.Where(row => row.Group == "进程 CPU" || row.Group == "设备逐核负载").ToList();
                case "memory": return rows.Where(row => row.Group == "进程内存").ToList();
                case "temperature": return rows.Where(row => row.Group == "温度").ToList();
                case "thermal": return rows.Where(row => row.Group == "系统热状态").ToList();
                default: return rows.ToList();
            }
        }

        private IReadOnlyList<ComparisonRow> SummaryRows()
        {
            return _report?.Rows == null
                ? Array.Empty<ComparisonRow>()
                : _report.Rows.Where(row => row != null && !row.IsDetail).ToList();
        }

        private static IReadOnlyList<ComparisonRow> RowsByLabel(IReadOnlyList<ComparisonRow> rows, params string[] labels)
        {
            var lookup = new Dictionary<string, ComparisonRow>(StringComparer.Ordinal);
            foreach (ComparisonRow row in rows ?? Array.Empty<ComparisonRow>())
            {
                if (row == null || row.IsDetail) continue;
                lookup.TryAdd(row.Label, row);
            }
            return labels.Select(label => lookup.TryGetValue(label, out ComparisonRow row) ? row : null)
                .Where(row => row != null).ToList();
        }

        private IReadOnlyList<ComparisonRow> OverviewRows(int inputCount)
        {
            if (_report?.Rows == null) return Array.Empty<ComparisonRow>();
            var selected = new List<ComparisonRow>();
            AddLabel(selected, "FPS", "FPS 1% Low", "FPS Std", "FPS Var", "FPS >=18%", "FPS >=25%",
                "FPS Drop", "FPS Min", "FPS Median", "FPS MedRange");
            AddLabel(selected, "TinyJank 发生率", "SmallJank 发生率", "Jank 发生率（不含 BigJank）",
                "BigJank 发生率", "卡顿时长占比");
            AddLabel(selected, "FrameTime", "FrameTime Std", "FrameTime Var", "FrameTime >=100ms",
                "FrameTime Delta>100ms/h");
            AddLabel(selected, "进程 CPU Raw");
            AddLabel(selected, row => row.Label.StartsWith("进程 CPU Normalized", StringComparison.Ordinal));
            AddBestGroup(selected, "进程内存", 3, MemoryOverviewPriority);
            AddBestGroup(selected, "温度", 3, TemperatureOverviewPriority);
            AddBestGroup(selected, "系统热状态", 3, _ => 0);
            return selected;
        }

        private void AddLabel(List<ComparisonRow> rows, params string[] labels)
        {
            foreach (string label in labels)
                AddFirst(rows, row => string.Equals(row.Label, label, StringComparison.Ordinal));
        }

        private void AddLabel(List<ComparisonRow> rows, Func<ComparisonRow, bool> predicate)
        {
            AddFirst(rows, predicate);
        }

        private void AddBestGroup(List<ComparisonRow> rows, string group, int maximum, Func<ComparisonRow, int> tieBreak)
        {
            List<ComparisonRow> candidates = _report.Rows
                .Where(row => !row.IsDetail && string.Equals(row.Group, group, StringComparison.Ordinal) && !rows.Contains(row))
                .ToList();
            List<ComparisonRow> populated = candidates
                .Where(row => ValidValueCount(row) > 0)
                .OrderByDescending(ValidValueCount)
                .ThenByDescending(tieBreak)
                .ThenBy(row => row.Label, StringComparer.Ordinal)
                .Take(Math.Max(1, maximum))
                .ToList();
            if (populated.Count > 0)
            {
                rows.AddRange(populated);
                return;
            }

            ComparisonRow fallback = candidates
                .OrderByDescending(tieBreak)
                .ThenBy(row => row.Label, StringComparer.Ordinal)
                .FirstOrDefault();
            if (fallback != null) rows.Add(fallback);
        }

        private static int ValidValueCount(ComparisonRow row)
        {
            return row.Values == null ? 0 : row.Values.Count(value => !string.Equals(value, "--", StringComparison.Ordinal));
        }

        private static int MemoryOverviewPriority(ComparisonRow row)
        {
            string label = row.SubLabel ?? row.Label ?? "";
            if (label.IndexOf("physical_footprint", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (label.IndexOf("footprint", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (label.IndexOf("pss", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (label.IndexOf("rss", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 0;
        }

        private static int TemperatureOverviewPriority(ComparisonRow row)
        {
            string label = row.SubLabel ?? row.Label ?? "";
            if (label.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (label.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (label.IndexOf("soc", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (label.IndexOf("npu", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 0;
        }

        private void AddFirst(List<ComparisonRow> rows, Func<ComparisonRow, bool> predicate)
        {
            ComparisonRow row = _report.Rows.FirstOrDefault(candidate => !candidate.IsDetail && !rows.Contains(candidate) && predicate(candidate));
            if (row != null) rows.Add(row);
        }

        private List<(int Slot, ComparisonInput Input)> OccupiedInputs()
        {
            return Enumerable.Range(0, _slots.Length).Where(index => _slots[index]?.Document != null)
                .Select(index => (Slot: index, Input: _slots[index])).ToList();
        }

        private List<MetricOption> CreateMetrics(List<(int Slot, ComparisonInput Input)> inputs, string category)
        {
            var options = new List<MetricOption>();
            if (category == "fps")
            {
                options.Add(new MetricOption("fps", "FPS", "帧/s", "可信帧观测得到的 FPS；缺测断线。", 30, 0, null, false,
                    samples => Points(samples, sample => sample.HasFps && sample.FpsUpdated ? sample.Fps : (double?)null),
                    rows => RowsByLabel(rows, "FPS", "FPS Std", "FPS Var", "FPS >=18%", "FPS >=25%",
                        "FPS Drop", "FPS Min", "FPS Median", "FPS MedRange")));
                options.Add(new MetricOption("smooth", "Smooth", "指数", "Smooth 稳帧指数；当前采集模型没有该字段，按缺测显示。",
                    null, 0, null, false, samples => Enumerable.Empty<ComparisonChartPoint>(),
                    rows => RowsByLabel(rows, "Smooth")));
                options.Add(new MetricOption("fps-1low", "1%Low(FPS)", "帧/s", "完整逐帧数据中最慢 1% 帧换算的 FPS；窗口汇总数据保持缺测。",
                    null, 0, null, false, samples => Enumerable.Empty<ComparisonChartPoint>(),
                    rows => RowsByLabel(rows, "FPS 1% Low")));
                options.Add(new MetricOption("smalljank", "SmallJank", "次/10min", "SmallJank 发生率；当前采集模型没有该字段，按缺测显示。",
                    null, 0, null, false, samples => Enumerable.Empty<ComparisonChartPoint>(),
                    rows => RowsByLabel(rows, "SmallJank 发生率")));
                options.Add(new MetricOption("jank", "Jank", "次/窗", "包含 BigJank，仅取可信帧窗口。", null, 0, null, false,
                    samples => Points(samples, sample => sample.FpsUpdated && sample.HasJank && MetricStatistics.IsReliableJankSample(sample) ? sample.Jank : (double?)null),
                    rows => RowsByLabel(rows, "Jank 发生率（不含 BigJank）")));
                options.Add(new MetricOption("bigjank", "BigJank", "次/窗", "可信帧窗口中的 BigJank 次数。", null, 0, null, false,
                    samples => Points(samples, sample => sample.FpsUpdated && sample.HasJank && MetricStatistics.IsReliableJankSample(sample) ? sample.BigJank : (double?)null),
                    rows => RowsByLabel(rows, "BigJank 发生率")));
                options.Add(new MetricOption("stutter", "Stutter", "%", "有效卡顿时间 / 有效观测时间。", null, 0, 100, false,
                    samples => Points(samples, sample => sample.HasStutter ? sample.StutterPercent : (double?)null),
                    rows => RowsByLabel(rows, "卡顿时长占比")));
            }
            else if (category == "frametime")
            {
                options.Add(new MetricOption("frametime", "FrameTime (Max)", "ms", "窗口最大帧间隔；缺测断线。", 100, 0, null, false,
                    samples => Points(samples, FrameTimeMax), rows => RowsByLabel(rows, "FrameTime Max")));
                options.Add(new MetricOption("frametime-mean", "FrameTime Mean", "ms", "窗口平均帧间隔；仅真实更新样本。", null, 0, null, false,
                    samples => Points(samples, sample => sample.HasFrameTimeMean ? sample.FrameTimeMeanMs : (double?)null),
                    rows => RowsByLabel(rows, "FrameTime")));
                options.Add(new MetricOption("frametime-p95", "FrameTime P95", "ms", "逐帧间隔的 P95；旧现场缺测时保持 --。", null, 0, null, false,
                    samples => Points(samples, sample => sample.HasFrameTimeP95 ? sample.FrameTimeP95Ms : (double?)null),
                    rows => RowsByLabel(rows, "FrameTime P95")));
            }
            else if (category == "cpu")
            {
                options.Add(new MetricOption("cpu-raw", "进程 CPU Raw", "%", "所选进程原始 CPU 使用率。", null, 0, 100, false,
                    samples => Points(samples, sample => sample.HasCpu && sample.CpuUpdated ? sample.CpuPercent : (double?)null),
                    rows => RowsByLabel(rows, "进程 CPU Raw")));
                options.Add(new MetricOption("cpu-normalized", "进程 CPU Normalized", "%", "按在线核数归一化，不与原始 CPU 混算。", null, 0, 100, false,
                    samples => Points(samples, sample => sample.HasCpuNormalized && sample.CpuNormalizedUpdated ? sample.CpuNormalizedPercent : (double?)null),
                    rows => RowsByLabel(rows, "进程 CPU Normalized")));
                int cores = Math.Min(32, inputs.SelectMany(entry => entry.Input.Document.Samples ?? new List<PerfSample>())
                    .Where(sample => sample != null && sample.HasCpuCoreUsage && sample.CpuCoreUpdated)
                    .Select(sample => sample.CpuCorePercents?.Count ?? 0).DefaultIfEmpty(0).Max());
                for (int core = 0; core < cores; core++)
                {
                    int captured = core;
                    options.Add(new MetricOption("cpu-core-" + captured, "设备核心 " + captured, "%", "设备逐核负载，不是应用逐核占用。", null, 0, 100, false,
                        samples => Points(samples, sample => sample.HasCpuCoreUsage && sample.CpuCoreUpdated && sample.CpuCorePercents != null
                            && sample.CpuCorePercents.Count > captured ? sample.CpuCorePercents[captured] : (double?)null),
                        rows => RowsByLabel(rows, "设备 CPU " + captured)));
                }
            }

            else if (category == "memory")
            {
                var memoryMetrics = inputs.Select(entry => MetricStatistics.PreferredMemoryMetric(entry.Input.Document.Samples ?? new List<PerfSample>()))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string memoryMetric in memoryMetrics)
                {
                    string captured = memoryMetric;
                    options.Add(new MetricOption("memory-" + captured, captured.ToUpperInvariant(), "MB",
                        "仅比较 " + captured + " 口径，不把 PSS、Footprint、RSS 混为同一序列。", null, 0, null, false,
                        samples => Points(samples, sample => MetricStatistics.IsPreferredMemorySample(sample, captured) ? sample.MemoryMb : (double?)null),
                        rows => RowsByLabel(rows, "进程内存 / " + captured)));
                }
            }
            else if (category == "temperature")
            {
                var sensors = PerformanceChartControl.SortTemperatureSensorNames(inputs.SelectMany(entry => entry.Input.Document.Samples ?? new List<PerfSample>())
                    .Where(sample => sample != null && sample.HasTemperature && sample.TemperatureUpdated && sample.TemperatureCelsius != null)
                    .SelectMany(sample => sample.TemperatureCelsius.Keys));
                foreach (string sensor in sensors)
                {
                    string captured = sensor;
                    options.Add(new MetricOption("temperature-" + captured, captured, "°C",
                        "同名传感器在不同机型上的位置和含义可能不同，请结合来源判断。", null, -20, 120, false,
                        samples => Points(samples, sample => sample.HasTemperature && sample.TemperatureUpdated && sample.TemperatureCelsius != null
                            && sample.TemperatureCelsius.TryGetValue(captured, out double value) ? value : (double?)null),
                        rows => RowsByLabel(rows, "温度 / " + captured)));
                }
            }
            else if (category == "thermal")
            {
                var platforms = inputs.Select(entry => (entry.Input.Document.Device?.Platform ?? "未知").ToLowerInvariant()).Distinct().ToList();
                foreach (string platform in platforms)
                {
                    string captured = platform;
                    bool android = string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase);
                    bool ios = string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase);
                    if (!android && !ios) continue;
                    int max = android ? 6 : 3;
                    options.Add(new MetricOption("thermal-" + captured, DevicePlatformNames.DisplayName(platform), "级别",
                        android ? "Android Thermal Status：0 正常至 6 关机。" : "iOS Thermal State：0 正常至 3 临界。", null, 0, max, true,
                        samples => Points(samples, sample => sample.HasThermalState && sample.ThermalStateUpdated
                            && sample.ThermalStateLevel >= 0 && sample.ThermalStateLevel <= max ? sample.ThermalStateLevel : (double?)null),
                        rows => RowsByLabel(rows, "热状态 / " + captured + "（最高）")));
                }
            }
            return options;
        }

        private static IEnumerable<ComparisonChartPoint> Points(IReadOnlyList<PerfSample> samples, Func<PerfSample, double?> selector)
        {
            foreach (PerfSample sample in samples)
            {
                double? value = selector(sample);
                if (value.HasValue && double.IsFinite(value.Value)) yield return new ComparisonChartPoint { TimeSec = sample.ElapsedSec, Value = value.Value };
            }
        }

        private static double? FrameTimeMax(PerfSample sample)
        {
            if (sample.HasFrameTimeMax) return sample.FrameTimeMaxMs;
            if (sample.HasFrameTime) return sample.FrameTimeMs;
            if (sample.HasFrameTimeMean) return sample.FrameTimeMeanMs;
            return null;
        }

        private string CategoryTabLabel(string category)
        {
            if (!_isChinese)
            {
                int index = Array.IndexOf(Categories, category);
                return index >= 0 ? CategoryLabels[index] : category;
            }

            switch (category)
            {
                case "fps": return "帧率";
                case "frametime": return "帧耗时 / 卡顿";
                case "cpu": return "处理器";
                case "memory": return "内存";
                case "temperature": return "温度";
                case "thermal": return "系统热状态";
                default: return "概览";
            }
        }

        private string CategoryTitle(string category)
        {
            if (!_isChinese)
            {
                switch (category)
                {
                    case "fps": return "FPS / 帧率";
                    case "frametime": return "FrameTime / Jank";
                    case "cpu": return "CPU";
                    case "memory": return "内存 / Memory";
                    case "temperature": return "温度 / Battery Temperature";
                    case "thermal": return "系统热状态 / Thermal State";
                    default: return "概览";
                }
            }

            switch (category)
            {
                case "fps": return "帧率";
                case "frametime": return "帧耗时 / 卡顿";
                case "cpu": return "处理器";
                case "memory": return "内存";
                case "temperature": return "温度";
                case "thermal": return "系统热状态";
                default: return "概览";
            }
        }

        private string CategoryDescription(string category)
        {
            if (!_isChinese)
            {
                switch (category)
                {
                    case "fps": return "切换 FPS、Smooth、1%Low、SmallJank、Jank、BigJank 与 Stutter；统计表只显示当前口径。";
                    case "frametime": return "切换窗口最大、均值与 P95 帧间隔；统计表随子页签同步。";
                    case "cpu": return "原始 CPU、归一化 CPU 与设备逐核负载分开呈现，统计表只显示当前口径。";
                    case "memory": return "按 PSS、Physical Footprint、RSS 等真实口径分别比较。";
                    case "temperature": return "按传感器名称查看温度，跨机型同名传感器不保证位置相同。";
                    case "thermal": return "iOS 与 Android 使用独立最大级别，避免不同平台数值直接混合。";
                    default: return "概览仅汇总 PerfDog 可验证统计；曲线见后续 FPS、FrameTime、CPU 等分类区块，所有缺测均保持 --。";
                }
            }

            switch (category)
            {
                case "fps": return "切换帧率、稳帧指数、1% 低帧率、轻微卡顿、卡顿、严重卡顿与卡顿时长占比；统计表只显示当前口径。";
                case "frametime": return "切换最大帧耗时、平均帧耗时与 P95 帧耗时；统计表随子页签同步。";
                case "cpu": return "原始处理器占用、归一化处理器占用与设备逐核负载分开呈现，统计表只显示当前口径。";
                case "memory": return "按进程专用内存、物理内存占用、常驻内存等真实口径分别比较。";
                case "temperature": return "按传感器名称查看温度，跨机型同名传感器不保证位置相同。";
                case "thermal": return "苹果系统与安卓系统使用独立最大等级，避免不同平台数值直接混合。";
                default: return "概览仅汇总可验证统计；曲线见后续帧率、帧耗时、处理器等分类区块，所有缺测均保持 --。";
            }
        }

        private string TimeTrendLabel() => _isChinese ? "相同时间趋势" : "Align Time Trends";

        private string TimeTrendTooltip() => _isChinese
            ? "勾选后，将所有现场曲线按比例拉伸到相同最长时长；只影响图表查看，不改变统计数据或现场文件。取消勾选恢复原始时间轴。"
            : "Stretch every session to the same longest duration for comparison. This only changes the chart view, not statistics or session files. Clear it to restore the original timeline.";

        private string MetricLabel(MetricOption metric)
        {
            if (!_isChinese) return metric.Label;
            switch (metric.Key)
            {
                case "fps": return "帧率";
                case "smooth": return "稳帧指数";
                case "fps-1low": return "1% 低帧率";
                case "smalljank": return "轻微卡顿";
                case "jank": return "卡顿";
                case "bigjank": return "严重卡顿";
                case "stutter": return "卡顿时长占比";
                case "frametime": return "帧耗时（最大）";
                case "frametime-mean": return "平均帧耗时";
                case "frametime-p95": return "帧耗时 P95";
                case "cpu-raw": return "原始处理器占用";
                case "cpu-normalized": return "归一化处理器占用";
                case "thermal-android": return "安卓";
                case "thermal-ios": return "苹果系统";
            }

            if (metric.Key.StartsWith("cpu-core-", StringComparison.Ordinal)) return "核心 " + metric.Key.Substring("cpu-core-".Length) + " 负载";
            if (metric.Key.StartsWith("memory-", StringComparison.Ordinal)) return DisplayMemoryMetric(metric.Label);
            if (metric.Key.StartsWith("temperature-", StringComparison.Ordinal)) return DisplaySensorName(metric.Label);
            return metric.Label;
        }

        private string MetricDescription(MetricOption metric)
        {
            if (!_isChinese) return metric.Description;
            switch (metric.Key)
            {
                case "fps": return "可信帧观测得到的帧率；缺测时曲线断开。";
                case "smooth": return "稳帧指数；当前采集模型没有该字段，按缺测显示。";
                case "fps-1low": return "完整逐帧数据中最慢 1% 帧换算的帧率；窗口汇总数据保持缺测。";
                case "smalljank": return "轻微卡顿发生率；当前采集模型没有该字段，按缺测显示。";
                case "jank": return "包含严重卡顿，仅取可信帧窗口。";
                case "bigjank": return "可信帧窗口中的严重卡顿次数。";
                case "stutter": return "有效卡顿时间占有效观测时间的比例。";
                case "frametime": return "窗口最大帧耗时；缺测时曲线断开。";
                case "frametime-mean": return "窗口平均帧耗时；仅使用真实更新样本。";
                case "frametime-p95": return "逐帧间隔的 P95；旧现场缺测时保持 --。";
                case "cpu-raw": return "所选进程的原始处理器占用率。";
                case "cpu-normalized": return "按在线核心数归一化，不与原始处理器占用混算。";
                case "thermal-android": return "安卓系统热状态：0 为正常，6 为关机。";
                case "thermal-ios": return "苹果系统热状态：0 为正常，3 为临界。";
            }

            if (metric.Key.StartsWith("cpu-core-", StringComparison.Ordinal)) return "设备逐核负载，并非应用逐核占用。";
            if (metric.Key.StartsWith("memory-", StringComparison.Ordinal))
            {
                string memory = metric.Key.Substring("memory-".Length);
                return "仅比较 " + DisplayMemoryMetric(memory) + " 口径，不把不同内存口径混为同一序列。";
            }
            if (metric.Key.StartsWith("temperature-", StringComparison.Ordinal)) return "同名传感器在不同机型上的位置和含义可能不同，请结合来源判断。";
            return metric.Description;
        }

        private string MetricUnit(string unit)
        {
            if (!_isChinese || string.IsNullOrWhiteSpace(unit)) return unit;
            switch (unit)
            {
                case "帧/s": return "帧/秒";
                case "次/窗": return "次/窗口";
                case "次/10min": return "次/10分钟";
                case "次/h": return "次/小时";
                case "ms": return "毫秒";
                case "级别": return "等级";
                default: return unit;
            }
        }

        private string DisplayRowLabel(string label, string group)
        {
            if (!_isChinese || string.IsNullOrWhiteSpace(label)) return label;
            switch (group)
            {
                case "帧率 / 卡顿":
                    switch (label)
                    {
                        case "Avg(FPS)": return "平均帧率";
                        case "Smooth(稳帧指数)": return "稳帧指数";
                        case "1% Low(FPS)": return "1% 低帧率";
                        case "Std(FPS)": return "帧率标准差";
                        case "Var(FPS)": return "帧率方差";
                        case "FPS >= 18 [%]": return "帧率 ≥18 占比";
                        case "FPS >= 25 [%]": return "帧率 ≥25 占比";
                        case "Drop(FPS)[/h]": return "掉帧频率（次/小时）";
                        case "Min(FPS)": return "最低帧率";
                        case "Median(FPS)": return "帧率中位数";
                        case "MedRange(FPS)[%]": return "帧率中位区间占比";
                        case "TinyJank(/10min)": return "极轻微卡顿（次/10分钟）";
                        case "SmallJank(/10min)": return "小卡顿（次/10分钟）";
                        case "Jank(/10min)": return "卡顿（次/10分钟，不含严重卡顿）";
                        case "Jank(/10min, 含 BigJank)": return "卡顿（次/10分钟，含严重卡顿）";
                        case "BigJank(/10min)": return "严重卡顿（次/10分钟）";
                        case "Stutter[%]": return "卡顿时长占比";
                        case "卡顿时长占比": return "卡顿时长占比";
                    }
                    break;
                case "帧耗时":
                    switch (label)
                    {
                        case "Avg(FrameTime)[ms]": return "平均帧耗时（毫秒）";
                        case "Max(FrameTime)[ms]": return "最大帧耗时（毫秒）";
                        case "Window P95(FrameTime)[ms]": return "帧耗时 P95（毫秒）";
                        case "Std(FrameTime)[ms]": return "帧耗时标准差（毫秒）";
                        case "Var(FrameTime)[ms^2]": return "帧耗时方差（毫秒²）";
                        case "FrameTime >= 100ms [%]": return "帧耗时 ≥100 毫秒占比";
                        case "Delta(FrameTime)>100ms/h": return "帧耗时突增（次/小时）";
                    }
                    break;
                case "进程 CPU":
                    if (label == "Raw") return "原始处理器占用";
                    if (label == "Normalized") return "归一化处理器占用";
                    break;
                case "设备逐核负载":
                    if (label.StartsWith("CPU ", StringComparison.Ordinal)) return "核心 " + label.Substring("CPU ".Length) + " 负载";
                    break;
                case "进程内存": return DisplayMemoryMetric(label);
                case "温度": return DisplaySensorName(label);
                case "系统热状态":
                    return label.Replace("android", "安卓", StringComparison.OrdinalIgnoreCase)
                        .Replace("ios", "苹果系统", StringComparison.OrdinalIgnoreCase)
                        .Replace("（最高）", "（最高等级）", StringComparison.Ordinal);
            }
            return label;
        }

        private static string DisplayMemoryMetric(string metric)
        {
            if (string.IsNullOrWhiteSpace(metric)) return metric;
            switch (metric.Trim().ToLowerInvariant())
            {
                case "pss": return "进程专用内存（PSS）";
                case "rss": return "常驻内存（RSS）";
                case "physical_footprint":
                case "footprint": return "物理内存占用";
                default: return metric;
            }
        }

        private static string DisplaySensorName(string sensor)
        {
            if (string.IsNullOrWhiteSpace(sensor)) return sensor;
            switch (sensor.Trim().ToLowerInvariant())
            {
                case "battery": return "电池";
                case "cpu": return "处理器";
                case "gpu": return "图形处理器";
                case "npu": return "神经网络处理器";
                case "soc": return "系统级芯片";
                default: return sensor;
            }
        }

        internal Border SlotCardForTest(int slot) => _slotCards[slot];
        internal Button TabButtonForTest(string category)
        {
            int index = Array.IndexOf(Categories, category);
            return index < 0 ? null : _tabButtons[index];
        }

        internal string ActiveCategoryForTest => _activeCategory;
        internal string ActiveMetricForTest => _selectedMetrics.TryGetValue(_activeCategory, out string metric) ? metric : "";
        internal ComparisonReport ReportForTest => _report;
        internal IReadOnlyList<string> OverviewLabelsForTest => OverviewRows(OccupiedInputs().Count).Select(row => row.Label).ToList();
        internal double AppliedStartForTest => ReportStartSeconds;
        internal double? AppliedEndForTest => null;
        internal Border SectionForTest(string category) => _sectionHosts.TryGetValue(category, out Border section) ? section : null;

        internal void SetCategoryForTest(string category)
        {
            NavigateToCategory(category);
        }

        internal void SetMetricForTest(string metric)
        {
            SelectMetric(_activeCategory, metric);
        }

        private static string FormatDuration(double seconds)
        {
            seconds = Math.Max(0, seconds);
            int minutes = (int)(seconds / 60);
            int remaining = (int)Math.Round(seconds - minutes * 60);
            return minutes + ":" + remaining.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

        private static IBrush ResourceBrush(string key, string fallback)
        {
            object value;
            if (Application.Current != null && Application.Current.TryFindResource(key, out value) && value is IBrush brush) return brush;
            return Brush.Parse(fallback);
        }

        private static void BindForeground(TextBlock text, string key, string fallback)
        {
            text.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));
            if (text.Foreground == null) text.Foreground = Brush.Parse(fallback);
        }

        private sealed class MetricOption
        {
            public MetricOption(string key, string label, string unit, string description, double? threshold,
                double? minimum, double? maximum, bool integerAxis, Func<IReadOnlyList<PerfSample>, IEnumerable<ComparisonChartPoint>> points,
                Func<IReadOnlyList<ComparisonRow>, IReadOnlyList<ComparisonRow>> rowsForMetric = null)
            {
                Key = key;
                Label = label;
                Unit = unit;
                Description = description;
                Threshold = threshold;
                Minimum = minimum;
                Maximum = maximum;
                IntegerAxis = integerAxis;
                Points = points;
                RowsForMetric = rowsForMetric;
                EmptyText = "当前区间没有 " + label + " 数据";
            }

            public string Key { get; }
            public string Label { get; }
            public string Unit { get; }
            public string Description { get; }
            public double? Threshold { get; }
            public double? Minimum { get; }
            public double? Maximum { get; }
            public bool IntegerAxis { get; }
            public Func<IReadOnlyList<PerfSample>, IEnumerable<ComparisonChartPoint>> Points { get; }
            public Func<IReadOnlyList<ComparisonRow>, IReadOnlyList<ComparisonRow>> RowsForMetric { get; }
            public string EmptyText { get; }
        }
    }
}
