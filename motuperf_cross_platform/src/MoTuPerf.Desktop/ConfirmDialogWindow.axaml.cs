using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MoTuPerf.Desktop
{
    public partial class ConfirmDialogWindow : Window
    {
        private TextBlock TitleText;
        private TextBlock MessageText;
        private Button AcceptButton;
        private Button CancelButton;

        public ConfirmDialogWindow() : this("确认", "是否继续？", "确定", "取消") { }
        public ConfirmDialogWindow(string title, string message, string accept, string cancel)
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            AcceptButton.Content = accept;
            CancelButton.Content = cancel;
        }
        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            TitleText = this.FindControl<TextBlock>("TitleText");
            MessageText = this.FindControl<TextBlock>("MessageText");
            AcceptButton = this.FindControl<Button>("AcceptButton");
            CancelButton = this.FindControl<Button>("CancelButton");
        }
        private void Accept(object sender, RoutedEventArgs e) { Close(true); }
        private void Cancel(object sender, RoutedEventArgs e) { Close(false); }
    }
}
