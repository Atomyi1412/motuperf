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
        public bool IsUiEntry { get; set; } = true;
    }

    internal sealed class HarmonyPackageRecord
    {
        public string BundleId { get; set; }
        public int UserId { get; set; }
    }

    /// <summary>
    /// Applications and processes collected from one HDC inventory pass. The
    /// picker uses this snapshot so both lists describe the same device state
    /// and share the same process start-time evidence.
    /// </summary>
    public sealed class HarmonyTargetInventory
    {
        public HarmonyTargetInventory()
        {
            Apps = new List<AppInfo>();
            Processes = new List<ProcessInfo>();
            ProcessInventoryError = "";
        }

        public List<AppInfo> Apps { get; set; }
        public List<ProcessInfo> Processes { get; set; }
        public string ProcessInventoryError { get; set; }
    }

    /// <summary>
    /// Owns the HDC boundary for OpenHarmony/HarmonyOS devices. Harmony is kept
    /// as its own platform even when the selected application is an Android
    /// compatibility application.
    /// </summary>
    public sealed class HarmonyLookupService
    {
        // The picker loads the app and process inventories in parallel. HDC
        // devices do not all tolerate concurrent shell sessions, so serialize
        // shell commands per service instance while keeping the UI tasks
        // independently cancellable.
        private readonly SemaphoreSlim _shellGate = new SemaphoreSlim(1, 1);
        private readonly Func<string, string[], int, CancellationToken, Task<ProcessResult>> _shellExecutor;

        public HarmonyLookupService()
            : this(null)
        {
        }

        internal HarmonyLookupService(Func<string, string[], int, CancellationToken, Task<ProcessResult>> shellExecutor)
        {
            _shellExecutor = shellExecutor ?? ExecuteHdcShellAsync;
        }

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
            HashSet<string> issues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in Lines((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")))
            {
                string line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line == "[Empty]") continue;
                string[] parts = Regex.Split(line, @"\s+");
                // HDC on Windows can expose the local serial-console control
                // port as `COM1 UART Ready ...` even when no Harmony device is
                // connected. It is an HDC transport endpoint, not a phone or
                // tablet that can be inspected or sampled.
                if (IsHdcControlPort(parts.Length == 0 ? "" : parts[0], parts)) continue;
                // A non-zero HDC invocation may still return a usable target
                // list. Ignore command-launch diagnostics so text such as
                // `hdc: command not found` cannot become a fake device.
                if (IsDiscoveryDiagnosticLine(line)) continue;
                if (line.StartsWith("[Fail]", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(DescribeDiscoveryFailure(new ProcessResult(1, line, "")));
                    continue;
                }
                if (line.StartsWith("[", StringComparison.Ordinal)) continue;
                string serial = parts[0];
                if (!Regex.IsMatch(serial, @"^[A-Za-z0-9][A-Za-z0-9_.:-]*$")) continue;
                string state = FindTargetState(parts);
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
                issues.Add(result.ExitCode == 0
                    ? "未发现鸿蒙设备，请连接 USB、开启开发者调试并在设备端授权 HDC。"
                    : DescribeDiscoveryFailure(result));
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
                if (IsHdcFailure(result)) return null;
                return ParseDiscovery(result).Devices.Any(delegate(DeviceInfo device)
                {
                    return string.Equals(device.Udid, serial, StringComparison.Ordinal);
                });
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        public async Task<HarmonyTargetInventory> ListTargetsAsync(string serial, CancellationToken token)
        {
            List<int> userIds;
            try
            {
                userIds = await ReadHarmonyUserIdsAsync(serial, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { userIds = new List<int> { 0 }; }

            List<AppInfo> apps = await ReadBundleManagerAppsAsync(serial, userIds, token).ConfigureAwait(false);
            bool dumpFailed = apps.Count == 0;
            HashSet<string> seen = new HashSet<string>(
                apps.Select(delegate(AppInfo app) { return app.BundleId; }),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                // Keep the same package sources as ListAppsAsync. This pass is
                // intentionally shared with process discovery below so the
                // picker never combines two independent inventory snapshots.
                foreach (HarmonyPackageRecord package in await ReadAndroidPackagesAsync(serial, userIds, token).ConfigureAwait(false))
                    AddApp(apps, seen, package.BundleId, "Android 兼容应用或系统应用", harmonyUserId: package.UserId);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            List<ProcessInfo> processes = new List<ProcessInfo>();
            string processInventoryError = "";
            try
            {
                processes = await ReadProcessesWithStartTimesAsync(serial, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                // The application inventory is an independent HDC evidence
                // source. Keep it usable when every process view is blocked,
                // but carry a bounded reason to the picker so the user knows
                // why no PID can be selected yet.
                processInventoryError = Sanitize(exception.Message, 240);
            }
            MergeProcessApps(apps, processes);
            if (apps.Count == 0 && dumpFailed)
                throw new IOException(string.IsNullOrWhiteSpace(processInventoryError)
                    ? "无法读取鸿蒙应用列表，请检查 HDC 授权，或在设备上打开应用后刷新进程。"
                    : "无法读取鸿蒙应用和进程列表：" + processInventoryError);

            return new HarmonyTargetInventory
            {
                Apps = ExpandHarmonyUserInstances(apps)
                    .OrderByDescending(delegate(AppInfo app) { return app.Recommended; })
                    .ThenBy(delegate(AppInfo app) { return app.BundleId; })
                    .ThenBy(delegate(AppInfo app) { return app.HarmonyUserId; })
                    .ToList(),
                Processes = processes,
                ProcessInventoryError = processInventoryError
            };
        }

        public async Task<List<AppInfo>> ListAppsAsync(string serial, CancellationToken token)
        {
            List<int> userIds;
            try
            {
                userIds = await ReadHarmonyUserIdsAsync(serial, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { userIds = new List<int> { 0 }; }

            List<AppInfo> apps = await ReadBundleManagerAppsAsync(serial, userIds, token).ConfigureAwait(false);
            bool dumpFailed = apps.Count == 0;
            HashSet<string> seen = new HashSet<string>(apps.Select(delegate(AppInfo app) { return app.BundleId; }), StringComparer.OrdinalIgnoreCase);
            try
            {
                // Do not use `-3` here. Harmony devices can expose launchable
                // system/preinstalled applications through the Android
                // compatibility layer as well as third-party packages.
                foreach (HarmonyPackageRecord package in await ReadAndroidPackagesAsync(serial, userIds, token).ConfigureAwait(false))
                    AddApp(apps, seen, package.BundleId, "Android 兼容应用或系统应用", harmonyUserId: package.UserId);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Bundle Manager and package-manager views are not guaranteed to
            // agree across HarmonyOS releases. Always merge real processes so
            // an installed/running app remains selectable even when one
            // inventory command omits it.
            try
            {
                List<ProcessInfo> processes = await ReadProcessListAsync(serial, token).ConfigureAwait(false);
                MergeProcessApps(apps, processes);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (apps.Count == 0 && dumpFailed)
                throw new IOException("无法读取鸿蒙应用列表，请检查 HDC 授权，或在设备上打开应用后刷新进程。");
            return ExpandHarmonyUserInstances(apps)
                .OrderByDescending(delegate(AppInfo app) { return app.Recommended; })
                .ThenBy(delegate(AppInfo app) { return app.BundleId; })
                .ThenBy(delegate(AppInfo app) { return app.HarmonyUserId; })
                .ToList();
        }

        public async Task<List<ProcessInfo>> ListProcessesAsync(string serial, CancellationToken token)
        {
            return await ReadProcessesWithStartTimesAsync(serial, token).ConfigureAwait(false);
        }

        private async Task<List<ProcessInfo>> ReadProcessesWithStartTimesAsync(string serial, CancellationToken token)
        {
            List<ProcessInfo> processes = await ReadProcessListAsync(serial, token).ConfigureAwait(false);
            if (processes.Count > 0)
            {
                Dictionary<int, long> starts = new Dictionary<int, long>();
                foreach (string[] command in BuildProcessStatCommands(processes.Select(p => p.Pid)))
                {
                    ProcessResult stats;
                    try
                    {
                        stats = await RunShellAsync(serial, command, 12000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // The process rows are still valid evidence even if
                        // this optional start-time batch is unavailable.
                        continue;
                    }
                    // Some vendor HDC builds return a non-zero code when one
                    // PID in the batch is unreadable, while still returning
                    // valid /proc/<pid>/stat rows for the other PIDs. Keep the
                    // usable rows so one restricted process does not remove
                    // start-time protection from the whole process list.
                    if (stats == null || IsHdcFailure(stats)) continue;
                    string statOutput = (stats.Stdout ?? "") + "\n" + (stats.Stderr ?? "");
                    foreach (KeyValuePair<int, long> pair in AndroidLookupService.ParseProcessStartTimeTicks(statOutput))
                        starts[pair.Key] = pair.Value;
                }
                foreach (ProcessInfo process in processes)
                    if (starts.TryGetValue(process.Pid, out long ticks)) process.HarmonyStartTimeTicks = ticks;
            }
            return processes;
        }

        internal static List<string[]> BuildProcessStatCommands(IEnumerable<int> pids)
        {
            const int batchSize = 64;
            List<int> values = (pids ?? Enumerable.Empty<int>())
                .Where(delegate(int pid) { return pid > 0; })
                .Distinct()
                .OrderBy(delegate(int pid) { return pid; })
                .ToList();
            List<string[]> commands = new List<string[]>();
            for (int offset = 0; offset < values.Count; offset += batchSize)
            {
                IEnumerable<int> batch = values.Skip(offset).Take(batchSize);
                string pidList = string.Join(" ", batch.Select(delegate(int pid) { return pid.ToString(CultureInfo.InvariantCulture); }));
                commands.Add(new[] { "sh", "-c", "for p in " + pidList + "; do cat /proc/$p/stat 2>/dev/null; done" });
            }
            return commands;
        }

        internal static List<int> ParseUserIds(string output)
        {
            HashSet<int> ids = new HashSet<int> { 0 };
            MatchCollection matches = Regex.Matches(
                output ?? "",
                @"(?i)(?:UserInfo\{\s*(?:id\s*[:=]\s*)?|\buser(?:\s+id|Id)?\s*(?:[:=]\s*)?)\s*(?<id>\d+)",
                RegexOptions.CultureInvariant);
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    && id >= 0)
                    ids.Add(id);
            }
            return ids.OrderBy(delegate(int id) { return id; }).ToList();
        }

        private async Task<List<int>> ReadHarmonyUserIdsAsync(string serial, CancellationToken token)
        {
            HashSet<int> userIds = new HashSet<int> { 0 };
            foreach (string[] command in BuildUserInventoryCommands())
            {
                try
                {
                    ProcessResult result = await RunShellAsync(serial, command, 10000, token).ConfigureAwait(false);
                    if (result == null || IsHdcFailure(result)) continue;
                    foreach (int userId in ParseUserIds((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")))
                        userIds.Add(userId);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return userIds.OrderBy(delegate(int id) { return id; }).ToList();
        }

        internal static IReadOnlyList<string[]> BuildUserInventoryCommands()
        {
            // Harmony releases expose the profile list through different
            // command families. Query both and merge the readable IDs so a
            // work profile is not hidden just because the legacy pm wrapper
            // is missing.
            return new[]
            {
                new[] { "pm", "list", "users" },
                new[] { "cmd", "user", "list" }
            };
        }

        private async Task<List<HarmonyPackageRecord>> ReadAndroidPackagesAsync(string serial, IEnumerable<int> userIds, CancellationToken token)
        {
            // Always merge both forms. Some Harmony releases return only a
            // partial APK-path list from `-f` while the plain command exposes
            // additional preinstalled or compatibility packages. Running the
            // fallback only when the first command is empty silently drops
            // those applications.
            return await ReadPackageCommandsAsync(
                serial,
                BuildPackageInventoryCommands(userIds),
                token).ConfigureAwait(false);
        }

        internal static List<string[]> BuildPackageInventoryCommands(IEnumerable<int> userIds)
        {
            List<int> users = (userIds ?? Enumerable.Empty<int>())
                .Distinct()
                .OrderBy(delegate(int id) { return id; })
                .ToList();
            List<string[]> commands = new List<string[]>
            {
                new[] { "pm", "list", "packages", "-f" }
            };
            AddUserScopedCommands(commands, new[] { "pm", "list", "packages", "-f" }, users);

            // A few compatibility containers expose package names but reject
            // the APK-path form. Keep this fallback broad as well; filtering
            // to third-party packages would hide valid launchable apps.
            commands.Add(new[] { "pm", "list", "packages" });
            AddUserScopedCommands(commands, new[] { "pm", "list", "packages" }, users);

            // Android compatibility containers on some Harmony builds expose
            // the package-manager service through `cmd package` while the
            // legacy `pm` wrapper is unavailable or incomplete. Keep both
            // command families so an application is not hidden by a shell
            // compatibility difference.
            commands.Add(new[] { "cmd", "package", "list", "packages", "-f" });
            AddUserScopedCommands(commands, new[] { "cmd", "package", "list", "packages", "-f" }, users);
            commands.Add(new[] { "cmd", "package", "list", "packages" });
            AddUserScopedCommands(commands, new[] { "cmd", "package", "list", "packages" }, users);
            return commands;
        }

        private static void AddUserScopedCommands(IList<string[]> commands, string[] baseCommand, IEnumerable<int> userIds)
        {
            foreach (int userId in userIds ?? Enumerable.Empty<int>())
            {
                string value = userId.ToString(CultureInfo.InvariantCulture);
                foreach (string option in new[] { "--user", "-u", "-U", "--user-id" })
                {
                    List<string> scoped = (baseCommand ?? Array.Empty<string>()).ToList();
                    scoped.Add(option);
                    scoped.Add(value);
                    commands.Add(scoped.ToArray());
                }
            }
        }

        internal static int CommandUserId(IEnumerable<string> command)
        {
            string[] parts = (command ?? Enumerable.Empty<string>()).ToArray();
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (!string.Equals(parts[i], "--user", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parts[i], "-u", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parts[i], "-U", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parts[i], "--user-id", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
                    && userId >= 0) return userId;
            }
            return -1;
        }

        private async Task<List<HarmonyPackageRecord>> ReadPackageCommandsAsync(string serial, IEnumerable<string[]> commands, CancellationToken token)
        {
            List<HarmonyPackageRecord> packages = new List<HarmonyPackageRecord>();
            foreach (string[] command in commands ?? Enumerable.Empty<string[]>())
            {
                try
                {
                    ProcessResult result = await RunShellAsync(serial, command, 15000, token).ConfigureAwait(false);
                    if (result == null || IsHdcFailure(result)) continue;
                    int userId = CommandUserId(command);
                    foreach (string packageName in ParseAndroidPackages((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")))
                    {
                        if (packages.Any(delegate(HarmonyPackageRecord item)
                        {
                            return string.Equals(item.BundleId, packageName, StringComparison.OrdinalIgnoreCase)
                                && item.UserId == userId;
                        })) continue;
                        packages.Add(new HarmonyPackageRecord { BundleId = packageName, UserId = userId });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return packages;
        }

        private async Task<List<AppInfo>> ReadBundleManagerAppsAsync(string serial, IEnumerable<int> userIds, CancellationToken token)
        {
            List<AppInfo> apps = new List<AppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string[]> commands = BuildBundleManagerInventoryCommands(userIds);
            foreach (string[] command in commands)
            {
                try
                {
                    ProcessResult result = await RunShellAsync(serial, command, 20000, token).ConfigureAwait(false);
                    if (result == null || IsHdcFailure(result)) continue;
                    int userId = CommandUserId(command);
                    foreach (AppInfo app in ParseApps((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")))
                    {
                        // A Bundle Manager record can carry its own profile
                        // identity. Prefer that evidence over the command
                        // option: vendor builds sometimes echo a global
                        // record while a scoped command is being used, and
                        // assigning the option blindly would duplicate the
                        // app into the wrong Harmony profile.
                        int recordUserId = app.HarmonyUserId >= 0 ? app.HarmonyUserId : userId;
                        AppInfo merged = AddApp(apps, seen, app.BundleId, "Bundle Manager 应用", app.Name, app.Version, app.HasLaunchEntry,
                            harmonyLaunchEntries: app.HarmonyLaunchEntries, harmonyUserId: recordUserId);
                        if (merged != null)
                            foreach (int appUserId in app.HarmonyUserIds ?? new List<int>())
                                AddHarmonyUser(merged, appUserId);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return apps;
        }

        internal static List<string[]> BuildBundleManagerInventoryCommands(IEnumerable<int> userIds)
        {
            List<int> users = (userIds ?? Enumerable.Empty<int>())
                .Where(delegate(int id) { return id >= 0; })
                .Distinct()
                .OrderBy(delegate(int id) { return id; })
                .ToList();
            List<string[]> commands = new List<string[]> { new[] { "bm", "dump", "-a" } };
            foreach (int userId in users)
            {
                string value = userId.ToString(CultureInfo.InvariantCulture);
                // Harmony releases disagree on the spelling of the scoped
                // Bundle Manager user option. Run all known forms and merge
                // their results so one CLI dialect cannot hide a user's apps.
                foreach (string option in new[] { "-u", "--user", "-U", "--user-id" })
                    commands.Add(new[] { "bm", "dump", "-a", option, value });
            }
            return commands;
        }

        private async Task<List<ProcessInfo>> ReadProcessListAsync(string serial, CancellationToken token)
        {
            List<ProcessInfo> allProcesses = new List<ProcessInfo>();
            bool commandSucceeded = false;
            string diagnostics = "";
            foreach (string[] command in BuildProcessInventoryCommands())
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
                if (result != null && !IsHdcFailure(result))
                {
                    List<ProcessInfo> parsed = ParseProcesses((result.Stdout ?? "") + "\n" + (result.Stderr ?? ""), serial);
                    if (parsed.Count > 0 || result.ExitCode == 0)
                        commandSucceeded = true;
                    allProcesses.AddRange(parsed);
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

        internal static IReadOnlyList<string[]> BuildProcessInventoryCommands()
        {
            // Harmony vendors expose different process-name and identity
            // columns. Query the complete bounded matrix so a device that
            // rejects ARGS, UID, USER, NAME, or CMDLINE cannot hide a target.
            List<string[]> commands = new List<string[]>();
            string[] processFields = { "ARGS", "NAME", "COMM", "COMMAND", "CMDLINE" };
            string[][] layouts =
            {
                new[] { "PID", "{0}" },
                new[] { "UID", "PID", "PPID", "{0}" },
                new[] { "USER", "PID", "PPID", "{0}" },
                new[] { "PID", "UID", "PPID", "{0}" },
                new[] { "PID", "USER", "PPID", "{0}" }
            };
            foreach (string field in processFields)
                foreach (string[] layout in layouts)
                    commands.Add(new[] { "ps", "-A", "-o", string.Join(",", layout.Select(delegate(string part)
                    {
                        return part == "{0}" ? field : part;
                    })) });
            commands.Add(new[] { "ps", "-ef" });
            commands.Add(new[] { "ps", "-A" });
            commands.Add(new[] { "ps" });
            return commands;
        }

        public Task<ProcessResult> LaunchAppAsync(string serial, string bundleId, CancellationToken token)
        {
            return LaunchAppAsync(serial, bundleId, null, token);
        }

        public async Task<ProcessResult> LaunchAppAsync(string serial, string bundleId, IEnumerable<int> harmonyUserIds, CancellationToken token)
        {
            return await LaunchAppAsync(serial, bundleId, harmonyUserIds, null, token).ConfigureAwait(false);
        }

        public Task<ProcessResult> LaunchAppAsync(string serial, AppInfo app, CancellationToken token)
        {
            if (app == null)
                return Task.FromResult(new ProcessResult(1, "", "未选择有效的鸿蒙应用。"));
            if (app.IsProcessOnly && !app.CanAttemptLaunch)
                return Task.FromResult(new ProcessResult(1, "", "该鸿蒙目标没有独立启动入口，只能选择运行中的真实进程采集。"));
            IEnumerable<int> rawUsers = app.HarmonyUserId >= 0
                ? new[] { app.HarmonyUserId }
                : app.HarmonyUserIds ?? new List<int>();
            List<int> selectedUsers = rawUsers
                .Where(delegate(int userId) { return userId >= 0; })
                .Distinct()
                .OrderBy(delegate(int userId) { return userId; })
                .ToList();
            if (app.HarmonyUserId < 0 && selectedUsers.Count > 1)
            {
                return Task.FromResult(new ProcessResult(
                    1,
                    "",
                    "鸿蒙应用的用户作用域不明确，请重新选择具体用户后再启动。"));
            }
            return LaunchAppAsync(serial, app.BundleId, selectedUsers, app.HarmonyLaunchEntries, token);
        }

        private async Task<ProcessResult> LaunchAppAsync(
            string serial,
            string bundleId,
            IEnumerable<int> harmonyUserIds,
            IEnumerable<HarmonyLaunchEntryInfo> harmonyLaunchEntries,
            CancellationToken token)
        {
            if (!ValidHarmonyApplicationBundle(bundleId)) return new ProcessResult(1, "", "鸿蒙应用包名无效。");
            List<int> users = (harmonyUserIds ?? Enumerable.Empty<int>())
                .Where(delegate(int userId) { return userId >= 0; })
                .Distinct()
                .OrderBy(delegate(int userId) { return userId; })
                .ToList();
            if (users.Count == 0) users.Add(-1);

            List<HarmonyLaunchEntryInfo> knownEntries = (harmonyLaunchEntries ?? Enumerable.Empty<HarmonyLaunchEntryInfo>())
                .Where(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return entry != null && entry.IsUiEntry && !string.IsNullOrWhiteSpace(entry.Ability);
                })
                .GroupBy(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return (entry.Module ?? "") + "|" + entry.Ability;
                }, StringComparer.OrdinalIgnoreCase)
                .Select(delegate(IGrouping<string, HarmonyLaunchEntryInfo> group) { return group.First(); })
                .ToList();
            List<ProcessResult> attempts = new List<ProcessResult>();
            HashSet<string> abilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int userId in users)
            {
                // Reuse the real entries discovered with the aggregate app
                // inventory before requiring a second `bm dump -n` command.
                // Some vendor builds expose `bm dump -a` but reject or omit
                // the per-bundle form.
                foreach (HarmonyLaunchEntryInfo entry in knownEntries)
                {
                    abilities.Add(entry.Ability);
                    foreach (string[] startArgs in AbilityStartArgsVariants(bundleId, entry.Module, entry.Ability, userId))
                    {
                        ProcessResult result = await RunShellAsync(serial, startArgs, 15000, token).ConfigureAwait(false);
                        attempts.Add(result);
                        if (IsSuccessfulCommand(result)) return result;
                    }
                }

                ProcessResult detail = await ReadLaunchDetailAsync(serial, bundleId, userId, token).ConfigureAwait(false);
                if (detail != null) attempts.Add(detail);
                string detailOutput = detail == null ? "" : (detail.Stdout ?? "") + "\n" + (detail.Stderr ?? "");
                foreach (HarmonyLaunchEntryPoint entry in ParseLaunchEntryPoints(detailOutput))
                {
                    if (entry.IsUiEntry && !string.IsNullOrWhiteSpace(entry.Ability)) abilities.Add(entry.Ability);
                    if (entry.IsUiEntry && !string.IsNullOrWhiteSpace(entry.Ability) && !string.IsNullOrWhiteSpace(entry.Module))
                    {
                        foreach (string[] startArgs in AbilityStartArgsVariants(bundleId, entry.Module, entry.Ability, userId))
                        {
                            ProcessResult result = await RunShellAsync(serial, startArgs, 15000, token).ConfigureAwait(false);
                            attempts.Add(result);
                            if (IsSuccessfulCommand(result)) return result;
                        }
                    }
                }
                foreach (string ability in abilities)
                {
                    foreach (string[] startArgs in AbilityStartArgsVariants(bundleId, "", ability, userId))
                    {
                        ProcessResult result = await RunShellAsync(serial, startArgs, 15000, token).ConfigureAwait(false);
                        attempts.Add(result);
                        if (IsSuccessfulCommand(result)) return result;
                    }
                }
                foreach (string[] startArgs in AbilityStartArgsVariants(bundleId, "", "", userId))
                {
                    ProcessResult packageStart = await RunShellAsync(serial, startArgs, 15000, token).ConfigureAwait(false);
                    attempts.Add(packageStart);
                    if (IsSuccessfulCommand(packageStart)) return packageStart;
                }

                // Android compatibility packages often have no Harmony Ability
                // metadata. Resolve the real launcher component before using the
                // package-only fallbacks so apps with a non-default activity can
                // still be started deterministically.
                string component = await ResolveAndroidLaunchComponentAsync(serial, bundleId, userId, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(component))
                {
                    ProcessResult componentStart = await RunShellAsync(serial, AndroidComponentStartArgs(component, userId), 15000, token).ConfigureAwait(false);
                    attempts.Add(componentStart);
                    if (IsSuccessfulCommand(componentStart)) return componentStart;
                    if (userId == 0)
                    {
                        componentStart = await RunShellAsync(serial, new[] { "am", "start", "-n", component }, 15000, token).ConfigureAwait(false);
                        attempts.Add(componentStart);
                        if (IsSuccessfulCommand(componentStart)) return componentStart;
                    }
                }

                ProcessResult androidStart = await RunShellAsync(serial, AndroidPackageStartArgs(bundleId, userId), 15000, token).ConfigureAwait(false);
                attempts.Add(androidStart);
                if (IsSuccessfulCommand(androidStart)) return androidStart;
                ProcessResult monkeyStart = await RunShellAsync(serial, MonkeyStartArgs(bundleId, userId), 15000, token).ConfigureAwait(false);
                attempts.Add(monkeyStart);
                if (IsSuccessfulCommand(monkeyStart)) return monkeyStart;
            }
            return CombineLaunchFailures(attempts);
        }

        private async Task<ProcessResult> ReadLaunchDetailAsync(string serial, string bundleId, int userId, CancellationToken token)
        {
            List<ProcessResult> scopedAttempts = new List<ProcessResult>();
            if (userId >= 0)
            {
                string value = userId.ToString(CultureInfo.InvariantCulture);
                // Match the inventory compatibility rule. Some releases accept
                // only one of these spellings when dumping one bundle for a
                // user; keep the selected profile on every attempt.
                foreach (string option in new[] { "-u", "--user", "-U", "--user-id" })
                {
                    ProcessResult scoped = await RunShellAsync(serial, new[] { "bm", "dump", "-n", bundleId, option, value }, 12000, token).ConfigureAwait(false);
                    if (scoped == null || IsHdcFailure(scoped)) continue;
                    scopedAttempts.Add(scoped);
                    string scopedOutput = (scoped.Stdout ?? "") + "\n" + (scoped.Stderr ?? "");
                    if (ParseLaunchEntryPoints(scopedOutput).Count > 0) return scoped;
                }

                // A scoped lookup that did not expose an entry is still a
                // meaningful result for the selected profile. Never query
                // the unscoped Bundle Manager for a secondary/work user: on
                // some Harmony builds that command resolves the owner user's
                // installation and can feed the wrong Ability into launch.
                if (userId != 0)
                    return CombineLaunchFailures(scopedAttempts);
            }

            // User 0 is the device owner; an unscoped query is equivalent to
            // that profile on implementations that do not support user flags.
            return await RunShellAsync(serial, new[] { "bm", "dump", "-n", bundleId }, 12000, token).ConfigureAwait(false);
        }

        private static string[] AbilityStartArgs(string bundleId, string module, string ability, int userId)
        {
            List<string> args = new List<string> { "aa", "start" };
            if (userId >= 0) { args.Add("-U"); args.Add(userId.ToString(CultureInfo.InvariantCulture)); }
            args.Add("-b"); args.Add(bundleId);
            if (!string.IsNullOrWhiteSpace(module)) { args.Add("-m"); args.Add(module); }
            if (!string.IsNullOrWhiteSpace(ability)) { args.Add("-a"); args.Add(ability); }
            return args.ToArray();
        }

        private static IEnumerable<string[]> AbilityStartArgsVariants(string bundleId, string module, string ability, int userId)
        {
            if (userId < 0)
            {
                yield return AbilityStartArgs(bundleId, module, ability, -1);
                yield break;
            }

            // The user selector changed spelling between aa implementations.
            // Try every documented form while preserving the selected profile;
            // do not silently fall back to the default user for a work profile.
            foreach (string option in new[] { "-U", "--user", "-u", "--user-id" })
            {
                List<string> args = new List<string> { "aa", "start", option, userId.ToString(CultureInfo.InvariantCulture), "-b", bundleId };
                if (!string.IsNullOrWhiteSpace(module)) { args.Add("-m"); args.Add(module); }
                if (!string.IsNullOrWhiteSpace(ability)) { args.Add("-a"); args.Add(ability); }
                yield return args.ToArray();
            }

            // User 0 is the device owner and an unscoped aa start is equivalent
            // on implementations that do not expose a user option. For a
            // secondary/work user, never launch into another profile.
            if (userId == 0) yield return AbilityStartArgs(bundleId, module, ability, -1);
        }

        private static string[] AndroidComponentStartArgs(string component, int userId)
        {
            List<string> args = new List<string> { "am", "start" };
            if (userId >= 0) { args.Add("--user"); args.Add(userId.ToString(CultureInfo.InvariantCulture)); }
            args.Add("-n"); args.Add(component);
            return args.ToArray();
        }

        private static string[] AndroidPackageStartArgs(string bundleId, int userId)
        {
            List<string> args = new List<string> { "am", "start" };
            if (userId >= 0) { args.Add("--user"); args.Add(userId.ToString(CultureInfo.InvariantCulture)); }
            args.Add("-a"); args.Add("android.intent.action.MAIN");
            args.Add("-c"); args.Add("android.intent.category.LAUNCHER");
            args.Add("-p"); args.Add(bundleId);
            return args.ToArray();
        }

        private static string[] MonkeyStartArgs(string bundleId, int userId)
        {
            List<string> args = new List<string> { "monkey" };
            if (userId >= 0) { args.Add("--user"); args.Add(userId.ToString(CultureInfo.InvariantCulture)); }
            args.Add("-p"); args.Add(bundleId);
            args.Add("-c"); args.Add("android.intent.category.LAUNCHER");
            args.Add("1");
            return args.ToArray();
        }

        private async Task<string> ResolveAndroidLaunchComponentAsync(string serial, string bundleId, int userId, CancellationToken token)
        {
            List<string[]> commands = new List<string[]>();
            foreach (string[] baseCommand in new[]
            {
                new[] { "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", bundleId },
                new[] { "pm", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", bundleId },
                new[] { "cmd", "package", "resolve-activity", "--brief", bundleId }
            })
            {
                if (userId >= 0)
                {
                    List<string> scoped = baseCommand.ToList();
                    scoped.Insert(scoped.Count - 1, "--user");
                    scoped.Insert(scoped.Count - 1, userId.ToString(CultureInfo.InvariantCulture));
                    commands.Add(scoped.ToArray());
                    if (userId == 0)
                        commands.Add(baseCommand);
                }
                else
                {
                    // An unknown user retains the legacy unscoped compatibility
                    // query. A secondary/work profile must never resolve a
                    // Launcher component from the owner user.
                    commands.Add(baseCommand);
                }
            }
            foreach (string[] command in commands)
            {
                try
                {
                    ProcessResult result = await RunShellAsync(serial, command, 10000, token).ConfigureAwait(false);
                    if (result == null || IsHdcFailure(result)) continue;
                    string component = ParseAndroidLaunchComponent(bundleId, result.Stdout + "\n" + result.Stderr);
                    if (!string.IsNullOrWhiteSpace(component)) return component;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return "";
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
            foreach (string json in ExtractJsonValues(output))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        CollectAppsFromJson(document.RootElement, apps, seen, "", false);
                    }
                }
                catch (JsonException) { }
            }
            bool insideAbilityCollection = false;
            int abilityIndent = -1;
            foreach (string raw in Lines(output))
            {
                if (ExtractJsonValues((raw ?? "").Trim()).Count > 0) continue;
                string line = raw.Trim().Trim('{', '}', ',', ' ', '\t');
                int indent = raw.Length - raw.TrimStart().Length;
                if (IsAbilityCollectionLine(line))
                {
                    insideAbilityCollection = true;
                    abilityIndent = indent;
                    continue;
                }
                if (insideAbilityCollection && indent <= abilityIndent)
                    insideAbilityCollection = false;
                if (insideAbilityCollection) continue;
                // Compact JSON was already parsed structurally above. Do not
                // run the flat key/value fallback over the same line, where
                // a nested Ability name could be mistaken for the app label.
                if (ExtractJsonValues(line).Count > 0) continue;
                Match keyed = Regex.Match(
                    line,
                    @"(?i)(?:[""']?)(?:bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|bundle|packageName|package_name|packageId|package_id|package\s+name|package|modulePackage|module_package|module\s+package|applicationId|application_id|appId|app_id|appIdentifier|app_identifier)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.]+)",
                    RegexOptions.CultureInvariant);
                if (keyed.Success)
                {
                    AddApp(apps, seen, keyed.Groups[1].Value, "Bundle Manager 应用",
                        ExtractTextField(line, "appName|applicationName|appLabel|label|displayName|name"),
                        ExtractTextField(line, "versionName|versionCode|versionNumber|version"),
                        harmonyUserId: ParseEmbeddedHarmonyUserId(line));
                }
                string scalar = line.Trim('"', '\'');
                if (ValidBundle(scalar)) AddApp(apps, seen, scalar);
                Match leadingBundle = Regex.Match(
                    line,
                    @"^(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:\s|[,;}]|$)",
                    RegexOptions.CultureInvariant);
                if (leadingBundle.Success) AddApp(apps, seen, leadingBundle.Groups["bundle"].Value, "Bundle Manager 应用");
            }
            ParseNamedBundleLines(output, apps, seen);
            // Indented/key-value Bundle Manager output does not have a JSON
            // node boundary. If it contains exactly one application, its
            // real Ability entries can still be attached safely without
            // assigning one app's launch target to another app.
            if (apps.Count == 1)
                AddHarmonyLaunchEntries(apps[0], ToHarmonyLaunchEntries(ParseLaunchEntryPoints(output)));
            return apps;
        }

        private static bool IsAbilityCollectionLine(string value)
        {
            string key;
            string ignored;
            return TryParseKeyValueField(value, out key, out ignored) && IsAbilityInfoCollection(key);
        }

        private static bool IsBundleRecordCollection(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName);
            return string.Equals(normalized, "bundleInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleRecords", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedBundles", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseKeyValueField(string value, out string key, out string fieldValue)
        {
            Match match = Regex.Match(
                (value ?? "").Trim(),
                @"^(?:[-\s]*)?(?<key>[A-Za-z][A-Za-z0-9_ -]*)\s*[:=]\s*(?<value>.*)$",
                RegexOptions.CultureInvariant);
            key = match.Success ? match.Groups["key"].Value.Trim() : "";
            fieldValue = match.Success ? match.Groups["value"].Value.Trim() : "";
            return match.Success;
        }

        private static bool TryParseAbilityNameField(string value, bool allowGenericName, out string ability)
        {
            bool ignored;
            return TryParseAbilityNameField(value, allowGenericName, out ability, out ignored);
        }

        private static bool TryParseAbilityNameField(string value, bool allowGenericName, out string ability, out bool isUiEntry)
        {
            Match match = Regex.Match(
                (value ?? "").Trim(),
                @"^(?:[-\s]*)?(?<key>[A-Za-z][A-Za-z0-9_ -]*)\s*[:=]\s*['""]?(?<value>[A-Za-z][A-Za-z0-9_.$-]*)",
                RegexOptions.CultureInvariant);
            ability = "";
            isUiEntry = true;
            if (!match.Success || !IsAbilityNameProperty(match.Groups["key"].Value, allowGenericName)) return false;
            ability = match.Groups["value"].Value.Trim();
            isUiEntry = IsUiAbilityField(match.Groups["key"].Value);
            return ValidEntryToken(ability);
        }

        private static void ParseNamedBundleLines(string output, IList<AppInfo> apps, ISet<string> seen)
        {
            string bundle = "";
            int bundleIndent = -1;
            int bundleCollectionIndent = -1;
            string module = "";
            bool insideAbilityCollection = false;
            bool insideNonUiAbilityCollection = false;
            int abilityIndent = -1;
            foreach (string raw in Lines(output))
            {
                string line = raw ?? "";
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;

                string collectionKey;
                string collectionValue;
                if (TryParseKeyValueField(trimmed, out collectionKey, out collectionValue)
                    && IsBundleRecordCollection(collectionKey))
                {
                    bundleCollectionIndent = indent;
                    continue;
                }
                if (bundleCollectionIndent >= 0 && indent <= bundleCollectionIndent)
                    bundleCollectionIndent = -1;

                if (TryParseKeyValueField(trimmed, out collectionKey, out collectionValue)
                    && IsAbilityInfoCollection(collectionKey))
                {
                    insideAbilityCollection = true;
                    insideNonUiAbilityCollection = !IsUiAbilityField(collectionKey);
                    abilityIndent = indent;
                    string inlineAbility = collectionValue.Trim().Trim(',', '"', '\'');
                    if (!string.IsNullOrWhiteSpace(bundle) && ValidEntryToken(inlineAbility))
                    {
                        AppInfo inlineApp = apps.FirstOrDefault(delegate(AppInfo app)
                        {
                            return app != null && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
                        });
                        AddHarmonyLaunchEntries(inlineApp, new[]
                        {
                            new HarmonyLaunchEntryInfo { Module = module, Ability = inlineAbility, IsUiEntry = !insideNonUiAbilityCollection }
                        });
                    }
                    continue;
                }

                if (insideAbilityCollection)
                {
                    if (indent > abilityIndent)
                    {
                        string abilityValue;
                        bool abilityIsUi;
                        if (TryParseAbilityNameField(trimmed, true, out abilityValue, out abilityIsUi)
                            && !string.IsNullOrWhiteSpace(bundle))
                        {
                            AppInfo owner = apps.FirstOrDefault(delegate(AppInfo app)
                            {
                                return app != null && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
                            });
                            AddHarmonyLaunchEntries(owner, new[]
                            {
                                new HarmonyLaunchEntryInfo
                                {
                                    Module = module,
                                    Ability = abilityValue,
                                    IsUiEntry = !insideNonUiAbilityCollection && abilityIsUi
                                }
                            });
                        }
                        // Everything below an ability collection belongs to
                        // the current entry. Do not let nested metadata create
                        // a second application record.
                        continue;
                    }
                    insideAbilityCollection = false;
                    insideNonUiAbilityCollection = false;
                }

                Match moduleMatch = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:moduleName|module_name|module\s+name|mainModuleName|main_module_name|main\s+module\s+name|entryModuleName|entry_module_name|entry\s+module\s+name|entryModule|module)\s*[:=]\s*['""]?(?<module>[A-Za-z][A-Za-z0-9_.-]*)",
                    RegexOptions.CultureInvariant);
                if (moduleMatch.Success)
                {
                    module = moduleMatch.Groups["module"].Value;
                    continue;
                }

                string directAbilityValue;
                bool directAbilityIsUi;
                if (TryParseAbilityNameField(trimmed, false, out directAbilityValue, out directAbilityIsUi)
                    && !string.IsNullOrWhiteSpace(bundle))
                {
                    AppInfo owner = apps.FirstOrDefault(delegate(AppInfo app)
                    {
                        return app != null && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
                    });
                    AddHarmonyLaunchEntries(owner, new[]
                    {
                        new HarmonyLaunchEntryInfo
                        {
                            Module = module,
                            Ability = directAbilityValue,
                            IsUiEntry = directAbilityIsUi
                        }
                    });
                    continue;
                }

                Match bundleMatch = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|bundle|packageName|package_name|packageId|package_id|package\s+name|package|modulePackage|module_package|module\s+package|applicationId|application_id|appId|app_id|appIdentifier|app_identifier)\s*[:=]\s*['""]?(?<bundle>[A-Za-z][A-Za-z0-9_.]+)",
                    RegexOptions.CultureInvariant);
                Match nameBundle = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?name\s*[:=]\s*['""]?(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)",
                    RegexOptions.CultureInvariant);
                string discoveredBundle = bundleMatch.Success
                    ? bundleMatch.Groups["bundle"].Value
                    : nameBundle.Success
                        && (ValidBundle(nameBundle.Groups["bundle"].Value) || bundleCollectionIndent >= 0)
                        && (string.IsNullOrWhiteSpace(bundle) || indent <= bundleIndent)
                        ? nameBundle.Groups["bundle"].Value
                        : "";
                if (!string.IsNullOrWhiteSpace(discoveredBundle))
                {
                    bool newRecord = !string.Equals(bundle, discoveredBundle, StringComparison.OrdinalIgnoreCase)
                        || indent <= bundleIndent;
                    bundle = discoveredBundle;
                    if (newRecord)
                    {
                        bundleIndent = indent;
                        module = "";
                        insideAbilityCollection = false;
                        insideNonUiAbilityCollection = false;
                        abilityIndent = -1;
                    }
                    AddApp(apps, seen, bundle, "Bundle Manager 应用");
                    continue;
                }

                Match name = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:appName|applicationName|appLabel|label|displayName)\s*[:=]\s*['""]?(?<name>[^,'""\r\n}]+)",
                    RegexOptions.CultureInvariant);
                if (name.Success && !string.IsNullOrWhiteSpace(bundle))
                {
                    AddApp(apps, seen, bundle, "Bundle Manager 应用", name: name.Groups["name"].Value.Trim());
                    continue;
                }
                if (string.IsNullOrWhiteSpace(bundle)) continue;
                Match version = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:versionName|versionCode|versionNumber|version|version_code)\s*[:=]\s*['""]?([^,'""\r\n}]+)",
                    RegexOptions.CultureInvariant);
                if (version.Success)
                    AddApp(apps, seen, bundle, "Bundle Manager 应用", version: version.Groups[1].Value.Trim());
                Match user = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:userId|user_id|uid|user)\s*[:=]\s*['""]?(?<id>\d+)",
                    RegexOptions.CultureInvariant);
                if (user.Success && int.TryParse(user.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId))
                    AddApp(apps, seen, bundle, "Bundle Manager 应用", harmonyUserId: userId);
            }
        }

        private static void CollectAppsFromJson(
            JsonElement node,
            IList<AppInfo> apps,
            ISet<string> seen,
            string fallbackBundle,
            bool insideAbilityCollection)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                string namedBundle = JsonPropertyValue(node, "name");
                string bundle = FirstNonEmpty(
                    JsonPropertyValue(node, "bundleName"), JsonPropertyValue(node, "bundleId"),
                    JsonPropertyValue(node, "bundle_name"), JsonPropertyValue(node, "bundle name"),
                    JsonPropertyValue(node, "bundle"), JsonPropertyValue(node, "packageName"),
                    JsonPropertyValue(node, "package_name"), JsonPropertyValue(node, "packageId"),
                    JsonPropertyValue(node, "package_id"), JsonPropertyValue(node, "package"),
                    JsonPropertyValue(node, "modulePackage"), JsonPropertyValue(node, "module_package"),
                    JsonPropertyValue(node, "applicationId"), JsonPropertyValue(node, "application_id"),
                    JsonPropertyValue(node, "appId"), JsonPropertyValue(node, "app_id"),
                    JsonPropertyValue(node, "appIdentifier"), JsonPropertyValue(node, "app_identifier"),
                    IsHarmonyBundleRecord(node, namedBundle) ? namedBundle : "",
                    fallbackBundle);
                if (!insideAbilityCollection && ValidHarmonyApplicationBundle(bundle))
                {
                    AddApp(apps, seen, bundle, "Bundle Manager 应用",
                        FirstNonEmpty(JsonPropertyValue(node, "appName"), JsonPropertyValue(node, "applicationName"),
                            JsonPropertyValue(node, "appLabel"), JsonPropertyValue(node, "label"),
                            JsonPropertyValue(node, "displayName"), NameIfNotBundle(namedBundle, bundle),
                            NestedJsonPropertyValue(node, "applicationInfo", "appName", "applicationName", "appLabel", "label", "displayName", "name"),
                            NestedJsonPropertyValue(node, "appInfo", "appName", "applicationName", "appLabel", "label", "displayName", "name")),
                        FirstNonEmpty(JsonPropertyValue(node, "versionName"), JsonPropertyValue(node, "versionCode"),
                            JsonPropertyValue(node, "versionNumber"), JsonPropertyValue(node, "version_code"), JsonPropertyValue(node, "version"),
                            NestedJsonPropertyValue(node, "applicationInfo", "versionName", "versionCode", "versionNumber", "version"),
                            NestedJsonPropertyValue(node, "appInfo", "versionName", "versionCode", "versionNumber", "version")),
                        ParseLaunchEntryPoints(node.GetRawText()).Any(delegate(HarmonyLaunchEntryPoint entry) { return entry.IsUiEntry; }),
                        harmonyLaunchEntries: ToHarmonyLaunchEntries(ParseLaunchEntryPoints(node.GetRawText())),
                        harmonyUserId: ParseEmbeddedHarmonyUserId(node));
                }
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    bool childInsideAbilityCollection = insideAbilityCollection || IsAbilityInfoCollection(property.Name);
                    string childFallback = childInsideAbilityCollection ? "" : ValidBundle(property.Name) ? property.Name : "";
                    CollectAppsFromJson(property.Value, apps, seen, childFallback, childInsideAbilityCollection);
                }
                return;
            }
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                    CollectAppsFromJson(value, apps, seen, fallbackBundle, insideAbilityCollection);
                return;
            }
            if (node.ValueKind == JsonValueKind.String)
            {
                string scalar = node.GetString() ?? "";
                if (!insideAbilityCollection && ValidBundle(scalar)) AddApp(apps, seen, scalar, "Bundle Manager 应用");
            }
        }

        private static bool IsHarmonyBundleRecord(JsonElement node, string candidateBundle)
        {
            if (node.ValueKind != JsonValueKind.Object || !ValidHarmonyApplicationBundle(candidateBundle)) return false;
            return HasJsonProperty(node, "versionCode", "versionName", "versionNumber", "version", "compatibleVersion",
                "targetVersion", "hapModuleInfos", "moduleInfos", "applicationInfo", "appInfo", "abilityInfos",
                "installTime", "updateTime", "userId", "bundleInfo", "label", "appName", "applicationName", "displayName")
                || node.EnumerateObject().Any(delegate(JsonProperty property)
                {
                    return IsAbilityInfoCollection(property.Name);
                });
        }

        private static bool HasJsonProperty(JsonElement node, params string[] names)
        {
            if (node.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in node.EnumerateObject())
                foreach (string name in names ?? new string[0])
                    if (string.Equals(NormalizePropertyName(property.Name), NormalizePropertyName(name), StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        private static int ParseEmbeddedHarmonyUserId(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return -1;
            string direct = FirstNonEmpty(
                JsonPropertyValue(node, "userId"), JsonPropertyValue(node, "user_id"),
                JsonPropertyValue(node, "user"),
                NestedJsonPropertyValue(node, "applicationInfo", "userId", "user_id", "user"),
                NestedJsonPropertyValue(node, "appInfo", "userId", "user_id", "user"));
            if (!string.IsNullOrWhiteSpace(direct)) return ParseHarmonyUserId(direct);
            string uid = FirstNonEmpty(
                JsonPropertyValue(node, "uid"),
                NestedJsonPropertyValue(node, "applicationInfo", "uid"),
                NestedJsonPropertyValue(node, "appInfo", "uid"));
            return string.IsNullOrWhiteSpace(uid) ? -1 : ParseProcessUserValue(uid, "uid");
        }

        private static int ParseEmbeddedHarmonyUserId(string value)
        {
            string text = (value ?? "").Trim();
            Match uid = Regex.Match(text, @"(?i)\buid\b\s*[:=]\s*[""']?(?<value>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant);
            if (uid.Success) return ParseProcessUserValue(uid.Groups["value"].Value, "uid");
            return ParseHarmonyUserId(text);
        }

        private static string NameIfNotBundle(string value, string bundle)
        {
            return string.Equals(value ?? "", bundle ?? "", StringComparison.OrdinalIgnoreCase) ? "" : value;
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
                MergeProcessFields(existing, process);
            }
            return byPid.Values
                .OrderByDescending(delegate(ProcessInfo process) { return process.Recommended; })
                .ThenBy(delegate(ProcessInfo process) { return process.Name; })
                .ThenBy(delegate(ProcessInfo process) { return process.Pid; })
                .ToList();
        }

        private static void MergeProcessFields(ProcessInfo target, ProcessInfo source)
        {
            if (target == null || source == null) return;

            // Different Harmony ps implementations expose different columns.
            // Merge each field independently so a later, sparse view cannot
            // erase an application identity found by an earlier view.
            target.Name = FirstNonEmpty(target.Name, source.Name);
            target.BundleId = FirstNonEmpty(target.BundleId, source.BundleId);
            target.DisplayName = FirstNonEmpty(target.DisplayName, source.DisplayName);
            target.DeviceUdid = FirstNonEmpty(target.DeviceUdid, source.DeviceUdid);
            target.StartedAt = FirstNonEmpty(target.StartedAt, source.StartedAt);
            target.OwnerName = FirstNonEmpty(target.OwnerName, source.OwnerName);
            target.OwnerBundleId = FirstNonEmpty(target.OwnerBundleId, source.OwnerBundleId);
            target.OwnerDisplayName = FirstNonEmpty(target.OwnerDisplayName, source.OwnerDisplayName);
            target.OwnershipSource = FirstNonEmpty(target.OwnershipSource, source.OwnershipSource);
            target.ApplicationState = FirstNonEmpty(target.ApplicationState, source.ApplicationState);
            target.ApplicationExecutablePath = FirstNonEmpty(target.ApplicationExecutablePath, source.ApplicationExecutablePath);
            target.Platform = FirstNonEmpty(target.Platform, source.Platform);
            target.Reason = FirstNonEmpty(target.Reason, source.Reason);
            target.IconKey = FirstNonEmpty(target.IconKey, source.IconKey);
            target.IconPath = FirstNonEmpty(target.IconPath, source.IconPath);

            if (target.HarmonyUserId < 0 && source.HarmonyUserId >= 0) target.HarmonyUserId = source.HarmonyUserId;
            if (target.ResponsiblePid <= 0 && source.ResponsiblePid > 0) target.ResponsiblePid = source.ResponsiblePid;
            if (target.OwnerPid <= 0 && source.OwnerPid > 0) target.OwnerPid = source.OwnerPid;
            if (target.CoalitionId <= 0 && source.CoalitionId > 0) target.CoalitionId = source.CoalitionId;
            if (target.StartAbsTime <= 0 && source.StartAbsTime > 0) target.StartAbsTime = source.StartAbsTime;
            if (target.AndroidStartTimeTicks <= 0 && source.AndroidStartTimeTicks > 0) target.AndroidStartTimeTicks = source.AndroidStartTimeTicks;
            if (target.HarmonyStartTimeTicks <= 0 && source.HarmonyStartTimeTicks > 0) target.HarmonyStartTimeTicks = source.HarmonyStartTimeTicks;
            if (target.ProcessUniqueId <= 0 && source.ProcessUniqueId > 0) target.ProcessUniqueId = source.ProcessUniqueId;

            target.OwnershipVerified = target.OwnershipVerified || source.OwnershipVerified;
            target.OwnershipAmbiguous = target.OwnershipAmbiguous || source.OwnershipAmbiguous;
            target.ForegroundApplication = target.ForegroundApplication || source.ForegroundApplication;
            target.Recommended = target.Recommended || source.Recommended;
        }

        internal static List<string> ParseAndroidPackages(string output)
        {
            List<string> packages = new List<string>();
            foreach (string raw in Lines(output))
            {
                string line = (raw ?? "").Trim();
                bool hasPackagePrefix = line.StartsWith("package:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("package=", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase);
                if (hasPackagePrefix)
                {
                    int prefixLength = line.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase) ? "pkg:".Length : "package:".Length;
                    string value = line.Substring(prefixLength).Trim().TrimStart('=');
                    int equals = value.LastIndexOf('=');
                    if (equals >= 0) value = value.Substring(equals + 1).Trim();
                    else
                    {
                        Match first = Regex.Match(value, @"^([^\s]+)");
                        value = first.Success ? first.Groups[1].Value : value;
                    }
                    value = value.Trim('"', '\'', ',', ';');
                    if (!ValidBundle(value))
                    {
                        // Vendor package managers sometimes omit the final
                        // `=com.example.app` and return only the APK path.
                        // Recover the bundle-shaped path segment while
                        // ignoring common APK file names.
                        MatchCollection candidates = Regex.Matches(
                            value,
                            @"(?<![A-Za-z0-9_])([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?![A-Za-z0-9_])",
                            RegexOptions.CultureInvariant);
                        for (int index = candidates.Count - 1; index >= 0; index--)
                        {
                            string candidate = candidates[index].Groups[1].Value;
                            if (candidate.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
                            if (ValidBundle(candidate))
                            {
                                value = candidate;
                                break;
                            }
                        }
                    }
                    if (ValidBundle(value) && !packages.Contains(value, StringComparer.OrdinalIgnoreCase)) packages.Add(value);
                }
                Match bare = Regex.Match(
                    line,
                    @"^(?<package>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:\s|$)",
                    RegexOptions.CultureInvariant);
                if (bare.Success && !packages.Contains(bare.Groups["package"].Value, StringComparer.OrdinalIgnoreCase))
                    packages.Add(bare.Groups["package"].Value);
            }
            return packages;
        }

        internal static string ParseAndroidLaunchComponent(string bundleId, string output)
        {
            if (!ValidBundle(bundleId)) return "";
            MatchCollection matches = Regex.Matches(
                output ?? "",
                @"(?<![A-Za-z0-9_])(?<package>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)/(?<activity>[A-Za-z][A-Za-z0-9_.$]*|\.[A-Za-z0-9_.$]+)",
                RegexOptions.CultureInvariant);
            foreach (Match match in matches)
            {
                if (!string.Equals(match.Groups["package"].Value, bundleId, StringComparison.OrdinalIgnoreCase)) continue;
                string activity = match.Groups["activity"].Value;
                if (activity.StartsWith(".", StringComparison.Ordinal)) activity = bundleId + activity;
                return bundleId + "/" + activity;
            }
            return "";
        }

        internal static List<ProcessInfo> ParseProcesses(string output, string serial)
        {
            List<ProcessInfo> processes = new List<ProcessInfo>();
            int pidIndex = -1;
            int commandIndex = -1;
            int bundleIndex = -1;
            int userIndex = -1;
            string userFieldName = "";
            foreach (string raw in Lines(output))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                    Match keyedPid = Regex.Match(line, @"(?i)\bpid\s*[:=]\s*(?<pid>\d+)", RegexOptions.CultureInvariant);
                if (keyedPid.Success
                    && int.TryParse(keyedPid.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keyedValue)
                    && keyedValue > 0)
                {
                    string keyedCommand = ExtractKeyValueField(line,
                        "cmd|cmdline|args|command|comm|exec(?:utable)?|process(?:Name)?|name|bundle[-_ ]?name|bundle(?:Id|_id)?|package[-_ ]?name|package(?:Id|_id)?");
                    string keyedName = ProcessNameFromCommand(keyedCommand);
                    string keyedBundle = ExtractExplicitBundleField(line);
                    int keyedUser = ParseProcessUserId(line);
                    if (!string.IsNullOrWhiteSpace(keyedName)) processes.Add(CreateProcess(keyedValue, keyedName, serial, keyedUser, keyedBundle));
                    continue;
                }
                string[] parts = Regex.Split(line, @"\s+");
                int headerPid = IndexOfIgnoreCase(parts, "PID");
                if (headerPid >= 0)
                {
                    pidIndex = headerPid;
                    commandIndex = IndexOfAnyIgnoreCase(parts, "ARGS", "CMD", "CMDLINE", "COMMAND", "COMM", "EXEC", "EXECUTABLE", "PROCNAME", "NAME",
                        "BUNDLE", "BUNDLE_NAME", "BUNDLENAME", "BUNDLEID", "PACKAGE", "PACKAGE_NAME", "PACKAGENAME", "PACKAGEID");
                    bundleIndex = IndexOfAnyIgnoreCase(parts, "BUNDLE", "BUNDLE_NAME", "BUNDLENAME", "BUNDLEID", "BUNDLE_ID",
                        "PACKAGE", "PACKAGE_NAME", "PACKAGENAME", "PACKAGEID", "PACKAGE_ID", "APPLICATIONID", "APPLICATION_ID");
                    userIndex = IndexOfAnyIgnoreCase(parts, "UID", "USER", "USERID", "USER_ID");
                    userFieldName = userIndex >= 0 && userIndex < parts.Length ? parts[userIndex] : "";
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
                int userId = userIndex >= 0 && userIndex < parts.Length
                    ? ParseProcessUserValue(parts[userIndex], userFieldName)
                    : InferHeaderlessProcessUserId(parts, rowPidIndex, rowCommandIndex);
                string explicitBundle = bundleIndex >= 0 && bundleIndex < parts.Length
                    ? NormalizeExplicitBundle(parts[bundleIndex])
                    : "";
                processes.Add(CreateProcess(pid, name, serial, userId, explicitBundle));
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

            // A Bundle ID is not a complete Harmony target identity. The same
            // bundle can be installed in the owner, work, guest, or secondary
            // profile. Normalize the inventory before attaching live PIDs so a
            // process from one profile cannot mark or launch another profile's
            // application row.
            List<AppInfo> normalized = ExpandHarmonyUserInstances(apps);
            apps.Clear();
            foreach (AppInfo app in normalized) apps.Add(app);

            List<ProcessInfo> validProcesses = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .ToList();
            foreach (ProcessInfo process in validProcesses)
            {
                string processBundle = FirstNonEmpty(process.BundleId, process.OwnerBundleId);
                if (!ValidHarmonyApplicationBundle(processBundle))
                {
                    AppInfo processOnly = apps.FirstOrDefault(delegate(AppInfo app)
                    {
                        return app != null && app.ProcessPid == process.Pid
                            && (string.IsNullOrWhiteSpace(app.ProcessName)
                                || string.Equals(app.ProcessName, process.Name, StringComparison.OrdinalIgnoreCase));
                    });
                    if (processOnly != null)
                    {
                        processOnly.IsRunning = true;
                        AddHarmonyUser(processOnly, process.HarmonyUserId);
                        continue;
                    }
                    AppInfo unknown = new AppInfo
                    {
                        BundleId = "",
                        Name = FirstNonEmpty(process.DisplayName, process.Name, "PID " + process.Pid.ToString(CultureInfo.InvariantCulture)),
                        ProcessPid = process.Pid,
                        ProcessName = process.Name,
                        Platform = "harmony",
                        Recommended = process.Recommended,
                        Reason = "运行中的鸿蒙进程 · PID " + process.Pid.ToString(CultureInfo.InvariantCulture),
                        IsRunning = true,
                        IsProcessOnly = true,
                        HarmonyUserId = process.HarmonyUserId
                    };
                    AddHarmonyUser(unknown, process.HarmonyUserId);
                    apps.Add(unknown);
                    continue;
                }

                List<AppInfo> bundleMatches = apps.Where(delegate(AppInfo app)
                {
                    return app != null && string.Equals(app.BundleId, processBundle, StringComparison.OrdinalIgnoreCase);
                }).ToList();
                AppInfo existing = null;
                if (process.HarmonyUserId >= 0)
                {
                    existing = bundleMatches.FirstOrDefault(delegate(AppInfo app)
                    {
                        return app.HarmonyUserId == process.HarmonyUserId;
                    });
                    if (existing == null
                        && bundleMatches.Count == 1
                        && bundleMatches[0].HarmonyUserId < 0
                        && (bundleMatches[0].HarmonyUserIds == null || bundleMatches[0].HarmonyUserIds.Count == 0))
                    {
                        // An unscoped inventory row can still be bound safely
                        // when the only live process supplies an explicit
                        // profile. Complete that row instead of creating a
                        // duplicate process-only application.
                        existing = bundleMatches[0];
                        existing.HarmonyUserId = process.HarmonyUserId;
                        AddHarmonyUser(existing, process.HarmonyUserId);
                    }
                }
                else if (bundleMatches.Count == 1)
                {
                    // If HDC omitted the UID, bind only when the bundle is
                    // unambiguous. Never choose the first row of a
                    // multi-profile application.
                    existing = bundleMatches[0];
                }
                if (existing != null)
                {
                    existing.IsRunning = true;
                    if (existing.IsProcessOnly
                        && existing.ProcessPid > 0
                        && existing.ProcessPid != process.Pid)
                    {
                        // A process-only row represents a real process only
                        // while it is unique for this Bundle/profile. Once a
                        // second PID appears, leave process choice explicit.
                        existing.ProcessPid = 0;
                        existing.ProcessName = "";
                    }
                    AddHarmonyUser(existing, process.HarmonyUserId);
                    continue;
                }

                // Keep a real running process visible even when Bundle
                // Manager/package-manager output omitted it. A known user is
                // retained on the new row so it can be selected and launched
                // without falling back to another profile.
                apps.Add(new AppInfo
                {
                    BundleId = processBundle,
                    Name = FirstNonEmpty(process.DisplayName, process.Name, processBundle),
                    ProcessPid = process.Pid,
                    ProcessName = process.Name,
                    Platform = "harmony",
                    Recommended = process.ForegroundApplication,
                    Reason = "运行中的鸿蒙应用",
                    IsRunning = true,
                    IsProcessOnly = true,
                    HarmonyUserId = process.HarmonyUserId
                });
                AddHarmonyUser(apps[apps.Count - 1], process.HarmonyUserId);
            }
        }

        internal static string DescribeDiscoveryException(Exception exception)
        {
            if (exception is TimeoutException) return "HDC 响应超时，请检查 USB 连接、设备解锁状态和 HDC 调试授权后刷新。";
            if (exception is FileNotFoundException || exception is System.ComponentModel.Win32Exception)
                return "未找到或无法启动 HDC。MoTuPerf 已自动检查安装包、PATH 和常见 DevEco/OpenHarmony SDK 目录；如果 SDK 安装在自定义位置，请将 MOTUPERF_HDC 设置为 hdc.exe 的完整路径，然后重启 MoTuPerf。";
            return "鸿蒙设备检测失败：" + Sanitize(exception == null ? "" : exception.Message, 240);
        }

        internal static string DescribeDiscoveryFailure(ProcessResult result)
        {
            string output = ((result == null ? "" : result.Stdout) + "\n" + (result == null ? "" : result.Stderr)).Trim();
            string lower = output.ToLowerInvariant();
            if (lower.Contains("device not found") || lower.Contains("not connected"))
                return "HDC 未连接到设备，请检查数据线、调试开关和设备端授权后刷新。";
            if (lower.Contains("command not found") || lower.Contains("is not recognized") || lower.Contains("no such file"))
                return "未找到 HDC 运行组件。MoTuPerf 已自动检查常见 SDK 目录；请安装官方鸿蒙 SDK 的 toolchains，或在自定义安装位置使用 MOTUPERF_HDC 指定 hdc.exe，然后重启 MoTuPerf。";
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
                @"(?i)(?:[""']?)(?:mainElementName|mainElement|mainAbility|mainAbilityName|entryAbility|entryAbilityName|abilityName|ability_name|ability\s+name|[A-Za-z][A-Za-z0-9_]*ExtensionAbilityName)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.$-]*)(?:[""']?)",
                RegexOptions.CultureInvariant);
            if (textValue.Success) return textValue.Groups[1].Value.Trim();
            string json = ExtractJsonValue(output);
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
                @"(?i)(?:[""']?)(?:moduleName|module_name|module\s+name|mainModuleName|main_module_name|main\s+module\s+name|entryModuleName|entry_module_name|entry\s+module\s+name|entryModule|module)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.-]*)",
                RegexOptions.CultureInvariant);
            if (textValue.Success) return textValue.Groups[1].Value.Trim();
            string json = ExtractJsonValue(output);
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
                foreach (JsonProperty property in node.EnumerateObject())
                    if (IsAbilityNameProperty(property.Name, false))
                    {
                        string value = JsonScalarText(property.Value);
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
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
            foreach (string json in ExtractJsonValues(output))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        CollectLaunchEntryPoints(document.RootElement, "", false, false, false, entries);
                    }
                }
                catch (JsonException) { }
            }

            // Text dumps can repeat the same module through aliases such as
            // `moduleName` and `entryModuleName`. Pairing all module matches
            // with all ability matches by global index attaches later
            // abilities to an earlier module. Walk fields in source order and
            // keep the most recent module for the following ability fields.
            ParseFlatLaunchEntries(output, entries);

            // Some Harmony releases print bm dump as an indented key/value
            // document instead of JSON. In that form abilityInfos entries are
            // usually written as "- name: EntryAbility". Keep the parser
            // scoped to an ability collection so unrelated app metadata named
            // "name" is never guessed as a launch target.
            ParseIndentedLaunchEntries(output, entries);
            if (entries.Count == 0)
                AddLaunchEntry(entries, ParseMainModule(output), ParseMainAbility(output));
            return entries;
        }

        private static void ParseFlatLaunchEntries(string output, IList<HarmonyLaunchEntryPoint> entries)
        {
            string currentModule = "";
            foreach (string raw in Lines(output))
            {
                MatchCollection fields = Regex.Matches(
                    raw ?? "",
                    @"(?i)(?:[""']?)(?<key>moduleName|module_name|module\s+name|mainModuleName|main_module_name|main\s+module\s+name|entryModuleName|entry_module_name|entry\s+module\s+name|entryModule|module|mainElementName|mainElement|mainAbility|mainAbilityName|entryAbility|entryAbilityName|abilityName|ability_name|ability\s+name|[A-Za-z][A-Za-z0-9_]*AbilityName)(?:[""']?)\s*[:=]\s*[""']?(?<value>[A-Za-z][A-Za-z0-9_.$-]*)(?:[""']?)",
                    RegexOptions.CultureInvariant);
                foreach (Match field in fields)
                {
                    string key = field.Groups["key"].Value;
                    string value = field.Groups["value"].Value;
                    if (IsLaunchModuleField(key))
                    {
                        currentModule = value;
                        continue;
                    }
                    AddLaunchEntry(entries, currentModule, value, IsUiAbilityField(key));
                }
            }
        }

        private static bool IsLaunchModuleField(string key)
        {
            string normalized = NormalizePropertyName(key);
            return normalized.IndexOf("module", StringComparison.OrdinalIgnoreCase) >= 0
                && normalized.IndexOf("ability", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void ParseIndentedLaunchEntries(string output, IList<HarmonyLaunchEntryPoint> entries)
        {
            string module = "";
            bool insideAbilities = false;
            bool insideNonUiAbilities = false;
            int abilityIndent = -1;
            foreach (string raw in Lines(output))
            {
                string line = raw ?? "";
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;
                Match moduleMatch = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:moduleName|module_name|module\s+name|mainModuleName|main_module_name|main\s+module\s+name|entryModuleName|entry_module_name|entry\s+module\s+name|entryModule|module)\s*[:=]\s*['""]?([A-Za-z][A-Za-z0-9_.-]*)",
                    RegexOptions.CultureInvariant);
                if (moduleMatch.Success)
                {
                    module = moduleMatch.Groups[1].Value;
                    if (insideAbilities && abilityIndent >= 0 && indent <= abilityIndent)
                    {
                        insideAbilities = false;
                        insideNonUiAbilities = false;
                    }
                }

                string collectionKey;
                string collectionValue;
                if (TryParseKeyValueField(trimmed, out collectionKey, out collectionValue)
                    && IsAbilityInfoCollection(collectionKey))
                {
                    insideAbilities = true;
                    insideNonUiAbilities = !IsUiAbilityField(collectionKey);
                    abilityIndent = indent;
                    continue;
                }

                string abilityValue;
                bool abilityIsUi;
                if (insideAbilities && indent <= abilityIndent
                    && !TryParseAbilityNameField(trimmed, true, out abilityValue, out abilityIsUi))
                {
                    insideAbilities = false;
                    insideNonUiAbilities = false;
                }
                if (!insideAbilities) continue;

                if (TryParseAbilityNameField(trimmed, true, out abilityValue, out abilityIsUi))
                    AddLaunchEntry(entries, module, abilityValue, !insideNonUiAbilities && abilityIsUi);
            }
        }

        private static void CollectLaunchEntryPoints(
            JsonElement node,
            string inheritedModule,
            bool insideAbilityInfo,
            bool allowScalarEntry,
            bool insideNonUiAbilityInfo,
            IList<HarmonyLaunchEntryPoint> entries)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                string module = FirstNonEmpty(JsonPropertyValue(node, "moduleName"), JsonPropertyValue(node, "module_name"),
                    JsonPropertyValue(node, "mainModuleName"), JsonPropertyValue(node, "main_module_name"),
                    JsonPropertyValue(node, "entryModuleName"), JsonPropertyValue(node, "entry_module_name"),
                    JsonPropertyValue(node, "entryModule"), JsonPropertyValue(node, "module"), inheritedModule);
                string ability = FirstNonEmpty(JsonPropertyValue(node, "mainElementName"), JsonPropertyValue(node, "mainElement"), JsonPropertyValue(node, "mainAbility"),
                    JsonPropertyValue(node, "mainAbilityName"), JsonPropertyValue(node, "entryAbility"),
                    JsonPropertyValue(node, "entryAbilityName"), JsonPropertyValue(node, "abilityName"),
                    JsonPropertyValue(node, "extensionAbilityName"), JsonPropertyValue(node, "serviceExtensionAbilityName"),
                    JsonPropertyValue(node, "formExtensionAbilityName"), JsonPropertyValue(node, "dataShareExtensionAbilityName"),
                    FindJsonAbilityName(node, insideAbilityInfo));
                if (!string.IsNullOrWhiteSpace(ability))
                    AddLaunchEntry(entries, module, ability, !insideNonUiAbilityInfo && IsUiAbilityObject(node, insideAbilityInfo));
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    bool isAbilityCollection = IsAbilityInfoCollection(property.Name);
                    bool childIsAbilityInfo = insideAbilityInfo || isAbilityCollection;
                    bool childIsNonUiAbilityInfo = insideNonUiAbilityInfo
                        || (isAbilityCollection && !IsUiAbilityField(property.Name));
                    bool childAllowsScalarEntry = isAbilityCollection
                        && (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.String);
                    if (childIsAbilityInfo && property.Value.ValueKind == JsonValueKind.Object && ValidEntryToken(property.Name))
                        AddLaunchEntry(entries, module, property.Name, !childIsNonUiAbilityInfo);
                    CollectLaunchEntryPoints(property.Value, module, childIsAbilityInfo, childAllowsScalarEntry, childIsNonUiAbilityInfo, entries);
                }
                return;
            }
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                    CollectLaunchEntryPoints(value, inheritedModule, insideAbilityInfo, allowScalarEntry, insideNonUiAbilityInfo, entries);
                return;
            }
            if (node.ValueKind == JsonValueKind.String && insideAbilityInfo && allowScalarEntry)
                AddLaunchEntry(entries, inheritedModule, node.GetString(), !insideNonUiAbilityInfo);
        }

        private static bool IsUiAbilityObject(JsonElement node, bool insideAbilityInfo)
        {
            if (insideAbilityInfo && HasJsonProperty(node, "className", "serviceExtensionAbilityName", "formExtensionAbilityName",
                "dataShareExtensionAbilityName", "extensionAbilityName")) return false;
            if (HasJsonProperty(node, "mainElementName", "mainElement", "mainAbility", "mainAbilityName",
                "entryAbility", "entryAbilityName", "abilityName", "ability_name")) return true;
            return !HasJsonProperty(node, "className", "serviceAbilityName", "formAbilityName",
                "dataShareAbilityName", "workSchedulerAbilityName");
        }

        private static bool IsAbilityInfoCollection(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName);
            if (normalized.EndsWith("ExtensionAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("ExtensionAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("ExtensionAbilityInfoList", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("ExtensionAbilityList", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("ExtensionAbilities", StringComparison.OrdinalIgnoreCase))
                return true;
            // Some Bundle Manager versions omit the `Extension` segment for
            // service/form/data-share/work-scheduler collections. Treat the
            // normalized Ability-shaped suffix as a collection marker so its
            // className and vendor-specific *AbilityName fields are parsed
            // with the same ownership boundary as standard AbilityInfo.
            if (normalized.EndsWith("AbilityInfo", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("AbilityInfos", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("AbilityInfoList", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("AbilityList", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("Abilities", StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(propertyName, "abilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "abilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "abilityInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "abilityList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "abilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "entryAbilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "entryAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "entryAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "launchAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "launchAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionAbilityInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionAbilityList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionAbilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "serviceExtensionAbilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "serviceExtensionAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "serviceExtensionAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "serviceExtensionAbilityInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "serviceExtensionAbilityList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "formExtensionAbilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "formExtensionAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "formExtensionAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "formExtensionAbilityInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "formExtensionAbilityList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "dataShareExtensionAbilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "dataShareExtensionAbilityInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "dataShareExtensionAbilityInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "dataShareExtensionAbilityInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "dataShareExtensionAbilityList", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindJsonAbilityName(JsonElement node, bool allowGenericName)
        {
            if (node.ValueKind != JsonValueKind.Object) return "";
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!IsAbilityNameProperty(property.Name, allowGenericName)) continue;
                string value = JsonScalarText(property.Value);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static bool IsAbilityNameProperty(string propertyName, bool allowGenericName)
        {
            string normalized = NormalizePropertyName(propertyName);
            if (allowGenericName && (string.Equals(normalized, "name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "className", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("AbilityName", StringComparison.OrdinalIgnoreCase))) return true;
            return string.Equals(normalized, "mainElementName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "mainElement", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "mainAbility", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "mainAbilityName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "entryAbility", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "entryAbilityName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "abilityName", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("ExtensionAbilityName", StringComparison.OrdinalIgnoreCase);
        }

        private static string JsonPropertyValue(JsonElement node, string name)
        {
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!string.Equals(NormalizePropertyName(property.Name), NormalizePropertyName(name), StringComparison.OrdinalIgnoreCase)) continue;
                return JsonScalarText(property.Value);
            }
            return "";
        }

        private static string JsonScalarText(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                return value.ToString();
            return "";
        }

        private static string NestedJsonPropertyValue(JsonElement node, string container, params string[] names)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (IsAbilityInfoCollection(property.Name)) continue;
                    if (string.Equals(NormalizePropertyName(property.Name), NormalizePropertyName(container), StringComparison.OrdinalIgnoreCase))
                    {
                        string value = FindNestedJsonScalar(property.Value, names);
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                    string nested = NestedJsonPropertyValue(property.Value, container, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                {
                    string nested = NestedJsonPropertyValue(value, container, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            return "";
        }

        private static string FindNestedJsonScalar(JsonElement node, IEnumerable<string> names)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in names ?? Enumerable.Empty<string>())
                {
                    string direct = JsonPropertyValue(node, name);
                    if (!string.IsNullOrWhiteSpace(direct)) return direct;
                }
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (IsAbilityInfoCollection(property.Name)) continue;
                    string nested = FindNestedJsonScalar(property.Value, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                {
                    string nested = FindNestedJsonScalar(value, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            return "";
        }

        private static string NormalizePropertyName(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).ToArray());
        }

        private static void AddLaunchEntry(IList<HarmonyLaunchEntryPoint> entries, string module, string ability, bool isUiEntry = true)
        {
            module = (module ?? "").Trim();
            ability = (ability ?? "").Trim();
            if (!ValidEntryToken(ability) || (!string.IsNullOrWhiteSpace(module) && !ValidEntryToken(module))) return;
            HarmonyLaunchEntryPoint existing = entries.FirstOrDefault(delegate(HarmonyLaunchEntryPoint entry)
            {
                return string.Equals(entry.Module, module, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Ability, ability, StringComparison.OrdinalIgnoreCase);
            });
            if (existing != null)
            {
                existing.IsUiEntry = existing.IsUiEntry || isUiEntry;
                return;
            }
            entries.Add(new HarmonyLaunchEntryPoint { Module = module, Ability = ability, IsUiEntry = isUiEntry });
        }

        private static bool IsUiAbilityField(string key)
        {
            string normalized = NormalizePropertyName(key);
            if (normalized.IndexOf("extension", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("service", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("form", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("datashare", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("workscheduler", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(normalized, "className", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
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
                        || string.Equals(property.Name, "main_module_name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "entryModuleName", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "entry_module_name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "entryModule", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "module", StringComparison.OrdinalIgnoreCase))
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

        private static List<string> ExtractJsonValues(string output)
        {
            List<string> values = new List<string>();
            string value = output ?? "";
            int start = -1;
            char opening = '\0';
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (start < 0)
                {
                    if (current != '{' && current != '[') continue;
                    start = i;
                    opening = current;
                    depth = 1;
                    inString = false;
                    escaped = false;
                    continue;
                }
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    continue;
                }
                if (current == opening) depth++;
                else if ((opening == '{' && current == '}') || (opening == '[' && current == ']')) depth--;
                if (depth != 0) continue;
                values.Add(value.Substring(start, i - start + 1));
                start = -1;
                opening = '\0';
            }
            return values;
        }

        private static string ExtractJsonValue(string output)
        {
            return ExtractJsonValues(output).FirstOrDefault() ?? "";
        }

        private static AppInfo AddApp(
            IList<AppInfo> apps,
            ISet<string> seen,
            string bundle,
            string reason = "",
            string name = "",
            string version = "",
            bool hasLaunchEntry = false,
            bool processOnly = false,
            int harmonyUserId = -1,
            IEnumerable<HarmonyLaunchEntryInfo> harmonyLaunchEntries = null)
        {
            bundle = (bundle ?? "").Trim().Trim('"', '\'', ',', ';');
            if (!ValidHarmonyApplicationBundle(bundle)) return null;
            AppInfo existing = apps.FirstOrDefault(delegate(AppInfo app)
            {
                return app != null && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
            });
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(name) && string.Equals(existing.Name, existing.BundleId, StringComparison.OrdinalIgnoreCase))
                    existing.Name = name.Trim();
                if (!string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(existing.Version)) existing.Version = version.Trim();
                if (string.IsNullOrWhiteSpace(existing.Reason) && !string.IsNullOrWhiteSpace(reason)) existing.Reason = reason;
                if (hasLaunchEntry) existing.HasLaunchEntry = true;
                if (!processOnly && (existing.HarmonyLaunchEntries == null || existing.HarmonyLaunchEntries.Count == 0))
                    existing.IsProcessOnly = false;
                if (processOnly) existing.IsRunning = true;
                if (existing.HarmonyUserId < 0 && harmonyUserId >= 0) existing.HarmonyUserId = harmonyUserId;
                AddHarmonyUser(existing, harmonyUserId);
                AddHarmonyLaunchEntries(existing, harmonyLaunchEntries);
                return existing;
            }
            if (!seen.Add(bundle)) return null;
            AppInfo app = new AppInfo
            {
                BundleId = bundle,
                Name = string.IsNullOrWhiteSpace(name) ? bundle : name.Trim(),
                Version = (version ?? "").Trim(),
                Platform = "harmony",
                Recommended = false,
                Reason = reason ?? "",
                HasLaunchEntry = hasLaunchEntry,
                IsProcessOnly = processOnly,
                IsRunning = processOnly,
                HarmonyUserId = harmonyUserId
            };
            AddHarmonyUser(app, harmonyUserId);
            AddHarmonyLaunchEntries(app, harmonyLaunchEntries);
            apps.Add(app);
            return app;
        }

        internal static List<AppInfo> ExpandHarmonyUserInstances(IEnumerable<AppInfo> source)
        {
            List<AppInfo> expanded = new List<AppInfo>();
            foreach (AppInfo app in source ?? Enumerable.Empty<AppInfo>())
            {
                if (app == null || !DevicePlatformNames.IsHarmony(app.Platform))
                {
                    if (app != null) expanded.Add(app);
                    continue;
                }

                List<int> users = (app.HarmonyUserIds ?? new List<int>())
                    .Where(delegate(int userId) { return userId >= 0; })
                    .Distinct()
                    .OrderBy(delegate(int userId) { return userId; })
                    .ToList();
                if (users.Count == 0 && app.HarmonyUserId >= 0) users.Add(app.HarmonyUserId);
                if (users.Count <= 1)
                {
                    if (users.Count == 1) app.HarmonyUserId = users[0];
                    expanded.Add(app);
                    continue;
                }

                // The same Bundle ID can be installed in owner, work, guest,
                // or secondary profiles. Keep one selectable row per real
                // profile so launch and process matching cannot silently use
                // the first user returned by HDC.
                foreach (int userId in users)
                {
                    AppInfo instance = CloneHarmonyApp(app, userId);
                    instance.HarmonyUserId = userId;
                    instance.HarmonyUserIds = new List<int> { userId };
                    expanded.Add(instance);
                }
            }
            return expanded;
        }

        private static AppInfo CloneHarmonyApp(AppInfo source, int userId)
        {
            bool preserveLiveProcess = source.HarmonyUserId == userId;
            AppInfo copy = new AppInfo
            {
                BundleId = source.BundleId,
                Name = source.Name,
                Version = source.Version,
                Platform = source.Platform,
                Recommended = preserveLiveProcess ? source.Recommended : false,
                Reason = source.Reason,
                IconKey = source.IconKey,
                IconPath = source.IconPath,
                ApkPath = source.ApkPath,
                ProcessPid = 0,
                ProcessName = "",
                IsRunning = preserveLiveProcess && source.IsRunning,
                IsProcessOnly = source.IsProcessOnly,
                HasLaunchEntry = source.HasLaunchEntry,
                HasNonUiLaunchEntry = source.HasNonUiLaunchEntry,
                HarmonyUserId = source.HarmonyUserId
            };
            if (preserveLiveProcess)
            {
                copy.ProcessPid = source.ProcessPid;
                copy.ProcessName = source.ProcessName;
            }
            copy.HarmonyUserIds = new List<int>(source.HarmonyUserIds ?? new List<int>());
            copy.HarmonyLaunchEntries = (source.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                .Where(delegate(HarmonyLaunchEntryInfo entry) { return entry != null; })
                .Select(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return new HarmonyLaunchEntryInfo
                    {
                        Module = entry.Module,
                        Ability = entry.Ability,
                        IsUiEntry = entry.IsUiEntry
                    };
                })
                .ToList();
            return copy;
        }

        private static List<HarmonyLaunchEntryInfo> ToHarmonyLaunchEntries(IEnumerable<HarmonyLaunchEntryPoint> entries)
        {
            return (entries ?? Enumerable.Empty<HarmonyLaunchEntryPoint>())
                .Where(delegate(HarmonyLaunchEntryPoint entry)
                {
                    return entry != null && !string.IsNullOrWhiteSpace(entry.Ability);
                })
                .Select(delegate(HarmonyLaunchEntryPoint entry)
                {
                    return new HarmonyLaunchEntryInfo
                    {
                        Module = entry.Module ?? "",
                        Ability = entry.Ability.Trim(),
                        IsUiEntry = entry.IsUiEntry
                    };
                })
                .ToList();
        }

        private static void AddHarmonyLaunchEntries(AppInfo app, IEnumerable<HarmonyLaunchEntryInfo> entries)
        {
            if (app == null || entries == null) return;
            if (app.HarmonyLaunchEntries == null) app.HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>();
            foreach (HarmonyLaunchEntryInfo entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Ability)) continue;
                HarmonyLaunchEntryInfo existing = app.HarmonyLaunchEntries.FirstOrDefault(delegate(HarmonyLaunchEntryInfo current)
                {
                    return string.Equals(current.Module ?? "", entry.Module ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(current.Ability, entry.Ability, StringComparison.OrdinalIgnoreCase);
                });
                if (existing != null)
                {
                    existing.IsUiEntry = existing.IsUiEntry || entry.IsUiEntry;
                    continue;
                }
                app.HarmonyLaunchEntries.Add(new HarmonyLaunchEntryInfo
                {
                    Module = (entry.Module ?? "").Trim(),
                    Ability = entry.Ability.Trim(),
                    IsUiEntry = entry.IsUiEntry
                });
            }
            bool hasUiEntry = app.HarmonyLaunchEntries.Any(delegate(HarmonyLaunchEntryInfo entry) { return entry.IsUiEntry; });
            bool hasNonUiEntry = app.HarmonyLaunchEntries.Any(delegate(HarmonyLaunchEntryInfo entry) { return !entry.IsUiEntry; });
            app.HasNonUiLaunchEntry = hasNonUiEntry && !hasUiEntry;
            if (hasUiEntry)
            {
                app.HasLaunchEntry = true;
                app.IsProcessOnly = false;
            }
            else if (app.HarmonyLaunchEntries.Count > 0)
            {
                app.HasLaunchEntry = false;
                app.IsProcessOnly = true;
            }
        }

        private static void AddHarmonyUser(AppInfo app, int userId)
        {
            if (app == null || userId < 0) return;
            if (app.HarmonyUserIds == null) app.HarmonyUserIds = new List<int>();
            if (!app.HarmonyUserIds.Contains(userId))
            {
                app.HarmonyUserIds.Add(userId);
                app.HarmonyUserIds.Sort();
            }
        }

        private static bool IsSuccessfulCommand(ProcessResult result)
        {
            if (result == null || result.ExitCode != 0 || IsHdcFailure(result)) return false;
            string output = (result.Stdout ?? "") + "\n" + (result.Stderr ?? "");
            return !Regex.IsMatch(output,
                @"(?im)(^|\n)\s*(?:error|failed|failure|exception)\b|not found|does not exist|invalid ability|unknown option|permission denied|no activities? found|unable to resolve|error type 3",
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

        private static string FindTargetState(string[] parts)
        {
            if (parts == null || parts.Length <= 1) return "connected";
            foreach (string part in parts.Skip(1))
            {
                string candidate = (part ?? "").Trim().Trim(',', ';', ':').ToLowerInvariant();
                if (candidate == "connected" || candidate == "device" || candidate == "online"
                    || candidate == "authorized" || candidate == "authenticated"
                    || candidate == "unauthorized" || candidate == "un-authorized" || candidate == "offline"
                    || candidate == "disconnected" || candidate == "disconnect" || candidate == "connecting"
                    || candidate == "error" || candidate == "ready")
                    return candidate;
            }
            return parts[1].ToLowerInvariant();
        }

        private static bool IsDiscoveryDiagnosticLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;
            string normalized = line.Trim().ToLowerInvariant();
            if (normalized.Contains("command not found")
                || normalized.Contains("is not recognized as an internal or external command")
                || normalized.Contains("no such file or directory"))
                return true;
            return normalized.StartsWith("error:", StringComparison.Ordinal)
                || normalized.StartsWith("failed:", StringComparison.Ordinal)
                || normalized.StartsWith("failure:", StringComparison.Ordinal)
                || normalized.StartsWith("exception:", StringComparison.Ordinal);
        }

        private static bool IsHdcControlPort(string serial, string[] parts)
        {
            if (Regex.IsMatch(serial ?? "", @"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            if (parts == null || parts.Length < 2) return false;
            string transport = (parts[1] ?? "").Trim().Trim(',', ';', ':');
            return string.Equals(transport, "UART", StringComparison.OrdinalIgnoreCase)
                || string.Equals(transport, "SERIAL", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindProcessTokenIndex(string[] parts, int start)
        {
            for (int i = Math.Max(0, start); i < (parts == null ? 0 : parts.Length); i++)
            {
                if (ValidBundle(BundleFromProcess(parts[i]))) return i;
                if (parts[i].StartsWith("/", StringComparison.Ordinal) || parts[i].StartsWith("[", StringComparison.Ordinal)) return i;
            }
            // Some vendor ps variants omit the header and expose a service
            // name without a dotted bundle. Keep scanning past numeric and
            // time metadata so the real process is still selectable.
            for (int i = Math.Max(0, start); i < (parts == null ? 0 : parts.Length); i++)
            {
                string candidate = (parts[i] ?? "").Trim().Trim(',', ';', ':');
                if (IsProcessMetadataToken(candidate)) continue;
                return i;
            }
            return -1;
        }

        private static int InferHeaderlessProcessUserId(string[] parts, int pidIndex, int commandIndex)
        {
            if (parts == null) return -1;
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == pidIndex || (commandIndex >= 0 && i >= commandIndex)) continue;
                string candidate = (parts[i] ?? "").Trim().Trim(',', ';', ':');
                if (!IsLikelyProcessUserToken(candidate)) continue;
                return ParseProcessUserValue(candidate, "uid");
            }
            return -1;
        }

        private static bool IsProcessMetadataToken(string value)
        {
            string candidate = (value ?? "").Trim().Trim(',', ';', ':');
            if (candidate.Length == 0 || candidate == "?" || candidate == "-") return true;
            if (IsLikelyProcessUserToken(candidate)) return true;
            if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int _)) return true;
            if (Regex.IsMatch(candidate, @"^\d+(?:\.\d+)?%?$", RegexOptions.CultureInvariant)) return true;
            if (Regex.IsMatch(candidate, @"^\d{1,3}:\d{2}(?::\d{2})?$", RegexOptions.CultureInvariant)) return true;
            return Regex.IsMatch(candidate, @"^[RSDTtZWI]$", RegexOptions.CultureInvariant);
        }

        private static bool IsLikelyProcessUserToken(string value)
        {
            string candidate = (value ?? "").Trim();
            if (Regex.IsMatch(candidate, @"(?i)^u\d+(?:_|$)", RegexOptions.CultureInvariant)) return true;
            if (Regex.IsMatch(candidate, @"(?i)^(?:uid|user(?:id)?)\s*[:=]", RegexOptions.CultureInvariant)) return true;
            return int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric)
                && numeric >= 100000;
        }

        private static string ProcessNameFromCommand(string command)
        {
            string value = (command ?? "").Trim().Trim('"', '\'');
            MatchCollection tokens = Regex.Matches(value, @"\S+");
            foreach (Match token in tokens)
            {
                string candidate = NormalizeProcessToken(token.Value);
                if (ValidBundle(BundleFromProcess(candidate))) return candidate;
            }
            return tokens.Count > 0
                ? NormalizeProcessToken(tokens[0].Value)
                : NormalizeProcessToken(value);
        }

        private static string NormalizeProcessToken(string token)
        {
            string value = (token ?? "").Trim().Trim('"', '\'', ',', ';');
            bool kernelName = value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']';
            if (kernelName)
                value = value.Substring(1, value.Length - 2);
            if (kernelName) return value;
            int separator = value.LastIndexOfAny(new[] { '/', '\\' });
            return separator >= 0 && separator < value.Length - 1
                ? value.Substring(separator + 1)
                : value;
        }

        private static bool ValidBundle(string value) { return Regex.IsMatch(value ?? "", @"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+$"); }

        private static bool ValidHarmonyApplicationBundle(string value)
        {
            return Regex.IsMatch(value ?? "", @"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*$");
        }

        private static string NormalizeExplicitBundle(string value)
        {
            string candidate = (value ?? "").Trim().Trim('"', '\'', ',', ';');
            return ValidHarmonyApplicationBundle(candidate) ? candidate : "";
        }

        private static string ExtractExplicitBundleField(string line)
        {
            Match match = Regex.Match(
                line ?? "",
                @"(?i)(?:^|[\s,{])(?:[""']?)(?:bundle[-_ ]?name|bundle(?:id|_id)?|package[-_ ]?name|package(?:id|_id)?|application[-_ ]?id|app(?:id|_id|identifier))(?:[""']?)\s*[:=]\s*[""']?(?<value>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)",
                RegexOptions.CultureInvariant);
            return match.Success ? NormalizeExplicitBundle(match.Groups["value"].Value) : "";
        }

        private static string ExtractTextField(string line, string fieldPattern)
        {
            if (string.IsNullOrWhiteSpace(fieldPattern)) return "";
            Match match = Regex.Match(
                line ?? "",
                @"(?i)(?:^|[,{;\s])(?:[""']?)(?:" + fieldPattern + @")(?:[""']?)\s*[:=]\s*[""']?([^,""'\r\n}]+)",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private static string ExtractKeyValueField(string line, string fieldPattern)
        {
            if (string.IsNullOrWhiteSpace(fieldPattern)) return "";
            Match match = Regex.Match(
                line ?? "",
                @"(?i)(?:^|[\s,])(?:" + fieldPattern + @")\s*[:=]\s*(?<value>[^,]+)",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["value"].Value.Trim().Trim('"', '\'') : "";
        }

        internal static int ParseHarmonyUserId(string value)
        {
            string text = (value ?? "").Trim();
            Match explicitId = Regex.Match(text, @"(?i)(?:user(?:id)?|uid)\s*[:=]\s*(?<id>\d+)", RegexOptions.CultureInvariant);
            if (explicitId.Success && int.TryParse(explicitId.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return id;
            Match profile = Regex.Match(text, @"(?i)^u(?<id>\d+)(?:_|$)", RegexOptions.CultureInvariant);
            if (profile.Success && int.TryParse(profile.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return id;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id >= 0 ? id : -1;
        }

        private static int ParseProcessUserId(string line)
        {
            Match match = Regex.Match(
                line ?? "",
                @"(?i)(?:^|[\s,])(?<field>uid|user(?:id)?|user_id)\s*[:=]\s*(?<value>[^,\s]+)",
                RegexOptions.CultureInvariant);
            return match.Success
                ? ParseProcessUserValue(match.Groups["value"].Value, match.Groups["field"].Value)
                : -1;
        }

        private static int ParseProcessUserValue(string value, string fieldName)
        {
            string text = (value ?? "").Trim();
            bool isUid = string.Equals(fieldName, "uid", StringComparison.OrdinalIgnoreCase);
            bool isUser = string.Equals(fieldName, "user", StringComparison.OrdinalIgnoreCase);
            if (isUid || isUser)
            {
                Match profile = Regex.Match(text, @"(?i)^u(?<id>\d+)(?:_|$)", RegexOptions.CultureInvariant);
                if (profile.Success && int.TryParse(profile.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int profileId))
                    return profileId;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid) && uid >= 100000)
                    return uid / 100000;
                // `UID` is commonly a Linux/system account (for example
                // 2000=shell), not a Harmony profile. Only a USER column is
                // allowed to interpret a small explicit integer as a profile.
                if (isUid) return -1;
                if (isUser && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId) && userId >= 0)
                    return userId;
            }
            return ParseHarmonyUserId(text);
        }

        private static ProcessInfo CreateProcess(int pid, string name, string serial, int harmonyUserId = -1, string explicitBundle = "")
        {
            return new ProcessInfo
            {
                Pid = pid,
                Name = name,
                DisplayName = name,
                // A single-segment Bundle is accepted only when HDC labels the
                // field explicitly. Ordinary process names still use the
                // dotted executable heuristic so `foundation` and similar
                // system services do not become fake applications.
                BundleId = ValidHarmonyApplicationBundle(explicitBundle) ? explicitBundle : BundleFromProcess(name),
                DeviceUdid = serial ?? "",
                Platform = "harmony",
                HarmonyUserId = harmonyUserId,
                Recommended = false,
                ForegroundApplication = false
            };
        }

        private async Task<ProcessResult> RunShellAsync(string serial, IEnumerable<string> command, int timeoutMs, CancellationToken token)
        {
            await _shellGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string[] commandParts = command == null ? new string[0] : command.ToArray();
                return await _shellExecutor(serial, commandParts, timeoutMs, token).ConfigureAwait(false);
            }
            finally
            {
                _shellGate.Release();
            }
        }

        private static Task<ProcessResult> ExecuteHdcShellAsync(string serial, string[] command, int timeoutMs, CancellationToken token)
        {
            return ProcessRunner.RunAsync(
                RuntimeTools.HdcExecutable,
                TargetArgs(serial, command ?? new string[0]),
                timeoutMs,
                token);
        }

        private async Task PopulateDeviceDetailsAsync(DeviceInfo device, CancellationToken token)
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
            foreach (string[] command in BuildRenderServiceInfoCommands())
            {
                try
                {
                    ProcessResult resolution = await RunShellAsync(device.Udid, command, 5000, token).ConfigureAwait(false);
                    if (resolution == null || IsHdcFailure(resolution)) continue;
                    string parsed = ParseResolution((resolution.Stdout ?? "") + "\n" + (resolution.Stderr ?? ""));
                    if (parsed.Length > 0)
                    {
                        device.Resolution = parsed;
                        break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        internal static IReadOnlyList<string[]> BuildRenderServiceInfoCommands()
        {
            // Harmony releases expose RenderService with different action names.
            // Keep both real command forms instead of assuming one vendor layout.
            return new[]
            {
                new[] { "hidumper", "-s", "RenderService", "-a", "surface" },
                new[] { "hidumper", "-s", "RenderService", "-a", "screen" }
            };
        }

        internal static string ParseResolution(string text)
        {
            List<string> sizes = new List<string>();
            foreach (string line in Lines(text))
            {
                if (Regex.IsMatch(line, @"(?i)isVirtual\s*[=:]\s*true", RegexOptions.CultureInvariant)) continue;
                Match match = Regex.Match(line,
                    @"(?i)(?:physical\s+resolution|physicalResolution|displayResolution|resolution)\s*[=:]\s*([1-9]\d*)\s*[x×]\s*([1-9]\d*)",
                    RegexOptions.CultureInvariant);
                if (!match.Success)
                    match = Regex.Match(line,
                        @"(?i)\bwidth\s*[=:]\s*([1-9]\d*)\D+\bheight\s*[=:]\s*([1-9]\d*)",
                        RegexOptions.CultureInvariant);
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
            return state == "device" || state == "connected" || state == "online"
                || state == "authorized" || state == "authenticated";
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
