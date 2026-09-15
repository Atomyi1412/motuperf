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
        public UpdatePromptWindow() { InitializeComponent(); }
        public UpdatePromptWindow(UpdateManifest manifest)
        {
            InitializeComponent();
            _versionText.Text = "v" + manifest.Version;
            _messageText.Text = "发现新的 MoTuPerf 版本，更新说明可在发布页面查看。\n是否现在下载并安装？";
        }
        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _versionText = this.FindControl<TextBlock>("VersionText");
            _messageText = this.FindControl<TextBlock>("MessageText");
        }
        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
        private void Update(object sender, RoutedEventArgs e) { Close(UpdatePromptChoice.Update); }
        private void Later(object sender, RoutedEventArgs e) { Close(UpdatePromptChoice.Later); }
        private void Skip(object sender, RoutedEventArgs e) { Close(UpdatePromptChoice.Skip); }
    }
}
