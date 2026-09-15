using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSharpIosPerfMonitor
{
    public sealed class PlatformDeviceDiscovery
    {
        public PlatformDeviceDiscovery()
        {
            Devices = new List<DeviceInfo>();
            Diagnostic = "";
            AppleDriverMissing = false;
            AppleDriverActionAvailable = false;
        }

        public List<DeviceInfo> Devices { get; set; }
        public string Diagnostic { get; set; }
        public bool AppleDriverMissing { get; set; }
        public bool AppleDriverActionAvailable { get; set; }
    }

    public sealed class DeviceDiscoveryReport
    {
        public DeviceDiscoveryReport()
        {
            Devices = new List<DeviceInfo>();
            IosDiagnostic = "";
            AndroidDiagnostic = "";
            AppleDriverMissing = false;
            AppleDriverActionAvailable = false;
        }

        public List<DeviceInfo> Devices { get; set; }
        public string IosDiagnostic { get; set; }
        public string AndroidDiagnostic { get; set; }
        public bool AppleDriverMissing { get; set; }
        public bool AppleDriverActionAvailable { get; set; }

        public string StatusMessage
        {
            get
            {
                if (Devices.Count > 0) return "已检测到设备：" + Devices.Count;
                return "未检测到设备，请连接设备、检查授权后刷新。";
            }
        }
    }

    public sealed class DeviceInfo
    {
        public DeviceInfo()
        {
            Udid = "";
            Name = "";
            MarketName = "";
            Brand = "";
            ProductVersion = "";
            ConnType = "";
            CpuInfo = "";
            GpuInfo = "";
            Resolution = "";
            Platform = "ios";
        }

        public string Udid { get; set; }
        public string Name { get; set; }
        public string MarketName { get; set; }
        public string Brand { get; set; }
        public string ProductVersion { get; set; }
        public string ConnType { get; set; }
        public string CpuInfo { get; set; }
        public string GpuInfo { get; set; }
        public string Resolution { get; set; }
        public string Platform { get; set; }
        public bool Recommended { get; set; }

        public string PickerLabel
        {
            get
            {
                return FirstNonEmpty(MarketName, Name, Platform == "android" ? "Android 设备" : "iOS 设备");
            }
        }

        public override string ToString()
        {
            string platform = string.IsNullOrWhiteSpace(Platform) ? "ios" : Platform.ToLowerInvariant();
            string title = platform == "android" ? "Android" : "iOS";
            string name = FirstNonEmpty(MarketName, Name, title + " 设备");
            string suffix = Udid.Length > 6 ? Udid.Substring(Udid.Length - 6) : Udid;
            string mark = Recommended ? "推荐 - " : "";
            return string.Format("{0}{1} - {2} {3} - {4} - {5}", mark, name, title, ProductVersion, FirstNonEmpty(ConnType, "USB"), suffix);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return "";
        }
    }

    public sealed class AppInfo
    {
        public AppInfo()
        {
            BundleId = "";
            Name = "";
            Version = "";
            Reason = "";
            Platform = "";
            IconKey = "";
            IconPath = "";
            ApkPath = "";
        }

        public string BundleId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Platform { get; set; }
        public bool Recommended { get; set; }
        public string Reason { get; set; }
        public string IconKey { get; set; }
        public string IconPath { get; set; }
        public string ApkPath { get; set; }

        public string ListLabel
        {
            get
            {
                string mark = Recommended ? "推荐 - " : "";
                string reason = string.IsNullOrWhiteSpace(Reason) ? "" : "  (" + Reason + ")";
                return mark + Name + reason;
            }
        }

        public override string ToString()
        {
            string mark = Recommended ? "推荐 - " : "";
            string version = string.IsNullOrWhiteSpace(Version) ? "" : " - " + Version;
            return string.Format("{0}{1} - {2}{3}", mark, FirstNonEmpty(Name, BundleId), BundleId, version);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return "";
        }
    }

    public sealed class ProcessInfo
    {
        public ProcessInfo()
        {
            Name = "";
            BundleId = "";
            DisplayName = "";
            DeviceUdid = "";
            StartedAt = "";
            OwnerName = "";
            OwnerBundleId = "";
            OwnerDisplayName = "";
            OwnershipSource = "";
            ApplicationState = "";
            ApplicationExecutablePath = "";
            Reason = "";
            Platform = "";
            IconKey = "";
            IconPath = "";
        }

        public int Pid { get; set; }
        public string Name { get; set; }
        public string BundleId { get; set; }
        public string DisplayName { get; set; }
        public string DeviceUdid { get; set; }
        public string StartedAt { get; set; }
        public int ResponsiblePid { get; set; }
        public long CoalitionId { get; set; }
        public long StartAbsTime { get; set; }
        public long AndroidStartTimeTicks { get; set; }
        public long ProcessUniqueId { get; set; }
        public int OwnerPid { get; set; }
        public string OwnerName { get; set; }
        public string OwnerBundleId { get; set; }
        public string OwnerDisplayName { get; set; }
        public string OwnershipSource { get; set; }
        public bool OwnershipVerified { get; set; }
        public bool OwnershipAmbiguous { get; set; }
        public string ApplicationState { get; set; }
        public string ApplicationExecutablePath { get; set; }
        public bool ForegroundApplication { get; set; }
        public string Platform { get; set; }
        public bool Recommended { get; set; }
        public string Reason { get; set; }
        public string IconKey { get; set; }
        public string IconPath { get; set; }

        public string ListLabel
        {
            get
            {
                string mark = Recommended ? "推荐 - " : "";
                string name = string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
                string reason = string.IsNullOrWhiteSpace(Reason) ? "" : "  (" + Reason + ")";
                return mark + name + reason;
            }
        }

        public string PickerName
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
                if (!Name.StartsWith("com.apple.WebKit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(StartedAt))
                {
                    return name;
                }
                DateTimeOffset started;
                return DateTimeOffset.TryParse(StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out started)
                    ? name + " · " + started.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : name;
            }
        }

        public override string ToString()
        {
            string mark = Recommended ? "推荐" : "进程";
            string name = string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
            string bundle = string.IsNullOrWhiteSpace(BundleId) ? Name : BundleId;
            return string.Format("{0} - pid {1} - {2} - {3}", mark, Pid, name, bundle);
        }
    }

    public sealed class PerfSample
    {
        public PerfSample()
        {
            Source = "";
            Note = "";
            FpsScope = "";
            FrameSource = "";
            MemoryMetric = "";
            MemorySource = "";
            CpuSource = "";
            CpuNormalizedSource = "";
            CpuCorePercents = new List<double>();
            CpuCoreSource = "";
            CpuCoreScope = "";
            TemperatureCelsius = new Dictionary<string, double>();
            TemperatureSource = "";
            TemperatureScope = "";
            ThermalStateName = "";
            ThermalStateSource = "";
            ThermalStateScope = "";
        }

        public DateTime Timestamp { get; set; }
        public double ElapsedSec { get; set; }
        public int TargetPid { get; set; }
        public bool HasFps { get; set; }
        public double Fps { get; set; }
        public bool FpsUpdated { get; set; }
        public bool HasAverageFps { get; set; }
        public double AverageFps { get; set; }
        public bool HasFrameTime { get; set; }
        public double FrameTimeMs { get; set; }
        public bool HasFrameTimeMean { get; set; }
        public double FrameTimeMeanMs { get; set; }
        public bool HasFrameTimeP95 { get; set; }
        public double FrameTimeP95Ms { get; set; }
        public bool HasFrameTimeMax { get; set; }
        public double FrameTimeMaxMs { get; set; }
        public bool HasJank { get; set; }
        public double Jank { get; set; }
        public double BigJank { get; set; }
        public bool HasStutter { get; set; }
        public double StutterPercent { get; set; }
        public double JankTimeMs { get; set; }
        public bool HasFrameObservation { get; set; }
        public int FrameCount { get; set; }
        public double FrameObservationMs { get; set; }
        public bool NoPresentFrames { get; set; }
        public double ResumeGapMs { get; set; }
        public bool HasRefreshRate { get; set; }
        public double RefreshRateHz { get; set; }
        public bool OrderedFrames { get; set; }
        public bool ApproximateFrameMetrics { get; set; }
        public bool SourceDegraded { get; set; }
        public int DuplicateFrameTimestamps { get; set; }
        public int OutOfOrderFrameTimestamps { get; set; }
        public int InvalidFrameIntervals { get; set; }
        public bool FrameRingBufferOverrun { get; set; }
        public bool HasFrameTargetVerification { get; set; }
        public bool FrameTargetVerified { get; set; }
        public int SurfaceOwnerPid { get; set; }
        public int SurfaceOwnerUid { get; set; }
        public string FpsScope { get; set; }
        public string FrameSource { get; set; }
        public bool HasFrameSourceElapsed { get; set; }
        public double FrameSourceElapsedSec { get; set; }
        public bool HasFrameSourceSequence { get; set; }
        public long FrameSourceSequence { get; set; }
        public bool FrameSourceSequenceDiscontinuity { get; set; }
        public long MissingFrameSourceWindows { get; set; }
        public bool HasMemory { get; set; }
        public double MemoryMb { get; set; }
        public bool MemoryUpdated { get; set; }
        public string MemoryMetric { get; set; }
        public string MemorySource { get; set; }
        public bool HasMemoryRss { get; set; }
        public double MemoryRssMb { get; set; }
        public bool HasCpu { get; set; }
        public double CpuPercent { get; set; }
        public bool CpuUpdated { get; set; }
        public string CpuSource { get; set; }
        public bool HasCpuNormalized { get; set; }
        public double CpuNormalizedPercent { get; set; }
        public bool CpuNormalizedUpdated { get; set; }
        public string CpuNormalizedSource { get; set; }
        public bool HasCpuCoreUsage { get; set; }
        public List<double> CpuCorePercents { get; set; }
        public int CpuCoreCount { get; set; }
        public bool CpuCoreUpdated { get; set; }
        public string CpuCoreSource { get; set; }
        public string CpuCoreScope { get; set; }
        public bool HasTemperature { get; set; }
        public Dictionary<string, double> TemperatureCelsius { get; set; }
        public bool TemperatureUpdated { get; set; }
        public string TemperatureSource { get; set; }
        public string TemperatureScope { get; set; }
        public bool HasThermalState { get; set; }
        public int ThermalStateLevel { get; set; }
        public string ThermalStateName { get; set; }
        public bool ThermalStateUpdated { get; set; }
        public string ThermalStateSource { get; set; }
        public string ThermalStateScope { get; set; }
        public bool HasFreshnessMetadata { get; set; }
        public string Source { get; set; }
        public string Note { get; set; }
    }

    public enum ScreenshotOrientation
    {
        Unknown = 0,
        Portrait = 1,
        PortraitUpsideDown = 2,
        LandscapeHomeToRight = 3,
        LandscapeHomeToLeft = 4
    }

    public sealed class ScreenshotInfo
    {
        public ScreenshotInfo()
        {
            Path = "";
        }

        public DateTime Timestamp { get; set; }
        public double ElapsedSec { get; set; }
        public string Path { get; set; }
        public ScreenshotOrientation Orientation { get; set; }

        public override string ToString()
        {
            return string.Format("{0:0.0}s - {1}", ElapsedSec, System.IO.Path.GetFileName(Path));
        }
    }

    public sealed class CaptureConfig
    {
        public CaptureConfig()
        {
            Udid = "";
            BundleId = "com.tencent.xin";
            TargetName = "";
            Platform = "ios";
            ProductVersion = "";
            ScreenshotIntervalSec = 3;
            TargetOwnerName = "";
            CollectFps = true;
            CollectMemory = true;
            CollectCpu = true;
            CollectTemperature = true;
            CollectThermalState = true;
        }

        public string Platform { get; set; }
        public string Udid { get; set; }
        public string BundleId { get; set; }
        public int? TargetPid { get; set; }
        public string TargetName { get; set; }
        public long TargetStartAbsTime { get; set; }
        public long TargetAndroidStartTimeTicks { get; set; }
        public long TargetCoalitionId { get; set; }
        public int TargetOwnerPid { get; set; }
        public string TargetOwnerName { get; set; }
        public string ProductVersion { get; set; }
        public bool CollectFps { get; set; }
        public bool CollectMemory { get; set; }
        public bool CollectCpu { get; set; }
        public bool CollectTemperature { get; set; }
        public bool CollectThermalState { get; set; }
        public bool CaptureScreenshots { get; set; }
        public int ScreenshotIntervalSec { get; set; }
    }
}
