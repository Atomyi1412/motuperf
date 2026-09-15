using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MoTuPerf.Desktop
{
    public partial class DataDirectorySettingsWindow : Window
    {
        private readonly string _currentDirectory;
        private TextBox _directoryTextBox;
        private TextBlock _statusText;
        public DataDirectorySettingsWindow() : this(CSharpIosPerfMonitor.RuntimeTools.DataDirectory) { }
        public DataDirectorySettingsWindow(string currentDirectory)
        {
            _currentDirectory = Path.GetFullPath(currentDirectory ?? "");
            InitializeComponent();
            _directoryTextBox = this.FindControl<TextBox>("DirectoryTextBox");
            _statusText = this.FindControl<TextBlock>("StatusText");
            _directoryTextBox.Text = _currentDirectory;
        }

        private async void Browse(object sender, RoutedEventArgs e)
        {
            try
            {
                IStorageFolder suggested = null;
                try { suggested = await StorageProvider.TryGetFolderFromPathAsync(_directoryTextBox.Text); } catch { }
                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择 MoTuPerf 数据目录",
                    AllowMultiple = false,
                    SuggestedStartLocation = suggested
                });
                if (folders.Count > 0)
                {
                    string path = folders[0].TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path)) _directoryTextBox.Text = Path.GetFullPath(path);
                }
            }
            catch (Exception exception) { _statusText.Text = "选择目录失败：" + exception.Message; }
        }

        private void Apply(object sender, RoutedEventArgs e)
        {
            string path = (_directoryTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path)) { _statusText.Text = "请选择数据目录。"; return; }
            try { Close(Path.GetFullPath(path)); }
            catch (Exception exception) { _statusText.Text = "目录无效：" + exception.Message; }
        }

        private void Cancel(object sender, RoutedEventArgs e) { Close(null); }
        private void InitializeComponent() { Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); }
    }
}
