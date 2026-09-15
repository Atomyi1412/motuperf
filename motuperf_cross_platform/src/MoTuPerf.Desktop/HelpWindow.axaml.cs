using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MoTuPerf.Desktop
{
    public partial class HelpWindow : Window
    {
        private const string VersionLogUrl = "https://more2.feishu.cn/docx/EJ6Hdr6lbokuUsxr8FecLA7Qneg";
        public HelpWindow() { Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); }
        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }
        private void OpenVersionLog(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = VersionLogUrl, UseShellExecute = true }); }
            catch { }
        }
        private void CloseWindow(object sender, RoutedEventArgs e) { Close(); }
    }
}
