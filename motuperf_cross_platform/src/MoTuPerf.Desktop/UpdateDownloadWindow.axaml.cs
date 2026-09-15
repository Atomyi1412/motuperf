using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public partial class UpdateDownloadWindow : Window
    {
        private ProgressBar _progress;
        private TextBlock _detail;
        private Button _cancelButton;
        private bool _failureShown;
        public event Action CancelRequested;
        public UpdateDownloadWindow() { InitializeComponent(); }
        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _progress = this.FindControl<ProgressBar>("Progress");
            _detail = this.FindControl<TextBlock>("Detail");
            _cancelButton = this.FindControl<Button>("CancelButton");
        }
        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
        private void Cancel(object sender, RoutedEventArgs e)
        {
            if (_failureShown) { Close(); return; }
            CancelRequested?.Invoke(); _cancelButton.IsEnabled = false; _detail.Text = "正在取消下载...";
        }
        public void SetProgress(UpdateDownloadProgress progress)
        {
            if (progress?.Percent.HasValue == true) _progress.Value = Math.Max(0, Math.Min(100, progress.Percent.Value));
            _detail.Text = progress?.TotalBytes.HasValue == true
                ? "已下载 " + FormatSize(progress.BytesReceived) + " / " + FormatSize(progress.TotalBytes.Value)
                : "已下载 " + FormatSize(progress?.BytesReceived ?? 0);
        }
        public void Complete() { Close(); }
        public void ShowFailure(string message) { _failureShown = true; _detail.Text = message; _cancelButton.Content = "关闭"; _cancelButton.IsEnabled = true; }
        private static string FormatSize(long bytes) { if (bytes < 1024) return bytes + " B"; if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB"; return (bytes / 1024d / 1024d).ToString("0.0") + " MB"; }
    }
}
