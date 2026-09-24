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
    internal sealed class HarmonyLaunchEntryPoint
    {
        public string Module { get; set; }
        public string Ability { get; set; }
    }

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
            List<AppInfo> apps = await ReadBundleManagerAppsAsync(serial, token).ConfigureAwait(false);
            bool dumpFailed = apps.Count == 0;
            HashSet<string> seen = new HashSet<string>(apps.Select(delegate(AppInfo app) { return app.BundleId; }), StringComparer.OrdinalIgnoreCase);
            try
            {
                // Do not use `-3` here. Harmony devices can expose launchable
                // system/preinstalled applications through the Android
                // compatibility layer as well as third-party packages.
                foreach (string packageName in await ReadAndroidPackagesAsync(serial, token).ConfigureAwait(false))
                    AddApp(apps, seen, packageName, "Android 兼容应用或系统应用");
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Bundle Manager and package-manager views are not guaranteed to
            // agree across HarmonyOS releases. Always merge real processes so
            // an installed/running app remains selectable even when one
            // inventory command omits it.
            try
            {
                MergeProcessApps(apps, await ReadProcessListAsync(serial, token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (apps.Count == 0 && dumpFailed)
                throw new IOException("无法读取鸿蒙应用列表，请检查 HDC 授权，或在设备上打开应用后刷新进程。");
            return apps.OrderByDescending(delegate(AppInfo app) { return app.Recommended; }).ThenBy(delegate(AppInfo app) { return app.BundleId; }).ToList();
        }

        public async Task<List<ProcessInfo>> ListProcessesAsync(string serial, CancellationToken token)
        {
            List<ProcessInfo> processes = await ReadProcessListAsync(serial, token).ConfigureAwait(false);
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

        private async Task<List<string>> ReadAndroidPackagesAsync(string serial, CancellationToken token)
        {
            ProcessResult result = await RunShellAsync(serial, new[] { "pm", "list", "packages", "-f" }, 15000, token).ConfigureAwait(false);
            if (result.ExitCode == 0 && !IsHdcFailure(result))
            {
                List<string> packages = ParseAndroidPackages(result.Stdout);
                if (packages.Count > 0) return packages;
            }

            // A few compatibility containers expose package names but reject
            // the APK-path form. Keep this fallback broad as well; filtering
            // to third-party packages would hide valid launchable apps.
            result = await RunShellAsync(serial, new[] { "pm", "list", "packages" }, 15000, token).ConfigureAwait(false);
            return result.ExitCode == 0 && !IsHdcFailure(result)
                ? ParseAndroidPackages(result.Stdout)
                : new List<string>();
        }

        private async Task<List<AppInfo>> ReadBundleManagerAppsAsync(string serial, CancellationToken token)
        {
            List<AppInfo> apps = new List<AppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ProcessResult[] results = new ProcessResult[2];
            results[0] = await RunShellAsync(serial, new[] { "bm", "dump", "-a" }, 20000, token).ConfigureAwait(false);
            if (results[0] != null && results[0].ExitCode == 0 && !IsHdcFailure(results[0]))
            {
                foreach (AppInfo app in ParseApps(results[0].Stdout)) AddApp(apps, seen, app.BundleId, "Bundle Manager 应用");
            }
            if (apps.Count == 0)
            {
                // Some releases require an explicit user when dumping the
                // installed bundle list. This is a fallback only; unsupported
                // options are ignored and do not hide package/process results.
                results[1] = await RunShellAsync(serial, new[] { "bm", "dump", "-a", "-u", "0" }, 20000, token).ConfigureAwait(false);
                if (results[1] != null && results[1].ExitCode == 0 && !IsHdcFailure(results[1]))
                    foreach (AppInfo app in ParseApps(results[1].Stdout)) AddApp(apps, seen, app.BundleId, "Bundle Manager 应用");
            }
            return apps;
        }

        private async Task<List<ProcessInfo>> ReadProcessListAsync(string serial, CancellationToken token)
        {
            List<ProcessInfo> allProcesses = new List<ProcessInfo>();
            bool commandSucceeded = false;
            string diagnostics = "";
            string[][] commands = new[]
            {
                new[] { "ps", "-A", "-o", "PID,ARGS" },
                new[] { "ps", "-ef" },
                new[] { "ps", "-A" },
                new[] { "ps" }
            };
            foreach (string[] command in commands)
            {
                ProcessResult result;
                try
                {
                    result = await RunShellAsync(serial, command, 12000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    diagnostics = exception.Message;
                    continue;
                }
                if (result != null && result.ExitCode == 0 && !IsHdcFailure(result))
                {
                    commandSucceeded = true;
                    allProcesses.AddRange(ParseProcesses(result.Stdout, serial));
                }
                else if (result != null)
                {
                    diagnostics = result.Stdout + " " + result.Stderr;
                }
            }
            List<ProcessInfo> processes = MergeProcesses(allProcesses);
            if (processes.Count == 0 && !commandSucceeded)
                throw new IOException("无法读取鸿蒙进程：" + Sanitize(diagnostics, 240));
            return processes;
        }

        public async Task<ProcessResult> LaunchAppAsync(string serial, string bundleId, CancellationToken token)
        {
            if (!ValidBundle(bundleId)) return new ProcessResult(1, "", "鸿蒙应用包名无效。");
            ProcessResult detail = await RunShellAsync(serial, new[] { "bm", "dump", "-n", bundleId }, 12000, token);
            List<ProcessResult> attempts = new List<ProcessResult>();
            if (detail != null) attempts.Add(detail);
            HashSet<string> abilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HarmonyLaunchEntryPoint entry in ParseLaunchEntryPoints(detail == null ? "" : detail.Stdout))
            {
                if (!string.IsNullOrWhiteSpace(entry.Ability)) abilities.Add(entry.Ability);
                if (!string.IsNullOrWhiteSpace(entry.Ability) && !string.IsNullOrWhiteSpace(entry.Module))
                {
                    ProcessResult result = await RunShellAsync(serial, new[] { "aa", "start", "-b", bundleId, "-m", entry.Module, "-a", entry.Ability }, 15000, token).ConfigureAwait(false);
                    attempts.Add(result);
                    if (IsSuccessfulCommand(result)) return result;
                }
            }
            foreach (string ability in abilities)
            {
                ProcessResult result = await RunShellAsync(serial, new[] { "aa", "start", "-b", bundleId, "-a", ability }, 15000, token).ConfigureAwait(false);
                attempts.Add(result);
                if (IsSuccessfulCommand(result)) return result;
            }
            ProcessResult packageStart = await RunShellAsync(serial, new[] { "aa", "start", "-b", bundleId }, 15000, token).ConfigureAwait(false);
            attempts.Add(packageStart);
            if (IsSuccessfulCommand(packageStart)) return packageStart;
            ProcessResult androidStart = await RunShellAsync(serial, new[] { "am", "start", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", "-p", bundleId }, 15000, token).ConfigureAwait(false);
            attempts.Add(androidStart);
            if (IsSuccessfulCommand(androidStart)) return androidStart;
            ProcessResult monkeyStart = await RunShellAsync(serial, new[] { "monkey", "-p", bundleId, "-c", "android.intent.category.LAUNCHER", "1" }, 15000, token).ConfigureAwait(false);
            attempts.Add(monkeyStart);
            if (IsSuccessfulCommand(monkeyStart)) return monkeyStart;
            return CombineLaunchFailures(attempts);
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
            MatchCollection keyed = Regex.Matches(
                output ?? "",
                @"(?i)(?:[""']?)(?:bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|packageName|package_name|modulePackage|module_package|module\s+package|applicationId|application_id|appIdentifier|app_identifier|package\s+name)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.]+)",
                RegexOptions.CultureInvariant);
            foreach (Match match in keyed)
            {
                AddApp(apps, seen, match.Groups[1].Value);
            }
            foreach (string raw in Lines(output))
            {
                string line = raw.Trim().Trim('{', '}', ',', ' ', '\t');
                if (ValidBundle(line)) AddApp(apps, seen, line);
                Match leadingBundle = Regex.Match(
                    line,
                    @"^(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:\s|[,;}]|$)",
                    RegexOptions.CultureInvariant);
                if (leadingBundle.Success) AddApp(apps, seen, leadingBundle.Groups["bundle"].Value, "Bundle Manager 应用");
            }
            return apps;
        }

        internal static List<ProcessInfo> MergeProcesses(IEnumerable<ProcessInfo> source)
        {
            Dictionary<int, ProcessInfo> byPid = new Dictionary<int, ProcessInfo>();
            foreach (ProcessInfo process in source ?? Enumerable.Empty<ProcessInfo>())
            {
                if (!ProcessTargetMatcher.IsValidTarget(process)) continue;
                ProcessInfo existing;
                if (!byPid.TryGetValue(process.Pid, out existing))
                {
                    byPid[process.Pid] = process;
                    continue;
                }
                bool replace = string.IsNullOrWhiteSpace(existing.BundleId) && !string.IsNullOrWhiteSpace(process.BundleId);
                if (!replace && existing.HarmonyStartTimeTicks <= 0 && process.HarmonyStartTimeTicks > 0) replace = true;
                if (replace) byPid[process.Pid] = process;
            }
            return byPid.Values
                .OrderByDescending(delegate(ProcessInfo process) { return process.Recommended; })
                .ThenBy(delegate(ProcessInfo process) { return process.Name; })
                .ThenBy(delegate(ProcessInfo process) { return process.Pid; })
                .ToList();
        }

        internal static List<string> ParseAndroidPackages(string output)
        {
            List<string> packages = new List<string>();
            foreach (string raw in Lines(output))
            {
                string line = (raw ?? "").Trim();
                if (!line.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;
                string value = line.Substring("package:".Length).Trim();
                int equals = value.LastIndexOf('=');
                if (equals >= 0) value = value.Substring(equals + 1).Trim();
                else
                {
                    Match first = Regex.Match(value, @"^([^\s]+)");
                    value = first.Success ? first.Groups[1].Value : value;
                }
                value = value.Trim('"', '\'', ',', ';');
                if (ValidBundle(value) && !packages.Contains(value, StringComparer.OrdinalIgnoreCase)) packages.Add(value);
            }
            return packages;
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
                int headerPid = IndexOfIgnoreCase(parts, "PID");
                if (headerPid >= 0)
                {
                    pidIndex = headerPid;
                    commandIndex = IndexOfAnyIgnoreCase(parts, "ARGS", "CMD", "COMMAND", "NAME");
                    continue;
                }
                int rowPidIndex = pidIndex;
                if (rowPidIndex < 0)
                {
                    if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int firstPid)) rowPidIndex = 0;
                    else if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int secondPid)) rowPidIndex = 1;
                }
                if (rowPidIndex < 0 || parts.Length <= rowPidIndex
                    || !int.TryParse(parts[rowPidIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) || pid <= 0) continue;
                int rowCommandIndex = commandIndex >= 0 && commandIndex < parts.Length ? commandIndex : FindProcessTokenIndex(parts, rowPidIndex + 1);
                if (rowCommandIndex < 0) continue;
                string name = ProcessNameFromCommand(string.Join(" ", parts.Skip(rowCommandIndex)));
                if (string.IsNullOrWhiteSpace(name)) continue;
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

        internal static void MergeProcessApps(IList<AppInfo> apps, IEnumerable<ProcessInfo> processes)
        {
            if (apps == null || processes == null) return;
            HashSet<string> seen = new HashSet<string>(apps.Where(a => a != null).Select(a => a.BundleId), StringComparer.OrdinalIgnoreCase);
            foreach (ProcessInfo process in processes)
            {
                if (process == null || !ValidBundle(process.BundleId) || !seen.Add(process.BundleId)) continue;
                apps.Add(new AppInfo
                {
                    BundleId = process.BundleId,
                    Name = process.BundleId,
                    Platform = "harmony",
                    Recommended = process.ForegroundApplication,
                    Reason = "运行中的鸿蒙应用"
                });
            }
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
            Match textValue = Regex.Match(
                output ?? "",
                @"(?i)(?:[""']?)(?:mainElementName|mainAbility|mainAbilityName|entryAbility|entryAbilityName|abilityName)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.$-]*)(?:[""']?)",
                RegexOptions.CultureInvariant);
            if (textValue.Success) return textValue.Groups[1].Value.Trim();
            string json = ExtractJsonObject(output);
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json)) return FindMainAbility(doc.RootElement);
            }
            catch (JsonException) { return ""; }
        }

        internal static string ParseMainModule(string output)
        {
            Match textValue = Regex.Match(
                output ?? "",
                @"(?i)(?:[""']?)(?:moduleName|module_name|mainModuleName|main_module_name)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.-]*)",
                RegexOptions.CultureInvariant);
            if (textValue.Success) return textValue.Groups[1].Value.Trim();
            string json = ExtractJsonObject(output);
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json)) return FindMainModule(doc.RootElement);
            }
            catch (JsonException) { return ""; }
        }

        private static string FindMainAbility(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "mainAbility", "mainElementName", "mainAbilityName", "entryAbility", "entryAbilityName", "abilityName" })
                    foreach (JsonProperty property in node.EnumerateObject())
                        if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(property.Value.GetString())) return property.Value.GetString();
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

        internal static List<HarmonyLaunchEntryPoint> ParseLaunchEntryPoints(string output)
        {
            List<HarmonyLaunchEntryPoint> entries = new List<HarmonyLaunchEntryPoint>();
            string json = ExtractJsonObject(output);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        CollectLaunchEntryPoints(document.RootElement, "", entries);
                    }
                }
                catch (JsonException) { }
            }

            MatchCollection abilityMatches = Regex.Matches(
                output ?? "",
                @"(?i)(?:[""']?)(?:mainElementName|mainAbility|mainAbilityName|entryAbility|entryAbilityName|abilityName)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.$-]*)",
                RegexOptions.CultureInvariant);
            MatchCollection moduleMatches = Regex.Matches(
                output ?? "",
                @"(?i)(?:[""']?)(?:moduleName|module_name|mainModuleName|main_module_name)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.-]*)",
                RegexOptions.CultureInvariant);
            for (int i = 0; i < abilityMatches.Count; i++)
            {
                string module = moduleMatches.Count == 0
                    ? ""
                    : moduleMatches[Math.Min(i, moduleMatches.Count - 1)].Groups[1].Value;
                AddLaunchEntry(entries, module, abilityMatches[i].Groups[1].Value);
            }
            if (entries.Count == 0)
                AddLaunchEntry(entries, ParseMainModule(output), ParseMainAbility(output));
            return entries;
        }

        private static void CollectLaunchEntryPoints(JsonElement node, string inheritedModule, IList<HarmonyLaunchEntryPoint> entries)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                string module = FirstNonEmpty(JsonPropertyValue(node, "moduleName"), JsonPropertyValue(node, "module_name"),
                    JsonPropertyValue(node, "mainModuleName"), JsonPropertyValue(node, "main_module_name"), inheritedModule);
                string ability = FirstNonEmpty(JsonPropertyValue(node, "mainElementName"), JsonPropertyValue(node, "mainAbility"),
                    JsonPropertyValue(node, "mainAbilityName"), JsonPropertyValue(node, "entryAbility"),
                    JsonPropertyValue(node, "entryAbilityName"), JsonPropertyValue(node, "abilityName"));
                if (!string.IsNullOrWhiteSpace(ability)) AddLaunchEntry(entries, module, ability);
                foreach (JsonProperty property in node.EnumerateObject())
                    CollectLaunchEntryPoints(property.Value, module, entries);
                return;
            }
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                    CollectLaunchEntryPoints(value, inheritedModule, entries);
            }
        }

        private static string JsonPropertyValue(JsonElement node, string name)
        {
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString() ?? "";
            }
            return "";
        }

        private static void AddLaunchEntry(IList<HarmonyLaunchEntryPoint> entries, string module, string ability)
        {
            module = (module ?? "").Trim();
            ability = (ability ?? "").Trim();
            if (!ValidEntryToken(ability) || (!string.IsNullOrWhiteSpace(module) && !ValidEntryToken(module))) return;
            if (entries.Any(delegate(HarmonyLaunchEntryPoint entry)
            {
                return string.Equals(entry.Module, module, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Ability, ability, StringComparison.OrdinalIgnoreCase);
            })) return;
            entries.Add(new HarmonyLaunchEntryPoint { Module = module, Ability = ability });
        }

        private static bool ValidEntryToken(string value)
        {
            return Regex.IsMatch(value ?? "", @"^[A-Za-z][A-Za-z0-9_.$-]*$", RegexOptions.CultureInvariant);
        }

        private static string FindMainModule(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                    if ((string.Equals(property.Name, "moduleName", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "module_name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "mainModuleName", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "main_module_name", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString())) return property.Value.GetString();
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    string found = FindMainModule(property.Value);
                    if (found.Length > 0) return found;
                }
            }
            if (node.ValueKind == JsonValueKind.Array)
                foreach (JsonElement value in node.EnumerateArray())
                {
                    string found = FindMainModule(value);
                    if (found.Length > 0) return found;
                }
            return "";
        }

        private static string ExtractJsonObject(string output)
        {
            string value = output ?? "";
            int start = value.IndexOf('{');
            int end = value.LastIndexOf('}');
            return start >= 0 && end >= start ? value.Substring(start, end - start + 1) : "";
        }


        private static void AddApp(IList<AppInfo> apps, ISet<string> seen, string bundle, string reason = "")
        {
            bundle = (bundle ?? "").Trim().Trim('"', '\'', ',', ';');
            if (!ValidBundle(bundle) || !seen.Add(bundle)) return;
            apps.Add(new AppInfo
            {
                BundleId = bundle,
                Name = bundle,
                Platform = "harmony",
                Recommended = false,
                Reason = reason ?? ""
            });
        }

        private static bool IsSuccessfulCommand(ProcessResult result)
        {
            if (result == null || result.ExitCode != 0 || IsHdcFailure(result)) return false;
            string output = (result.Stdout ?? "") + "\n" + (result.Stderr ?? "");
            return !Regex.IsMatch(output,
                @"(?im)(^|\n)\s*(?:error|failed|failure|exception)\b|not found|does not exist|invalid ability|unknown option|permission denied",
                RegexOptions.CultureInvariant);
        }

        private static ProcessResult CombineLaunchFailures(IEnumerable<ProcessResult> attempts)
        {
            string stdout = string.Join("\n", (attempts ?? Enumerable.Empty<ProcessResult>())
                .Where(delegate(ProcessResult result) { return result != null && !string.IsNullOrWhiteSpace(result.Stdout); })
                .Select(delegate(ProcessResult result) { return result.Stdout; }));
            string stderr = string.Join("\n", (attempts ?? Enumerable.Empty<ProcessResult>())
                .Where(delegate(ProcessResult result) { return result != null && !string.IsNullOrWhiteSpace(result.Stderr); })
                .Select(delegate(ProcessResult result) { return result.Stderr; }));
            return new ProcessResult(1, Sanitize(stdout, 4000), Sanitize(stderr, 4000));
        }

        private static bool IsHdcFailure(ProcessResult result)
        {
            string output = (result == null ? "" : result.Stdout) + "\n" + (result == null ? "" : result.Stderr);
            return output.IndexOf("[Fail]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int IndexOfIgnoreCase(string[] values, string expected)
        {
            if (values == null) return -1;
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], expected, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int IndexOfAnyIgnoreCase(string[] values, params string[] expected)
        {
            if (expected == null) return -1;
            foreach (string item in expected)
            {
                int index = IndexOfIgnoreCase(values, item);
                if (index >= 0) return index;
            }
            return -1;
        }

        private static int FindProcessTokenIndex(string[] parts, int start)
        {
            for (int i = Math.Max(0, start); i < (parts == null ? 0 : parts.Length); i++)
            {
                if (ValidBundle(BundleFromProcess(parts[i]))) return i;
                if (parts[i].StartsWith("/", StringComparison.Ordinal) || parts[i].StartsWith("[", StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static string ProcessNameFromCommand(string command)
        {
            string value = (command ?? "").Trim().Trim('"', '\'');
            MatchCollection tokens = Regex.Matches(value, @"\S+");
            foreach (Match token in tokens)
            {
                string candidate = token.Value.Trim('"', '\'');
                if (ValidBundle(BundleFromProcess(candidate))) return candidate;
            }
            int space = value.IndexOf(' ');
            return (space >= 0 ? value.Substring(0, space) : value).Trim();
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
            string value = name.Trim().Trim('"', '\'');
            Match explicitBundle = Regex.Match(
                value,
                @"(?i)(?:bundle[-_ ]?name|package[-_ ]?name)\s*[=:]\s*[""']?([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?::[A-Za-z0-9_.-]+)?",
                RegexOptions.CultureInvariant);
            if (explicitBundle.Success && ValidBundle(explicitBundle.Groups[1].Value)) return explicitBundle.Groups[1].Value;
            Match match = Regex.Match(
                value,
                @"(?<![A-Za-z0-9_])([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?::[A-Za-z0-9_.-]+)?(?:/|$)",
                RegexOptions.CultureInvariant);
            return match.Success && ValidBundle(match.Groups[1].Value) ? match.Groups[1].Value : "";
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
