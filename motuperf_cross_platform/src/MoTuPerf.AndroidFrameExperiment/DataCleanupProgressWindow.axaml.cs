using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public partial class DataCleanupProgressWindow : Window, INotifyPropertyChanged
    {
        private RuntimeDataCleanupProgress _progress = new RuntimeDataCleanupProgress(0, 0, 0, 0, 0, 0);
        private DateTime _startedAt = DateTime.UtcNow;
        private bool _allowClose;

        public DataCleanupProgressWindow()
        {
            InitializeComponent();
            DataContext = this;
            Closing += OnClosing;
        }

        public double Percent { get { return _progress.Percent; } }
        public string ProgressText
        {
            get { return RuntimeDataManager.FormatSize(_progress.ProcessedBytes) + " / " + RuntimeDataManager.FormatSize(_progress.TotalBytes) + "（" + _progress.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%）"; }
        }
        public string DetailText
        {
            get { return "已处理 " + _progress.ProcessedFiles + " / " + _progress.TotalFiles + " 个文件，已清理 " + RuntimeDataManager.FormatSize(_progress.DeletedBytes); }
        }
        public string EstimateText
        {
            get
            {
                if (_progress.TotalBytes == 0 || _progress.ProcessedBytes >= _progress.TotalBytes) return "预计剩余：即将完成";
                double elapsedSeconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
                if (_progress.ProcessedBytes <= 0 || elapsedSeconds <= 0) return "预计剩余：计算中...";
                double remainingSeconds = elapsedSeconds * (_progress.TotalBytes - _progress.ProcessedBytes) / _progress.ProcessedBytes;
                return "预计剩余：" + FormatDuration(remainingSeconds);
            }
        }
        public new event PropertyChangedEventHandler PropertyChanged;

        public void Apply(RuntimeDataCleanupProgress progress)
        {
            if (progress == null) return;
            _progress = progress;
            Notify(nameof(Percent));
            Notify(nameof(ProgressText));
            Notify(nameof(DetailText));
            Notify(nameof(EstimateText));
        }

        public void Complete()
        {
            _allowClose = true;
            Close();
        }

        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (!_allowClose) e.Cancel = true;
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 60) return Math.Max(1, (int)Math.Ceiling(seconds)) + " 秒";
            if (seconds < 3600) return ((int)(seconds / 60)) + " 分钟";
            return ((int)(seconds / 3600)) + " 小时";
        }

        private void Notify(string name) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        private void InitializeComponent() { Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); }
    }
}
