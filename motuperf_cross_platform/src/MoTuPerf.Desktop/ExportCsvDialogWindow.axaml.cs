using Avalonia.Controls;
using Avalonia.Interactivity;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public partial class ExportCsvDialogWindow : Window
    {
        public ExportCsvDialogWindow()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }

        private void ExportSummary(object sender, RoutedEventArgs e) { Close((CsvExportMode?)CsvExportMode.PerSecondSummary); }
        private void ExportRawDetail(object sender, RoutedEventArgs e) { Close((CsvExportMode?)CsvExportMode.RawDetail); }
        private void Cancel(object sender, RoutedEventArgs e) { Close(null); }
    }
}
