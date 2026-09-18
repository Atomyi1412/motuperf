using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MoTuPerf.Desktop
{
    public partial class DeviceDiagnosticsWindow : Window
    {
        private TextBlock _overallStatusText;
        private TextBlock _androidSummaryText;
        private TextBlock _iosSummaryText;
        private TextBlock _androidDiagnosticText;
        private TextBlock _iosDiagnosticText;
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
            _androidDiagnosticText.Text = snapshot.AndroidDiagnostic;
            _iosDiagnosticText.Text = snapshot.IosDiagnostic;
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
            _androidDiagnosticText = this.FindControl<TextBlock>("AndroidDiagnosticText");
            _iosDiagnosticText = this.FindControl<TextBlock>("IosDiagnosticText");
            _appleDriverDetailText = this.FindControl<TextBlock>("AppleDriverDetailText");
            _appleDriverDetail = this.FindControl<Border>("AppleDriverDetail");
        }

        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void CloseWindow(object sender, RoutedEventArgs e) { Close(); }
    }
}
