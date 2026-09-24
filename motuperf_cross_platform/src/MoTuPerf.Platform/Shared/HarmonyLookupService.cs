using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    /// <summary>
    /// Owns the HDC boundary for OpenHarmony/HarmonyOS devices. Harmony is kept
    /// as its own platform even when the selected application is an Android
    /// compatibility application.
    /// </summary>
    public sealed class HarmonyLookupService
    {
        public async Task<PlatformDeviceDiscovery> DiscoverDevicesAsync(CancellationToken token)
        {
            PlatformDeviceDiscovery report = new PlatformDeviceDiscovery();
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    RuntimeTools.HdcExecutable,
                    new[] { "list", "targets", "-v" },
                    12000,
                    token);
                if (result.ExitCode != 0)
                {
                    report.Diagnostic = DescribeDiscoveryFailure(result);
                    return report;
                }

                report = ParseDiscovery(result);
                foreach (DeviceInfo device in report.Devices)
                {
                    await PopulateDeviceDetailsAsync(device, token).ConfigureAwait(false);
                }
                return report;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                report.Diagnostic = DescribeDiscoveryException(exception);
                return report;
            }
        }

        internal static PlatformDeviceDiscovery ParseDiscovery(ProcessResult result)
        {
            PlatformDeviceDiscovery report = new PlatformDeviceDiscovery();
            if (result == null)
            {
                report.Diagnostic = "HDC 未返回检测结果。";
                return report;
            }
            if (result.ExitCode != 0)
            {
                report.Diagnostic = DescribeDiscoveryFailure(result);
                return report;
            }

            HashSet<string> issues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in Lines(result.Stdout))
            {
                string line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line == "[Empty]") continue;
                if (line.StartsWith("[Fail]", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(DescribeDiscoveryFailure(new ProcessResult(1, line, "")));
                    continue;
                }
                if (line.StartsWith("[", StringComparison.Ordinal)) continue;
                string[] parts = Regex.Split(line, @"\s+");
                string serial = parts[0];
                if (!Regex.IsMatch(serial, @"^[A-Za-z0-9][A-Za-z0-9_.:-]*$")) continue;
                string state = parts.Length == 1 ? "connected" : parts[parts.Length > 2 && (parts[1] == "USB" || parts[1] == "TCP") ? 2 : 1].ToLowerInvariant();
                if (IsConnectedState(state))
                {
                    if (report.Devices.Any(d => d.Udid == serial)) continue;
                    report.Devices.Add(new DeviceInfo
                    {
                        Udid = serial,
                        Name = "鸿蒙设备 " + ShortId(serial),
                        MarketName = "鸿蒙设备 " + ShortId(serial),
                        ConnType = "HDC",
                        Platform = "harmony",
                        Recommended = report.Devices.Count == 0
                    });
                    continue;
                }
                if (state.Contains("unauthorized") || state.Contains("un-authorized"))
                    issues.Add("鸿蒙设备未授权：请解锁设备，在设备端允许 USB 调试/HDC 调试后刷新。");
                else if (state.Contains("offline"))
                    issues.Add("鸿蒙设备离线：请重新插拔 USB、检查数据线和开发者调试开关后刷新。");
                else if (state.Contains("disconnect"))
                    issues.Add("鸿蒙设备连接已断开：请检查 USB 连接后刷新。");
                else
                    issues.Add("鸿蒙设备当前不可采集（" + Sanitize(state, 40) + "），请在设备端确认调试授权。");
            }
            if (report.Devices.Count == 0 && issues.Count == 0)
                issues.Add("未发现鸿蒙设备，请连接 USB、开启开发者调试并在设备端授权 HDC。");
            report.Diagnostic = string.Join("\n", issues);
            return report;
        }

        public async Task<bool?> ProbeOnlineAsync(string serial, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    RuntimeTools.HdcExecutable,
                    new[] { "list", "targets", "-v" },
                    5000,
                    token);
                if (result.ExitCode != 0) return null;
                if ((result.Stdout ?? "").Contains("[Fail]")) return null;
                return ParseDiscovery(result).Devices.Any(delegate(DeviceInfo device)
                {
                    return string.Equals(device.Udid, serial, StringComparison.Ordinal);
                });
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        public async Task<List<AppInfo>> ListAppsAsync(string serial, CancellationToken token)
        {
            ProcessResult dump = await RunShellAsync(serial, new[] { "bm", "dump", "-a" }, 20000, token).ConfigureAwait(false);
            List<AppInfo> apps = dump.ExitCode == 0 ? ParseApps(dump.Stdout) : new List<AppInfo>();
            if (dump.ExitCode != 0 || (dump.Stdout ?? "").Contains("[Fail]"))
                throw new IOException("无法读取鸿蒙应用列表，请检查 HDC 授权，或在设备上打开应用后选择进程。" + Sanitize(dump.Stdout + " " + dump.Stderr, 240));
            return apps.OrderByDescending(delegate(AppInfo app) { return app.Recommended; }).ThenBy(delegate(AppInfo app) { return app.BundleId; }).ToList();
        }

        public async Task<List<ProcessInfo>> ListProcessesAsync(string serial, CancellationToken token)
        {
            ProcessResult result = await RunShellAsync(serial, new[] { "ps", "-A", "-o", "PID,ARGS" }, 12000, token).ConfigureAwait(false);
            if (result == null || result.ExitCode != 0 || ParseProcesses(result.Stdout, serial).Count == 0)
            {
                result = await RunShellAsync(serial, new[] { "ps", "-ef" }, 12000, token).ConfigureAwait(false);
            }
            if (result == null || result.ExitCode != 0 || (result.Stdout ?? "").Contains("[Fail]"))
                throw new IOException("无法读取鸿蒙进程：" + Sanitize(result?.Stdout + " " + result?.Stderr, 240));
            List<ProcessInfo> processes = ParseProcesses(result.Stdout, serial);
            if (processes.Count > 0)
            {
                string pids = string.Join(" ", processes.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture)));
                ProcessResult stats = await RunShellAsync(serial, new[] { "sh", "-c", "for p in " + pids + "; do cat /proc/$p/stat 2>/dev/null; done" }, 12000, token);
                Dictionary<int, long> starts = AndroidLookupService.ParseProcessStartTimeTicks(stats.Stdout);
                foreach (ProcessInfo process in processes)
                    if (starts.TryGetValue(process.Pid, out long ticks)) process.HarmonyStartTimeTicks = ticks;
            }
            return processes;
        }

        public async Task<ProcessResult> LaunchAppAsync(string serial, string bundleId, CancellationToken token)
        {
            if (!ValidBundle(bundleId)) return new ProcessResult(1, "", "鸿蒙应用包名无效。");
            ProcessResult detail = await RunShellAsync(serial, new[] { "bm", "dump", "-n", bundleId }, 12000, token);
            string ability = ParseMainAbility(detail.Stdout);
            if (string.IsNullOrWhiteSpace(ability))
                return new ProcessResult(1, "", "设备未提供可启动的入口 Ability，请在设备上打开应用，再刷新并选择进程。");
            ProcessResult result = await RunShellAsync(serial, new[] { "aa", "start", "-b", bundleId, "-a", ability }, 15000, token).ConfigureAwait(false);
            return result.ExitCode == 0 && result.Stdout.IndexOf("start ability successfully", StringComparison.OrdinalIgnoreCase) < 0
                ? new ProcessResult(1, result.Stdout, result.Stderr) : result;
        }

        public Task HydrateAppIconsAsync(string serial, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token)
        {
            // HDC does not expose a portable icon extraction contract. Keep the
            // real package/process rows immediately usable and leave icons empty.
            return Task.CompletedTask;
        }

        internal static List<AppInfo> ParseApps(string output)
        {
            List<AppInfo> apps = new List<AppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in Lines(output))
            {
                string line = raw.Trim();
                Match named = Regex.Match(line, "^\\s*[\"']?bundleName[\"']?\\s*[:=]\\s*[\"']?([A-Za-z][A-Za-z0-9_.]+)", RegexOptions.IgnoreCase);
                string bundle = named.Success ? named.Groups[1].Value : line.Trim('"', ',', ' ', '\t');
                if (!ValidBundle(bundle)) continue;
                if (string.IsNullOrWhiteSpace(bundle) || !seen.Add(bundle)) continue;
                apps.Add(new AppInfo
                {
                    BundleId = bundle,
                    Name = bundle,
                    Platform = "harmony",
                    Recommended = false
                });
            }
            return apps;
        }

        internal static List<ProcessInfo> ParseProcesses(string output, string serial)
        {
            List<ProcessInfo> processes = new List<ProcessInfo>();
            int pidIndex = -1;
            int commandIndex = -1;
            foreach (string raw in Lines(output))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = Regex.Split(line, @"\s+");
                if (parts.Contains("PID"))
                {
                    pidIndex = Array.IndexOf(parts, "PID");
                    commandIndex = Array.FindIndex(parts, x => x == "ARGS" || x == "CMD" || x == "COMMAND" || x == "NAME");
                    continue;
                }
                if (pidIndex < 0 || commandIndex < 0 || parts.Length <= Math.Max(pidIndex, commandIndex)
                    || !int.TryParse(parts[pidIndex], out int pid) || pid <= 0) continue;
                string name = parts[commandIndex];
                string bundle = BundleFromProcess(name);
                processes.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    DisplayName = name,
                    BundleId = bundle,
                    DeviceUdid = serial ?? "",
                    Platform = "harmony",
                    Recommended = false,
                    ForegroundApplication = false
                });
            }
            return processes
                .GroupBy(delegate(ProcessInfo process) { return process.Pid.ToString(CultureInfo.InvariantCulture) + "|" + process.Name; })
                .Select(delegate(IGrouping<string, ProcessInfo> group) { return group.First(); })
                .Where(ProcessTargetMatcher.IsValidTarget)
                .OrderByDescending(delegate(ProcessInfo process) { return process.Recommended; })
                .ThenBy(delegate(ProcessInfo process) { return process.Name; })
                .ThenBy(delegate(ProcessInfo process) { return process.Pid; })
                .ToList();
        }

        internal static string DescribeDiscoveryException(Exception exception)
        {
            if (exception is TimeoutException) return "HDC 响应超时，请检查 USB 连接、设备解锁状态和 HDC 调试授权后刷新。";
            if (exception is FileNotFoundException || exception is System.ComponentModel.Win32Exception)
                return "未找到或无法启动 HDC。请安装官方鸿蒙 SDK 的 toolchains，将其目录加入 PATH，或将 MOTUPERF_HDC 设置为 hdc.exe 的完整路径，然后重启 MoTuPerf。";
            return "鸿蒙设备检测失败：" + Sanitize(exception == null ? "" : exception.Message, 240);
        }

        internal static string DescribeDiscoveryFailure(ProcessResult result)
        {
            string output = ((result == null ? "" : result.Stdout) + "\n" + (result == null ? "" : result.Stderr)).Trim();
            string lower = output.ToLowerInvariant();
            if (lower.Contains("device not found") || lower.Contains("not connected"))
                return "HDC 未连接到设备，请检查数据线、调试开关和设备端授权后刷新。";
            if (lower.Contains("command not found") || lower.Contains("is not recognized") || lower.Contains("no such file"))
                return "未找到 HDC 运行组件，请安装官方鸿蒙 SDK 的 toolchains 并配置 PATH 或 MOTUPERF_HDC，然后重启 MoTuPerf。";
            if (lower.Contains("timeout")) return "HDC 响应超时，请检查 USB 连接和设备授权后刷新。";
            return "HDC 检测失败（退出码 " + (result == null ? -1 : result.ExitCode).ToString(CultureInfo.InvariantCulture) + "）。" + Sanitize(output, 240);
        }

        internal static List<string> TargetArgs(string serial, params string[] command)
        {
            List<string> args = new List<string>();
            if (!string.IsNullOrWhiteSpace(serial)) { args.Add("-t"); args.Add(serial); }
            args.Add("shell");
            if (command != null) args.Add(string.Join(" ", command.Select(ShellQuote)));
            return args;
        }

        private static string ShellQuote(string value) { return "'" + (value ?? "").Replace("'", "'\"'\"'") + "'"; }

        internal static string ParseMainAbility(string output)
        {
            int start = (output ?? "").IndexOf('{');
            if (start < 0) return "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(output.Substring(start))) return FindMainAbility(doc.RootElement);
            }
            catch (JsonException) { return ""; }
        }

        private static string FindMainAbility(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "mainAbility", "mainElementName" })
                    if (node.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    string found = FindMainAbility(property.Value);
                    if (found.Length > 0) return found;
                }
            }
            if (node.ValueKind == JsonValueKind.Array)
                foreach (JsonElement value in node.EnumerateArray())
                {
                    string found = FindMainAbility(value);
                    if (found.Length > 0) return found;
                }
            return "";
        }

        private static bool ValidBundle(string value) { return Regex.IsMatch(value ?? "", @"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+$"); }

        private static async Task<ProcessResult> RunShellAsync(string serial, IEnumerable<string> command, int timeoutMs, CancellationToken token)
        {
            return await ProcessRunner.RunAsync(RuntimeTools.HdcExecutable, TargetArgs(serial, command == null ? new string[0] : command.ToArray()), timeoutMs, token).ConfigureAwait(false);
        }

        private static async Task PopulateDeviceDetailsAsync(DeviceInfo device, CancellationToken token)
        {
            Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "const.product.model", "const.product.brand", "const.ohos.version", "const.product.name" })
            {
                try
                {
                    ProcessResult result = await RunShellAsync(device.Udid, new[] { "param", "get", key }, 5000, token).ConfigureAwait(false);
                    string value = FirstLine(result.Stdout);
                    if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value, "fail|error|not found|not exist", RegexOptions.IgnoreCase)) properties[key] = value;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            string model = Value(properties, "const.product.model");
            string product = Value(properties, "const.product.name");
            device.Name = FirstNonEmpty(model, product, device.Name);
            device.MarketName = device.Name;
            device.Brand = Value(properties, "const.product.brand");
            device.ProductVersion = Value(properties, "const.ohos.version");
            try
            {
                ProcessResult resolution = await RunShellAsync(device.Udid, new[] { "hidumper", "-s", "RenderService", "-a", "screen" }, 5000, token).ConfigureAwait(false);
                if (resolution.ExitCode == 0) device.Resolution = ParseResolution(resolution.Stdout);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        internal static string ParseResolution(string text)
        {
            List<string> sizes = new List<string>();
            foreach (string line in Lines(text))
            {
                if (!line.Contains("isVirtual=false")) continue;
                Match match = Regex.Match(line, @"physical resolution=([1-9]\d*)x([1-9]\d*)");
                if (match.Success) sizes.Add(match.Groups[1].Value + "x" + match.Groups[2].Value);
            }
            return string.Join(" / ", sizes.Distinct());
        }

        private static string BundleFromProcess(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string value = name;
            int colon = value.IndexOf(':');
            if (colon > 0) value = value.Substring(0, colon);
            return ValidBundle(value) ? value : "";
        }

        private static bool IsConnectedState(string state)
        {
            return state == "device" || state == "connected" || state == "online";
        }

        private static IEnumerable<string> Lines(string text)
        {
            return (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        private static string ShortId(string value)
        {
            string text = value ?? "";
            return text.Length <= 6 ? text : text.Substring(text.Length - 6);
        }

        private static string Value(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values) if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            return "";
        }

        private static string FirstLine(string value)
        {
            return FirstNonEmpty(Lines(value).ToArray());
        }

        private static string Sanitize(string value, int max)
        {
            return CaptureDiagnosticsLog.Sanitize(value, max);
        }
    }
}
