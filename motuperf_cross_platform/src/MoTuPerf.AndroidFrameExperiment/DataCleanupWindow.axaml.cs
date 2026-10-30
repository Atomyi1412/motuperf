using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public sealed class DataCleanupItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public DataCleanupItem(RuntimeDataCategory category)
        {
            Id = category.Id;
            DisplayName = category.DisplayName;
            Description = category.Description;
            Location = category.Location;
            SizeBytes = category.SizeBytes;
        }
        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public string Location { get; private set; }
        public long SizeBytes { get; private set; }
        public string SizeLabel { get { return RuntimeDataManager.FormatSize(SizeBytes); } }
        public bool IsSelected
        {
            get { return _isSelected; }
            set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class DataCleanupWindow : Window, INotifyPropertyChanged
    {
        private readonly string _root;
        private bool _isLoading = true;
        private bool _isBusy;
        private string _statusText = "";
        public DataCleanupWindow() : this(CSharpIosPerfMonitor.RuntimeTools.DataDirectory) { }
        public DataCleanupWindow(string root)
        {
            _root = Path.GetFullPath(root ?? "");
            Items = new ObservableCollection<DataCleanupItem>();
            InitializeComponent();
            DataContext = this;
            Opened += delegate { _ = LoadAsync(); };
        }
        public ObservableCollection<DataCleanupItem> Items { get; private set; }
        public bool IsLoading { get { return _isLoading; } private set { if (_isLoading == value) return; _isLoading = value; Notify(nameof(IsLoading)); Notify(nameof(CanClear)); } }
        public bool IsBusy { get { return _isBusy; } private set { if (_isBusy == value) return; _isBusy = value; Notify(nameof(IsBusy)); Notify(nameof(CanClear)); } }
        public bool CanClear { get { return !IsLoading && !IsBusy && Items.Any(item => item.IsSelected); } }
        public string SelectedSummary
        {
            get
            {
                long bytes = Items.Where(item => item.IsSelected).Sum(item => item.SizeBytes);
                return bytes == 0 ? "未选择" : "已选 " + RuntimeDataManager.FormatSize(bytes);
            }
        }
        public string StatusText { get { return _statusText; } private set { if (_statusText == value) return; _statusText = value; Notify(nameof(StatusText)); } }
        public new event PropertyChangedEventHandler PropertyChanged;

        private async Task LoadAsync()
        {
            IsLoading = true;
            try
            {
                IReadOnlyList<RuntimeDataCategory> categories = await Task.Run(delegate { return RuntimeDataManager.GetCategories(_root); });
                Items.Clear();
                foreach (RuntimeDataCategory category in categories)
                {
                    DataCleanupItem item = new DataCleanupItem(category);
                    item.PropertyChanged += ItemChanged;
                    Items.Add(item);
                }
                StatusText = "数据目录：" + _root;
            }
            catch (Exception exception) { StatusText = "读取数据大小失败：" + exception.Message; }
            finally { IsLoading = false; Notify(nameof(SelectedSummary)); Notify(nameof(CanClear)); }
        }

        private void ItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DataCleanupItem.IsSelected)) { Notify(nameof(SelectedSummary)); Notify(nameof(CanClear)); }
        }

        private void SelectAll(object sender, RoutedEventArgs e) { foreach (DataCleanupItem item in Items) item.IsSelected = true; }
        private void ClearAll(object sender, RoutedEventArgs e) { foreach (DataCleanupItem item in Items) item.IsSelected = false; }

        private async void ClearSelected(object sender, RoutedEventArgs e)
        {
            if (!CanClear) return;
            string[] ids = Items.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
            bool confirmed = await new ConfirmDialogWindow("确认清理数据", "将清理已选择的数据，删除后无法恢复，是否继续？", "清理", "取消").ShowDialog<bool>(this);
            if (!confirmed) return;
            IsBusy = true;
            StatusText = "正在清理数据，请稍候...";
            DataCleanupProgressWindow progressWindow = new DataCleanupProgressWindow();
            Task progressDialog = null;
            try
            {
                progressDialog = progressWindow.ShowDialog(this);
                Progress<RuntimeDataCleanupProgress> progress = new Progress<RuntimeDataCleanupProgress>(progressWindow.Apply);
                RuntimeDataCleanupResult result = await Task.Run(delegate { return RuntimeDataManager.Delete(_root, ids, progress); });
                StatusText = result.FailedFiles == 0
                    ? "已清理 " + RuntimeDataManager.FormatSize(result.DeletedBytes)
                    : BuildFailureStatus(result);
                await LoadAsync();
            }
            catch (Exception exception) { StatusText = "清理失败：" + exception.Message; }
            finally
            {
                if (progressDialog != null)
                {
                    progressWindow.Complete();
                    await progressDialog;
                }
                IsBusy = false;
                Notify(nameof(CanClear));
            }
        }

        internal static string BuildFailureStatus(RuntimeDataCleanupResult result)
        {
            RuntimeDataCleanupFailure first = result.Failures.FirstOrDefault();
            string detail = first == null ? "请打开数据目录检查文件占用或权限" : Path.GetFileName(first.Path) + "：" + first.Reason;
            return "已清理 " + RuntimeDataManager.FormatSize(result.DeletedBytes) + "，有 " + result.FailedFiles + " 个文件未能删除（" + detail + "）";
        }

        private void OpenDirectory(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = _root, UseShellExecute = true });
            }
            catch (Exception exception) { StatusText = "打开目录失败：" + exception.Message; }
        }

        private void Cancel(object sender, RoutedEventArgs e) { Close(); }
        private void Notify(string name) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        private void InitializeComponent() { Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); }
    }
}
