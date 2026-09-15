using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public static class RuntimeTools
    {
        public const string PackageManifestFileName = "motuperf-package.json";

        public static string PythonExecutable
        {
            get
            {
                string overridden = TestOverride("MOTUPERF_PYTHON");
                if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

                string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "python", "python.exe");
                if (File.Exists(bundled)) return bundled;
                if (IsPackagedBuild)
                {
                    throw new FileNotFoundException(
                        "安装包内置的 iOS 运行环境缺失或损坏，请重新安装 MoTuPerf。",
                        bundled);
                }
                return "python";
            }
        }

        public static string AdbExecutable
        {
            get
            {
                string overridden = TestOverride("MOTUPERF_ADB");
                if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

                string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "android", "adb.exe");
                if (File.Exists(bundled)) return bundled;
                if (IsPackagedBuild)
                {
                    throw new FileNotFoundException(
                        "安装包内置的 Android ADB 运行环境缺失或损坏，请重新安装 MoTuPerf。",
                        bundled);
                }
                return "adb";
            }
        }

        public static bool IsPackagedBuild
        {
            get
            {
                return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PackageManifestFileName));
            }
        }

        public static string UserDataDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string DataDirectory
        {
            get { return Path.Combine(UserDataDirectory, "data"); }
        }

        public static string ResolveToolPath(string fileName)
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", fileName ?? "");
            if (File.Exists(local)) return local;
            string dev = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "tools", fileName ?? ""));
            if (File.Exists(dev)) return dev;
            throw new FileNotFoundException("找不到 " + fileName + "，请重新安装 MoTuPerf。", local);
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
            string adbDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "android");
            if (!File.Exists(Path.Combine(adbDirectory, "adb.exe"))) return;

            string currentPath = startInfo.Environment.ContainsKey("PATH") ? startInfo.Environment["PATH"] : Environment.GetEnvironmentVariable("PATH");
            startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                ? adbDirectory
                : adbDirectory + Path.PathSeparator + currentPath;
        }

        public static bool HasAppleMobileDeviceSupport()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using (RegistryKey service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service"))
                    {
                        if (service != null) return true;
                    }
                }
                catch
                {
                }
            }

            string commonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            string commonProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            return AppleServiceFileExists(commonProgramFiles) || AppleServiceFileExists(commonProgramFilesX86);
        }

        public static bool CanOfferAppleDriverRepair()
        {
            return OperatingSystem.IsWindows();
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
                return result.ExitCode == 0
                    && ((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")).IndexOf("VID_05AC", StringComparison.OrdinalIgnoreCase) >= 0;
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

        public static string DescribeIosException(Exception exception)
        {
            if (exception is FileNotFoundException && IsPackagedBuild)
            {
                return "iOS 运行环境缺失或损坏，请重新安装 MoTuPerf。";
            }
            string detail = FirstLine(exception == null ? "" : exception.Message);
            return string.IsNullOrWhiteSpace(detail)
                ? "iOS 设备检测失败，请重新安装 MoTuPerf 后重试。"
                : "iOS 设备检测失败：" + detail;
        }

        public static string DescribeIosFailure(ProcessResult result)
        {
            string output = ((result == null ? "" : result.Stdout) + "\n" + (result == null ? "" : result.Stderr)).Trim();
            string lower = output.ToLowerInvariant();
            if (lower.Contains("no module named") || lower.Contains("modulenotfounderror"))
            {
                return "iOS 运行组件不完整，请重新安装 MoTuPerf。";
            }
            if (lower.Contains("not paired") || lower.Contains("invalidhostid") || lower.Contains("trust") || lower.Contains("password protected") || lower.Contains("locked"))
            {
                return "iOS 设备尚未信任或仍处于锁定状态，请解锁设备并在手机上选择“信任此电脑”。";
            }
            if (!HasAppleMobileDeviceSupport() || lower.Contains("usbmux") || lower.Contains("apple mobile device"))
            {
                return HasAppleMobileDeviceSupport()
                    ? "Apple Mobile Device 服务连接失败，请重启 Apple Mobile Device Service，并重新插拔设备。"
                    : "未安装 Apple 移动设备驱动，请安装官方“Apple 设备”应用后重新插拔设备。";
            }
            string detail = FirstLine(output);
            return string.IsNullOrWhiteSpace(detail)
                ? "iOS 设备检测失败，请检查 USB 连接、设备信任状态和 Apple Mobile Device 服务。"
                : "iOS 设备检测失败：" + detail;
        }

        public static string NoIosDeviceMessage()
        {
            return HasAppleMobileDeviceSupport()
                ? "未检测到 iOS 设备，请解锁设备、确认“信任此电脑”并重新插拔 USB。"
                : "未安装 Apple 移动设备驱动，请安装官方“Apple 设备”应用后重新插拔设备。";
        }

        private static bool AppleServiceFileExists(string commonProgramFiles)
        {
            if (string.IsNullOrWhiteSpace(commonProgramFiles)) return false;
            return File.Exists(Path.Combine(commonProgramFiles, "Apple", "Mobile Device Support", "AppleMobileDeviceService.exe"));
        }

        private static string TestOverride(string name)
        {
            return string.Equals(Environment.GetEnvironmentVariable("MOTUPERF_TEST_TOOL_OVERRIDE"), "1", StringComparison.Ordinal)
                ? Environment.GetEnvironmentVariable(name)
                : "";
        }

        private static string FirstLine(string value)
        {
            string[] lines = (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string line = lines.Length == 0 ? "" : lines[0].Trim();
            return line.Length <= 180 ? line : line.Substring(0, 180) + "...";
        }
    }
}
