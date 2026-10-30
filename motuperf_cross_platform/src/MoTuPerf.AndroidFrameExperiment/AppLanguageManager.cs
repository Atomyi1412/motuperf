using System;
using System.IO;
using System.Text.Json;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public sealed class LanguageSettingsStore
    {
        private sealed class SettingsDocument
        {
            public bool Chinese { get; set; }
        }

        private readonly string _path;

        public LanguageSettingsStore(string path = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        }

        public static string DefaultPath
        {
            get { return Path.Combine(RuntimeTools.DataDirectory, "language-settings.json"); }
        }

        public bool LoadChinese()
        {
            try
            {
                if (!File.Exists(_path)) return false;
                SettingsDocument document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_path), JsonOptions);
                return document != null && document.Chinese;
            }
            catch
            {
                return false;
            }
        }

        public void SaveChinese(bool chinese)
        {
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_path, JsonSerializer.Serialize(new SettingsDocument { Chinese = chinese }, JsonOptions));
            }
            catch
            {
                // Language persistence must never prevent the monitor from running.
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public static class AppLanguageManager
    {
        private static readonly LanguageSettingsStore Settings = new LanguageSettingsStore();
        private static bool _isChinese;

        public static event Action LanguageChanged;

        public static bool IsChinese { get { return _isChinese; } }
        public static string SelectorText { get { return _isChinese ? "English" : "中文"; } }
        public static string SelectorToolTip
        {
            get { return _isChinese ? "Switch metric names to English" : "切换到中文指标名称"; }
        }

        public static void Initialize()
        {
            _isChinese = Settings.LoadChinese();
        }

        public static void SetChinese(bool chinese, bool persist = true)
        {
            if (persist) Settings.SaveChinese(chinese);
            if (_isChinese == chinese) return;
            _isChinese = chinese;
            LanguageChanged?.Invoke();
        }

        public static string Text(string english, string chinese)
        {
            return _isChinese ? chinese : english;
        }

        public static string MetricDisplayName(string metricName)
        {
            switch (metricName)
            {
                case "Screenshot": return Text("Screenshot", "设备截图");
                case "FPS": return Text("FPS", "帧率");
                case "Display FrameTime": return Text("Display FrameTime", "显示帧耗时");
                case "Process Memory": return Text("Process Memory", "进程内存");
                case "Process CPU Raw": return Text("Process CPU Raw", "进程原始 CPU");
                case "Process CPU Normalized": return Text("Process CPU Normalized", "进程归一化 CPU");
                case "Device Temperature": return Text("Device Temperature", "设备温度");
                case "Thermal State": return Text("Thermal State", "系统热状态");
                case "Thermal Status": return Text("Thermal Status", "系统热状态");
                case "Jank": return Text("Jank", "卡顿");
                case "BigJank": return Text("BigJank", "严重卡顿");
                default: return metricName;
            }
        }

        public static string MetricDescription(string metricName)
        {
            switch (metricName)
            {
                case "Screenshot": return Text("Device screenshot", "设备截图");
                case "FPS": return Text("Frames rendered per second", "每秒渲染帧数");
                case "Display FrameTime": return Text("Maximum frame interval in the display window", "显示窗口内的最大帧间隔");
                case "Process Memory": return Text("Memory used by the selected process", "所选进程占用的内存");
                case "Process CPU Raw": return Text("Raw CPU usage of the selected process", "所选进程的原始 CPU 占用");
                case "Process CPU Normalized": return Text("CPU usage normalized by online cores", "按在线核心数归一化的 CPU 占用");
                case "Device Temperature": return Text("Device sensor temperature", "设备传感器温度");
                case "Thermal State": return Text("System thermal status", "系统热状态");
                case "Thermal Status": return Text("System thermal status", "系统热状态");
                case "Jank": return Text("Jank events in a frame window", "帧窗口内的卡顿次数");
                case "BigJank": return Text("Severe jank events in a frame window", "帧窗口内的严重卡顿次数");
                default: return metricName;
            }
        }

        public static string MetricUnit(string metricName, string fallback)
        {
            switch (metricName)
            {
                case "FPS": return Text("frames/s", "帧/秒");
                case "Display FrameTime": return Text("ms", "毫秒");
                case "Process CPU Raw":
                case "Process CPU Normalized": return "%";
                case "Device Temperature": return "°C";
                case "Thermal State":
                case "Thermal Status": return "0-6";
                default: return fallback;
            }
        }

        public static string DataTileLabel(string key, string englishFallback)
        {
            if (!_isChinese) return englishFallback;
            switch (key)
            {
                case "pid": return "PID";
                case "elapsed": return "时长";
                case "cpu": return "原始 CPU";
                case "memory": return "内存";
                case "fps": return "帧率";
                case "frame": return "帧耗时";
                case "jank": return "卡顿";
                case "bigJank": return "严重卡顿";
                case "temperature": return "温度";
                case "thermalState": return "热状态";
                default: return englishFallback;
            }
        }

        public static string AnalysisLabel(string label, string unit)
        {
            string display = _isChinese ? ChineseAnalysisLabel(label) : label;
            string displayUnit = _isChinese ? ChineseUnit(unit) : unit;
            return string.IsNullOrWhiteSpace(displayUnit) ? display : display + " (" + displayUnit + ")";
        }

        public static string LegendLabel(string label)
        {
            if (!_isChinese) return label;
            switch (label)
            {
                case "FPS": return "帧率";
                case "Jank": return "卡顿";
                case "BigJank": return "严重卡顿";
                case "FrameTime": return "帧耗时";
                case "Memory": return "内存";
                case "CPU Raw": return "原始 CPU";
                case "CPU Normalized": return "归一化 CPU";
                default: return label;
            }
        }

        public static string SensorName(string sensorName)
        {
            string sensor = (sensorName ?? "").Trim();
            if (!_isChinese || sensor.Length == 0) return sensor;
            switch (sensor.ToLowerInvariant())
            {
                case "battery": return "电池";
                case "skin": return "外壳";
                case "power amplifier": return "功放";
                default: return sensor;
            }
        }

        private static string ChineseAnalysisLabel(string label)
        {
            switch (label)
            {
                case "FPS": return "帧率";
                case "FrameTime": return "帧耗时";
                case "Jank": return "卡顿次数";
                case "BigJank": return "严重卡顿次数";
                case "Process Memory": return "进程内存";
                case "CPU Raw": return "原始 CPU";
                case "CPU Normalized": return "归一化 CPU";
                case "Thermal State":
                case "Thermal Status": return "系统热状态";
            }
            if (label.StartsWith("Temperature / ", StringComparison.Ordinal))
                return "温度 / " + label.Substring("Temperature / ".Length);
            return label;
        }

        private static string ChineseUnit(string unit)
        {
            switch (unit)
            {
                case "帧/s": return "帧/秒";
                case "次/窗": return "次/窗口";
                case "级别": return "等级";
                default: return unit;
            }
        }
    }
}
