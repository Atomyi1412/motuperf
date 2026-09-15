using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MoTuPerf.Platform;

namespace CSharpIosPerfMonitor
{
    // Compatibility facade for the shared lookup/collector code. Platform-specific
    // paths stay here instead of leaking into the device and metric services.
    public static class RuntimeTools
    {
        public const string PackageManifestFileName = "motuperf-package.json";
        private static readonly RuntimeToolResolver Resolver = CreateResolver();

        public static string PythonExecutable { get { return ResolveExecutable(Resolver.PythonExecutable, Resolver.OperatingSystem == "macos" ? "python3" : "python"); } }
        public static string AdbExecutable { get { return ResolveExecutable(Resolver.AdbExecutable, "adb"); } }
        public static bool IsPackagedBuild { get { return File.Exists(Resolver.PackageManifestPath); } }
        public static string UserDataDirectory { get { return Resolver.UserDataDirectory; } }
        public static string DataDirectory { get { return Resolver.UserDataDirectory; } }

        public static void PrepareUserDataDirectory() { Resolver.PrepareUserDataDirectory(); }

        public static Task MigrateLegacyDataAsync(CancellationToken token)
        {
            return Resolver.MigrateLegacyDataAsync(token);
        }

        public static Task<string> ChangeUserDataDirectoryAsync(string directory, CancellationToken token, IProgress<RuntimeDataMigrationProgress> progress = null)
        {
            return Resolver.ChangeUserDataDirectoryAsync(directory, token, progress);
        }

        public static string ResolveToolPath(string fileName)
        {
            string packaged = Path.Combine(Resolver.ToolsDirectory, fileName ?? "");
            if (File.Exists(packaged)) return packaged;
            string dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", fileName ?? ""));
            if (File.Exists(dev)) return dev;
            throw new FileNotFoundException("找不到 " + fileName + "，请重新安装 MoTuPerf。", packaged);
        }

        public static Task<ProcessResult> RunPythonAsync(IEnumerable<string> arguments, int timeoutMs, CancellationToken token)
        {
            return ProcessRunner.RunAsync(PythonExecutable, arguments, timeoutMs, token);
        }

        public static Task<ProcessResult> RunPythonAsync(string arguments, int timeoutMs, CancellationToken token)
        {
            return ProcessRunner.RunAsync(PythonExecutable, arguments, timeoutMs, token);
        }

        public static Task<ProcessResult> RunTideviceAsync(IEnumerable<string> arguments, int timeoutMs, CancellationToken token)
        {
            List<string> command = new List<string> { "-m", "tidevice" };
            if (arguments != null) command.AddRange(arguments);
            return RunPythonAsync(command, timeoutMs, token);
        }

        public static Process StartPythonStreaming(IEnumerable<string> arguments)
        {
            return ProcessRunner.StartStreaming(PythonExecutable, arguments);
        }

        public static void ConfigureProcessEnvironment(ProcessStartInfo startInfo)
        {
            if (startInfo == null) return;
            string adbDirectory = Path.GetDirectoryName(Resolver.AdbExecutable) ?? "";
            string currentPath = startInfo.Environment.ContainsKey("PATH")
                ? startInfo.Environment["PATH"]
                : Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(adbDirectory) && Directory.Exists(adbDirectory))
            {
                startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                    ? adbDirectory
                    : adbDirectory + Path.PathSeparator + currentPath;
            }
        }

        public static bool HasAppleMobileDeviceSupport()
        {
            if (Resolver.OperatingSystem == "macos") return true;
            if (!OperatingSystem.IsWindows()) return false;

            string commonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            string commonProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            return AppleServiceFileExists(commonProgramFiles) || AppleServiceFileExists(commonProgramFilesX86);
        }

        public static bool CanOfferAppleDriverRepair()
        {
            return Resolver.OperatingSystem == "windows" && OperatingSystem.IsWindows();
        }

