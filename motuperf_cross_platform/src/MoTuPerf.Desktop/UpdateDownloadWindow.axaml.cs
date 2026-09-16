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
        private TextBlock _percentText;
        private TextBlock _titleText;
        private Button _cancelButton;
        private bool _failureShown;
        public event Action CancelRequested;
        public UpdateDownloadWindow()
        {
            InitializeComponent();
            ContentDialogSizing.Attach(this);
        }
        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _progress = this.FindControl<ProgressBar>("Progress");
            _detail = this.FindControl<TextBlock>("Detail");
            _percentText = this.FindControl<TextBlock>("PercentText");
            _titleText = this.FindControl<TextBlock>("TitleText");
            _cancelButton = this.FindControl<Button>("CancelButton");
        }
        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
        private void Cancel(object sender, RoutedEventArgs e)
        {
            if (_failureShown) { Close(); return; }
            _cancelButton.IsEnabled = false;
            _progress.IsIndeterminate = false;
            _titleText.Text = "正在取消下载";
            _detail.Text = "正在取消下载...";
            CancelRequested?.Invoke();
        }
        public void SetProgress(UpdateDownloadProgress progress)
        {
            if (_failureShown || !_cancelButton.IsEnabled) return;
            bool hasPercent = progress?.Percent.HasValue == true;
            _progress.IsIndeterminate = !hasPercent;
            if (hasPercent) _progress.Value = Math.Max(0, Math.Min(100, progress.Percent.Value));
            _percentText.Text = hasPercent ? _progress.Value.ToString("0.0") + "%" : "下载中";
            _detail.Text = progress?.TotalBytes.HasValue == true
                ? "已下载 " + FormatSize(progress.BytesReceived) + " / " + FormatSize(progress.TotalBytes.Value)
                : "已下载 " + FormatSize(progress?.BytesReceived ?? 0);
        }
        public void Complete() { Close(); }
        public void ShowFailure(string message)
        {
            _failureShown = true;
            _progress.IsIndeterminate = false;
            _titleText.Text = "下载失败";
            _percentText.Text = "未完成";
            _detail.Text = message;
            _cancelButton.Content = "关闭";
            _cancelButton.IsEnabled = true;
        }
        private static string FormatSize(long bytes) { if (bytes < 1024) return bytes + " B"; if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB"; return (bytes / 1024d / 1024d).ToString("0.0") + " MB"; }
    }
}
