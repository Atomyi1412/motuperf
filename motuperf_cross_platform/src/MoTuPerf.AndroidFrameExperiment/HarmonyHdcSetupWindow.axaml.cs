using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public partial class HarmonyHdcSetupWindow : Window
    {
        private TextBlock ActionStatusText;
        private TextBlock SelectedHdcPathText;

        internal const string HdcDownloadUrl = "https://developer.huawei.com/consumer/cn/download/";
        internal const string HdcGuideUrl = "https://gitee.com/openharmony/developtools_hdc/blob/master/README_zh.md";

        public event EventHandler RecheckRequested;

        public HarmonyHdcSetupWindow()
        {
            InitializeComponent();
            ContentDialogSizing.Attach(this);
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            ActionStatusText = this.FindControl<TextBlock>("ActionStatusText");
            SelectedHdcPathText = this.FindControl<TextBlock>("SelectedHdcPathText");
            UpdateSelectedHdcPath();
        }

        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void OpenHdcDownload(object sender, RoutedEventArgs e)
        {
            SetActionStatus(OpenExternal(HdcDownloadUrl)
                ? "已打开官方下载中心：在“所有工具”中只下载 Command Line Tools。"
                : "无法自动打开浏览器，请复制上方地址到浏览器打开。", false);
        }

        private void OpenHdcGuide(object sender, RoutedEventArgs e)
        {
            SetActionStatus(OpenExternal(HdcGuideUrl)
                ? "已打开官方 HDC 获取说明。"
                : "无法自动打开浏览器，请复制上方地址到浏览器打开。", false);
        }

        private async void CopyHdcDownload(object sender, RoutedEventArgs e)
        {
            try
            {
                TopLevel topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard == null)
                {
                    SetActionStatus("当前系统不支持自动复制，请手动选择上方地址复制。", true);
                    return;
                }

                await topLevel.Clipboard.SetTextAsync(HdcDownloadUrl);
                SetActionStatus("官方 SDK 下载中心地址已复制。", false);
            }
            catch
            {
                SetActionStatus("复制失败，请手动选择上方地址复制。", true);
            }
        }

        private async void SelectHdcFile(object sender, RoutedEventArgs e)
        {
            try
            {
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择 HDC 文件（" + RuntimeTools.HdcExecutableName + "）",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("HDC 可执行文件")
                        {
                            Patterns = new[] { RuntimeTools.HdcExecutableName }
                        }
                    }
                });
                if (files == null || files.Count == 0) return;
                string path = files[0].TryGetLocalPath();
                if (!RuntimeTools.IsValidHdcExecutablePath(path))
                {
                    SetActionStatus("请选择解压后的 " + RuntimeTools.HdcExecutableName + " 文件。不要选择压缩包、文件夹或其他工具。", true);
                    return;
                }

                RuntimeTools.ConfigureHdcExecutable(path);
                UpdateSelectedHdcPath();
                SetActionStatus("HDC 位置已保存，正在重新检测设备...", false);
                RecheckRequested?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                SetActionStatus("保存 HDC 位置失败：" + FirstLine(exception.Message), true);
            }
        }

        private async void SelectHdcDirectory(object sender, RoutedEventArgs e)
        {
            try
            {
                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择解压后的 Command Line Tools 文件夹",
                    AllowMultiple = false
                });
                if (folders == null || folders.Count == 0) return;

                string directory = folders[0].TryGetLocalPath();
                string hdcPath = RuntimeTools.FindHdcInDirectory(directory);
                if (!RuntimeTools.IsValidHdcExecutablePath(hdcPath))
                {
                    SetActionStatus("这个文件夹里没有找到 HDC。请先解压 Command Line Tools，再选择最外层的 command-line-tools 文件夹。", true);
                    return;
                }

                RuntimeTools.ConfigureHdcExecutable(hdcPath);
                UpdateSelectedHdcPath();
                SetActionStatus("已自动找到 HDC，正在重新检测设备...", false);
                RecheckRequested?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                SetActionStatus("识别 HDC 文件夹失败：" + FirstLine(exception.Message), true);
            }
        }

        private void RequestDeviceRefresh(object sender, RoutedEventArgs e)
        {
            RecheckRequested?.Invoke(this, EventArgs.Empty);
            Close();
        }

        private void SetActionStatus(string message, bool isError)
        {
            ActionStatusText.Text = message;
            ActionStatusText.Classes.Set("errorHint", isError);
        }

        private void UpdateSelectedHdcPath()
        {
            if (SelectedHdcPathText == null) return;
            string path = RuntimeTools.ConfiguredHdcPath;
            SelectedHdcPathText.Text = string.IsNullOrWhiteSpace(path)
                ? "还没有选择 HDC 文件。"
                : "已保存位置：" + path;
        }

        private static string FirstLine(string value)
        {
            string[] lines = (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string line = lines.Length == 0 ? "" : lines[0].Trim();
            return line.Length <= 180 ? line : line.Substring(0, 180) + "...";
        }

        private static bool OpenExternal(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void CloseWindow(object sender, RoutedEventArgs e) { Close(); }
    }
}
