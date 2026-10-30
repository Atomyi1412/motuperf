using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MoTuPerf.Desktop
{
    public partial class DeviceDiagnosticsWindow : Window
    {
        public event EventHandler RecheckRequested;
        private TextBlock _overallStatusText;
        private TextBlock _androidSummaryText;
        private TextBlock _iosSummaryText;
        private TextBlock _harmonySummaryText;
        private TextBlock _androidDiagnosticText;
        private TextBlock _iosDiagnosticText;
        private TextBlock _harmonyDiagnosticText;
        private Button _harmonyHdcSetupButton;
        private TextBlock _appleDriverDetailText;
        private Border _appleDriverDetail;

        public DeviceDiagnosticsWindow()
            : this(DeviceDiagnosticsFormatter.Loading())
        {
        }

        internal DeviceDiagnosticsWindow(DeviceDiagnosticsSnapshot snapshot)
        {
            InitializeComponent();
            _overallStatusText.Text = snapshot.OverallStatus;
            _androidSummaryText.Text = snapshot.AndroidSummary;
            _iosSummaryText.Text = snapshot.IosSummary;
            _harmonySummaryText.Text = snapshot.HarmonySummary;
            _androidDiagnosticText.Text = snapshot.AndroidDiagnostic;
            _iosDiagnosticText.Text = snapshot.IosDiagnostic;
            _harmonyDiagnosticText.Text = snapshot.HarmonyDiagnostic;
            _harmonyHdcSetupButton.IsVisible = snapshot.HarmonyHdcActionAvailable;
            _appleDriverDetailText.Text = snapshot.AppleDriverMissing
                ? "未检测到 Apple 移动设备支持，可在设备选择窗口使用驱动按钮下载或修复。"
                : snapshot.AppleDriverActionAvailable
                    ? "Apple 设备驱动支持修复操作。"
                    : "当前未提供驱动修复操作。";
            _appleDriverDetail.IsVisible = snapshot.AppleDriverMissing || snapshot.AppleDriverActionAvailable;
            ContentDialogSizing.Attach(this);
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _overallStatusText = this.FindControl<TextBlock>("OverallStatusText");
            _androidSummaryText = this.FindControl<TextBlock>("AndroidSummaryText");
            _iosSummaryText = this.FindControl<TextBlock>("IosSummaryText");
            _harmonySummaryText = this.FindControl<TextBlock>("HarmonySummaryText");
            _androidDiagnosticText = this.FindControl<TextBlock>("AndroidDiagnosticText");
            _iosDiagnosticText = this.FindControl<TextBlock>("IosDiagnosticText");
            _harmonyDiagnosticText = this.FindControl<TextBlock>("HarmonyDiagnosticText");
            _harmonyHdcSetupButton = this.FindControl<Button>("HarmonyHdcSetupButton");
            _appleDriverDetailText = this.FindControl<TextBlock>("AppleDriverDetailText");
            _appleDriverDetail = this.FindControl<Border>("AppleDriverDetail");
        }

        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private async void OpenHarmonyHdcSetup(object sender, RoutedEventArgs e)
        {
            HarmonyHdcSetupWindow window = new HarmonyHdcSetupWindow();
            window.RecheckRequested += delegate
            {
                RecheckRequested?.Invoke(this, EventArgs.Empty);
            };
            await window.ShowDialog(this);
        }

        private void CloseWindow(object sender, RoutedEventArgs e) { Close(); }
    }
}