        internal static bool ContainsAppleUsbHardwareId(string output)
        {
            return (output ?? "").IndexOf("VID_05AC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static async Task<bool> IsAppleUsbDeviceConnectedAsync(CancellationToken token)
        {
            if (!CanOfferAppleDriverRepair()) return false;
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    "pnputil.exe",
                    new[] { "/enum-devices", "/connected" },
                    4000,
                    token);
                return result.ExitCode == 0 && ContainsAppleUsbHardwareId(result.Stdout + "\n" + result.Stderr);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsAppleMobileDeviceServiceRunningOutput(int exitCode, string output)
        {
            if (exitCode != 0) return false;
            string value = output ?? "";
            return value.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0
                || Regex.IsMatch(value, @"STATE\s*:\s*4\b", RegexOptions.IgnoreCase);
        }

        public static bool IsAppleMobileDeviceServiceRunning()
        {
            if (!CanOfferAppleDriverRepair()) return false;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                startInfo.ArgumentList.Add("query");
                startInfo.ArgumentList.Add("Apple Mobile Device Service");
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null || !process.WaitForExit(2500)) return false;
                    string output = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
                    return IsAppleMobileDeviceServiceRunningOutput(process.ExitCode, output);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool AppleServiceFileExists(string commonProgramFiles)
        {
            if (string.IsNullOrWhiteSpace(commonProgramFiles)) return false;
            return File.Exists(Path.Combine(commonProgramFiles, "Apple", "Mobile Device Support", "AppleMobileDeviceService.exe"));
        }

        public static string DescribeIosException(Exception exception)
        {
            if (exception is FileNotFoundException && IsPackagedBuild)
            {
                return "iOS 运行环境缺失或损坏，请重新安装 MoTuPerf。";
            }
            if (!HasAppleMobileDeviceSupport()) return NoIosDeviceMessage();
            string detail = FirstLine(exception == null ? "" : exception.Message);
            return string.IsNullOrWhiteSpace(detail)
                ? "iOS 设备检测失败，请重新安装 MoTuPerf 后重试。"
                : "iOS 设备检测失败：" + detail;
        }

        public static string DescribeIosFailure(ProcessResult result)
        {
            string output = ((result == null ? "" : result.Stdout) + "\n" + (result == null ? "" : result.Stderr)).Trim();
            string lower = output.ToLowerInvariant();
            if (lower.Contains("no module named") || lower.Contains("modulenotfounderror")) return "iOS 运行组件不完整，请重新安装 MoTuPerf。";
            if (lower.Contains("not paired") || lower.Contains("invalidhostid") || lower.Contains("trust") || lower.Contains("password protected") || lower.Contains("locked"))
            {
                return "iOS 设备尚未信任或仍处于锁定状态，请解锁设备并在手机上选择“信任此电脑”。";
            }
            if (lower.Contains("usbmux") || lower.Contains("no device") || lower.Contains("not found"))
            {
                return NoIosDeviceMessage();
            }
            string detail = FirstLine(output);
            return string.IsNullOrWhiteSpace(detail)
                ? "iOS 设备检测失败，请检查 USB 连接、设备信任状态和开发者模式。"
                : "iOS 设备检测失败：" + detail;
        }

        public static string NoIosDeviceMessage()
        {
            if (!HasAppleMobileDeviceSupport())
            {
                return "未安装 Apple 移动设备驱动，请安装官方“Apple 设备”应用后重新插拔设备。";
            }
            if (!IsAppleMobileDeviceServiceRunning())
            {
                return "Apple 移动设备驱动已安装，但 Apple Mobile Device 服务未运行或通信通道异常，请点击修复驱动后重试。";
            }
            return "未检测到 iOS 设备，请解锁设备、确认“信任此电脑”、开启开发者模式，并检查 Apple Mobile Device 服务与 USB 通信端口。";
        }

        private static RuntimeToolResolver CreateResolver()
        {
            string baseDirectory = AppContext.BaseDirectory;
            RuntimeToolResolver development = new RuntimeToolResolver(baseDirectory, false);
            bool packaged = File.Exists(development.PackageManifestPath);
            return packaged ? new RuntimeToolResolver(baseDirectory, true) : development;
        }

        private static string ResolveExecutable(string bundled, string fallback)
        {
            if (File.Exists(bundled)) return bundled;
            if (IsPackagedBuild) throw new FileNotFoundException("安装包内置运行环境缺失或损坏。", bundled);
            return fallback;
        }

        private static string FirstLine(string value)
        {
            string[] lines = (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string line = lines.Length == 0 ? "" : lines[0].Trim();
            return line.Length <= 180 ? line : line.Substring(0, 180) + "...";
        }
    }
}
