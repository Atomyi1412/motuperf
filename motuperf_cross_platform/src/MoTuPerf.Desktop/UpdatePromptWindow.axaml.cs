using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public enum UpdatePromptChoice { Later, Skip, Update }

    public partial class UpdatePromptWindow : Window
    {
        private TextBlock _versionText;
        private TextBlock _messageText;
        private TextBlock _publishedText;
        private Button _releaseNotesButton;
        public UpdatePromptWindow() { InitializeComponent(); }
        public UpdatePromptWindow(UpdateManifest manifest)
        {
            InitializeComponent();
            _versionText.Text = "v" + manifest.Version;
            _messageText.Text = "发现新的 MoTuPerf 版本。是否现在下载并安装？";
            _publishedText.Text = manifest.PublishedAtUtc.HasValue ? "发布时间：" + manifest.PublishedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            _releaseNotesButton.IsVisible = !string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl);
            _releaseNotesButton.Tag = manifest.ReleaseNotesUrl;
            this.FindControl<TextBlock>("ReleaseNotesText").Text = manifest.ReleaseNotes.Count > 0
                ? "• " + string.Join("\n• ", manifest.ReleaseNotes)
                : "此版本暂未提供更新内容。";
            this.FindControl<Button>("SkipButton").IsVisible = !manifest.Mandatory;
        }
        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _versionText = this.FindControl<TextBlock>("VersionText");
            _messageText = this.FindControl<TextBlock>("MessageText");
            _publishedText = this.FindControl<TextBlock>("PublishedText");
            _releaseNotesButton = this.FindControl<Button>("ReleaseNotesButton");
            ContentDialogSizing.Attach(this);
        }
        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
        private void Update(object sender, RoutedEventArgs e) { Close(UpdatePromptChoice.Update); }
        private void Later(object sender, RoutedEventArgs e) { Close(UpdatePromptChoice.Later); }
        private void Skip(object sender, RoutedEventArgs e) { Close(UpdatePromptChoice.Skip); }
        private void OpenReleaseNotes(object sender, RoutedEventArgs e)
        {
            string url = (_releaseNotesButton?.Tag as string) ?? "";
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }
    }
}
