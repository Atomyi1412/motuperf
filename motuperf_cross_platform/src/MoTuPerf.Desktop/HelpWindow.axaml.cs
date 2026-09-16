using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MoTuPerf.Desktop
{
    public partial class HelpWindow : Window
    {
        public HelpWindow() { Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); }
        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }
        private void CloseWindow(object sender, RoutedEventArgs e) { Close(); }
    }
}
