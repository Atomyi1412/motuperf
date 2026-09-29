using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
        public int HarmonyUserId { get; set; } = -1;
        public int HarmonyAppIndex { get; set; } = -1;
    }

    internal sealed class HarmonyPackageRecord
    {
        public string BundleId { get; set; }
        public int UserId { get; set; }
        public int AppIndex { get; set; } = -1;
    }

    internal sealed class HarmonyUserDiscovery
    {
        public HarmonyUserDiscovery()
        {
            UserIds = new List<int>();
            Diagnostic = "";
        }

        public List<int> UserIds { get; set; }
        public string Diagnostic { get; set; }
    }

    /// <summary>
    /// Ability Manager records may carry process state without Bundle identity.
    /// Only an explicit Bundle or a directly state-bearing running record in
    /// the same PID record can prove that the PID is a real target.
    /// </summary>
    internal sealed class HarmonyProcessBinding
    {
        public int Pid { get; set; }
        public string BundleId { get; set; }
        public string ProcessName { get; set; }
        public int HarmonyUserId { get; set; } = -1;
        public int HarmonyAppIndex { get; set; } = -1;
        public bool Foreground { get; set; }
        // A candidate from a direct, state-bearing AppRunningRecord still
        // needs a readable executable before it can become a new target.
        public bool PidEvidenceVerified { get; set; }
    }

    /// <summary>
    /// Read-only icon evidence returned by Bundle Manager. The lists are kept
    /// separate because an icon may be a directly addressable remote file or
    /// a resource inside a HAP archive.
    /// </summary>
    internal sealed class HarmonyIconMetadata
    {
        public HarmonyIconMetadata()
        {
            IconPaths = new List<string>();
            HapPaths = new List<string>();
            IconEntries = new List<string>();
        }

        public List<string> IconPaths { get; private set; }
        public List<string> HapPaths { get; private set; }
        public List<string> IconEntries { get; private set; }

        public void Merge(HarmonyIconMetadata other)
        {
            if (other == null) return;
            AddDistinct(IconPaths, other.IconPaths);
            AddDistinct(HapPaths, other.HapPaths);
            AddDistinct(IconEntries, other.IconEntries);
        }

        private static void AddDistinct(ICollection<string> target, IEnumerable<string> values)
        {
            foreach (string value in values ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value)) target.Add(value);
        }
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
            UserInventoryError = "";
        }

        public List<AppInfo> Apps { get; set; }
        public List<ProcessInfo> Processes { get; set; }
        public string ProcessInventoryError { get; set; }
        public string UserInventoryError { get; set; }
    }

    /// <summary>
    /// Owns the HDC boundary for OpenHarmony/HarmonyOS devices. Harmony is kept
    /// as its own platform even when the selected application is an Android
    /// compatibility application.
    /// </summary>
    public sealed class HarmonyLookupService
    {
        private sealed class HarmonyInstanceScope
        {
            public HarmonyInstanceScope(int harmonyUserId, int harmonyAppIndex)
            {
                HarmonyUserId = harmonyUserId;
                HarmonyAppIndex = harmonyAppIndex;
            }

            public int HarmonyUserId { get; private set; }
            public int HarmonyAppIndex { get; private set; }
        }

        private const int ProcessInventoryBudgetMs = 30000;
        private const int ProcessStartTimeBudgetMs = 20000;
        private const int UserInventoryBudgetMs = 20000;
        private const int BundleInventoryBudgetMs = 30000;
        private const int PackageInventoryBudgetMs = 30000;
        private const int IconHydrationBudgetMs = 30000;
        private const int DeviceDetailBudgetMs = 20000;
        private const int AppIndexHelpTimeoutMs = 5000;

        // appIndex is an inventory/runtime identity, not an official aa
        // selector on every Harmony build. Only use a vendor selector when
        // the connected device advertises that exact option in its own help.
        private static readonly string[] HarmonyAppIndexStartOptionNames =
        {
            "--app-index", "--appIndex", "--app_index",
            "--app-clone-index", "--appCloneIndex", "--app_clone_index",
            "--clone-index", "--cloneIndex", "--clone_index"
        };

        // The picker loads the app and process inventories in parallel. HDC
        // devices do not all tolerate concurrent shell sessions, so serialize
        // shell commands per service instance while keeping the UI tasks
        // independently cancellable.
        private readonly SemaphoreSlim _shellGate = new SemaphoreSlim(1, 1);
        private readonly Func<string, string[], int, CancellationToken, Task<ProcessResult>> _shellExecutor;
        private readonly int _processStartTimeBudgetMs;
        private readonly int _iconHydrationBudgetMs;
        private readonly int _deviceDetailBudgetMs;

        public HarmonyLookupService()
            : this(null, IconHydrationBudgetMs, DeviceDetailBudgetMs, ProcessStartTimeBudgetMs)
        {
        }

        internal HarmonyLookupService(Func<string, string[], int, CancellationToken, Task<ProcessResult>> shellExecutor)
            : this(shellExecutor, IconHydrationBudgetMs, DeviceDetailBudgetMs, ProcessStartTimeBudgetMs)
        {
        }

        internal HarmonyLookupService(
            Func<string, string[], int, CancellationToken, Task<ProcessResult>> shellExecutor,
            int iconHydrationBudgetMs,
            int deviceDetailBudgetMs)
            : this(shellExecutor, iconHydrationBudgetMs, deviceDetailBudgetMs, ProcessStartTimeBudgetMs)
        {
        }

        internal HarmonyLookupService(
            Func<string, string[], int, CancellationToken, Task<ProcessResult>> shellExecutor,
            int iconHydrationBudgetMs,
            int deviceDetailBudgetMs,
            int processStartTimeBudgetMs)
        {
            _shellExecutor = shellExecutor ?? ExecuteHdcShellAsync;
            _processStartTimeBudgetMs = Math.Max(1, processStartTimeBudgetMs);
            _iconHydrationBudgetMs = Math.Max(1, iconHydrationBudgetMs);
            _deviceDetailBudgetMs = Math.Max(1, deviceDetailBudgetMs);
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
            // Only stdout may use the legacy serial-only format. Require a
            // complete state-bearing row before trusting a stderr identity.
            foreach (var row in Lines(result.Stdout).Select(line => new { Line = line, Legacy = true })
                .Concat(Lines(result.Stderr).Select(line => new { Line = line, Legacy = false })))
            {
                string line = row.Line.Trim();
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
                string failureHint = DiscoveryFailureHint(line);
                if (IsDiscoveryDiagnosticLine(line))
                {
                    issues.Add(failureHint.Length > 0 ? failureHint
                        : DescribeDiscoveryFailure(new ProcessResult(result.ExitCode, line, "")));
                    continue;
                }
                if (line.StartsWith("[", StringComparison.Ordinal)) continue;
                string serial = parts[0];
                if (!Regex.IsMatch(serial, @"^[A-Za-z0-9][A-Za-z0-9_.:-]*$")) continue;
                if (parts.Length == 1 && (!row.Legacy || IsReservedDiscoveryToken(serial))) continue;
                string state = FindTargetState(parts);
                if (state.Length == 0)
                {
                    // Only an otherwise unparseable line may use the broad
                    // diagnostic classifier. A valid target row is classified
                    // by its state column so serials/metadata containing words
                    // such as offline cannot poison a connected device.
                    if (failureHint.Length > 0) issues.Add(failureHint);
                    continue;
                }
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
                if (state.Contains("unauthoriz") || state.Contains("unauthoris"))
                    issues.Add("鸿蒙设备未授权：请解锁设备，在设备端允许 USB 调试/HDC 调试后刷新。");
                else if (state.Contains("permission"))
                    issues.Add("鸿蒙设备权限不足：请解锁设备并允许 HDC/USB 调试授权后刷新。");
                else if (state.Contains("offline"))
                    issues.Add("鸿蒙设备离线：请重新插拔 USB、检查数据线和开发者调试开关后刷新。");
                else if (state.Contains("disconnect") || state.Contains("not connected"))
                    issues.Add("鸿蒙设备未连接：请检查 USB 连接、数据线和设备端调试开关后刷新。");
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
            HarmonyUserDiscovery userDiscovery = await ReadHarmonyUserIdsAsync(serial, token).ConfigureAwait(false);
            List<int> userIds = userDiscovery.UserIds;

            List<AppInfo> apps = await ReadBundleManagerAppsAsync(serial, userIds, token).ConfigureAwait(false);
            bool dumpFailed = apps.Count == 0;
            HashSet<string> seen = new HashSet<string>(
                apps.Select(HarmonyApplicationScopeKey),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                // Keep the same package sources as ListAppsAsync. This pass is
                // intentionally shared with process discovery below so the
                // picker never combines two independent inventory snapshots.
                foreach (HarmonyPackageRecord package in await ReadAndroidPackagesAsync(serial, userIds, token).ConfigureAwait(false))
                    AddApp(apps, seen, package.BundleId, "Android 兼容应用或系统应用", harmonyUserId: package.UserId,
                        harmonyAppIndex: package.AppIndex);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            BindUnscopedInstalledAppsToSingleUser(apps, userIds);

            List<ProcessInfo> processes = new List<ProcessInfo>();
            string processInventoryError = "";
            try
            {
                processes = await ReadProcessesWithStartTimesAsync(serial, userIds, token).ConfigureAwait(false);
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
                    ? string.IsNullOrWhiteSpace(userDiscovery.Diagnostic)
                        ? "无法读取鸿蒙应用列表，请检查 HDC 授权，或在设备上打开应用后刷新进程。"
                        : "无法读取鸿蒙应用列表：" + userDiscovery.Diagnostic
                    : "无法读取鸿蒙应用和进程列表：" + processInventoryError);

            return new HarmonyTargetInventory
            {
                Apps = ExpandHarmonyUserInstances(apps)
                    .OrderByDescending(delegate(AppInfo app) { return app.Recommended; })
                    .ThenBy(delegate(AppInfo app) { return app.BundleId; })
                    .ThenBy(delegate(AppInfo app) { return app.HarmonyUserId; })
                    .ToList(),
                Processes = processes,
                ProcessInventoryError = processInventoryError,
                UserInventoryError = userDiscovery.Diagnostic
            };
        }

        public async Task<List<AppInfo>> ListAppsAsync(string serial, CancellationToken token)
        {
            HarmonyUserDiscovery userDiscovery = await ReadHarmonyUserIdsAsync(serial, token).ConfigureAwait(false);
            List<int> userIds = userDiscovery.UserIds;

            List<AppInfo> apps = await ReadBundleManagerAppsAsync(serial, userIds, token).ConfigureAwait(false);
            bool dumpFailed = apps.Count == 0;
            HashSet<string> seen = new HashSet<string>(apps.Select(HarmonyApplicationScopeKey), StringComparer.OrdinalIgnoreCase);
            try
            {
                // Do not use `-3` here. Harmony devices can expose launchable
                // system/preinstalled applications through the Android
                // compatibility layer as well as third-party packages.
                foreach (HarmonyPackageRecord package in await ReadAndroidPackagesAsync(serial, userIds, token).ConfigureAwait(false))
                    AddApp(apps, seen, package.BundleId, "Android 兼容应用或系统应用", harmonyUserId: package.UserId,
                        harmonyAppIndex: package.AppIndex);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Bundle Manager and package-manager views are not guaranteed to
            // agree across HarmonyOS releases. Always merge real processes so
            // an installed/running app remains selectable even when one
            // inventory command omits it.
            try
            {
                List<ProcessInfo> processes = await ReadProcessListAsync(serial, userIds, token).ConfigureAwait(false);
                MergeProcessApps(apps, processes);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            BindUnscopedInstalledAppsToSingleUser(apps, userIds);
            if (apps.Count == 0 && dumpFailed)
                throw new IOException(string.IsNullOrWhiteSpace(userDiscovery.Diagnostic)
                    ? "无法读取鸿蒙应用列表，请检查 HDC 授权，或在设备上打开应用后刷新进程。"
                    : "无法读取鸿蒙应用列表：" + userDiscovery.Diagnostic);
            return ExpandHarmonyUserInstances(apps)
                .OrderByDescending(delegate(AppInfo app) { return app.Recommended; })
                .ThenBy(delegate(AppInfo app) { return app.BundleId; })
                .ThenBy(delegate(AppInfo app) { return app.HarmonyUserId; })
                .ToList();
        }

        public async Task<List<ProcessInfo>> ListProcessesAsync(string serial, CancellationToken token)
        {
            // Process-only callers still need the real Harmony profile scope.
            // Without this pass, Ability Manager falls back to aggregate
            // output and a same-Bundle process in another user can be left
            // unknown or incorrectly treated as the selected process.
            HarmonyUserDiscovery userDiscovery = await ReadHarmonyUserIdsAsync(serial, token).ConfigureAwait(false);
            return await ReadProcessesWithStartTimesAsync(serial, userDiscovery.UserIds, token).ConfigureAwait(false);
        }

        private async Task<List<ProcessInfo>> ReadProcessesWithStartTimesAsync(
            string serial,
            IEnumerable<int> harmonyUserIds,
            CancellationToken token)
        {
            List<ProcessInfo> processes = await ReadProcessListAsync(serial, harmonyUserIds, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (processes.Count > 0)
            {
                Dictionary<int, long> starts = new Dictionary<int, long>();
                using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    budget.CancelAfter(_processStartTimeBudgetMs);
                    foreach (string[] command in BuildProcessStatCommands(processes.Select(p => p.Pid)))
                    {
                        if (budget.IsCancellationRequested)
                        {
                            token.ThrowIfCancellationRequested();
                            break;
                        }
                        ProcessResult stats;
                        try
                        {
                            stats = await RunShellAsync(serial, command, 12000, budget.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            token.ThrowIfCancellationRequested();
                            break;
                        }
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
                        foreach (KeyValuePair<int, long> pair in ParseProcessStartTimeTicksFromIndependentStreams(
                            stats.Stdout, stats.Stderr))
                            starts[pair.Key] = pair.Value;
                    }
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

        internal static Dictionary<int, long> ParseProcessStartTimeTicksFromIndependentStreams(
            string stdout,
            string stderr)
        {
            Dictionary<int, long> values = new Dictionary<int, long>();
            HashSet<int> conflicts = new HashSet<int>();
            foreach (string output in new[] { stdout ?? "", stderr ?? "" })
            {
                foreach (KeyValuePair<int, long> pair in AndroidLookupService.ParseProcessStartTimeTicks(output))
                {
                    if (conflicts.Contains(pair.Key)) continue;
                    if (values.TryGetValue(pair.Key, out long existing))
                    {
                        if (existing != pair.Value)
                        {
                            values.Remove(pair.Key);
                            conflicts.Add(pair.Key);
                        }
                        continue;
                    }
                    values[pair.Key] = pair.Value;
                }
            }
            return values;
        }

        internal static List<int> ParseUserIds(string output)
        {
            // An unreadable account list is not evidence for the owner
            // profile. Keep the unknown scope empty; callers may still use
            // real process rows for manual collection.
            return ParseExplicitUserIds(output);
        }

        internal static List<int> ParseExplicitUserIds(string output)
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (string raw in Lines(output))
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || IsAccountDiagnosticLine(line)) continue;

                Match match = Regex.Match(line,
                    @"^ID\s*:\s*(?<id>\d+)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    // pm/cmd user output is a complete UserInfo record. Do
                    // not accept a prefix such as `UserInfo{100oops...}` or
                    // an incomplete record split across a diagnostic line.
                    Match userInfo = Regex.Match(line,
                        @"^UserInfo\{\s*(?:id\s*[:=]\s*)?(?<id>\d+)(?=\s*[:},]).*\}\s*(?:running|stopped)?\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (userInfo.Success) match = userInfo;
                }
                if (!match.Success)
                {
                    // Account/HiDumper variants expose one complete key/value
                    // per line. Requiring the whole line prevents help text,
                    // errors and prose such as `user 100 does not exist`
                    // from becoming profile evidence.
                    match = Regex.Match(line,
                        @"^(?:user(?:[_ -]?id)?|local[_ -]?id|account[_ -]?id)\s*[:=]\s*(?<id>\d+)\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                if (!match.Success)
                {
                    match = Regex.Match(line,
                        @"^user\s+(?<id>\d+)\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                if (match.Success
                    && int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    && id >= 0)
                    ids.Add(id);
            }
            return ids.OrderBy(delegate(int id) { return id; }).ToList();
        }

        private static bool IsAccountDiagnosticLine(string line)
        {
            string value = (line ?? "").Trim();
            if (value.Length == 0) return true;
            if (value.StartsWith("[Fail]", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase)) return true;
            return Regex.IsMatch(value,
                @"^(?:error|failed|failure|exception|warning|usage|permission denied|denied|not found|invalid)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private async Task<HarmonyUserDiscovery> ReadHarmonyUserIdsAsync(string serial, CancellationToken token)
        {
            HarmonyUserDiscovery discovery = new HarmonyUserDiscovery();
            HashSet<int> userIds = new HashSet<int>();
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(UserInventoryBudgetMs);
                foreach (string[] command in BuildUserInventoryCommands())
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        discovery.Diagnostic = "用户资料查询达到总时限，已保留已读取的用户范围。";
                        break;
                    }
                    try
                    {
                        ProcessResult result = await RunShellAsync(serial, command, 10000, budget.Token).ConfigureAwait(false);
                        if (result == null)
                        {
                            continue;
                        }
                        if (IsHdcFailure(result)) continue;
                        // Keep the streams independent. A transport error or a
                        // diagnostic in stderr must not complete a valid record
                        // from stdout, and vice versa.
                        foreach (string output in new[] { result.Stdout ?? "", result.Stderr ?? "" })
                            foreach (int userId in ParseExplicitUserIds(output))
                                userIds.Add(userId);
                        if (result.ExitCode != 0 && userIds.Count == 0)
                            discovery.Diagnostic = "用户资料查询返回失败，已保留用户范围未知。";
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        discovery.Diagnostic = "用户资料查询达到总时限，已保留已读取的用户范围。";
                        break;
                    }
                    catch { }
                }
            }
            discovery.UserIds = userIds.OrderBy(delegate(int id) { return id; }).ToList();
            if (discovery.UserIds.Count == 0)
                discovery.Diagnostic = string.IsNullOrWhiteSpace(discovery.Diagnostic)
                    ? "用户资料不可读，已保留用户范围未知；不会默认使用 user 0。"
                    : discovery.Diagnostic + " 不会默认使用 user 0。";
            else
                discovery.Diagnostic = "";
            return discovery;
        }

        internal static IReadOnlyList<string[]> BuildUserInventoryCommands()
        {
            // Native Harmony has no pm/cmd dependency. acm is read-only here;
            // permission denial must not trigger root or account changes.
            return new[]
            {
                new[] { "acm", "dump", "-a" },
                new[] { "hidumper", "-s", "200", "-a", "-os_account_infos" },
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
                .Where(delegate(int userId) { return userId >= 0; })
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
                // Package-list -u includes uninstalled packages; -U prints
                // UIDs. Only --user selects a profile in pm/cmd package.
                List<string> scoped = (baseCommand ?? Array.Empty<string>()).ToList();
                scoped.Add("--user");
                scoped.Add(value);
                commands.Add(scoped.ToArray());
            }
        }

        internal static int CommandUserId(IEnumerable<string> command)
        {
            string[] parts = (command ?? Enumerable.Empty<string>()).ToArray();
            bool isCompatibilityPackageCommand = parts.Length > 0 &&
                (string.Equals(parts[0], "pm", StringComparison.Ordinal)
                    || (parts.Length > 1 && string.Equals(parts[0], "cmd", StringComparison.Ordinal)
                        && string.Equals(parts[1], "package", StringComparison.Ordinal)));
            bool isBundleManagerDump = parts.Length > 1
                && string.Equals(parts[0], "bm", StringComparison.Ordinal)
                && string.Equals(parts[1], "dump", StringComparison.Ordinal);
            bool isAbilityManagerDump = parts.Length > 1
                && string.Equals(parts[0], "aa", StringComparison.Ordinal)
                && string.Equals(parts[1], "dump", StringComparison.Ordinal);
            bool isAbilityManagerStart = parts.Length > 1
                && string.Equals(parts[0], "aa", StringComparison.Ordinal)
                && string.Equals(parts[1], "start", StringComparison.Ordinal);
            bool isAndroidManagerStart = parts.Length > 1
                && string.Equals(parts[0], "am", StringComparison.Ordinal)
                && string.Equals(parts[1], "start", StringComparison.Ordinal);
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                string option = parts[i];
                bool isUserOption = (isCompatibilityPackageCommand
                        && string.Equals(option, "--user", StringComparison.Ordinal))
                    || (isBundleManagerDump
                        && (string.Equals(option, "-u", StringComparison.Ordinal)
                            || string.Equals(option, "--user-id", StringComparison.Ordinal)))
                    || (isAbilityManagerDump
                        && (string.Equals(option, "-u", StringComparison.Ordinal)
                            || string.Equals(option, "--userId", StringComparison.Ordinal)))
                    || (isAbilityManagerStart
                        && string.Equals(option, "-u", StringComparison.Ordinal))
                    || (isAndroidManagerStart
                        && string.Equals(option, "--user", StringComparison.Ordinal));
                if (!isUserOption) continue;
                if (int.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
                    && userId >= 0) return userId;
            }
            return -1;
        }

        private async Task<List<HarmonyPackageRecord>> ReadPackageCommandsAsync(string serial, IEnumerable<string[]> commands, CancellationToken token)
        {
            List<HarmonyPackageRecord> packages = new List<HarmonyPackageRecord>();
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(PackageInventoryBudgetMs);
                foreach (string[] command in commands ?? Enumerable.Empty<string[]>())
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    try
                    {
                        ProcessResult result = await RunShellAsync(serial, command, 15000, budget.Token).ConfigureAwait(false);
                        if (result == null || IsHdcFailure(result)) continue;
                        int userId = CommandUserId(command);
                        foreach (HarmonyPackageRecord record in ParseAndroidPackageRecordsFromIndependentStreams(result.Stdout, result.Stderr))
                        {
                            if (packages.Any(delegate(HarmonyPackageRecord item)
                            {
                                return string.Equals(item.BundleId, record.BundleId, StringComparison.OrdinalIgnoreCase)
                                    && item.UserId == userId
                                    && item.AppIndex == record.AppIndex;
                            })) continue;
                            packages.Add(new HarmonyPackageRecord
                            {
                                BundleId = record.BundleId,
                                UserId = userId,
                                AppIndex = record.AppIndex
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { }
                }
            }
            return packages;
        }

        private async Task<List<AppInfo>> ReadBundleManagerAppsAsync(string serial, IEnumerable<int> userIds, CancellationToken token)
        {
            List<AppInfo> apps = new List<AppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string[]> commands = BuildBundleManagerInventoryCommands(userIds);
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(BundleInventoryBudgetMs);
                foreach (string[] command in commands)
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    try
                    {
                        ProcessResult result = await RunShellAsync(serial, command, 20000, budget.Token).ConfigureAwait(false);
                        if (result == null || IsHdcFailure(result)) continue;
                        int userId = CommandUserId(command);
                        // Bundle/account/Ability context belongs to one stream.
                        // Only complete parsed records may cross this boundary.
                        foreach (AppInfo app in ParseApps(result.Stdout)
                            .Concat(ParseApps(result.Stderr)))
                        {
                            // A Bundle Manager record can carry its own profile
                            // identity. Prefer that evidence over the command
                            // option: vendor builds sometimes echo a global
                            // record while a scoped command is being used, and
                            // assigning the option blindly would duplicate the
                            // app into the wrong Harmony profile.
                            int recordUserId = app.HarmonyUserId >= 0 ? app.HarmonyUserId : userId;
                            AppInfo merged = AddApp(apps, seen, app.BundleId, "Bundle Manager 应用", app.Name, app.Version, app.HasLaunchEntry,
                                harmonyLaunchEntries: AnnotateHarmonyLaunchEntries(app.HarmonyLaunchEntries, recordUserId, app.HarmonyAppIndex),
                                harmonyUserId: recordUserId, harmonyAppIndex: app.HarmonyAppIndex,
                                hasSystemAppEvidence: app.IsSystemApp || app.IsPreInstallApp || !string.IsNullOrWhiteSpace(app.InstallSource),
                                isSystemApp: app.IsSystemApp, isPreInstallApp: app.IsPreInstallApp,
                                installSource: app.InstallSource);
                            if (merged != null)
                                foreach (int appUserId in app.HarmonyUserIds ?? new List<int>())
                                    AddHarmonyUser(merged, appUserId);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { }
                }
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
                // OpenHarmony's Bundle Manager defines -u and --user-id for
                // the profile. Do not reuse aa or Android package aliases:
                // -U is not a user selector in native Harmony commands.
                foreach (string option in new[] { "-u", "--user-id" })
                    commands.Add(new[] { "bm", "dump", "-a", option, value });
            }
            return commands;
        }

        private async Task<List<ProcessInfo>> ReadProcessListAsync(
            string serial,
            IEnumerable<int> harmonyUserIds,
            CancellationToken token)
        {
            List<ProcessInfo> allProcesses = new List<ProcessInfo>();
            bool commandSucceeded = false;
            string diagnostics = "";
            using (CancellationTokenSource psBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                psBudget.CancelAfter(ProcessInventoryBudgetMs);
                foreach (string[] command in BuildProcessInventoryCommands())
                {
                    if (psBudget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        diagnostics = "进程命令查询达到总时限。";
                        break;
                    }
                    ProcessResult result;
                    try
                    {
                        result = await RunShellAsync(serial, command, 12000, psBudget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        diagnostics = "进程命令查询达到总时限。";
                        break;
                    }
                    catch (Exception exception)
                    {
                        diagnostics = exception.Message;
                        continue;
                    }
                    if (result != null && !IsHdcFailure(result))
                    {
                        // ps headers must not interpret rows on another stream.
                        List<ProcessInfo> parsed = ParseProcesses(result.Stdout, serial)
                            .Concat(ParseProcesses(result.Stderr, serial))
                            .ToList();
                        // A zero exit code only proves that HDC executed the
                        // command.  A header-only or empty response is not a
                        // usable process inventory and must not hide a later
                        // diagnostic or make the picker look silently empty.
                        if (parsed.Count > 0)
                            commandSucceeded = true;
                        else
                        {
                            string commandOutput = ((result.Stdout ?? "") + " " + (result.Stderr ?? "")).Trim();
                            // Do not let a later successful-but-empty view
                            // erase a useful permission/transport reason from
                            // an earlier process command.
                            if (!string.IsNullOrWhiteSpace(commandOutput)
                                && !IsProcessInventoryHeaderOnly(commandOutput))
                                diagnostics = commandOutput;
                        }
                        allProcesses.AddRange(parsed);
                    }
                    else if (result != null)
                    {
                        string commandOutput = ((result.Stdout ?? "") + " " + (result.Stderr ?? "")).Trim();
                        if (!string.IsNullOrWhiteSpace(commandOutput)
                            && !IsProcessInventoryHeaderOnly(commandOutput))
                            diagnostics = commandOutput;
                    }
                }
            }
            // Some vendor builds expose a usable HDC shell and /proc while
            // removing ps entirely, denying every ps -o field, or returning a
            // partial process view. Always take one bounded read-only /proc
            // snapshot as a complementary source so a partially successful ps
            // command cannot hide unopened applications or system processes.
            // Normal ps views remain the stronger source for any vendor-
            // provided Bundle column and are still merged first; conflicts are
            // handled by the existing PID identity checks.
            if (token.IsCancellationRequested)
                token.ThrowIfCancellationRequested();
            foreach (string[] command in BuildProcProcessInventoryCommands())
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    ProcessResult result = await RunShellAsync(serial, command, 8000, token).ConfigureAwait(false);
                    if (result == null || IsHdcFailure(result)) continue;
                    // The /proc script emits a private marker before any
                    // process row. Only parse a stream carrying that
                    // marker; shell diagnostics or unrelated command
                    // output must never become phantom process records.
                    List<ProcessInfo> parsed = new List<ProcessInfo>();
                    foreach (string output in new[] { result.Stdout ?? "", result.Stderr ?? "" })
                    {
                        if (output.IndexOf("__MOTUPERF_PROC_LIST__", StringComparison.Ordinal) < 0)
                            continue;
                        parsed.AddRange(ParseProcesses(output, serial, inferBundleFromProcessName: false));
                    }
                    if (parsed.Count == 0) continue;
                    allProcesses.AddRange(parsed);
                    commandSucceeded = true;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    diagnostics = exception.Message;
                }
            }
            token.ThrowIfCancellationRequested();
            List<ProcessInfo> processes = MergeProcesses(allProcesses);
            await CompleteCommProcessNamesAsync(serial, processes, token).ConfigureAwait(false);
            // Ability Manager can expose running targets absent from ps.
            // Discover them independently, then apply the same conflict checks
            // used for enrichment. A Bundle alone is not an executable name.
            try
            {
                List<HarmonyProcessBinding> bindings = await ReadAbilityProcessBindingsAsync(
                    serial, harmonyUserIds, token).ConfigureAwait(false);
                // Ability Manager is an independent evidence source. A valid
                // binding counts as a usable inventory response even when the
                // later conflict checks deliberately discard it as a target.
                // This keeps ambiguous real PIDs from becoming a false hard
                // error, while an actually empty successful command still
                // cannot mask a missing process inventory.
                await CompleteMissingAbilityProcessNamesAsync(serial, bindings, token).ConfigureAwait(false);
                if (bindings.Any(binding => ValidHarmonyApplicationBundle(binding.BundleId)
                    || !string.IsNullOrWhiteSpace(binding.ProcessName)))
                    commandSucceeded = true;
                AddIndependentAbilityProcesses(processes, bindings, serial);
                ApplyAbilityProcessBindings(processes, bindings);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (processes.Count == 0 && !commandSucceeded)
                throw new IOException("无法读取鸿蒙进程：" + (string.IsNullOrWhiteSpace(diagnostics)
                    ? "HDC 未返回可解析的进程记录。"
                    : Sanitize(diagnostics, 240)));
            return processes.OrderByDescending(process => process.Recommended)
                .ThenBy(process => process.Name).ThenBy(process => process.Pid).ToList();
        }

        internal static IReadOnlyList<string[]> BuildProcProcessInventoryCommands()
        {
            // Keep the marker in the output so a vendor diagnostic line cannot
            // be mistaken for a process row. The command reads only procfs;
            // it does not start, stop, or mutate any device process.
            const string script =
                "printf '__MOTUPERF_PROC_LIST__\\n'; "
                + "for p in /proc/[0-9]*; do "
                + "pid=${p##*/}; "
                + "uid=$(sed -n 's/^Uid:[[:space:]]*\\([0-9][0-9]*\\).*/\\1/p' \"$p/status\" 2>/dev/null | head -n 1); "
                + "[ -n \"$uid\" ] || uid=-1; "
                + "printf 'PID=%s UID=%s ' \"$pid\" \"$uid\"; "
                + "if [ -r \"$p/cmdline\" ] && [ -s \"$p/cmdline\" ]; then "
                + "printf 'CMDLINE='; tr '\\000' ' ' < \"$p/cmdline\"; "
                + "else printf 'COMM='; sed -n 's/^[0-9][0-9]* \\(.*\\) .*/\\1/p' \"$p/stat\" 2>/dev/null; fi; "
                + "printf '\\n'; done";
            return new[] { new[] { "sh", "-c", script } };
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

        private static bool IsProcessInventoryHeaderOnly(string output)
        {
            string[] lines = Lines(output)
                .Select(line => (line ?? "").Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            if (lines.Length == 0) return true;
            return lines.All(line =>
            {
                string[] fields = Regex.Split(line, @"\s+");
                bool hasPid = fields.Any(field => string.Equals(field, "PID", StringComparison.OrdinalIgnoreCase));
                bool hasName = fields.Any(field => string.Equals(field, "ARGS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field, "CMD", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field, "CMDLINE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field, "COMMAND", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field, "COMM", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field, "NAME", StringComparison.OrdinalIgnoreCase));
                return hasPid && hasName && fields.All(field => Regex.IsMatch(field, @"^[A-Z_]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            });
        }

        internal static IReadOnlyList<string[]> BuildAbilityInventoryCommands(IEnumerable<int> harmonyUserIds)
        {
            List<string[]> commands = new List<string[]>
            {
                new[] { "aa", "dump", "-a" },
                // OpenHarmony's official process dump is -r/--process.
                new[] { "aa", "dump", "-r" },
                new[] { "aa", "dump", "--process" }
            };
            foreach (int userId in (harmonyUserIds ?? Enumerable.Empty<int>())
                .Where(delegate(int value) { return value >= 0; })
                .Distinct()
                .OrderBy(delegate(int value) { return value; }))
            {
                string value = userId.ToString(CultureInfo.InvariantCulture);
                // Only documented user-scope spellings; never drop a scope
                // to make an unsupported command succeed.
                commands.Add(new[] { "aa", "dump", "-a", "-u", value });
                commands.Add(new[] { "aa", "dump", "-a", "--userId", value });
                commands.Add(new[] { "aa", "dump", "-r", "-u", value });
                commands.Add(new[] { "aa", "dump", "-r", "--userId", value });
            }
            return commands;
        }

        internal static List<string[]> BuildProcessNameCommands(IEnumerable<int> pids)
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
                string pidList = string.Join(" ", batch.Select(delegate(int pid)
                    { return pid.ToString(CultureInfo.InvariantCulture); }));
                commands.Add(new[]
                {
                    "sh",
                    "-c",
                    "for p in " + pidList
                        + "; do if [ -r /proc/$p/cmdline ]; then printf 'PID=%s CMDLINE=' $p; "
                        + "tr '\\000' ' ' < /proc/$p/cmdline; printf '\\n'; fi; done"
                });
            }
            return commands;
        }

        private async Task<List<HarmonyProcessBinding>> ReadAbilityProcessBindingsAsync(
            string serial,
            IEnumerable<int> harmonyUserIds,
            CancellationToken token)
        {
            List<HarmonyProcessBinding> bindings = new List<HarmonyProcessBinding>();
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(8000);
                foreach (string[] command in BuildAbilityInventoryCommands(harmonyUserIds))
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.IsCancellationRequested) break;
                    ProcessResult result;
                    try
                    {
                        result = await RunShellAsync(serial, command, 2000, budget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { continue; }
                    if (result == null || IsHdcFailure(result)) continue;
                    // Independent streams cannot complete each other's partial
                    // records. Nonzero command exits may still carry valid rows.
                    int scopedUserId = CommandUserId(command);
                    List<HarmonyProcessBinding> stdoutBindings = ParseAbilityProcessBindings(result.Stdout);
                    List<HarmonyProcessBinding> stderrBindings = ParseAbilityProcessBindings(result.Stderr);
                    ApplyMissingAbilityUserScope(stdoutBindings, scopedUserId);
                    ApplyMissingAbilityUserScope(stderrBindings, scopedUserId);
                    bindings.AddRange(stdoutBindings);
                    bindings.AddRange(stderrBindings);
                }
            }
            token.ThrowIfCancellationRequested();
            return bindings;
        }

        private static void ApplyMissingAbilityUserScope(
            IEnumerable<HarmonyProcessBinding> bindings,
            int scopedUserId)
        {
            if (scopedUserId < 0) return;
            foreach (HarmonyProcessBinding binding in bindings ?? Enumerable.Empty<HarmonyProcessBinding>())
            {
                // An explicit record field is stronger than the command
                // scope. Only fill the field when this scoped query omitted
                // it; conflicting explicit evidence remains visible to the
                // existing ambiguity checks.
                if (binding != null && binding.HarmonyUserId < 0)
                    binding.HarmonyUserId = scopedUserId;
            }
        }

        private async Task CompleteMissingAbilityProcessNamesAsync(
            string serial,
            IList<HarmonyProcessBinding> bindings,
            CancellationToken token)
        {
            if (bindings == null) return;
            List<int> pids = bindings
                .Where(delegate(HarmonyProcessBinding binding)
                {
                    return binding != null && binding.Pid > 0
                        && (ValidHarmonyApplicationBundle(binding.BundleId) || binding.PidEvidenceVerified)
                        && string.IsNullOrWhiteSpace(binding.ProcessName);
                })
                .Select(delegate(HarmonyProcessBinding binding) { return binding.Pid; })
                .Distinct()
                .OrderBy(delegate(int pid) { return pid; })
                .ToList();
            if (pids.Count == 0) return;

            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(4000);
                foreach (string[] command in BuildProcessNameCommands(pids))
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.IsCancellationRequested) break;
                    ProcessResult result;
                    try
                    {
                        result = await RunShellAsync(serial, command, 2000, budget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { continue; }
                    if (result == null || IsHdcFailure(result)) continue;
                    foreach (KeyValuePair<int, string> pair in ParseProcessNamesFromIndependentStreams(
                        result.Stdout, result.Stderr))
                    {
                        foreach (HarmonyProcessBinding binding in bindings.Where(delegate(HarmonyProcessBinding item)
                        {
                            return item != null && item.Pid == pair.Key && string.IsNullOrWhiteSpace(item.ProcessName);
                        }))
                        {
                            binding.ProcessName = pair.Value;
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
        }

        private async Task CompleteCommProcessNamesAsync(
            string serial,
            IList<ProcessInfo> processes,
            CancellationToken token)
        {
            if (processes == null) return;
            List<ProcessInfo> commRows = processes
                .Where(delegate(ProcessInfo process)
                {
                    return process != null && process.Pid > 0 && process.HarmonyNameIsComm
                        && !process.OwnershipAmbiguous;
                })
                .ToList();
            if (commRows.Count == 0) return;

            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(4000);
                foreach (string[] command in BuildProcessNameCommands(commRows.Select(process => process.Pid)))
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.IsCancellationRequested) break;
                    ProcessResult result;
                    try
                    {
                        result = await RunShellAsync(serial, command, 2000, budget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { continue; }
                    if (result == null || IsHdcFailure(result)) continue;
                    foreach (KeyValuePair<int, string> pair in ParseProcessNamesFromIndependentStreams(
                        result.Stdout, result.Stderr))
                    {
                        ProcessInfo process = commRows.FirstOrDefault(item => item.Pid == pair.Key);
                        if (process == null || !MatchesCompleteProcessName(process, pair.Value)) continue;
                        CompleteGenericProcessName(process, pair.Value);
                        if (string.IsNullOrWhiteSpace(process.BundleId)
                            && !string.Equals(process.OwnershipSource, "proc-inventory", StringComparison.Ordinal))
                            process.BundleId = BundleFromProcess(pair.Value);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
        }

        internal static Dictionary<int, string> ParseProcessNamesFromProc(string output)
        {
            Dictionary<int, string> names = new Dictionary<int, string>();
            HashSet<int> conflicts = new HashSet<int>();
            foreach (string raw in Lines(output))
            {
                Match match = Regex.Match(raw ?? "", @"^\s*PID=(?<pid>\d+)\s+CMDLINE=(?<command>.*)$", RegexOptions.CultureInvariant);
                if (!match.Success || !int.TryParse(match.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                    || pid <= 0) continue;
                string name = ProcessNameFromCommand(match.Groups["command"].Value);
                if (string.IsNullOrWhiteSpace(name) || conflicts.Contains(pid)) continue;
                if (names.TryGetValue(pid, out string existing) && !string.Equals(existing, name, StringComparison.Ordinal))
                {
                    names.Remove(pid);
                    conflicts.Add(pid);
                    continue;
                }
                names[pid] = name;
            }
            return names;
        }

        public Task<ProcessResult> LaunchAppAsync(string serial, string bundleId, CancellationToken token)
        {
            return LaunchAppAsync(serial, bundleId, null, token);
        }

        public async Task<ProcessResult> LaunchAppAsync(string serial, string bundleId, IEnumerable<int> harmonyUserIds, CancellationToken token)
        {
            return await LaunchAppAsync(serial, bundleId, harmonyUserIds, null, -1, token).ConfigureAwait(false);
        }

        public Task<ProcessResult> LaunchAppAsync(string serial, AppInfo app, CancellationToken token)
        {
            if (app == null)
                return Task.FromResult(new ProcessResult(1, "", "未选择有效的鸿蒙应用。"));
            if (app.IsProcessOnly && !app.CanAttemptLaunch)
                return Task.FromResult(new ProcessResult(1, "", "该鸿蒙目标没有独立启动入口，只能选择运行中的真实进程采集。"));
            IEnumerable<int> rawUsers = app.HarmonyUserId >= 0
                ? new[] { app.HarmonyUserId }
                : (app.HarmonyUserIds ?? new List<int>())
                    .Concat((app.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                        .Where(delegate(HarmonyLaunchEntryInfo entry)
                        {
                            return entry != null && entry.HarmonyUserId >= 0;
                        })
                        .Select(delegate(HarmonyLaunchEntryInfo entry) { return entry.HarmonyUserId; }));
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
            if (app.HarmonyUserId < 0 && selectedUsers.Count == 0)
            {
                return Task.FromResult(new ProcessResult(
                    1,
                    "",
                    "鸿蒙应用的用户作用域不可读，不会执行无范围启动；请刷新用户资料或直接选择真实进程。"));
            }
            return LaunchAppAsync(serial, app.BundleId, selectedUsers, app.HarmonyLaunchEntries,
                app.HarmonyAppIndex, token);
        }

        private async Task<ProcessResult> LaunchAppAsync(
            string serial,
            string bundleId,
            IEnumerable<int> harmonyUserIds,
            IEnumerable<HarmonyLaunchEntryInfo> harmonyLaunchEntries,
            int harmonyAppIndex,
            CancellationToken token)
        {
            if (!ValidHarmonyApplicationBundle(bundleId)) return new ProcessResult(1, "", "鸿蒙应用包名无效。");
            List<int> users = (harmonyUserIds ?? Enumerable.Empty<int>())
                .Where(delegate(int userId) { return userId >= 0; })
                .Distinct()
                .OrderBy(delegate(int userId) { return userId; })
                .ToList();
            if (users.Count == 0)
                return new ProcessResult(1, "", "鸿蒙应用的用户作用域不可读，不会执行无范围启动。");

            List<HarmonyLaunchEntryInfo> knownEntries = (harmonyLaunchEntries ?? Enumerable.Empty<HarmonyLaunchEntryInfo>())
                .Where(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return entry != null && !string.IsNullOrWhiteSpace(entry.Ability);
                })
                .GroupBy(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return (entry.Module ?? "") + "|" + entry.Ability + "|"
                        + entry.HarmonyUserId.ToString(CultureInfo.InvariantCulture) + "|"
                        + entry.HarmonyAppIndex.ToString(CultureInfo.InvariantCulture);
                }, StringComparer.Ordinal)
                .Select(delegate(IGrouping<string, HarmonyLaunchEntryInfo> group)
                {
                    return group.OrderByDescending(delegate(HarmonyLaunchEntryInfo entry) { return entry.IsUiEntry; }).First();
                })
                .OrderByDescending(delegate(HarmonyLaunchEntryInfo entry) { return entry.IsUiEntry; })
                .ToList();
            List<string> appIndexOptions = harmonyAppIndex > 0
                ? await ReadHarmonyAppIndexStartOptionsAsync(serial, token).ConfigureAwait(false)
                : new List<string>();
            if (harmonyAppIndex > 0 && appIndexOptions.Count == 0)
            {
                return new ProcessResult(1, "",
                    "设备未在 aa start 帮助中声明可用的鸿蒙分身索引参数（appIndex="
                    + harmonyAppIndex.ToString(CultureInfo.InvariantCulture)
                    + "）。为避免误启动主应用，请直接选择该分身对应的真实进程采集。\n"
                    + "如果厂商 HDC 支持分身启动，请更新设备工具后刷新；当前目标仍可通过 PID 采集。\n");
            }
            IEnumerable<string> startOptions = harmonyAppIndex > 0
                ? appIndexOptions
                : new[] { "" };
            List<ProcessResult> attempts = new List<ProcessResult>();
            foreach (int userId in users)
            {
                // Detail output belongs to the selected Harmony profile. Do
                // not carry an Ability discovered for one profile into the
                // next profile when the same Bundle is installed in both.
                // Harmony resolves Ability and module names with exact string
                // equality. Keep case-distinct entries available for retry.
                HashSet<string> abilities = new HashSet<string>(StringComparer.Ordinal);
                // Reuse the real entries discovered with the aggregate app
                // inventory before requiring a second `bm dump -n` command.
                // Some vendor builds expose `bm dump -a` but reject or omit
                // the per-bundle form.
                List<HarmonyLaunchEntryInfo> scopedKnownEntries = knownEntries
                    .Where(delegate(HarmonyLaunchEntryInfo candidate)
                    {
                        return (candidate.HarmonyUserId < 0 || candidate.HarmonyUserId == userId)
                            && (harmonyAppIndex <= 0
                                ? (harmonyAppIndex < 0 || candidate.HarmonyAppIndex < 0
                                    || candidate.HarmonyAppIndex == harmonyAppIndex)
                                : candidate.HarmonyAppIndex == harmonyAppIndex);
                    })
                    .ToList();
                bool hasExplicitAbilityEvidence = scopedKnownEntries.Count > 0;
                bool hasUiAbility = scopedKnownEntries.Any(delegate(HarmonyLaunchEntryInfo entry) { return entry.IsUiEntry; });
                foreach (HarmonyLaunchEntryInfo entry in scopedKnownEntries)
                {
                    abilities.Add(entry.Ability);
                    foreach (string appIndexOption in startOptions)
                    {
                        ProcessResult result = await RunHarmonyScopedStartAsync(serial,
                            BuildHarmonyAppIndexStartArgs(bundleId, entry.Module, entry.Ability, userId,
                                appIndexOption, harmonyAppIndex), token).ConfigureAwait(false);
                        attempts.Add(result);
                        if (IsSuccessfulCommand(result)) return result;
                    }
                }

                var detail = await ReadLaunchDetailAsync(serial, bundleId, userId, harmonyAppIndex, token).ConfigureAwait(false);
                if (detail.Result != null) attempts.Add(detail.Result);
                List<HarmonyLaunchEntryPoint> detailEntries = detail.Entries
                    .Where(delegate(HarmonyLaunchEntryPoint candidate) { return candidate != null && !string.IsNullOrWhiteSpace(candidate.Ability); })
                    .OrderByDescending(delegate(HarmonyLaunchEntryPoint candidate) { return candidate.IsUiEntry; })
                    .ToList();
                hasExplicitAbilityEvidence = hasExplicitAbilityEvidence || detailEntries.Count > 0;
                hasUiAbility = hasUiAbility || detailEntries.Any(delegate(HarmonyLaunchEntryPoint entry) { return entry.IsUiEntry; });
                foreach (HarmonyLaunchEntryPoint entry in detailEntries)
                {
                    abilities.Add(entry.Ability);
                    foreach (string appIndexOption in startOptions)
                    {
                        ProcessResult result = await RunHarmonyScopedStartAsync(serial,
                            BuildHarmonyAppIndexStartArgs(bundleId, entry.Module, entry.Ability, userId,
                                appIndexOption, harmonyAppIndex), token).ConfigureAwait(false);
                        attempts.Add(result);
                        if (IsSuccessfulCommand(result)) return result;
                    }
                }
                foreach (string ability in abilities)
                {
                    foreach (string appIndexOption in startOptions)
                    {
                        ProcessResult result = await RunHarmonyScopedStartAsync(serial,
                            BuildHarmonyAppIndexStartArgs(bundleId, "", ability, userId,
                                appIndexOption, harmonyAppIndex), token).ConfigureAwait(false);
                        attempts.Add(result);
                        if (IsSuccessfulCommand(result)) return result;
                    }
                }
                // A clone index must never fall through to an unscoped or
                // Android compatibility launch. Those commands can start the
                // primary instance and would make the selected row lie.
                if (harmonyAppIndex > 0) continue;
                // A target with only real non-UI Ability evidence must not be
                // guessed as a normal launcher package after those entries
                // fail. Package/Android fallbacks are reserved for UI-capable
                // targets or targets with no Ability metadata at all.
                if (hasExplicitAbilityEvidence && !hasUiAbility)
                    continue;
                ProcessResult packageStart = await RunHarmonyScopedStartAsync(serial,
                    AbilityStartArgs(bundleId, "", "", userId), token).ConfigureAwait(false);
                attempts.Add(packageStart);
                if (IsSuccessfulCommand(packageStart)) return packageStart;

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
                }

                ProcessResult androidStart = await RunShellAsync(serial, AndroidPackageStartArgs(bundleId, userId), 15000, token).ConfigureAwait(false);
                attempts.Add(androidStart);
                if (IsSuccessfulCommand(androidStart)) return androidStart;
                // Monkey launches in the current user and has no documented
                // --user selector. A failed scoped am start must remain failed.
            }
            return CombineLaunchFailures(attempts);
        }

        private async Task<(ProcessResult Result, List<HarmonyLaunchEntryPoint> Entries)> ReadLaunchDetailAsync(
            string serial, string bundleId, int userId, int harmonyAppIndex, CancellationToken token)
        {
            List<ProcessResult> scopedAttempts = new List<ProcessResult>();
            if (userId >= 0)
            {
                string value = userId.ToString(CultureInfo.InvariantCulture);
                // Use only documented Bundle Manager profile selectors.
                foreach (string option in new[] { "-u", "--user-id" })
                {
                    ProcessResult scoped = await RunShellAsync(serial, new[] { "bm", "dump", "-n", bundleId, option, value }, 12000, token).ConfigureAwait(false);
                    if (scoped == null || IsHdcFailure(scoped)) continue;
                    scopedAttempts.Add(scoped);
                    List<HarmonyLaunchEntryPoint> entries = ParseLaunchEntryPointsFromIndependentStreams(
                        scoped.Stdout, scoped.Stderr, bundleId, userId, harmonyAppIndex);
                    if (entries.Count > 0) return (scoped, entries);
                }

                // Harmony user 0 is not necessarily the foreground account.
                // Keep every explicit profile scoped, even when flags fail.
                // Combined diagnostics may be truncated. They must never be
                // reparsed as source records after their scope was rejected.
                return (CombineLaunchFailures(scopedAttempts), new List<HarmonyLaunchEntryPoint>());
            }

            return (new ProcessResult(1, "", "鸿蒙应用的用户作用域不可读。"), new List<HarmonyLaunchEntryPoint>());
        }

        private async Task<List<string>> ReadHarmonyAppIndexStartOptionsAsync(string serial, CancellationToken token)
        {
            List<string[]> commands = new List<string[]>
            {
                new[] { "aa", "start", "--help" },
                new[] { "aa", "help", "start" }
            };
            foreach (string[] command in commands)
            {
                ProcessResult result;
                try
                {
                    result = await RunShellAsync(serial, command, AppIndexHelpTimeoutMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                if (result == null || IsHdcFailure(result)) continue;
                List<string> options = ParseHarmonyAppIndexStartOptions(
                    (result.Stdout ?? "") + "\n" + (result.Stderr ?? ""));
                if (options.Count > 0) return options;
            }
            return new List<string>();
        }

        internal static List<string> ParseHarmonyAppIndexStartOptions(string output)
        {
            List<string> options = new List<string>();
            foreach (string line in Lines(output))
            {
                if (Regex.IsMatch(line ?? "",
                    @"(?i)(?:unknown|unsupported|invalid|unrecognized|not recognized).{0,40}option",
                    RegexOptions.CultureInvariant)) continue;
                foreach (string option in HarmonyAppIndexStartOptionNames)
                {
                    if (!Regex.IsMatch(line ?? "",
                        @"(?<![A-Za-z0-9_-])" + Regex.Escape(option) + @"(?![A-Za-z0-9_-])",
                        RegexOptions.CultureInvariant)) continue;
                    if (!options.Contains(option, StringComparer.Ordinal)) options.Add(option);
                }
            }
            return options;
        }

        internal static string[] BuildHarmonyAppIndexStartArgs(
            string bundleId, string module, string ability, int userId, string appIndexOption, int appIndex)
        {
            List<string> args = AbilityStartArgs(bundleId, module, ability, userId).ToList();
            if (!string.IsNullOrWhiteSpace(appIndexOption) && appIndex >= 0)
            {
                args.Add(appIndexOption);
                args.Add(appIndex.ToString(CultureInfo.InvariantCulture));
            }
            return args.ToArray();
        }

        private static string[] AbilityStartArgs(string bundleId, string module, string ability, int userId)
        {
            List<string> args = new List<string> { "aa", "start" };
            // Upper-case -U means URI, not user ID.
            if (userId >= 0) { args.Add("-u"); args.Add(userId.ToString(CultureInfo.InvariantCulture)); }
            args.Add("-b"); args.Add(bundleId);
            if (!string.IsNullOrWhiteSpace(module)) { args.Add("-m"); args.Add(module); }
            if (!string.IsNullOrWhiteSpace(ability)) { args.Add("-a"); args.Add(ability); }
            return args.ToArray();
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
                    string component = ParseAndroidLaunchComponent(bundleId, result.Stdout);
                    if (string.IsNullOrWhiteSpace(component))
                        component = ParseAndroidLaunchComponent(bundleId, result.Stderr);
                    if (!string.IsNullOrWhiteSpace(component)) return component;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return "";
        }

        public async Task HydrateAppIconsAsync(string serial, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (apps == null || apps.Count == 0 || maxIcons <= 0) return;
            Directory.CreateDirectory(HarmonyIconCacheDir());

            Dictionary<string, string> iconsByScope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppInfo app in apps)
            {
                if (app == null || !DevicePlatformNames.IsHarmony(app.Platform)
                    || string.IsNullOrWhiteSpace(app.BundleId)) continue;
                string cached = CachedHarmonyIconPath(serial, app);
                app.IconPath = cached;
                iconsByScope[HarmonyIconScopeKey(app.BundleId, app.HarmonyUserId, app.HarmonyAppIndex)] = cached;
            }
            ApplyHarmonyIconPathsToProcesses(iconsByScope, processes);

            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(_iconHydrationBudgetMs);
                int attempted = 0;
                HashSet<string> attemptedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AppInfo app in apps
                    .Where(delegate(AppInfo item)
                    {
                        return item != null && DevicePlatformNames.IsHarmony(item.Platform)
                            && !string.IsNullOrWhiteSpace(item.BundleId);
                    })
                    .OrderByDescending(delegate(AppInfo item) { return item.Recommended; })
                    .ThenBy(delegate(AppInfo item) { return item.BundleId; })
                    .ThenBy(delegate(AppInfo item) { return item.HarmonyUserId; })
                    .ThenBy(delegate(AppInfo item) { return item.HarmonyAppIndex; }))
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    try
                    {
                        string scopeKey = HarmonyIconScopeKey(app.BundleId, app.HarmonyUserId, app.HarmonyAppIndex);
                        if (!string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath)) continue;
                        if (!attemptedScopes.Add(scopeKey)) continue;
                        if (attempted >= maxIcons) break;
                        attempted++;

                        string iconPath = await ExtractHarmonyAppIconAsync(serial, app, budget.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(iconPath)) continue;
                        app.IconPath = iconPath;
                        iconsByScope[scopeKey] = iconPath;
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { }
                }
            }
            foreach (AppInfo app in apps)
            {
                if (app == null || !DevicePlatformNames.IsHarmony(app.Platform)) continue;
                string scopeKey = HarmonyIconScopeKey(app.BundleId, app.HarmonyUserId, app.HarmonyAppIndex);
                if (iconsByScope.TryGetValue(scopeKey, out string iconPath))
                    app.IconPath = iconPath;
            }
            ApplyHarmonyIconPathsToProcesses(iconsByScope, processes);
        }

        internal static IReadOnlyList<string[]> BuildIconMetadataCommands(string bundleId, int harmonyUserId)
        {
            List<string[]> commands = new List<string[]>();
            if (!ValidHarmonyApplicationBundle(bundleId)) return commands;
            string cleanBundle = bundleId.Trim();
            if (harmonyUserId >= 0)
            {
                string value = harmonyUserId.ToString(CultureInfo.InvariantCulture);
                commands.Add(new[] { "bm", "dump", "-n", cleanBundle, "-u", value });
                commands.Add(new[] { "bm", "dump", "-n", cleanBundle, "--user-id", value });
            }
            else
            {
                // Unknown profile scope may still be used for metadata-only
                // display, but it must never be silently turned into a known
                // profile or used to launch the application.
                commands.Add(new[] { "bm", "dump", "-n", cleanBundle });
            }
            return commands;
        }

        internal static HarmonyIconMetadata ParseHarmonyIconMetadata(string output, string expectedBundleId)
        {
            HarmonyIconMetadata metadata = new HarmonyIconMetadata();
            string text = output ?? "";
            if (!string.IsNullOrWhiteSpace(expectedBundleId))
            {
                MatchCollection bundleMatches = Regex.Matches(
                    text,
                    @"(?i)['""]?(?:bundleName|bundleId|bundle_id|bundle|packageName|package_name)['""]?\s*[:=]\s*['""]?(?<bundle>[A-Za-z][A-Za-z0-9_.]*)",
                    RegexOptions.CultureInvariant);
                if (bundleMatches.Count > 0 && bundleMatches.Cast<Match>().Any(delegate(Match match)
                {
                    return !string.Equals(match.Groups["bundle"].Value.Trim(), expectedBundleId.Trim(), StringComparison.OrdinalIgnoreCase);
                }))
                    return metadata;
            }

            MatchCollection fields = Regex.Matches(
                text,
                @"(?i)(?:['""]?(?<key>iconPath|icon_path|iconFile|icon_file|iconFilePath|icon_file_path|applicationIconPath|appIconPath|hapPath|hap_path|hapFile|hap_file|hapFilePath|hap_file_path|modulePath|module_path|codePath|code_path|bundlePath|bundle_path|packagePath|package_path|installPath|install_path|icon|iconName|icon_name|iconResource|icon_resource|appIcon|app_icon|applicationIcon|application_icon)['""]?)\s*[:=]\s*(?:['""](?<value>[^'""]+)['""]|(?<value>[^\s,}\]]+))",
                RegexOptions.CultureInvariant);
            foreach (Match field in fields)
            {
                string key = field.Groups["key"].Value;
                string value = CleanHarmonyIconValue(field.Groups["value"].Value);
                if (value.Length == 0) continue;
                string lowerKey = key.ToLowerInvariant();
                if (lowerKey.Contains("hap") || lowerKey.Contains("modulepath") || lowerKey.Contains("codepath")
                    || lowerKey.Contains("bundlepath") || lowerKey.Contains("packagepath") || lowerKey.Contains("installpath"))
                {
                    if (IsHarmonyArchivePath(value)) AddDistinct(metadata.HapPaths, value);
                    continue;
                }
                if (IsHarmonyIconPath(value))
                {
                    AddDistinct(metadata.IconPaths, value);
                    continue;
                }
                if (lowerKey.Contains("icon") && LooksLikeHarmonyIconEntry(value))
                    AddDistinct(metadata.IconEntries, NormalizeArchiveEntry(value));
            }
            return metadata;
        }

        internal static string SelectHarmonyIconEntry(IEnumerable<string> entries, IEnumerable<string> iconReferences)
        {
            List<string> usable = (entries ?? Enumerable.Empty<string>())
                .Select(NormalizeArchiveEntry)
                .Where(LooksLikeHarmonyIconEntry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string reference in iconReferences ?? Enumerable.Empty<string>())
            {
                string normalizedReference = NormalizeArchiveEntry(reference);
                if (normalizedReference.Length == 0) continue;
                string exact = usable.FirstOrDefault(delegate(string entry)
                {
                    return string.Equals(entry, normalizedReference, StringComparison.OrdinalIgnoreCase)
                        || entry.EndsWith("/" + normalizedReference, StringComparison.OrdinalIgnoreCase)
                        || (Path.GetExtension(normalizedReference).Length == 0
                            && Path.GetFileNameWithoutExtension(entry).Equals(
                                Path.GetFileName(normalizedReference), StringComparison.OrdinalIgnoreCase));
                });
                if (!string.IsNullOrWhiteSpace(exact)) return exact;
            }
            return usable
                .Where(delegate(string entry) { return HarmonyIconEntryScore(entry) > 0; })
                .OrderByDescending(HarmonyIconEntryScore)
                .ThenByDescending(delegate(string entry) { return entry.Length; })
                .FirstOrDefault() ?? "";
        }

        internal static bool IsValidHarmonyIcon(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                FileInfo file = new FileInfo(path);
                if (file.Length < 16 || file.Length > 16 * 1024 * 1024) return false;
                byte[] header = new byte[12];
                using (FileStream stream = File.OpenRead(path))
                {
                    int read = stream.Read(header, 0, header.Length);
                    if (read != header.Length) return false;
                }
                bool png = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                    && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
                bool webp = header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F'
                    && header[3] == (byte)'F' && header[8] == (byte)'W' && header[9] == (byte)'E'
                    && header[10] == (byte)'B' && header[11] == (byte)'P';
                return png || webp;
            }
            catch { return false; }
        }

        private async Task<string> ExtractHarmonyAppIconAsync(string serial, AppInfo app, CancellationToken token)
        {
            HarmonyIconMetadata metadata = new HarmonyIconMetadata();
            foreach (string[] command in BuildIconMetadataCommands(app.BundleId, app.HarmonyUserId))
            {
                ProcessResult result;
                try
                {
                    result = await RunShellAsync(serial, command, 10000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                if (result == null || IsHdcFailure(result)) continue;
                metadata.Merge(ParseHarmonyIconMetadata(result.Stdout, app.BundleId));
                metadata.Merge(ParseHarmonyIconMetadata(result.Stderr, app.BundleId));
                foreach (string remotePath in metadata.IconPaths)
                {
                    string received = await TryReceiveHarmonyFileAsync(serial, remotePath, token).ConfigureAwait(false);
                    string committed = CommitHarmonyIcon(received, serial, app.HarmonyUserId, app.HarmonyAppIndex, app.BundleId);
                    if (!string.IsNullOrWhiteSpace(committed)) return committed;
                    TryDeleteHarmonyIconFile(received);
                }
                foreach (string hapPath in metadata.HapPaths)
                {
                    string archive = await TryReceiveHarmonyFileAsync(serial, hapPath, token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(archive)) continue;
                    string committed = ExtractHarmonyArchiveIcon(archive, metadata.IconEntries, serial, app.HarmonyUserId,
                        app.HarmonyAppIndex, app.BundleId);
                    TryDeleteHarmonyIconFile(archive);
                    if (!string.IsNullOrWhiteSpace(committed)) return committed;
                }
            }
            return "";
        }

        private static async Task<string> TryReceiveHarmonyFileAsync(string serial, string remotePath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(remotePath) || !(IsHarmonyIconPath(remotePath) || IsHarmonyArchivePath(remotePath))) return "";
            string temp = Path.Combine(HarmonyIconCacheDir(serial), "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(HarmonyIconCacheDir(serial));
                ProcessResult result = await ProcessRunner.RunAsync(
                    RuntimeTools.HdcExecutable,
                    FileReceiveArgs(serial, remotePath, temp),
                    12000,
                    token).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(temp))
                {
                    TryDeleteHarmonyIconFile(temp);
                    return "";
                }
                FileInfo file = new FileInfo(temp);
                if (file.Length <= 0 || file.Length > 128 * 1024 * 1024)
                {
                    TryDeleteHarmonyIconFile(temp);
                    return "";
                }
                return temp;
            }
            catch (OperationCanceledException) { throw; }
            catch { TryDeleteHarmonyIconFile(temp); return ""; }
        }

        private static string ExtractHarmonyArchiveIcon(string archivePath, IEnumerable<string> iconReferences, string serial,
            int harmonyUserId, int harmonyAppIndex, string bundleId)
        {
            string iconTemp = "";
            try
            {
                if (!File.Exists(archivePath) || new FileInfo(archivePath).Length > 128 * 1024 * 1024) return "";
                using (FileStream stream = File.OpenRead(archivePath))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    string selected = SelectHarmonyIconEntry(archive.Entries.Select(entry => entry.FullName), iconReferences);
                    ZipArchiveEntry entry = archive.Entries.FirstOrDefault(item =>
                        string.Equals(NormalizeArchiveEntry(item.FullName), selected, StringComparison.OrdinalIgnoreCase));
                    if (entry == null || entry.Length <= 0 || entry.Length > 16 * 1024 * 1024) return "";
                    iconTemp = Path.Combine(HarmonyIconCacheDir(serial), "." + Guid.NewGuid().ToString("N") + ".icon");
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(iconTemp))
                    {
                        input.CopyTo(output, 81920);
                    }
                }
                return CommitHarmonyIcon(iconTemp, serial, harmonyUserId, harmonyAppIndex, bundleId);
            }
            catch { return ""; }
            finally { TryDeleteHarmonyIconFile(iconTemp); }
        }

        private static void ApplyHarmonyIconPathsToProcesses(Dictionary<string, string> iconsByScope, IList<ProcessInfo> processes)
        {
            if (iconsByScope == null || processes == null) return;
            foreach (ProcessInfo process in processes)
            {
                if (process == null || !DevicePlatformNames.IsHarmony(process.Platform)
                    || string.IsNullOrWhiteSpace(process.BundleId)) continue;
                string scopeKey = HarmonyIconScopeKey(process.BundleId, process.HarmonyUserId, process.HarmonyAppIndex);
                if (iconsByScope.TryGetValue(scopeKey, out string iconPath)) process.IconPath = iconPath;
                else process.IconPath = "";
            }
        }

        private static string HarmonyIconScopeKey(string bundleId, int harmonyUserId, int harmonyAppIndex = -1)
        {
            return (bundleId ?? "").Trim() + "\n"
                + Math.Max(-1, harmonyUserId).ToString(CultureInfo.InvariantCulture) + "\n"
                + Math.Max(-1, harmonyAppIndex).ToString(CultureInfo.InvariantCulture);
        }

        private static string CommitHarmonyIcon(string sourcePath, string serial, int harmonyUserId,
            int harmonyAppIndex, string bundleId)
        {
            try
            {
                if (!IsValidHarmonyIcon(sourcePath) || !ValidHarmonyApplicationBundle(bundleId)) return "";
                string extension = IsPngFile(sourcePath) ? ".png" : ".webp";
                string output = HarmonyIconPath(serial, harmonyUserId, harmonyAppIndex, bundleId, extension);
                Directory.CreateDirectory(HarmonyIconCacheDir(serial));
                File.Move(sourcePath, output, true);
                string other = HarmonyIconPath(serial, harmonyUserId, harmonyAppIndex, bundleId, extension == ".png" ? ".webp" : ".png");
                TryDeleteHarmonyIconFile(other);
                return output;
            }
            catch { return ""; }
        }

        private static string CachedHarmonyIconPath(string serial, AppInfo app)
        {
            foreach (string extension in new[] { ".png", ".webp" })
            {
                string path = HarmonyIconPath(serial, app == null ? -1 : app.HarmonyUserId,
                    app == null ? -1 : app.HarmonyAppIndex,
                    app == null ? "" : app.BundleId, extension);
                if (IsValidHarmonyIcon(path)) return path;
                TryDeleteHarmonyIconFile(path);
            }
            return "";
        }

        private static string HarmonyIconPath(string serial, int harmonyUserId, int harmonyAppIndex,
            string bundleId, string extension)
        {
            string safe = Regex.Replace(bundleId ?? "", @"[^A-Za-z0-9._-]+", "_");
            if (safe.Length == 0) safe = "app";
            string user = harmonyUserId >= 0
                ? harmonyUserId.ToString(CultureInfo.InvariantCulture)
                : "unknown-user";
            // Keep the pre-appIndex cache name for ordinary Harmony targets so
            // existing icons remain readable. A concrete appIndex gets its own
            // scope because two instances can share the same user and Bundle.
            string appIndex = harmonyAppIndex >= 0
                ? "-" + harmonyAppIndex.ToString(CultureInfo.InvariantCulture)
                : "";
            return Path.Combine(HarmonyIconCacheDir(serial), user + appIndex + "-" + safe + extension);
        }

        private static string HarmonyIconCacheDir(string serial = "")
        {
            string safeSerial = Regex.Replace(serial ?? "", @"[^A-Za-z0-9._-]+", "_");
            if (safeSerial.Length == 0) safeSerial = "unknown-device";
            return Path.Combine(RuntimeTools.DataDirectory, "app-icons", safeSerial);
        }

        internal static IReadOnlyList<string> FileReceiveArgs(string serial, string remotePath, string localPath)
        {
            List<string> args = new List<string>();
            if (!string.IsNullOrWhiteSpace(serial))
            {
                args.Add("-t");
                args.Add(serial);
            }
            args.Add("file");
            args.Add("recv");
            args.Add(remotePath ?? "");
            args.Add(localPath ?? "");
            return args;
        }

        private static bool IsPngFile(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] header = new byte[8];
                    return stream.Read(header, 0, header.Length) == header.Length
                        && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                        && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
                }
            }
            catch { return false; }
        }

        private static bool IsHarmonyIconPath(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower.StartsWith("/") && (lower.EndsWith(".png") || lower.EndsWith(".webp"));
        }

        private static bool IsHarmonyArchivePath(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower.StartsWith("/") && (lower.EndsWith(".hap") || lower.EndsWith(".hsp") || lower.EndsWith(".apk"));
        }

        private static bool LooksLikeHarmonyIconEntry(string value)
        {
            string lower = NormalizeArchiveEntry(value).ToLowerInvariant();
            return lower.Length > 0 && (lower.EndsWith(".png") || lower.EndsWith(".webp")
                || lower.Contains("/media/") || lower.StartsWith("resources/"));
        }

        private static string NormalizeArchiveEntry(string value)
        {
            return (value ?? "").Trim().Trim('"', '\'', ',', ';').Replace('\\', '/').TrimStart('/');
        }

        private static string CleanHarmonyIconValue(string value)
        {
            return (value ?? "").Trim().Trim('"', '\'', ',', ';');
        }

        private static int HarmonyIconEntryScore(string entry)
        {
            string lower = NormalizeArchiveEntry(entry).ToLowerInvariant();
            if (lower.EndsWith(".9.png") || lower.Contains("notification") || lower.Contains("notify")
                || lower.Contains("splash") || lower.Contains("status") || lower.Contains("share")) return 0;
            int score = 10;
            if (lower.Contains("/media/")) score += 60;
            if (lower.Contains("icon")) score += 100;
            if (lower.Contains("launcher")) score += 80;
            if (lower.EndsWith(".png")) score += 10;
            return score;
        }

        private static void AddDistinct(ICollection<string> target, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value)) target.Add(value);
        }

        private static void TryDeleteHarmonyIconFile(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        internal static List<AppInfo> ParseApps(string output)
        {
            List<AppInfo> apps = new List<AppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string textOutput = output ?? "";
            foreach (string json in ExtractJsonValues(output))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        CollectAppsFromJson(document.RootElement, apps, seen, "", false, true, -1, false, false);
                    }
                    textOutput = textOutput.Replace(json, "\n");
                }
                catch (JsonException) { }
            }
            // Indented Bundle Manager dumps do not provide JSON node
            // boundaries. Apply the same metadata boundary used by the
            // launch parser before any flat-text fallback can see fields
            // such as metadata.bundleName or metadata.abilityInfos.
            string discoveryTextOutput = RemoveLaunchTextMetadata(textOutput);
            bool insideAbilityCollection = false;
            int abilityIndent = -1;
            foreach (string raw in Lines(discoveryTextOutput))
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
                    @"(?i)(?:[""']?)(?:bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|bundle|packageName|package_name|packageId|package_id|package\s+name|package|modulePackage|module_package|module\s+package|applicationId|application_id|appId|app_id|appIdentifier|app_identifier)(?:[""']?)\s*[:=]\s*[""']?([A-Za-z][A-Za-z0-9_.]*)",
                    RegexOptions.CultureInvariant);
                if (keyed.Success)
                {
                    ParseHarmonyInstallEvidence(line, out bool hasSystemAppEvidence, out bool isSystemApp,
                        out bool isPreInstallApp, out string installSource);
                    AddApp(apps, seen, keyed.Groups[1].Value, "Bundle Manager 应用",
                        ExtractTextField(line, "appName|applicationName|appLabel|label|displayName|name"),
                        ExtractTextField(line, "versionName|versionCode|versionNumber|version"),
                        harmonyUserId: ParseEmbeddedHarmonyUserId(line),
                        harmonyAppIndex: ParseEmbeddedHarmonyAppIndex(line),
                        hasSystemAppEvidence: hasSystemAppEvidence,
                        isSystemApp: isSystemApp,
                        isPreInstallApp: isPreInstallApp,
                        installSource: installSource);
                }
                string scalar = line.Trim('"', '\'');
                if (ValidBundle(scalar)) AddApp(apps, seen, scalar);
                Match leadingBundle = Regex.Match(
                    line,
                    @"^(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:\s|[,;}]|$)",
                    RegexOptions.CultureInvariant);
                if (leadingBundle.Success) AddApp(apps, seen, leadingBundle.Groups["bundle"].Value, "Bundle Manager 应用");
            }
            ParseNamedBundleLines(discoveryTextOutput, apps, seen);
            // Indented/key-value Bundle Manager output does not have a JSON
            // node boundary. If it contains exactly one application, its
            // real Ability entries can still be attached safely without
            // assigning one app's launch target to another app.
            if (apps.Count == 1
                && (apps[0].HarmonyLaunchEntries == null || apps[0].HarmonyLaunchEntries.Count == 0))
                AddHarmonyLaunchEntries(apps[0], ToHarmonyLaunchEntries(ParseLaunchEntryPoints(discoveryTextOutput), apps[0].HarmonyUserId));
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
                || string.Equals(normalized, "bundleList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleRecords", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedBundles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "application", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applications", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applicationInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applicationInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applicationList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedApps", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedApplications", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "appInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "appInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "package", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packages", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageNames", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBundleRecordObjectMap(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName);
            // Singular applicationInfo/bundleInfo objects are ordinary record
            // shapes. Only explicit collection/map containers may promote a
            // dotless object key such as "launcher" to an application.
            return string.Equals(normalized, "bundleInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleInfoList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleRecords", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedBundles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "application", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applications", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applicationInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "applicationList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedApps", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "installedApplications", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "appInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "package", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packages", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "packageNames", StringComparison.OrdinalIgnoreCase);
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

        private static bool TryParseExplicitAbilityType(string value, out bool isUiEntry)
        {
            isUiEntry = true;
            string key;
            string fieldValue;
            if (!TryParseKeyValueField(value, out key, out fieldValue)) return false;
            return TryClassifyExplicitAbilityType(key, fieldValue, out isUiEntry);
        }

        private static bool TryClassifyExplicitAbilityType(string key, string value, out bool isUiEntry)
        {
            isUiEntry = true;
            string normalized = NormalizePropertyName(key);
            bool isExtensionType = string.Equals(normalized, "extensionType", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "extensionTypeName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "extensionAbilityType", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "extensionAbilityTypeName", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(normalized, "type", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "abilityType", StringComparison.OrdinalIgnoreCase)
                && !isExtensionType) return false;

            string type = (value ?? "").Trim().Trim(',', '"', '\'');
            if (string.Equals(type, "PAGE", StringComparison.OrdinalIgnoreCase)
                || (!isExtensionType && type == "1"))
            {
                isUiEntry = true;
                return true;
            }
            if (string.Equals(type, "SERVICE", StringComparison.OrdinalIgnoreCase) || type == "2"
                || string.Equals(type, "DATA", StringComparison.OrdinalIgnoreCase) || type == "3"
                || string.Equals(type, "FORM", StringComparison.OrdinalIgnoreCase) || type == "4"
                || string.Equals(type, "EXTENSION", StringComparison.OrdinalIgnoreCase) || type == "5"
                || (isExtensionType && type == "1"))
            {
                isUiEntry = false;
                return true;
            }
            if (IsNonUiExtensionAbilityType(type))
            {
                isUiEntry = false;
                return true;
            }
            return false;
        }

        private static bool IsNonUiExtensionAbilityType(string value)
        {
            string type = NormalizePropertyName((value ?? "").Trim().Trim(',', '"', '\''));
            if (int.TryParse(type, out int numericType))
            {
                return numericType == 0
                    || (numericType >= 6 && numericType <= 40)
                    || numericType == 255
                    || (numericType >= 256 && numericType <= 266)
                    || (numericType >= 269 && numericType <= 270)
                    || (numericType >= 300 && numericType <= 307)
                    || (numericType >= 400 && numericType <= 409)
                    || (numericType >= 500 && numericType <= 508)
                    || numericType == 510
                    || numericType == 511;
            }
            switch (type.ToUpperInvariant())
            {
                case "FORM":
                case "WORKSCHEDULER":
                case "INPUTMETHOD":
                case "ACCESSIBILITY":
                case "DATASHARE":
                case "FILESHARE":
                case "STATICSUBSCRIBER":
                case "WALLPAPER":
                case "BACKUP":
                case "WINDOW":
                case "ENTERPRISEADMIN":
                case "FILEACCESSEXTENSION":
                case "THUMBNAIL":
                case "PREVIEW":
                case "PRINT":
                case "SHARE":
                case "PUSH":
                case "VPN":
                case "DRIVER":
                case "ACTION":
                case "ADSSERVICE":
                case "EMBEDDEDUI":
                case "INSIGHTINTENTUI":
                case "PHOTOEDITOR":
                case "FENCE":
                case "CALLERINFOQUERY":
                case "ASSETACCELERATION":
                case "FORMEDIT":
                case "DISTRIBUTED":
                case "APPSERVICE":
                case "LIVEFORM":
                case "SELECTION":
                case "WEBNATIVEMESSAGING":
                case "FAULTLOG":
                case "NOTIFICATIONSUBSCRIBER":
                case "CRYPTO":
                case "PARTNERAGENT":
                case "AGENT":
                case "AGENTUI":
                case "MODULAROBJECT":
                case "UKEYAUTH":
                case "STATUSBARVIEW":
                case "AUTOFILLPASSWORD":
                case "APPACCOUNTAUTHORIZATION":
                case "UI":
                case "REMOTENOTIFICATION":
                case "REMOTELOCATION":
                case "VOIP":
                case "ACCOUNTLOGOUT":
                case "LIVEVIEWLOCKSCREEN":
                case "LIVEVIEWCARD":
                case "UISERVICE":
                case "ASSETCACHE":
                case "SYSDIALOGUSERAUTH":
                case "SYSDIALOGCOMMON":
                case "SYSDIALOGATOMICSERVICEPANEL":
                case "SYSDIALOGPOWER":
                case "SYSDIALOGMEETIMECALL":
                case "SYSDIALOGMEETIMECONTACT":
                case "SYSDIALOGMEETIMEMESSAGE":
                case "SYSDIALOGPRINT":
                case "SYSPICKERMEDIACONTROL":
                case "SYSPICKERSHARE":
                case "SYSPICKERMEETIMECONTACT":
                case "SYSPICKERMEETIMECALLLOG":
                case "SYSPICKERPHOTOPICKER":
                case "SYSPICKERCAMERA":
                case "SYSPICKERNAVIGATION":
                case "SYSPICKERAPPSELECTOR":
                case "SYSPICKERFILEPICKER":
                case "SYSPICKERAUDIOPICKER":
                case "SYSCOMMONUI":
                case "AUTOFILLSMART":
                case "SYSPICKERPHOTOEDITOR":
                case "SYSVISUAL":
                case "RECENTPHOTO":
                case "AWCWEBPAGE":
                case "AWCNEWSFEED":
                case "EMBEDDEDCASHIER":
                case "CONTENTEMBED":
                case "HMSACCOUNT":
                case "ADS":
                    return true;
                default:
                    return false;
            }
        }

        private static void ParseNamedBundleLines(string output, IList<AppInfo> apps, ISet<string> seen)
        {
            string bundle = "";
            int bundleIndent = -1;
            int bundleCollectionIndent = -1;
            int bundleItemIndent = -1;
            string module = "";
            bool insideAbilityCollection = false;
            bool insideNonUiAbilityCollection = false;
            int abilityIndent = -1;
            int entryFieldIndent = -1;
            string pendingAbility = "";
            bool pendingExplicitAbilityType = false;
            bool pendingExplicitAbilityIsUi = true;
            int currentHarmonyUserId = -1;
            int pendingHarmonyUserId = -1;
            int currentHarmonyAppIndex = -1;
            int pendingHarmonyAppIndex = -1;
            string[] lines = Lines(output).ToArray();
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex] ?? "";
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;

                if (insideAbilityCollection)
                {
                    if (indent > abilityIndent)
                    {
                        int fieldIndent = indent + (trimmed.StartsWith("- ", StringComparison.Ordinal) ? 2 : 0);
                        if (entryFieldIndent >= 0 && fieldIndent > entryFieldIndent)
                            continue;
                        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                        {
                            pendingAbility = "";
                            pendingExplicitAbilityType = false;
                            pendingExplicitAbilityIsUi = true;
                        }
                        if (entryFieldIndent < 0 || fieldIndent < entryFieldIndent)
                            entryFieldIndent = fieldIndent;
                        if (TryParseExplicitAbilityType(trimmed, out bool explicitIsUi))
                        {
                            AppInfo typedOwner = FindHarmonyTextApp(apps, bundle,
                                currentHarmonyUserId, currentHarmonyAppIndex);
                            if (typedOwner != null && typedOwner.HarmonyLaunchEntries != null
                                && !string.IsNullOrWhiteSpace(pendingAbility))
                            {
                                HarmonyLaunchEntryInfo typedEntry = typedOwner.HarmonyLaunchEntries.LastOrDefault(delegate(HarmonyLaunchEntryInfo entry)
                                {
                                    return entry != null && entry.HarmonyUserId == currentHarmonyUserId
                                        && entry.HarmonyAppIndex == currentHarmonyAppIndex
                                        && string.Equals(entry.Module ?? "", module ?? "", StringComparison.Ordinal)
                                        && string.Equals(entry.Ability, pendingAbility, StringComparison.Ordinal);
                                });
                                if (typedEntry != null)
                                {
                                    typedEntry.IsUiEntry = explicitIsUi;
                                    RecomputeHarmonyLaunchFlags(typedOwner);
                                }
                                else
                                {
                                    pendingExplicitAbilityType = true;
                                    pendingExplicitAbilityIsUi = explicitIsUi;
                                }
                            }
                            else
                            {
                                pendingExplicitAbilityType = true;
                                pendingExplicitAbilityIsUi = explicitIsUi;
                            }
                            continue;
                        }
                        string abilityValue;
                        bool abilityIsUi;
                        if (TryParseAbilityNameField(trimmed, true, out abilityValue, out abilityIsUi)
                            && !string.IsNullOrWhiteSpace(bundle))
                        {
                            AppInfo owner = FindHarmonyTextApp(apps, bundle,
                                currentHarmonyUserId, currentHarmonyAppIndex);
                            AddHarmonyLaunchEntries(owner, new[]
                            {
                                new HarmonyLaunchEntryInfo
                                {
                                    Module = module,
                                    Ability = abilityValue,
                                    IsUiEntry = pendingExplicitAbilityType
                                        ? pendingExplicitAbilityIsUi
                                        : !insideNonUiAbilityCollection && abilityIsUi,
                                    HarmonyUserId = currentHarmonyUserId,
                                    HarmonyAppIndex = currentHarmonyAppIndex
                                }
                            });
                            pendingAbility = abilityValue;
                            pendingExplicitAbilityType = false;
                            pendingExplicitAbilityIsUi = true;
                        }
                        // Nested fields cannot create another application or
                        // reset the enclosing Ability collection's identity.
                        continue;
                    }
                    insideAbilityCollection = false;
                    insideNonUiAbilityCollection = false;
                    entryFieldIndent = -1;
                }

                string collectionKey;
                string collectionValue;
                if (TryParseKeyValueField(trimmed, out collectionKey, out collectionValue)
                    && IsBundleRecordCollection(collectionKey))
                {
                    // Keep the outer application container as the list
                    // boundary. Nested applicationInfo/packages/permission
                    // fields belong to the current record and must not reset
                    // the direct-item depth used below.
                    if (bundleCollectionIndent < 0 || indent <= bundleCollectionIndent)
                    {
                        bundleCollectionIndent = indent;
                        bundleItemIndent = -1;
                    }
                    continue;
                }
                if (bundleCollectionIndent >= 0 && indent <= bundleCollectionIndent)
                {
                    bundleCollectionIndent = -1;
                    bundleItemIndent = -1;
                }

                // An object map has a named child before any list item. Once
                // that shape is observed, nested permission/module arrays are
                // never eligible to become application records.
                if (bundleCollectionIndent >= 0 && indent > bundleCollectionIndent
                    && bundleItemIndent < 0
                    && !trimmed.StartsWith("- ", StringComparison.Ordinal))
                    bundleItemIndent = -2;

                if (bundleCollectionIndent >= 0 && indent > bundleCollectionIndent
                    && trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (bundleItemIndent < 0) bundleItemIndent = indent;
                    if (indent == bundleItemIndent)
                    {
                        string scalarItem = trimmed.Substring(2).Trim().Trim(',', '"', '\'');
                        if (ValidHarmonyApplicationBundle(scalarItem)
                            && scalarItem.IndexOf(':') < 0
                            && scalarItem.IndexOf('=') < 0)
                            AddApp(apps, seen, scalarItem, "Bundle Manager 应用",
                                harmonyUserId: currentHarmonyUserId,
                                harmonyAppIndex: currentHarmonyAppIndex);
                    }
                }

                Match appIndexField = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:appIndex|app_index|applicationIndex|application_index)\s*[:=]\s*[""']?(?<index>-?\d+)",
                    RegexOptions.CultureInvariant);
                if (appIndexField.Success)
                {
                    int parsedAppIndex = ParseExplicitHarmonyAppIndex(appIndexField.Groups["index"].Value);
                    if (parsedAppIndex >= 0)
                    {
                        bool startsNextBundleRecord = !string.IsNullOrWhiteSpace(bundle)
                            && bundleIndent >= 0 && indent <= bundleIndent
                            && HasFollowingBundleRecord(lines, lineIndex + 1, bundleIndent);
                        if (string.IsNullOrWhiteSpace(bundle) || startsNextBundleRecord)
                        {
                            pendingHarmonyAppIndex = parsedAppIndex;
                            if (startsNextBundleRecord)
                            {
                                currentHarmonyAppIndex = -1;
                                currentHarmonyUserId = -1;
                                module = "";
                                insideAbilityCollection = false;
                                insideNonUiAbilityCollection = false;
                                abilityIndent = -1;
                                entryFieldIndent = -1;
                            }
                        }
                        else
                        {
                            currentHarmonyAppIndex = parsedAppIndex;
                            AddApp(apps, seen, bundle, "Bundle Manager 应用",
                                harmonyUserId: currentHarmonyUserId,
                                harmonyAppIndex: currentHarmonyAppIndex);
                        }
                    }
                    continue;
                }

                if (TryParseKeyValueField(trimmed, out collectionKey, out collectionValue)
                    && IsAbilityInfoCollection(collectionKey))
                {
                    insideAbilityCollection = true;
                    insideNonUiAbilityCollection = !IsUiAbilityField(collectionKey);
                    abilityIndent = indent;
                    entryFieldIndent = -1;
                    pendingAbility = "";
                    pendingExplicitAbilityType = false;
                    pendingExplicitAbilityIsUi = true;
                    string inlineAbility = collectionValue.Trim().Trim(',', '"', '\'');
                    if (!string.IsNullOrWhiteSpace(bundle) && ValidEntryToken(inlineAbility))
                    {
                        AppInfo inlineApp = FindHarmonyTextApp(apps, bundle,
                            currentHarmonyUserId, currentHarmonyAppIndex);
                        AddHarmonyLaunchEntries(inlineApp, new[]
                        {
                            new HarmonyLaunchEntryInfo
                            {
                                Module = module,
                                Ability = inlineAbility,
                                IsUiEntry = !insideNonUiAbilityCollection,
                                HarmonyUserId = currentHarmonyUserId,
                                HarmonyAppIndex = currentHarmonyAppIndex
                            }
                        });
                    }
                    continue;
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
                    AppInfo owner = FindHarmonyTextApp(apps, bundle,
                        currentHarmonyUserId, currentHarmonyAppIndex);
                    AddHarmonyLaunchEntries(owner, new[]
                    {
                        new HarmonyLaunchEntryInfo
                        {
                            Module = module,
                            Ability = directAbilityValue,
                            IsUiEntry = directAbilityIsUi,
                            HarmonyUserId = currentHarmonyUserId,
                            HarmonyAppIndex = currentHarmonyAppIndex
                        }
                    });
                    continue;
                }

                Match bundleMatch = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|bundle|packageName|package_name|packageId|package_id|package\s+name|package|modulePackage|module_package|module\s+package|applicationId|application_id|appId|app_id|appIdentifier|app_identifier)\s*[:=]\s*['""]?(?<bundle>[A-Za-z][A-Za-z0-9_.]*)",
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
                        entryFieldIndent = -1;
                        pendingAbility = "";
                        pendingExplicitAbilityType = false;
                        pendingExplicitAbilityIsUi = true;
                        currentHarmonyUserId = pendingHarmonyUserId;
                        pendingHarmonyUserId = -1;
                        currentHarmonyAppIndex = pendingHarmonyAppIndex;
                        pendingHarmonyAppIndex = -1;
                    }
                    AddApp(apps, seen, bundle, "Bundle Manager 应用",
                        harmonyUserId: currentHarmonyUserId,
                        harmonyAppIndex: currentHarmonyAppIndex);
                    continue;
                }

                Match name = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:appName|applicationName|appLabel|label|displayName)\s*[:=]\s*['""]?(?<name>[^,'""\r\n}]+)",
                    RegexOptions.CultureInvariant);
                if (name.Success && !string.IsNullOrWhiteSpace(bundle))
                {
                    AddApp(apps, seen, bundle, "Bundle Manager 应用", name: name.Groups["name"].Value.Trim(),
                        harmonyUserId: currentHarmonyUserId,
                        harmonyAppIndex: currentHarmonyAppIndex);
                    continue;
                }
                Match leadingUser = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?<field>userId|user_id|uid|user)\s*[:=]\s*['""']?(?<id>u\d+(?:_[A-Za-z0-9]+)?|\d+)",
                    RegexOptions.CultureInvariant);
                if (leadingUser.Success)
                {
                    string leadingUserValue = leadingUser.Groups["id"].Value;
                    int resolvedLeadingUserId = string.Equals(leadingUser.Groups["field"].Value,
                        "uid", StringComparison.OrdinalIgnoreCase)
                        ? ParseProcessUserValue(leadingUserValue, "uid")
                        : ParseHarmonyUserId(leadingUserValue);
                    if (resolvedLeadingUserId < 0) continue;
                    bool startsNextBundleRecord = !string.IsNullOrWhiteSpace(bundle)
                        && bundleIndent >= 0 && indent <= bundleIndent
                        && HasFollowingBundleRecord(lines, lineIndex + 1, bundleIndent);
                    if (string.IsNullOrWhiteSpace(bundle) || startsNextBundleRecord)
                    {
                        // Some text dumps place the profile field before the
                        // Bundle record. Keep it pending until that record
                        // establishes its own Bundle boundary.
                        pendingHarmonyUserId = resolvedLeadingUserId;
                        if (startsNextBundleRecord)
                        {
                            currentHarmonyUserId = -1;
                            currentHarmonyAppIndex = -1;
                            module = "";
                            insideAbilityCollection = false;
                            insideNonUiAbilityCollection = false;
                            abilityIndent = -1;
                            entryFieldIndent = -1;
                        }
                    }
                    else
                    {
                        currentHarmonyUserId = resolvedLeadingUserId;
                        AddApp(apps, seen, bundle, "Bundle Manager 应用",
                            harmonyUserId: currentHarmonyUserId,
                            harmonyAppIndex: currentHarmonyAppIndex);
                    }
                    continue;
                }
                if (string.IsNullOrWhiteSpace(bundle)) continue;
                Match version = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:versionName|versionCode|versionNumber|version|version_code)\s*[:=]\s*['""]?([^,'""\r\n}]+)",
                    RegexOptions.CultureInvariant);
                if (version.Success)
                    AddApp(apps, seen, bundle, "Bundle Manager 应用", version: version.Groups[1].Value.Trim(),
                        harmonyUserId: currentHarmonyUserId,
                        harmonyAppIndex: currentHarmonyAppIndex);
            }
        }

        private static bool HasFollowingBundleRecord(IReadOnlyList<string> lines, int startIndex, int currentBundleIndent)
        {
            if (lines == null || currentBundleIndent < 0) return false;
            for (int index = Math.Max(0, startIndex); index < lines.Count; index++)
            {
                string raw = lines[index] ?? "";
                string trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                int indent = raw.Length - raw.TrimStart().Length;
                if (indent > currentBundleIndent) continue;
                return Regex.IsMatch(trimmed,
                    @"(?i)^(?:[-\s]*)?(?:bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|bundle|packageName|package_name|packageId|package_id|package\s+name|package|modulePackage|module_package|module\s+package|applicationId|application_id|appId|app_id|appIdentifier|app_identifier)\s*[:=]",
                    RegexOptions.CultureInvariant);
            }
            return false;
        }

        private static AppInfo FindHarmonyTextApp(
            IEnumerable<AppInfo> apps,
            string bundle,
            int harmonyUserId,
            int harmonyAppIndex)
        {
            List<AppInfo> matches = (apps ?? Enumerable.Empty<AppInfo>())
                .Where(delegate(AppInfo app)
                {
                    return app != null
                        && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                        && (harmonyUserId < 0 || app.HarmonyUserId < 0
                            || app.HarmonyUserId == harmonyUserId
                            || (app.HarmonyUserIds ?? new List<int>()).Contains(harmonyUserId))
                        && (harmonyAppIndex < 0 || app.HarmonyAppIndex < 0 || app.HarmonyAppIndex == harmonyAppIndex);
                })
                .ToList();
            if (matches.Count == 1) return matches[0];
            return matches.FirstOrDefault(delegate(AppInfo app)
            {
                return app.HarmonyUserId == harmonyUserId
                    && app.HarmonyAppIndex == harmonyAppIndex;
            });
        }

        private static void CollectAppsFromJson(
            JsonElement node,
            IList<AppInfo> apps,
            ISet<string> seen,
            string fallbackBundle,
            bool insideAbilityCollection,
            bool inventoryValues,
            int inheritedHarmonyUserId,
            bool allowObjectKeyBundle,
            bool insideMetadataContainer,
            int inheritedHarmonyAppIndex = -1)
        {
            // Metadata/resource/permission objects are descriptive data, even
            // when a vendor happens to use Bundle-shaped field names inside
            // them. Stop at the boundary so their fields cannot become an
            // application, user scope, or launch evidence.
            if (insideMetadataContainer) return;
            if (node.ValueKind == JsonValueKind.Object)
            {
                // Some Bundle Manager JSON variants put the profile on an
                // outer user/account object and nest the actual bundle record
                // below it. Carry that scope through inventory containers,
                // while allowing a child record's explicit userId to override
                // the inherited value.
                int embeddedHarmonyUserId = ParseEmbeddedHarmonyUserId(node);
                int effectiveHarmonyUserId = embeddedHarmonyUserId >= 0
                    ? embeddedHarmonyUserId
                    : inheritedHarmonyUserId;
                int embeddedHarmonyAppIndex = ParseEmbeddedHarmonyAppIndex(node);
                int effectiveHarmonyAppIndex = embeddedHarmonyAppIndex >= 0
                    ? embeddedHarmonyAppIndex
                    : inheritedHarmonyAppIndex;
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
                    NestedJsonPropertyValue(node, "applicationInfo", "bundleName", "bundleId", "bundle_name", "bundle_id", "name"),
                    NestedJsonPropertyValue(node, "appInfo", "bundleName", "bundleId", "bundle_name", "bundle_id", "name"),
                    NestedJsonPropertyValue(node, "bundleInfo", "bundleName", "bundleId", "bundle_name", "bundle_id", "name"),
                    inventoryValues && IsHarmonyBundleRecord(node, namedBundle) ? namedBundle : "",
                    fallbackBundle);
                if (!insideAbilityCollection && ValidHarmonyApplicationBundle(bundle))
                {
                    bool hasSystemAppEvidence;
                    bool isSystemApp;
                    bool isPreInstallApp;
                    string installSource;
                    ParseHarmonyInstallEvidence(node, out hasSystemAppEvidence, out isSystemApp,
                        out isPreInstallApp, out installSource);
                    AddApp(apps, seen, bundle, "Bundle Manager 应用",
                        FirstNonEmpty(JsonPropertyValue(node, "appName"), JsonPropertyValue(node, "applicationName"),
                            JsonPropertyValue(node, "appLabel"), JsonPropertyValue(node, "label"),
                            JsonPropertyValue(node, "displayName"), NameIfNotBundle(namedBundle, bundle),
                            NestedJsonPropertyValue(node, "applicationInfo", "appName", "applicationName", "appLabel", "label", "displayName", "name"),
                            NestedJsonPropertyValue(node, "appInfo", "appName", "applicationName", "appLabel", "label", "displayName", "name"),
                            NestedJsonPropertyValue(node, "bundleInfo", "appName", "applicationName", "appLabel", "label", "displayName", "name")),
                        FirstNonEmpty(JsonPropertyValue(node, "versionName"), JsonPropertyValue(node, "versionCode"),
                            JsonPropertyValue(node, "versionNumber"), JsonPropertyValue(node, "version_code"), JsonPropertyValue(node, "version"),
                            NestedJsonPropertyValue(node, "applicationInfo", "versionName", "versionCode", "versionNumber", "version"),
                            NestedJsonPropertyValue(node, "appInfo", "versionName", "versionCode", "versionNumber", "version"),
                            NestedJsonPropertyValue(node, "bundleInfo", "versionName", "versionCode", "versionNumber", "version")),
                        ParseLaunchEntryPoints(node.GetRawText()).Any(delegate(HarmonyLaunchEntryPoint entry) { return entry.IsUiEntry; }),
                        harmonyLaunchEntries: ToHarmonyLaunchEntries(
                            ParseLaunchEntryPoints(node.GetRawText()),
                            effectiveHarmonyUserId),
                        harmonyUserId: effectiveHarmonyUserId,
                        harmonyAppIndex: effectiveHarmonyAppIndex,
                        hasSystemAppEvidence: hasSystemAppEvidence,
                        isSystemApp: isSystemApp,
                        isPreInstallApp: isPreInstallApp,
                        installSource: installSource);
                }
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    bool childInsideAbilityCollection = insideAbilityCollection || IsAbilityInfoCollection(property.Name);
                    bool childInsideMetadataContainer = insideMetadataContainer
                        || IsNonLaunchMetadataContainer(property.Name);
                    // Only inventory containers may infer a Bundle from a key
                    // or string item. Labels, permissions and metadata can all
                    // contain dotted strings without describing another app.
                    bool emptyObjectBundleRecord = property.Value.ValueKind == JsonValueKind.Object
                        && !property.Value.EnumerateObject().Any();
                    bool keyedBundleRecord = (ValidBundle(property.Name) && !emptyObjectBundleRecord)
                        || (allowObjectKeyBundle
                            && ValidHarmonyApplicationBundle(property.Name)
                            && (IsStrongHarmonyBundleRecord(property.Value, property.Name)
                                || emptyObjectBundleRecord));
                    string childFallback = !childInsideAbilityCollection && !childInsideMetadataContainer && inventoryValues
                        && !ValidHarmonyApplicationBundle(bundle) && keyedBundleRecord ? property.Name : "";
                    bool childAllowObjectKeyBundle = !childInsideMetadataContainer
                        && IsBundleRecordObjectMap(property.Name);
                    CollectAppsFromJson(property.Value, apps, seen, childFallback, childInsideAbilityCollection,
                        !childInsideMetadataContainer && (IsBundleRecordCollection(property.Name)
                            || string.Equals(NormalizePropertyName(property.Name), "applicationInfo", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(NormalizePropertyName(property.Name), "appInfo", StringComparison.OrdinalIgnoreCase)),
                        effectiveHarmonyUserId, childAllowObjectKeyBundle, childInsideMetadataContainer,
                        effectiveHarmonyAppIndex);
                }
                return;
            }
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                    CollectAppsFromJson(value, apps, seen, fallbackBundle, insideAbilityCollection, inventoryValues,
                        inheritedHarmonyUserId, allowObjectKeyBundle, insideMetadataContainer,
                        inheritedHarmonyAppIndex);
                return;
            }
            if (node.ValueKind == JsonValueKind.String)
            {
                string scalar = node.GetString() ?? "";
                // Native Harmony bundle names are allowed to be a single
                // segment. JSON package inventories can return them as
                // scalar values, so do not apply the Android dotted-name
                // rule to this already-scoped inventory value.
                if (!insideAbilityCollection && !insideMetadataContainer && inventoryValues
                    && ValidHarmonyApplicationBundle(scalar))
                    AddApp(apps, seen, scalar, "Bundle Manager 应用",
                        harmonyUserId: inheritedHarmonyUserId,
                        harmonyAppIndex: inheritedHarmonyAppIndex);
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

        private static bool IsStrongHarmonyBundleRecord(JsonElement node, string candidateBundle)
        {
            if (node.ValueKind != JsonValueKind.Object || !ValidHarmonyApplicationBundle(candidateBundle)) return false;
            // A label/display name alone is not ownership evidence: metadata
            // objects can contain the same words. Object-key inventories need
            // a field that identifies an installed bundle record.
            return HasJsonProperty(node, "versionCode", "versionName", "versionNumber", "version",
                "compatibleVersion", "targetVersion", "hapModuleInfos", "moduleInfos", "applicationInfo",
                "appInfo", "abilityInfos", "installTime", "updateTime", "userId", "bundleInfo")
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
                NestedJsonPropertyValue(node, "appInfo", "userId", "user_id", "user"),
                NestedJsonPropertyValue(node, "bundleInfo", "userId", "user_id", "user"));
            if (!string.IsNullOrWhiteSpace(direct)) return ParseHarmonyUserId(direct);
            string uid = FirstNonEmpty(
                JsonPropertyValue(node, "uid"),
                NestedJsonPropertyValue(node, "applicationInfo", "uid"),
                NestedJsonPropertyValue(node, "appInfo", "uid"),
                NestedJsonPropertyValue(node, "bundleInfo", "uid"));
            return string.IsNullOrWhiteSpace(uid) ? -1 : ParseProcessUserValue(uid, "uid");
        }

        private static int ParseEmbeddedHarmonyUserId(string value)
        {
            string text = (value ?? "").Trim();
            Match uid = Regex.Match(text, @"(?i)\buid\b\s*[:=]\s*[""']?(?<value>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant);
            if (uid.Success) return ParseProcessUserValue(uid.Groups["value"].Value, "uid");
            return ParseHarmonyUserId(text);
        }

        private static int ParseEmbeddedHarmonyAppIndex(string value)
        {
            return ParseExplicitHarmonyAppIndex(ExtractTextField(value,
                "appIndex|app_index|applicationIndex|application_index"));
        }

        private static int ParseExplicitHarmonyAppIndex(string value)
        {
            string text = (value ?? "").Trim().Trim('"', '\'', ',', ';');
            if (text.Length == 0) return -1;
            Match match = Regex.Match(text, @"(?<!\d)-?\d+(?!\d)", RegexOptions.CultureInvariant);
            if (!match.Success) return -1;
            return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index >= 0 ? index : -1;
        }

        private static int ParseEmbeddedHarmonyAppIndex(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return -1;
            string direct = FirstNonEmpty(
                JsonPropertyValue(node, "appIndex"), JsonPropertyValue(node, "app_index"),
                JsonPropertyValue(node, "applicationIndex"), JsonPropertyValue(node, "application_index"),
                NestedJsonPropertyValue(node, "applicationInfo", "appIndex", "app_index", "applicationIndex", "application_index"),
                NestedJsonPropertyValue(node, "appInfo", "appIndex", "app_index", "applicationIndex", "application_index"),
                NestedJsonPropertyValue(node, "bundleInfo", "appIndex", "app_index", "applicationIndex", "application_index"));
            return ParseExplicitHarmonyAppIndex(direct);
        }

        private static string HarmonyApplicationScopeKey(AppInfo app)
        {
            if (app == null) return "";
            return (app.BundleId ?? "").Trim() + "\n"
                + Math.Max(-1, app.HarmonyAppIndex).ToString(CultureInfo.InvariantCulture);
        }

        private static string HarmonyApplicationScopeKey(string bundle, int harmonyUserId, int harmonyAppIndex)
        {
            return (bundle ?? "").Trim() + "\n"
                + Math.Max(-1, harmonyAppIndex).ToString(CultureInfo.InvariantCulture);
        }

        private static void ParseHarmonyInstallEvidence(
            JsonElement node,
            out bool hasEvidence,
            out bool isSystemApp,
            out bool isPreInstallApp,
            out string installSource)
        {
            string system = FirstNonEmpty(
                JsonPropertyValue(node, "isSystemApp"), JsonPropertyValue(node, "isSystem"),
                NestedJsonPropertyValue(node, "applicationInfo", "isSystemApp", "isSystem"),
                NestedJsonPropertyValue(node, "appInfo", "isSystemApp", "isSystem"),
                NestedJsonPropertyValue(node, "bundleInfo", "isSystemApp", "isSystem"));
            string preinstalled = FirstNonEmpty(
                JsonPropertyValue(node, "isPreInstallApp"), JsonPropertyValue(node, "isPreinstalled"),
                JsonPropertyValue(node, "isPreInstalled"),
                NestedJsonPropertyValue(node, "applicationInfo", "isPreInstallApp", "isPreinstalled", "isPreInstalled"),
                NestedJsonPropertyValue(node, "appInfo", "isPreInstallApp", "isPreinstalled", "isPreInstalled"),
                NestedJsonPropertyValue(node, "bundleInfo", "isPreInstallApp", "isPreinstalled", "isPreInstalled"));
            installSource = FirstNonEmpty(
                JsonPropertyValue(node, "installSource"), JsonPropertyValue(node, "installationSource"),
                NestedJsonPropertyValue(node, "applicationInfo", "installSource", "installationSource"),
                NestedJsonPropertyValue(node, "appInfo", "installSource", "installationSource"),
                NestedJsonPropertyValue(node, "bundleInfo", "installSource", "installationSource"));
            isSystemApp = IsBooleanLike(system) || IsSystemInstallSource(installSource);
            isPreInstallApp = IsBooleanLike(preinstalled)
                || IsPreInstallSource(installSource);
            hasEvidence = !string.IsNullOrWhiteSpace(system)
                || !string.IsNullOrWhiteSpace(preinstalled)
                || !string.IsNullOrWhiteSpace(installSource);
        }

        private static void ParseHarmonyInstallEvidence(
            string line,
            out bool hasEvidence,
            out bool isSystemApp,
            out bool isPreInstallApp,
            out string installSource)
        {
            string system = ExtractTextField(line, "isSystemApp|isSystem");
            string preinstalled = ExtractTextField(line, "isPreInstallApp|isPreinstalled|isPreInstalled");
            installSource = ExtractTextField(line, "installSource|installationSource");
            isSystemApp = IsBooleanLike(system) || IsSystemInstallSource(installSource);
            isPreInstallApp = IsBooleanLike(preinstalled) || IsPreInstallSource(installSource);
            hasEvidence = !string.IsNullOrWhiteSpace(system)
                || !string.IsNullOrWhiteSpace(preinstalled)
                || !string.IsNullOrWhiteSpace(installSource);
        }

        private static bool IsBooleanLike(string value)
        {
            return string.Equals((value ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals((value ?? "").Trim(), "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals((value ?? "").Trim(), "1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSystemInstallSource(string value)
        {
            string normalized = (value ?? "").Trim().Replace("_", "-").ToLowerInvariant();
            return normalized == "system" || normalized == "system-app" || normalized == "systemapp";
        }

        private static bool IsPreInstallSource(string value)
        {
            string normalized = (value ?? "").Trim().Replace("_", "-").ToLowerInvariant();
            return normalized == "pre-installed" || normalized == "preinstall" || normalized == "pre-installed-app";
        }

        private static string NameIfNotBundle(string value, string bundle)
        {
            return string.Equals(value ?? "", bundle ?? "", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        internal static List<ProcessInfo> MergeProcesses(IEnumerable<ProcessInfo> source)
        {
            List<ProcessInfo> merged = new List<ProcessInfo>();
            foreach (IGrouping<int, ProcessInfo> group in (source ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .Where(process => !IsHarmonyInventoryHelper(process))
                .GroupBy(process => process.Pid))
            {
                List<ProcessInfo> rows = group.ToList();
                ProcessInfo existing = rows[0];
                foreach (ProcessInfo process in rows.Skip(1))
                {
                    MergeProcessFields(existing, process);
                }
                CompleteMergedProcessName(existing, rows);
                merged.Add(existing);
            }
            return merged
                .OrderByDescending(delegate(ProcessInfo process) { return process.Recommended; })
                .ThenBy(delegate(ProcessInfo process) { return process.Name; })
                .ThenBy(delegate(ProcessInfo process) { return process.Pid; })
                .ToList();
        }

        internal static bool IsHarmonyInventoryHelper(ProcessInfo process)
        {
            if (process == null || !DeviceLookupService.IsHarmony(process.Platform)) return false;
            if (process.OwnershipVerified
                || !string.IsNullOrWhiteSpace(process.BundleId)
                || !string.IsNullOrWhiteSpace(process.OwnerBundleId)) return false;

            string name = NormalizeProcessToken(process.Name);
            return string.Equals(name, "sh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "printf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "sed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "head", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "tr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "hidumper", StringComparison.OrdinalIgnoreCase);
        }

        private static void CompleteMergedProcessName(ProcessInfo process, IEnumerable<ProcessInfo> rows)
        {
            if (process == null) return;
            List<string> concreteNames = (rows ?? Enumerable.Empty<ProcessInfo>())
                .Where(row => row != null && !row.HarmonyNameIsComm)
                .Select(row => NormalizeProcessToken(row == null ? "" : row.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name) && !IsGenericHarmonyProcessName(name))
                .Distinct(StringComparer.Ordinal).ToList();
            List<string> commNames = (rows ?? Enumerable.Empty<ProcessInfo>())
                .Where(row => row != null && row.HarmonyNameIsComm)
                .Select(row => NormalizeProcessToken(row.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name) && !IsGenericHarmonyProcessName(name))
                .Distinct(StringComparer.Ordinal).ToList();
            bool commConflict = concreteNames.Count == 1
                ? commNames.Any(name => !string.Equals(name, concreteNames[0], StringComparison.Ordinal)
                    && !IsMatchingHarmonyCommAlias(name, concreteNames[0]))
                : commNames.Count > 1;
            if (process.OwnershipAmbiguous || concreteNames.Count > 1 || commConflict)
            {
                MarkAbilityOwnershipAmbiguous(process);
                return;
            }
            if (concreteNames.Count == 1) CompleteGenericProcessName(process, concreteNames[0]);
            else if (commNames.Count == 1 && IsGenericHarmonyProcessName(process.Name))
            {
                CompleteGenericProcessName(process, commNames[0]);
                process.HarmonyNameIsComm = true;
            }
        }

        private static void CompleteGenericProcessName(ProcessInfo process, string concreteName)
        {
            if (!IsGenericHarmonyProcessName(process.Name)
                && !(process.HarmonyNameIsComm && MatchesCompleteProcessName(process, concreteName))) return;
            bool displayFollowsProcess = string.IsNullOrWhiteSpace(process.DisplayName)
                || string.Equals(process.DisplayName, process.Name, StringComparison.Ordinal);
            process.Name = concreteName;
            process.HarmonyNameIsComm = false;
            if (displayFollowsProcess) process.DisplayName = process.Name;
        }

        private static void MergeProcessFields(ProcessInfo target, ProcessInfo source)
        {
            if (target == null || source == null) return;

            bool nameConflict = !string.IsNullOrWhiteSpace(target.Name)
                && !string.IsNullOrWhiteSpace(source.Name)
                && !string.Equals(NormalizeProcessToken(target.Name), NormalizeProcessToken(source.Name), StringComparison.Ordinal)
                && !IsGenericHarmonyProcessName(target.Name)
                && !IsGenericHarmonyProcessName(source.Name)
                && !(target.HarmonyNameIsComm && !source.HarmonyNameIsComm && IsMatchingHarmonyCommAlias(target.Name, source.Name))
                && !(source.HarmonyNameIsComm && !target.HarmonyNameIsComm && IsMatchingHarmonyCommAlias(source.Name, target.Name));
            bool bundleConflict = !string.IsNullOrWhiteSpace(target.BundleId)
                && !string.IsNullOrWhiteSpace(source.BundleId)
                && !string.Equals(target.BundleId, source.BundleId, StringComparison.OrdinalIgnoreCase);
            bool ownerBundleConflict = !string.IsNullOrWhiteSpace(target.OwnerBundleId)
                && !string.IsNullOrWhiteSpace(source.OwnerBundleId)
                && !string.Equals(target.OwnerBundleId, source.OwnerBundleId, StringComparison.OrdinalIgnoreCase);
            bool userConflict = target.HarmonyUserId >= 0 && source.HarmonyUserId >= 0
                && target.HarmonyUserId != source.HarmonyUserId;
            bool appIndexConflict = target.HarmonyAppIndex >= 0 && source.HarmonyAppIndex >= 0
                && target.HarmonyAppIndex != source.HarmonyAppIndex;
            bool startConflict = target.HarmonyStartTimeTicks > 0 && source.HarmonyStartTimeTicks > 0
                && target.HarmonyStartTimeTicks != source.HarmonyStartTimeTicks;

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
            if (target.HarmonyAppIndex < 0 && source.HarmonyAppIndex >= 0) target.HarmonyAppIndex = source.HarmonyAppIndex;
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

            if (target.OwnershipAmbiguous || nameConflict || bundleConflict || ownerBundleConflict || userConflict
                || appIndexConflict || startConflict)
                MarkAbilityOwnershipAmbiguous(target);
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
                        // `=com.example.app` and return only an APK/HAP/HSP
                        // path. Recover the bundle-shaped path segment while
                        // ignoring the archive file name itself.
                        MatchCollection candidates = Regex.Matches(
                            value,
                            @"(?<![A-Za-z0-9_])([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?![A-Za-z0-9_])",
                            RegexOptions.CultureInvariant);
                        for (int index = candidates.Count - 1; index >= 0; index--)
                        {
                            string candidate = candidates[index].Groups[1].Value;
                            if (candidate.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                                || candidate.EndsWith(".hap", StringComparison.OrdinalIgnoreCase)
                                || candidate.EndsWith(".hsp", StringComparison.OrdinalIgnoreCase)) continue;
                            if (ValidBundle(candidate))
                            {
                                value = candidate;
                                break;
                            }
                        }
                        if (!ValidHarmonyApplicationBundle(value))
                        {
                            Match packageArchiveParent = Regex.Match(
                                value,
                                @"(?i)(?:^|[/\\])(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)/(?:base|entry|module)\.(?:apk|hap|hsp)(?:$|[=\s])",
                                RegexOptions.CultureInvariant);
                            if (packageArchiveParent.Success)
                                value = packageArchiveParent.Groups["bundle"].Value;
                        }
                    }
                    // Native Harmony bundle names are not required to contain
                    // a dot. Keep the explicit `package:` value eligible for
                    // those targets. Explicit APK/HAP/HSP paths are handled
                    // by the bounded parent-directory recovery above.
                    if (ValidHarmonyApplicationBundle(value) && !packages.Contains(value, StringComparer.OrdinalIgnoreCase)) packages.Add(value);
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

        internal static List<string> ParseAndroidPackagesFromIndependentStreams(string stdout, string stderr)
        {
            List<string> packages = new List<string>();
            foreach (string output in new[] { stdout ?? "", stderr ?? "" })
            {
                foreach (string packageName in ParseAndroidPackages(output))
                {
                    if (!packages.Contains(packageName, StringComparer.OrdinalIgnoreCase))
                        packages.Add(packageName);
                }
            }
            return packages;
        }

        internal static List<HarmonyPackageRecord> ParseAndroidPackageRecordsFromIndependentStreams(string stdout, string stderr)
        {
            List<HarmonyPackageRecord> records = new List<HarmonyPackageRecord>();
            foreach (string output in new[] { stdout ?? "", stderr ?? "" })
            {
                foreach (string raw in Lines(output))
                {
                    string line = raw ?? "";
                    int appIndex = ParseExplicitHarmonyAppIndex(ExtractTextField(line,
                        "appIndex|app_index|applicationIndex|application_index"));
                    List<string> bundles = ParseAndroidPackages(line);
                    Match packageField = Regex.Match(line,
                        "(?i)^(?:package|pkg)\\s*[:=]\\s*(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)*)",
                        RegexOptions.CultureInvariant);
                    if (packageField.Success && ValidHarmonyApplicationBundle(packageField.Groups["bundle"].Value)
                        && !bundles.Contains(packageField.Groups["bundle"].Value, StringComparer.OrdinalIgnoreCase))
                        bundles.Add(packageField.Groups["bundle"].Value);
                    if (bundles.Count == 0)
                    {
                        Match explicitMatch = Regex.Match(line,
                            "(?i)(?:bundleName|bundle_name|bundleId|bundle_id|packageName|package_name|packageId|package_id|applicationId|application_id)\\s*[:=]\\s*(?<bundle>[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)*)",
                            RegexOptions.CultureInvariant);
                        if (explicitMatch.Success && ValidHarmonyApplicationBundle(explicitMatch.Groups["bundle"].Value))
                            bundles.Add(explicitMatch.Groups["bundle"].Value);
                    }
                    foreach (string bundle in bundles)
                    {
                        if (records.Any(delegate(HarmonyPackageRecord item)
                        {
                            return string.Equals(item.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                                && item.AppIndex == appIndex;
                        })) continue;
                        records.Add(new HarmonyPackageRecord { BundleId = bundle, AppIndex = appIndex });
                    }
                }
            }
            return records;
        }

        internal static Dictionary<int, string> ParseProcessNamesFromIndependentStreams(string stdout, string stderr)
        {
            Dictionary<int, string> names = new Dictionary<int, string>();
            HashSet<int> conflicts = new HashSet<int>();
            foreach (string output in new[] { stdout ?? "", stderr ?? "" })
            {
                foreach (KeyValuePair<int, string> pair in ParseProcessNamesFromProc(output))
                {
                    if (conflicts.Contains(pair.Key)) continue;
                    if (names.TryGetValue(pair.Key, out string existing)
                        && !string.Equals(existing, pair.Value, StringComparison.Ordinal))
                    {
                        names.Remove(pair.Key);
                        conflicts.Add(pair.Key);
                        continue;
                    }
                    names[pair.Key] = pair.Value;
                }
            }
            return names;
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

        internal static List<ProcessInfo> ParseProcesses(
            string output,
            string serial,
            bool inferBundleFromProcessName = true)
        {
            List<ProcessInfo> processes = new List<ProcessInfo>();
            int pidIndex = -1;
            int commandIndex = -1;
            int bundleIndex = -1;
            int userIndex = -1;
            int appIndexIndex = -1;
            string userFieldName = "";
            string commandFieldName = "";
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
                        "cmd|cmdline|args|command|exec(?:utable)?|process(?:Name)?|name");
                    bool keyedComm = false;
                    if (string.IsNullOrWhiteSpace(keyedCommand))
                    {
                        keyedCommand = ExtractKeyValueField(line, "comm");
                        keyedComm = !string.IsNullOrWhiteSpace(keyedCommand);
                    }
                    string keyedName = ProcessNameFromCommand(keyedCommand);
                    string keyedBundle = ExtractExplicitBundleField(line);
                    int keyedUser = ParseProcessUserId(line);
                    int keyedAppIndex = ParseExplicitHarmonyAppIndex(ExtractKeyValueField(line,
                        "appIndex|app_index|applicationIndex|application_index"));
                    if (!string.IsNullOrWhiteSpace(keyedName) || !string.IsNullOrWhiteSpace(keyedBundle))
                        processes.Add(CreateProcess(keyedValue, keyedName, serial, keyedUser, keyedBundle,
                            keyedComm, inferBundleFromProcessName, keyedAppIndex));
                    continue;
                }
                string[] parts = Regex.Split(line, @"\s+");
                int headerPid = IndexOfIgnoreCase(parts, "PID");
                if (headerPid >= 0)
                {
                    pidIndex = headerPid;
                    // Bundle/package columns are ownership evidence, not a
                    // process-name source. A few vendor views expose only
                    // PID + Bundle, so keep the command column separate and
                    // allow the process name to remain missing.
                    commandIndex = IndexOfAnyIgnoreCase(parts, "ARGS", "CMD", "CMDLINE", "COMMAND", "COMM", "EXEC", "EXECUTABLE", "PROCNAME", "NAME");
                    commandFieldName = commandIndex >= 0 && commandIndex < parts.Length ? parts[commandIndex] : "";
                    bundleIndex = IndexOfAnyIgnoreCase(parts, "BUNDLE", "BUNDLE_NAME", "BUNDLENAME", "BUNDLEID", "BUNDLE_ID",
                        "PACKAGE", "PACKAGE_NAME", "PACKAGENAME", "PACKAGEID", "PACKAGE_ID", "APPLICATIONID", "APPLICATION_ID");
                    userIndex = IndexOfAnyIgnoreCase(parts, "UID", "USER", "USERID", "USER_ID");
                    userFieldName = userIndex >= 0 && userIndex < parts.Length ? parts[userIndex] : "";
                    appIndexIndex = IndexOfAnyIgnoreCase(parts, "APPINDEX", "APP_INDEX", "APPLICATIONINDEX", "APPLICATION_INDEX");
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
                int rowCommandIndex = commandIndex >= 0 && commandIndex < parts.Length
                    ? commandIndex
                    // When the header explicitly identifies a Bundle column,
                    // do not fall back to the Bundle value as a process name.
                    : bundleIndex >= 0 ? -1 : FindProcessTokenIndex(parts, rowPidIndex + 1);
                string name = rowCommandIndex < 0
                    ? ""
                    : ProcessNameFromCommand(string.Join(" ", parts.Skip(rowCommandIndex)));
                if (rowCommandIndex >= 0 && string.IsNullOrWhiteSpace(name)) continue;
                // Harmony vendors may expose the kernel-truncated COMM name
                // through COMMAND as well as COMM, using [name] notation.
                // Preserve that provenance so the short label cannot become
                // a guessed Bundle and conflict with the full ARGS/NAME row.
                bool rowNameIsComm = string.Equals(commandFieldName, "COMM", StringComparison.OrdinalIgnoreCase)
                    || IsBracketedProcessToken(parts, rowCommandIndex)
                    || IsHarmonyShortCommandName(commandFieldName, name);
                int userId = userIndex >= 0 && userIndex < parts.Length
                    ? ParseProcessUserValue(parts[userIndex], userFieldName)
                    : InferHeaderlessProcessUserId(parts, rowPidIndex, rowCommandIndex);
                string explicitBundle = bundleIndex >= 0 && bundleIndex < parts.Length
                    ? NormalizeExplicitBundle(parts[bundleIndex])
                    : "";
                int appIndex = appIndexIndex >= 0 && appIndexIndex < parts.Length
                    ? ParseExplicitHarmonyAppIndex(parts[appIndexIndex])
                    : ParseExplicitHarmonyAppIndex(ExtractKeyValueField(line,
                        "appIndex|app_index|applicationIndex|application_index"));
                processes.Add(CreateProcess(pid, name, serial, userId, explicitBundle,
                    rowNameIsComm,
                    inferBundleFromProcessName, appIndex));
            }
            return processes
                .GroupBy(process => new { process.Pid, process.Name, process.BundleId, process.HarmonyUserId, process.HarmonyAppIndex, process.HarmonyNameIsComm })
                .Select(group => group.First())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .OrderByDescending(delegate(ProcessInfo process) { return process.Recommended; })
                .ThenBy(delegate(ProcessInfo process) { return process.Name; })
                .ThenBy(delegate(ProcessInfo process) { return process.Pid; })
                .ToList();
        }

        private static bool IsBracketedProcessToken(string[] parts, int index)
        {
            if (parts == null || index < 0 || index >= parts.Length) return false;
            string value = parts[index] ?? "";
            return value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']';
        }

        private static bool IsHarmonyShortCommandName(string fieldName, string name)
        {
            if (!string.Equals(fieldName, "CMD", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fieldName, "COMMAND", StringComparison.OrdinalIgnoreCase))
                return false;
            string value = NormalizeProcessToken(name);
            return System.Text.Encoding.UTF8.GetByteCount(value) == 15
                && value.IndexOf('.', StringComparison.Ordinal) > 0
                && !value.StartsWith("com.", StringComparison.Ordinal);
        }

        internal static List<HarmonyProcessBinding> ParseAbilityProcessBindings(string output)
        {
            List<HarmonyProcessBinding> bindings = new List<HarmonyProcessBinding>();
            string textOutput = output ?? "";
            foreach (string json in ExtractJsonValues(output))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(json))
                        CollectAbilityProcessBindingsFromJson(document.RootElement, bindings);
                    textOutput = textOutput.Replace(json, "\nAppRunningRecords:\n");
                }
                catch (JsonException) { }
            }

            string currentBundle = "";
            string currentProcess = "";
            string currentState = "";
            int currentPid = 0;
            int currentUser = -1;
            bool currentDirectRunningRecord = false;
            bool skipRelatedPids = false;
            string[] lines = Lines(textOutput).ToArray();
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string raw = lines[lineIndex] ?? "";
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                int lineIndent = raw.Length - raw.TrimStart().Length;
                if (IsAbilityRecordBoundary(line))
                {
                    AddAbilityProcessBinding(bindings, currentPid, currentBundle, currentUser, currentState, currentProcess,
                        currentDirectRunningRecord && IsRunningAbilityState(currentState));
                    currentBundle = "";
                    currentProcess = "";
                    currentState = "";
                    currentPid = 0;
                    currentUser = -1;
                    currentDirectRunningRecord = IsDirectAppRunningRecordHeader(line);
                    skipRelatedPids = false;
                }
                if (Regex.IsMatch(line, @"(?i)^(?:root caller|uiextension provider)\s*#", RegexOptions.CultureInvariant))
                    skipRelatedPids = true;
                if (skipRelatedPids) continue;

                string bundle = AbilityDumpField(line, "bundle[-_ ]?(?:name|id)|bundle|package[-_ ]?(?:name|id)");
                bundle = NormalizeExplicitBundle(bundle);
                string process = AbilityDumpField(line, "process[-_ ]?name|process|comm|command");
                string pidText = AbilityDumpField(line, "pid|process[-_ ]?id|application[-_ ]?pid|app[-_ ]?pid");
                int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid);
                // A repeated identity field starts another flat record. Never
                // carry a previous record's Bundle/user/state across it.
                if (currentPid > 0 && (pid > 0
                    || (!string.IsNullOrWhiteSpace(bundle) && !string.IsNullOrWhiteSpace(currentBundle))
                    || (!string.IsNullOrWhiteSpace(process) && !string.IsNullOrWhiteSpace(currentProcess))))
                {
                    AddAbilityProcessBinding(bindings, currentPid, currentBundle, currentUser, currentState, currentProcess,
                        currentDirectRunningRecord && IsRunningAbilityState(currentState));
                    currentBundle = "";
                    currentPid = 0;
                    currentUser = -1;
                    currentProcess = "";
                    currentState = "";
                    currentDirectRunningRecord = false;
                }
                if (!string.IsNullOrWhiteSpace(bundle)) currentBundle = bundle;
                if (pid > 0) currentPid = pid;
                string userText = AbilityDumpField(line, "user[-_ ]?id");
                string uidText = AbilityDumpField(line, "uid|user");
                if (!string.IsNullOrWhiteSpace(userText) || !string.IsNullOrWhiteSpace(uidText))
                {
                    string rawUser = !string.IsNullOrWhiteSpace(userText) ? userText : uidText;
                    string userField = !string.IsNullOrWhiteSpace(userText) ? "userId" : "uid";
                    int parsedUser = ParseProcessUserValue(rawUser, userField);
                    bool startsNextRecord = currentPid > 0 && pid == 0
                        && string.IsNullOrWhiteSpace(bundle) && string.IsNullOrWhiteSpace(process)
                        && string.IsNullOrWhiteSpace(AbilityDumpField(line, "state|ability[-_ ]?state|application[-_ ]?state|app[-_ ]?state|is[-_ ]?foreground|foreground"))
                        && HasFollowingAbilityProcessRecord(lines, lineIndex + 1, lineIndent, currentProcess);
                    if (startsNextRecord)
                    {
                        // A few Ability Manager text dumps put the profile
                        // field before each record's Bundle/PID fields. Do not
                        // overwrite the previous PID's profile; close that
                        // record and carry the explicit user to the next one.
                        AddAbilityProcessBinding(bindings, currentPid, currentBundle, currentUser, currentState, currentProcess);
                        currentBundle = "";
                        currentProcess = "";
                        currentState = "";
                        currentPid = 0;
                        currentUser = parsedUser;
                    }
                    else
                    {
                        currentUser = parsedUser;
                    }
                }
                if (!string.IsNullOrWhiteSpace(process)) currentProcess = process.Trim();
                string state = AbilityDumpField(line, "state|ability[-_ ]?state|application[-_ ]?state|app[-_ ]?state");
                if (!string.IsNullOrWhiteSpace(state)) currentState = state;
                string foregroundFlag = AbilityDumpField(line, "is[-_ ]?foreground|foreground");
                if (string.IsNullOrWhiteSpace(state) && !string.IsNullOrWhiteSpace(foregroundFlag))
                    currentState = IsTrueFlag(foregroundFlag)
                        ? "foreground"
                        : IsFalseFlag(foregroundFlag) ? "background" : foregroundFlag;
            }
            AddAbilityProcessBinding(bindings, currentPid, currentBundle, currentUser, currentState, currentProcess,
                currentDirectRunningRecord && IsRunningAbilityState(currentState));
            return bindings;
        }

        private static bool HasFollowingAbilityProcessRecord(
            IReadOnlyList<string> lines,
            int startIndex,
            int currentIndent,
            string currentProcess)
        {
            if (lines == null) return false;
            for (int index = Math.Max(0, startIndex); index < lines.Count; index++)
            {
                string raw = lines[index] ?? "";
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (IsAbilityRecordBoundary(line)) return false;
                int indent = raw.Length - raw.TrimStart().Length;
                if (indent < currentIndent) return false;
                if (indent > currentIndent) continue;

                string bundle = AbilityDumpField(line, "bundle[-_ ]?(?:name|id)|bundle|package[-_ ]?(?:name|id)");
                string pid = AbilityDumpField(line, "pid|process[-_ ]?id|application[-_ ]?pid|app[-_ ]?pid");
                string process = AbilityDumpField(line, "process[-_ ]?name|process|comm|command");
                if (!string.IsNullOrWhiteSpace(bundle)
                    || !string.IsNullOrWhiteSpace(pid)
                    || (!string.IsNullOrWhiteSpace(currentProcess) && !string.IsNullOrWhiteSpace(process)))
                    return true;
                // A state or first process-name field still belongs to the
                // current record. Do not look through it into a later one.
                if (string.IsNullOrWhiteSpace(AbilityDumpField(line, "user[-_ ]?id|uid|user")))
                    return false;
            }
            return false;
        }

        private static string AbilityDumpField(string line, string fieldPattern)
        {
            Match match = Regex.Match(line ?? "",
                @"(?i)(?:^|[\s,])(?:" + fieldPattern
                + @")\s*(?:[:=#]\s*[""']?(?<value>[^,\s""'\]}]+)|\[\s*(?<value>[^\]]+)\])",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["value"].Value.Trim() : "";
        }

        private static void CollectAbilityProcessBindingsFromJson(
            JsonElement node,
            IList<HarmonyProcessBinding> bindings,
            bool ignoreProcessEvidence = false,
            bool insideProcessRecord = false,
            int inheritedHarmonyUserId = -1,
            int inheritedHarmonyAppIndex = -1)
        {
            if (ignoreProcessEvidence) return;
            if (node.ValueKind == JsonValueKind.Object)
            {
                int embeddedHarmonyUserId = ParseEmbeddedHarmonyUserId(node);
                int effectiveHarmonyUserId = embeddedHarmonyUserId >= 0
                    ? embeddedHarmonyUserId
                    : inheritedHarmonyUserId;
                int embeddedHarmonyAppIndex = ParseEmbeddedHarmonyAppIndex(node);
                int effectiveHarmonyAppIndex = embeddedHarmonyAppIndex >= 0
                    ? embeddedHarmonyAppIndex
                    : inheritedHarmonyAppIndex;
                string bundle = NormalizeExplicitBundle(FirstNonEmpty(
                    JsonPropertyValue(node, "bundleName"), JsonPropertyValue(node, "bundleId"),
                    JsonPropertyValue(node, "packageName"), JsonPropertyValue(node, "applicationId"),
                    JsonPropertyValue(node, "ownerBundleName"), JsonPropertyValue(node, "ownerBundleId")));
                string pidText = FirstNonEmpty(JsonPropertyValue(node, "pid"), JsonPropertyValue(node, "processId"),
                    JsonPropertyValue(node, "applicationPid"), JsonPropertyValue(node, "appPid"));
                string process = FirstNonEmpty(
                    JsonPropertyValue(node, "processName"),
                    JsonPropertyValue(node, "process_name"),
                    JsonPropertyValue(node, "process"),
                    JsonPropertyValue(node, "comm"),
                    JsonPropertyValue(node, "command"),
                    JsonPropertyValue(node, "cmdline"),
                    JsonPropertyValue(node, "name"));
                process = ProcessNameFromCommand(process);
                bool pidOnlyEvidence = insideProcessRecord && HasDirectAbilityProcessEvidence(node);
                if (int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid > 0
                    && (ValidHarmonyApplicationBundle(bundle) || !string.IsNullOrWhiteSpace(process) || pidOnlyEvidence))
                {
                    string state = FirstNonEmpty(JsonPropertyValue(node, "state"), JsonPropertyValue(node, "abilityState"),
                        JsonPropertyValue(node, "applicationState"), JsonPropertyValue(node, "appState"));
                    bool foreground = IsForegroundState(node)
                        || IsTrueFlag(FirstNonEmpty(JsonPropertyValue(node, "isForeground"),
                            JsonPropertyValue(node, "foreground")));
                    bindings.Add(new HarmonyProcessBinding
                    {
                        Pid = pid,
                        BundleId = bundle,
                        ProcessName = process,
                        HarmonyUserId = effectiveHarmonyUserId,
                        HarmonyAppIndex = effectiveHarmonyAppIndex,
                        Foreground = foreground,
                        PidEvidenceVerified = string.IsNullOrWhiteSpace(bundle)
                            && string.IsNullOrWhiteSpace(process)
                            && pidOnlyEvidence
                    });
                }
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    // Ability Manager may embed root callers, UI-extension
                    // providers, host processes, and diagnostic metadata in a
                    // running-record object. Their PIDs are related evidence,
                    // not the process owned by this record. Keep JSON aligned
                    // with the text parser's existing boundary rules.
                    bool relatedProcess = IsRelatedProcessContainer(property.Name)
                        || IsNonLaunchMetadataContainer(property.Name);
                    bool processRecords = IsAbilityProcessRecordCollection(property.Name);
                    CollectAbilityProcessBindingsFromJson(property.Value, bindings, relatedProcess, processRecords,
                        effectiveHarmonyUserId, effectiveHarmonyAppIndex);
                }
                return;
            }
            if (node.ValueKind == JsonValueKind.Array)
                foreach (JsonElement child in node.EnumerateArray())
                    CollectAbilityProcessBindingsFromJson(child, bindings, false, insideProcessRecord,
                        inheritedHarmonyUserId, inheritedHarmonyAppIndex);
        }

        private static bool HasDirectAbilityProcessEvidence(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return false;
            string[] states =
            {
                JsonPropertyValue(node, "state"),
                JsonPropertyValue(node, "abilityState"),
                JsonPropertyValue(node, "applicationState"),
                JsonPropertyValue(node, "appState")
            };
            bool hasState = states.Any(state => !string.IsNullOrWhiteSpace(state));
            if (hasState) return states.Any(IsRunningAbilityState);
            string foreground = FirstNonEmpty(JsonPropertyValue(node, "isForeground"),
                JsonPropertyValue(node, "foreground"));
            return IsBooleanFlag(foreground);
        }

        private static bool IsForegroundState(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return false;
            return new[]
            {
                JsonPropertyValue(node, "state"),
                JsonPropertyValue(node, "abilityState"),
                JsonPropertyValue(node, "applicationState"),
                JsonPropertyValue(node, "appState")
            }.Any(IsForegroundValue);
        }

        private static bool IsAbilityProcessRecordCollection(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName).ToLowerInvariant();
            return normalized == "apprunningrecords" || normalized == "apprunningrecord"
                || normalized == "applicationrecords" || normalized == "applicationrecord"
                || normalized == "applicationrunningrecords" || normalized == "applicationrunningrecord";
        }

        private static bool IsRelatedProcessContainer(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName).ToLowerInvariant();
            return normalized == "rootcaller" || normalized == "rootcallers"
                || normalized == "uiextensionprovider" || normalized == "uiextensionproviders"
                || normalized == "extensionprovider" || normalized == "extensionproviders"
                || normalized == "parentprocess" || normalized == "parentprocesses"
                || normalized == "relatedprocess" || normalized == "relatedprocesses"
                || normalized == "hostprocess" || normalized == "hostprocesses"
                || normalized == "caller" || normalized == "callers"
                || normalized == "provider" || normalized == "providers";
        }

        private static void AddAbilityProcessBinding(
            IList<HarmonyProcessBinding> bindings,
            int pid,
            string bundle,
            int userId,
            string state,
            string processName,
            bool allowPidOnly = false)
        {
            bundle = NormalizeExplicitBundle(bundle);
            string process = ProcessNameFromCommand(processName);
            if (pid <= 0 || (!ValidHarmonyApplicationBundle(bundle)
                && string.IsNullOrWhiteSpace(process) && !allowPidOnly)) return;
            bindings.Add(new HarmonyProcessBinding
            {
                Pid = pid,
                BundleId = bundle,
                ProcessName = process,
                HarmonyUserId = userId,
                Foreground = IsForegroundValue(state),
                PidEvidenceVerified = string.IsNullOrWhiteSpace(bundle)
                    && string.IsNullOrWhiteSpace(process) && allowPidOnly
            });
        }

        private static bool IsAbilityRecordBoundary(string line)
        {
            return Regex.IsMatch(line ?? "", @"(?i)^(?:mission|ability\s*record|application\s*record|app\s*record|app\s*running\s*record)s?\b", RegexOptions.CultureInvariant);
        }

        private static bool IsDirectAppRunningRecordHeader(string line)
        {
            return Regex.IsMatch(line ?? "", @"(?i)^(?:app\s*running\s*record|application\s*running\s*record|application\s*record)\s+(?:id\b|#)", RegexOptions.CultureInvariant);
        }

        private static bool IsRunningAbilityState(string value)
        {
            return IsForegroundValue(value)
                || string.Equals((value ?? "").Trim(), "background", StringComparison.OrdinalIgnoreCase)
                || string.Equals((value ?? "").Trim(), "app_state_background", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsForegroundValue(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "foreground" || lower == "active" || lower == "app_state_foreground";
        }

        private static bool IsTrueFlag(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        private static bool IsFalseFlag(string value)
        {
            return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) || value == "0";
        }

        private static bool IsBooleanFlag(string value)
        {
            return IsTrueFlag(value) || IsFalseFlag(value);
        }

        private static void AddIndependentAbilityProcesses(
            IList<ProcessInfo> processes,
            IEnumerable<HarmonyProcessBinding> bindings,
            string serial)
        {
            HashSet<int> knownPids = new HashSet<int>(processes.Select(process => process.Pid));
            foreach (IGrouping<int, HarmonyProcessBinding> group in bindings
                .Where(binding => binding != null && binding.Pid > 0 && !knownPids.Contains(binding.Pid))
                .GroupBy(binding => binding.Pid))
            {
                List<string> bundles = group.Select(binding => binding.BundleId)
                    .Where(bundle => ValidHarmonyApplicationBundle(bundle))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<int> users = group.Where(binding => binding.HarmonyUserId >= 0)
                    .Select(binding => binding.HarmonyUserId)
                    .Distinct().ToList();
                List<int> appIndices = group.Where(binding => binding.HarmonyAppIndex >= 0)
                    .Select(binding => binding.HarmonyAppIndex)
                    .Distinct().ToList();
                List<string> names = group.Select(binding => ProcessNameFromCommand(binding.ProcessName))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal).ToList();
                List<string> concreteNames = names
                    .Where(name => !IsGenericHarmonyProcessName(name))
                    .Distinct(StringComparer.Ordinal).ToList();
                // A single Bundle/PID is useful evidence even when every
                // executable-name probe is denied. Multiple Bundles or users
                // cannot identify ownership, but a unique real executable
                // name may still be retained as an explicitly ambiguous PID
                // row so the user can inspect/select it manually.
                if (concreteNames.Count > 1) continue;
                if ((bundles.Count > 1 || users.Count > 1)
                    && concreteNames.Count != 1) continue;
                if (appIndices.Count > 1 && concreteNames.Count != 1) continue;
                if (bundles.Count == 0 && concreteNames.Count == 0 && names.Count != 1) continue;
                string selectedName = concreteNames.Count == 1
                    ? concreteNames[0]
                    : bundles.Count == 1 ? "" : names[0];
                bool hasUnambiguousBundle = bundles.Count == 1 && users.Count <= 1;
                string selectedBundle = hasUnambiguousBundle ? bundles[0] : "";
                processes.Add(new ProcessInfo
                {
                    Pid = group.Key,
                    Name = selectedName,
                    DisplayName = string.IsNullOrWhiteSpace(selectedName) ? selectedBundle : selectedName,
                    BundleId = selectedBundle,
                    OwnerBundleId = selectedBundle,
                    OwnerName = selectedBundle,
                    OwnershipSource = string.IsNullOrWhiteSpace(selectedBundle) ? "" : "aa-dump",
                    OwnershipVerified = !string.IsNullOrWhiteSpace(selectedBundle),
                    HarmonyUserId = users.Count == 1 ? users[0] : -1,
                    HarmonyAppIndex = appIndices.Count == 1 ? appIndices[0] : -1,
                    DeviceUdid = serial ?? "",
                    Platform = "harmony",
                    Reason = string.IsNullOrWhiteSpace(selectedName)
                        ? "Ability Manager 已确认应用归属，进程名不可读"
                        : "Ability Manager 运行进程"
                });
            }
        }

        internal static void ApplyAbilityProcessBindings(
            IList<ProcessInfo> processes,
            IEnumerable<HarmonyProcessBinding> bindings)
        {
            if (processes == null || bindings == null) return;
            foreach (IGrouping<int, HarmonyProcessBinding> group in bindings
                .Where(delegate(HarmonyProcessBinding binding)
                {
                    return binding != null && binding.Pid > 0
                        && (ValidHarmonyApplicationBundle(binding.BundleId)
                            || !string.IsNullOrWhiteSpace(binding.ProcessName));
                })
                .GroupBy(delegate(HarmonyProcessBinding binding) { return binding.Pid; }))
            {
                ProcessInfo process = processes.FirstOrDefault(delegate(ProcessInfo item) { return item != null && item.Pid == group.Key; });
                if (process == null) continue;
                List<string> bundles = group.Select(delegate(HarmonyProcessBinding binding) { return binding.BundleId; })
                    .Where(delegate(string value) { return ValidHarmonyApplicationBundle(value); })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<int> users = group.Where(delegate(HarmonyProcessBinding binding) { return binding.HarmonyUserId >= 0; })
                    .Select(delegate(HarmonyProcessBinding binding) { return binding.HarmonyUserId; })
                    .Distinct().ToList();
                List<int> appIndices = group.Where(delegate(HarmonyProcessBinding binding) { return binding.HarmonyAppIndex >= 0; })
                    .Select(delegate(HarmonyProcessBinding binding) { return binding.HarmonyAppIndex; })
                    .Distinct().ToList();
                List<string> concreteNames = group.Select(binding => ProcessNameFromCommand(binding.ProcessName))
                    .Where(name => !string.IsNullOrWhiteSpace(name) && !IsGenericHarmonyProcessName(name))
                    .Distinct(StringComparer.Ordinal).ToList();
                // The read-only /proc fallback deliberately carries no Bundle
                // evidence. Keep that boundary intact even if a future merge
                // or vendor adapter leaves a dotted executable name in the
                // process row; only an explicit Ability Manager Bundle may
                // establish ownership for this source.
                string inferredBundle = process.HarmonyNameIsComm
                    || string.Equals(process.OwnershipSource, "proc-inventory", StringComparison.Ordinal)
                    ? ""
                    : BundleFromProcess(process.Name);
                bool nameConflict = !IsGenericHarmonyProcessName(process.Name)
                    && concreteNames.Any(name => !MatchesCompleteProcessName(process, name));
                if (process.OwnershipAmbiguous || bundles.Count > 1 || users.Count > 1 || appIndices.Count > 1
                    || nameConflict || concreteNames.Count > 1
                    || (users.Count == 1 && process.HarmonyUserId >= 0 && process.HarmonyUserId != users[0]))
                {
                    MarkAbilityOwnershipAmbiguous(process);
                    continue;
                }

                if (bundles.Count == 1)
                {
                    string bundle = bundles[0];
                    string inferred = inferredBundle;
                    string existing = FirstNonEmpty(process.OwnerBundleId, process.BundleId);
                    bool canReplaceInferred = !process.OwnershipVerified
                        && string.Equals(existing, inferred, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(process.OwnerBundleId);
                    if (!string.IsNullOrWhiteSpace(existing)
                        && !string.Equals(existing, bundle, StringComparison.OrdinalIgnoreCase)
                        && !canReplaceInferred)
                    {
                        MarkAbilityOwnershipAmbiguous(process);
                        continue;
                    }
                    process.BundleId = bundle;
                    process.OwnerBundleId = bundle;
                    process.OwnerName = bundle;
                    process.OwnershipSource = "aa-dump";
                    process.OwnershipVerified = true;
                    process.Reason = "Ability Manager 已确认应用归属";
                }
                // Complete the executable only after all ownership checks; a
                // display label or Bundle alone cannot identify an executable.
                if (concreteNames.Count == 1) CompleteGenericProcessName(process, concreteNames[0]);
                // A process-only record can confirm state/user, not Bundle.
                if (users.Count == 1) process.HarmonyUserId = users[0];
                if (appIndices.Count == 1) process.HarmonyAppIndex = appIndices[0];
                if (group.Any(delegate(HarmonyProcessBinding binding) { return binding.Foreground; }))
                {
                    process.ForegroundApplication = true;
                    process.Recommended = true;
                    if (bundles.Count == 0) process.Reason = "Ability Manager 前台进程";
                }
            }
        }

        private static void MarkAbilityOwnershipAmbiguous(ProcessInfo process)
        {
            process.OwnershipAmbiguous = true;
            process.OwnershipVerified = false;
            process.BundleId = "";
            process.OwnerBundleId = "";
            process.OwnerName = "";
            process.OwnershipSource = "";
            process.Recommended = false;
            process.ForegroundApplication = false;
            process.Reason = "进程归属信息冲突，请按真实 PID 选择";
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
                            && app.HarmonyUserId == process.HarmonyUserId
                            && app.HarmonyAppIndex == process.HarmonyAppIndex
                            && (string.IsNullOrWhiteSpace(app.ProcessName)
                                || string.Equals(app.ProcessName, process.Name, StringComparison.OrdinalIgnoreCase));
                    });
                    if (processOnly != null)
                    {
                        processOnly.IsRunning = true;
                        processOnly.Recommended = processOnly.Recommended || process.Recommended;
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
                        HarmonyUserId = process.HarmonyUserId,
                        HarmonyAppIndex = process.HarmonyAppIndex
                    };
                    AddHarmonyUser(unknown, process.HarmonyUserId);
                    apps.Add(unknown);
                    continue;
                }

                List<AppInfo> bundleMatches = apps.Where(delegate(AppInfo app)
                {
                    return app != null && string.Equals(app.BundleId, processBundle, StringComparison.OrdinalIgnoreCase)
                        && app.HarmonyAppIndex == process.HarmonyAppIndex;
                }).ToList();
                AppInfo existing = null;
                if (process.HarmonyUserId >= 0)
                {
                    existing = bundleMatches.FirstOrDefault(delegate(AppInfo app)
                    {
                        return app.HarmonyUserId == process.HarmonyUserId
                            && app.HarmonyAppIndex == process.HarmonyAppIndex;
                    });
                    if (existing == null
                        && bundleMatches.Count == 1
                        && bundleMatches[0].HarmonyUserId < 0
                        && !bundleMatches[0].IsRunning
                        && bundleMatches[0].ProcessPid <= 0
                        && validProcesses.Where(candidate => string.Equals(
                            FirstNonEmpty(candidate.BundleId, candidate.OwnerBundleId), processBundle,
                            StringComparison.OrdinalIgnoreCase)).All(candidate => candidate.HarmonyUserId == process.HarmonyUserId
                                && candidate.HarmonyAppIndex == process.HarmonyAppIndex)
                        && (bundleMatches[0].HarmonyUserIds == null || bundleMatches[0].HarmonyUserIds.Count == 0))
                    {
                        // An unscoped inventory row can still be bound safely
                        // when all live evidence supplies one explicit profile.
                        // Inspect the whole snapshot before changing the row so
                        // the result does not depend on process enumeration order.
                        existing = bundleMatches[0];
                        existing.HarmonyUserId = process.HarmonyUserId;
                        AddHarmonyUser(existing, process.HarmonyUserId);
                    }
                }
                else
                {
                    // All unknown-user processes for one Bundle belong to one
                    // unscoped row. Reuse that row even when concrete profile
                    // rows are also present; otherwise each worker/render
                    // process creates another duplicate app entry. A concrete
                    // profile row is never eligible for this fallback.
                    existing = bundleMatches.FirstOrDefault(delegate(AppInfo app)
                    {
                        return app.HarmonyUserId < 0 && app.HarmonyAppIndex == process.HarmonyAppIndex;
                    });
                }
                if (existing != null)
                {
                    existing.IsRunning = true;
                    // Keep the foreground recommendation in sync across the
                    // process and application projections. Bundle Manager
                    // does not expose runtime state, so an installed app row
                    // would otherwise miss the same recommendation shown by
                    // its live PID row.
                    existing.Recommended = existing.Recommended || process.Recommended;
                    if (HasUniqueProcessEvidence(existing, process, validProcesses))
                    {
                        // Carry the same process evidence into the app
                        // projection when it identifies one concrete target.
                        // This keeps app selection/restoration tied to the
                        // snapshot that produced the row.
                        existing.ProcessPid = process.Pid;
                        existing.ProcessName = process.Name;
                    }
                    else
                    {
                        // A Bundle/profile may expose a main process together
                        // with Worker/Render/Service processes. Once the
                        // candidate is ambiguous, leave process choice
                        // explicit instead of binding the first row observed.
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
                bool uniqueProcessEvidence = HasUniqueProcessEvidence(null, process, validProcesses);
                apps.Add(new AppInfo
                {
                    BundleId = processBundle,
                    Name = FirstNonEmpty(process.DisplayName, process.Name, processBundle),
                    ProcessPid = uniqueProcessEvidence ? process.Pid : 0,
                    ProcessName = uniqueProcessEvidence ? process.Name : "",
                    Platform = "harmony",
                    Recommended = process.Recommended || process.ForegroundApplication,
                    Reason = "运行中的鸿蒙应用",
                    IsRunning = true,
                    IsProcessOnly = true,
                    HarmonyUserId = process.HarmonyUserId,
                    HarmonyAppIndex = process.HarmonyAppIndex
                });
                AddHarmonyUser(apps[apps.Count - 1], process.HarmonyUserId);
            }
        }

        private static bool HasUniqueProcessEvidence(
            AppInfo app,
            ProcessInfo candidate,
            IEnumerable<ProcessInfo> processes)
        {
            if (candidate == null) return false;
            string bundle = FirstNonEmpty(candidate.BundleId, candidate.OwnerBundleId);
            if (!ValidHarmonyApplicationBundle(bundle)) return false;

            int targetUser = app != null ? app.HarmonyUserId : candidate.HarmonyUserId;
            int targetAppIndex = app != null ? app.HarmonyAppIndex : candidate.HarmonyAppIndex;
            int matches = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .Count(delegate(ProcessInfo process)
                {
                    string processBundle = FirstNonEmpty(process.BundleId, process.OwnerBundleId);
                    if (!string.Equals(processBundle, bundle, StringComparison.OrdinalIgnoreCase)) return false;
                    if (process.HarmonyAppIndex != targetAppIndex) return false;
                    if (targetUser < 0) return process.HarmonyUserId < 0;
                    // An unknown-user row could belong to this profile. Keep
                    // the app binding explicit until that ambiguity is gone.
                    return process.HarmonyUserId < 0 || process.HarmonyUserId == targetUser;
                });
            return matches == 1;
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
            string hint = DiscoveryFailureHint(output);
            if (hint.Length > 0) return hint;
            return "HDC 检测失败（退出码 " + (result == null ? -1 : result.ExitCode).ToString(CultureInfo.InvariantCulture) + "）。" + Sanitize(output, 240);
        }

        private static string DiscoveryFailureHint(string output)
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            string text = output ?? "";
            if (Regex.IsMatch(text, @"\b(?:un-?authori[sz]ed|not\s+authori[sz]ed)\b", options))
                return "鸿蒙设备未授权：请解锁设备，在设备端允许 USB 调试/HDC 调试后刷新。";
            if (Regex.IsMatch(text, @"\b(?:(?:permission|access)\s+denied|no\s+permissions?)\b", options))
                return "HDC 权限不足：请解锁设备并确认 USB/HDC 调试授权，检查电脑上的 HDC 访问权限后刷新。";
            if (Regex.IsMatch(text, @"\b(?:command\s+not\s+found|is\s+not\s+recognized|no\s+such\s+file)\b", options))
                return "未找到 HDC 运行组件。MoTuPerf 已自动检查常见 SDK 目录；请安装官方鸿蒙 SDK 的 toolchains，或使用 MOTUPERF_HDC 指定 HDC 可执行文件，然后重启 MoTuPerf。";
            if (Regex.IsMatch(text, @"\b(?:timeout|timed\s+out)\b", options))
                return "HDC 响应超时，请检查 USB 连接、设备解锁状态和调试授权后刷新。";
            if (Regex.IsMatch(text, @"\boffline\b", options))
                return "鸿蒙设备离线：请重新插拔 USB、检查数据线和开发者调试开关后刷新。";
            if (Regex.IsMatch(text, @"\b(?:device\s+not\s+found(?:ed)?|not\s+connected|no\s+devices?|disconnected)\b", options))
                return "HDC 未连接到设备，请检查数据线、调试开关和设备端授权后刷新。";
            return "";
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

        internal static List<HarmonyLaunchEntryPoint> ParseLaunchEntryPoints(
            string output, string expectedBundle = "", int expectedUser = -1,
            int expectedAppIndex = -1)
        {
            List<HarmonyLaunchEntryPoint> entries = new List<HarmonyLaunchEntryPoint>();
            string textOutput = output ?? "";
            foreach (string json in ExtractJsonValues(output))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        CollectLaunchEntryPoints(document.RootElement, "", false, false, false, entries,
                            expectedBundle: expectedBundle, expectedUser: expectedUser);
                    }
                    // Parsed objects retain their module and Ability type.
                    // Re-reading them as flat text loses both boundaries.
                    textOutput = textOutput.Replace(json, "\n");
                }
                catch (JsonException) { }
            }

            // Text dumps can repeat the same module through aliases such as
            // `moduleName` and `entryModuleName`. Pairing all module matches
            // with all ability matches by global index attaches later
            // abilities to an earlier module. Walk fields in source order and
            // keep the most recent module for the following ability fields.

            // Some Harmony releases print bm dump as an indented key/value
            // document instead of JSON. In that form abilityInfos entries are
            // usually written as "- name: EntryAbility". Keep the parser
            // scoped to an ability collection so unrelated app metadata named
            // "name" is never guessed as a launch target.
            foreach (string record in ScopedLaunchTextRecords(RemoveLaunchTextMetadata(textOutput), expectedBundle, expectedUser))
            {
                string flatOutput = ParseIndentedLaunchEntries(record, entries);
                ParseFlatLaunchEntries(flatOutput, entries);
                if (entries.Count == 0)
                    AddLaunchEntry(entries, ParseMainModule(flatOutput), ParseMainAbility(flatOutput));
            }
            if (expectedAppIndex > 0)
                entries = entries.Where(delegate(HarmonyLaunchEntryPoint entry)
                {
                    // A clone target requires explicit clone identity. An
                    // entry without appIndex may belong to the main instance.
                    return entry.HarmonyAppIndex == expectedAppIndex;
                }).ToList();
            else if (expectedAppIndex >= 0)
                entries = entries.Where(delegate(HarmonyLaunchEntryPoint entry)
                {
                    return entry.HarmonyAppIndex < 0 || entry.HarmonyAppIndex == expectedAppIndex;
                }).ToList();
            return entries;
        }

        internal static List<HarmonyLaunchEntryPoint> ParseLaunchEntryPointsFromIndependentStreams(
            string stdout, string stderr, string expectedBundle = "", int expectedUser = -1,
            int expectedAppIndex = -1)
        {
            List<HarmonyLaunchEntryPoint> entries = new List<HarmonyLaunchEntryPoint>();
            foreach (string output in new[] { stdout ?? "", stderr ?? "" })
            {
                foreach (HarmonyLaunchEntryPoint entry in ParseLaunchEntryPoints(output, expectedBundle, expectedUser, expectedAppIndex))
                    AddLaunchEntry(entries, entry.Module, entry.Ability, entry.IsUiEntry, entry.HarmonyAppIndex);
            }
            return entries;
        }

        private static bool IsLaunchBundleField(string key)
        {
            switch (NormalizePropertyName(key).ToLowerInvariant())
            {
                case "bundlename": case "bundleid": case "bundle":
                case "packagename": case "packageid": case "package":
                case "modulepackage": case "applicationid":
                    return true;
                default: return false;
            }
        }

        private static string RemoveLaunchTextMetadata(string output)
        {
            List<string> retained = new List<string>();
            int metadataIndent = -1;
            foreach (string line in Lines(output))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int indent = line.Length - line.TrimStart().Length;
                if (metadataIndent >= 0 && indent > metadataIndent) continue;
                metadataIndent = -1;
                if (TryParseKeyValueField(line, out string key, out string ignored)
                    && IsNonLaunchMetadataContainer(key))
                {
                    // A metadata/permission field inside a list item has
                    // sibling fields at the item's map indentation. Use the
                    // indentation after the list marker as the subtree
                    // boundary so later Bundle/name/version siblings survive
                    // while the nested descriptive values are removed.
                    metadataIndent = line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                        ? indent + 2
                        : indent;
                    continue;
                }
                retained.Add(line);
            }
            return string.Join("\n", retained);
        }

        private static bool LaunchIdentityMatches(string key, string value, string expectedBundle, int expectedUser)
        {
            if (IsLaunchBundleField(key))
                return string.Equals(value, expectedBundle, StringComparison.OrdinalIgnoreCase);
            string normalized = NormalizePropertyName(key).ToLowerInvariant();
            if (normalized == "userid" || normalized == "user" || normalized == "uid")
            {
                int userId = normalized == "uid" ? ParseProcessUserValue(value, "uid") : ParseHarmonyUserId(value);
                return userId >= 0 && userId == expectedUser;
            }
            return true;
        }

        private static bool JsonLaunchIdentityMatches(JsonElement node, string expectedBundle, int expectedUser,
            bool allowNamedBundle = true)
        {
            if (node.ValueKind == JsonValueKind.Array)
                return node.EnumerateArray().All(value => JsonLaunchIdentityMatches(value, expectedBundle, expectedUser));
            if (node.ValueKind != JsonValueKind.Object) return true;
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!LaunchIdentityMatches(property.Name, JsonScalarText(property.Value), expectedBundle, expectedUser))
                    return false;
                string name = NormalizePropertyName(property.Name);
                if (IsLaunchIdentityContainer(name)
                    && !JsonLaunchIdentityMatches(property.Value, expectedBundle, expectedUser)) return false;
            }
            string namedBundle = JsonPropertyValue(node, "name");
            return !allowNamedBundle || !IsHarmonyBundleRecord(node, namedBundle)
                || string.Equals(namedBundle, expectedBundle, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLaunchIdentityContainer(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName);
            return string.Equals(normalized, "applicationInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "appInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "bundleInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "elementName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "targetInfo", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ScopedLaunchTextRecords(string output, string expectedBundle, int expectedUser)
        {
            if (string.IsNullOrWhiteSpace(expectedBundle))
            {
                yield return output;
                yield break;
            }
            List<string> records = new List<string>();
            List<string> lines = new List<string>();
            foreach (string line in Lines(output))
            {
                string key;
                string value;
                if (TryParseKeyValueField(line, out key, out value) && IsLaunchBundleField(key) && lines.Count > 0
                    && (lines.Any(previous => TryParseKeyValueField(previous, out string previousKey, out string ignored)
                            && IsLaunchBundleField(previousKey))
                        || ParseLaunchEntryPoints(string.Join("\n", lines)).Count > 0))
                {
                    records.Add(string.Join("\n", lines));
                    lines.Clear();
                }
                lines.Add(line);
            }
            records.Add(string.Join("\n", lines));
            foreach (string record in records)
            {
                bool matches = true;
                foreach (string line in Lines(record))
                {
                    // Metadata has already been removed. Retain identity in
                    // actual applicationInfo/Ability containers as evidence.
                    if (TryParseKeyValueField(line, out string key, out string value)
                        && !LaunchIdentityMatches(key, value.Trim().Trim(',', '"', '\''), expectedBundle, expectedUser))
                        matches = false;
                    MatchCollection fields = Regex.Matches(line,
                        @"(?i)(?:^|[\s,;])['"" ]*(?<key>bundleName|bundle_name|bundle\s+name|bundleId|bundle_id|bundle\s+id|bundle|packageName|package_name|package\s+name|packageId|package_id|package|modulePackage|module_package|applicationId|application_id|appId|app_id|appIdentifier|app_identifier|userId|user_id|user|uid)['"" ]*\s*[:=]\s*['""]?(?<value>[^\s,'"";}]+)",
                        RegexOptions.CultureInvariant);
                    foreach (Match field in fields)
                        if (!LaunchIdentityMatches(field.Groups["key"].Value, field.Groups["value"].Value,
                            expectedBundle, expectedUser)) matches = false;
                }
                if (matches) yield return record;
            }
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

        private static string ParseIndentedLaunchEntries(string output, IList<HarmonyLaunchEntryPoint> entries)
        {
            List<string> flatLines = new List<string>();
            string module = "";
            bool insideAbilities = false;
            bool insideNonUiAbilities = false;
            int abilityIndent = -1;
            int entryFieldIndent = -1;
            string pendingAbility = "";
            bool pendingAbilityIsUi = true;
            bool pendingAbilityHasExplicitType = false;
            bool pendingAbilityExplicitIsUi = true;
            bool nextAbilityHasExplicitType = false;
            bool nextAbilityExplicitIsUi = true;
            Action flushAbility = delegate
            {
                if (!string.IsNullOrWhiteSpace(pendingAbility))
                    AddLaunchEntry(entries, module, pendingAbility,
                        pendingAbilityHasExplicitType ? pendingAbilityExplicitIsUi : pendingAbilityIsUi);
                pendingAbility = "";
                pendingAbilityHasExplicitType = false;
                pendingAbilityExplicitIsUi = true;
            };
            foreach (string raw in Lines(output))
            {
                string line = raw ?? "";
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;
                int fieldIndent = indent + (trimmed.StartsWith("- ", StringComparison.Ordinal) ? 2 : 0);
                if (insideAbilities && entryFieldIndent >= 0 && fieldIndent > entryFieldIndent)
                    continue;
                if (insideAbilities && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    flushAbility();
                Match moduleMatch = Regex.Match(
                    trimmed,
                    @"(?i)^(?:[-\s]*)?(?:moduleName|module_name|module\s+name|mainModuleName|main_module_name|main\s+module\s+name|entryModuleName|entry_module_name|entry\s+module\s+name|entryModule|module)\s*[:=]\s*['""]?([A-Za-z][A-Za-z0-9_.-]*)",
                    RegexOptions.CultureInvariant);
                if (moduleMatch.Success)
                {
                    if (insideAbilities && abilityIndent >= 0 && indent <= abilityIndent)
                    {
                        // Commit the previous module's last Ability before the
                        // outer module context changes. Nested metadata is
                        // filtered above and therefore cannot cross this boundary.
                        flushAbility();
                        insideAbilities = false;
                        insideNonUiAbilities = false;
                    }
                    module = moduleMatch.Groups[1].Value;
                }

                string collectionKey;
                string collectionValue;
                if (TryParseKeyValueField(trimmed, out collectionKey, out collectionValue)
                    && IsAbilityInfoCollection(collectionKey))
                {
                    flushAbility();
                    insideAbilities = true;
                    insideNonUiAbilities = !IsUiAbilityField(collectionKey);
                    abilityIndent = indent;
                    entryFieldIndent = -1;
                    nextAbilityHasExplicitType = false;
                    nextAbilityExplicitIsUi = true;
                    continue;
                }

                if (insideAbilities && indent > abilityIndent
                    && TryParseExplicitAbilityType(trimmed, out bool explicitIsUi))
                {
                    if (!string.IsNullOrWhiteSpace(pendingAbility))
                    {
                        pendingAbilityHasExplicitType = true;
                        pendingAbilityExplicitIsUi = explicitIsUi;
                    }
                    else
                    {
                        nextAbilityHasExplicitType = true;
                        nextAbilityExplicitIsUi = explicitIsUi;
                    }
                    continue;
                }

                string abilityValue;
                bool abilityIsUi;
                if (insideAbilities && indent <= abilityIndent
                    && !TryParseAbilityNameField(trimmed, true, out abilityValue, out abilityIsUi))
                {
                    flushAbility();
                    insideAbilities = false;
                    insideNonUiAbilities = false;
                    nextAbilityHasExplicitType = false;
                    nextAbilityExplicitIsUi = true;
                }
                if (!insideAbilities)
                {
                    flatLines.Add(line);
                    continue;
                }

                if (entryFieldIndent < 0 || fieldIndent < entryFieldIndent) entryFieldIndent = fieldIndent;
                if (TryParseAbilityNameField(trimmed, true, out abilityValue, out abilityIsUi))
                {
                    flushAbility();
                    pendingAbility = abilityValue;
                    pendingAbilityIsUi = !insideNonUiAbilities && abilityIsUi;
                    pendingAbilityHasExplicitType = nextAbilityHasExplicitType;
                    pendingAbilityExplicitIsUi = nextAbilityExplicitIsUi;
                    nextAbilityHasExplicitType = false;
                    nextAbilityExplicitIsUi = true;
                }
            }
            flushAbility();
            return string.Join("\n", flatLines);
        }

        private static void CollectLaunchEntryPoints(
            JsonElement node,
            string inheritedModule,
            bool insideAbilityInfo,
            bool allowScalarEntry,
            bool insideNonUiAbilityInfo,
            IList<HarmonyLaunchEntryPoint> entries,
            string keyedAbility = "",
            string expectedBundle = "",
            int expectedUser = -1,
            int inheritedHarmonyAppIndex = -1)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (!string.IsNullOrWhiteSpace(expectedBundle)
                    && !JsonLaunchIdentityMatches(node, expectedBundle, expectedUser, !insideAbilityInfo)) return;
                string module = FirstNonEmpty(JsonPropertyValue(node, "moduleName"), JsonPropertyValue(node, "module_name"),
                    JsonPropertyValue(node, "mainModuleName"), JsonPropertyValue(node, "main_module_name"),
                    JsonPropertyValue(node, "entryModuleName"), JsonPropertyValue(node, "entry_module_name"),
                    JsonPropertyValue(node, "entryModule"), JsonPropertyValue(node, "module"), inheritedModule);
                int embeddedHarmonyAppIndex = ParseEmbeddedHarmonyAppIndex(node);
                int effectiveHarmonyAppIndex = embeddedHarmonyAppIndex >= 0
                    ? embeddedHarmonyAppIndex
                    : inheritedHarmonyAppIndex;
                string ability = FirstNonEmpty(JsonPropertyValue(node, "mainElementName"), JsonPropertyValue(node, "mainElement"), JsonPropertyValue(node, "mainAbility"),
                    JsonPropertyValue(node, "mainAbilityName"), JsonPropertyValue(node, "entryAbility"),
                    JsonPropertyValue(node, "entryAbilityName"), JsonPropertyValue(node, "abilityName"),
                    JsonPropertyValue(node, "extensionAbilityName"), JsonPropertyValue(node, "serviceExtensionAbilityName"),
                    JsonPropertyValue(node, "formExtensionAbilityName"), JsonPropertyValue(node, "dataShareExtensionAbilityName"),
                    FindJsonAbilityName(node, insideAbilityInfo), keyedAbility);
                if (!string.IsNullOrWhiteSpace(ability))
                    AddLaunchEntry(entries, module, ability,
                        !insideNonUiAbilityInfo && IsUiAbilityObject(node, insideAbilityInfo),
                        effectiveHarmonyAppIndex);
                // A concrete Ability is one record. Its resources, skills and
                // metadata may contain names, but are not more launch entries.
                if (insideAbilityInfo && !string.IsNullOrWhiteSpace(ability)) return;
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(expectedBundle) && !insideAbilityInfo
                        && ValidBundle(property.Name)
                        && !string.Equals(property.Name, expectedBundle, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!insideAbilityInfo && IsNonLaunchMetadataContainer(property.Name)) continue;
                    bool isAbilityCollection = IsAbilityInfoCollection(property.Name);
                    bool childIsAbilityInfo = insideAbilityInfo || isAbilityCollection;
                    bool childIsNonUiAbilityInfo = insideNonUiAbilityInfo
                        || (isAbilityCollection && !IsUiAbilityField(property.Name));
                    bool childAllowsScalarEntry = isAbilityCollection
                        && (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.String);
                    if (insideAbilityInfo && !isAbilityCollection
                        && property.Value.ValueKind == JsonValueKind.Object && ValidEntryToken(property.Name))
                        CollectLaunchEntryPoints(property.Value, module, true, false, childIsNonUiAbilityInfo, entries,
                            property.Name, expectedBundle, expectedUser,
                            effectiveHarmonyAppIndex);
                    else
                        CollectLaunchEntryPoints(property.Value, module, childIsAbilityInfo, childAllowsScalarEntry, childIsNonUiAbilityInfo, entries,
                            expectedBundle: expectedBundle, expectedUser: expectedUser,
                            inheritedHarmonyAppIndex: effectiveHarmonyAppIndex);
                }
                return;
            }
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in node.EnumerateArray())
                    CollectLaunchEntryPoints(value, inheritedModule, insideAbilityInfo, allowScalarEntry, insideNonUiAbilityInfo, entries,
                        expectedBundle: expectedBundle, expectedUser: expectedUser,
                        inheritedHarmonyAppIndex: inheritedHarmonyAppIndex);
                return;
            }
            if (node.ValueKind == JsonValueKind.String && insideAbilityInfo && allowScalarEntry)
                AddLaunchEntry(entries, inheritedModule, node.GetString(), !insideNonUiAbilityInfo,
                    inheritedHarmonyAppIndex);
        }

        private static bool IsUiAbilityObject(JsonElement node, bool insideAbilityInfo)
        {
            bool explicitUiType;
            if (insideAbilityInfo && TryGetExplicitAbilityType(node, out explicitUiType))
                return explicitUiType;
            if (insideAbilityInfo && HasJsonProperty(node, "serviceExtensionAbilityName", "formExtensionAbilityName",
                "dataShareExtensionAbilityName", "extensionAbilityName")) return false;
            if (HasJsonProperty(node, "mainElementName", "mainElement", "mainAbility", "mainAbilityName",
                "entryAbility", "entryAbilityName", "abilityName", "ability_name")) return true;
            return !HasJsonProperty(node, "serviceAbilityName", "formAbilityName",
                "dataShareAbilityName", "workSchedulerAbilityName");
        }

        private static bool TryGetExplicitAbilityType(JsonElement node, out bool isUiEntry)
        {
            isUiEntry = true;
            if (node.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in node.EnumerateObject())
            {
                string name = NormalizePropertyName(property.Name);
                string value = JsonScalarText(property.Value).Trim();
                if (TryClassifyExplicitAbilityType(name, value, out bool explicitIsUi))
                {
                    isUiEntry = explicitIsUi;
                    return true;
                }
            }
            return false;
        }

        private static bool IsNonLaunchMetadataContainer(string propertyName)
        {
            string normalized = NormalizePropertyName(propertyName).ToLowerInvariant();
            return normalized == "metadata" || normalized == "resource" || normalized == "resources"
                || normalized == "permission" || normalized == "permissions"
                || normalized == "skill" || normalized == "skills"
                || normalized == "configuration" || normalized == "configurations";
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
                // BundleInfo exposes ExtensionAbilityInfo records through
                // extensionInfo(s) in some native and vendor dumps.
                || string.Equals(propertyName, "extensionInfo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionInfos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "extensionInfoList", StringComparison.OrdinalIgnoreCase)
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

        private static void AddLaunchEntry(
            IList<HarmonyLaunchEntryPoint> entries,
            string module,
            string ability,
            bool isUiEntry = true,
            int harmonyAppIndex = -1)
        {
            module = (module ?? "").Trim();
            ability = (ability ?? "").Trim();
            if (!ValidEntryToken(ability) || (!string.IsNullOrWhiteSpace(module) && !ValidEntryToken(module))) return;
            HarmonyLaunchEntryPoint existing = entries.FirstOrDefault(delegate(HarmonyLaunchEntryPoint entry)
            {
                return string.Equals(entry.Module, module, StringComparison.Ordinal)
                    && string.Equals(entry.Ability, ability, StringComparison.Ordinal)
                    && entry.HarmonyAppIndex == harmonyAppIndex;
            });
            if (existing != null)
            {
                existing.IsUiEntry = existing.IsUiEntry || isUiEntry;
                if (existing.HarmonyAppIndex < 0 && harmonyAppIndex >= 0)
                    existing.HarmonyAppIndex = harmonyAppIndex;
                return;
            }
            entries.Add(new HarmonyLaunchEntryPoint
            {
                Module = module,
                Ability = ability,
                IsUiEntry = isUiEntry,
                HarmonyAppIndex = harmonyAppIndex
            });
        }

        private static readonly string[] NonUiAbilityFieldMarkers = new[]
        {
            // OpenHarmony ExtensionAbilityType values and vendor collection
            // aliases. These records can be real launch evidence, but they
            // are not ordinary Page/UI entries for the picker.
            "extension",
            "service",
            "form",
            "datashare",
            "workscheduler",
            "worker",
            "inputmethod",
            "accessibility",
            "fileshare",
            "staticsubscriber",
            "wallpaper",
            "backup",
            "window",
            "print",
            "share",
            "push",
            "vpn",
            "photoeditor",
            "remoteobject",
            "embeddedui",
            "statusbarview",
            "autofill",
            "appaccountauthorization",
            "ui",
            "remotenotification",
            "remotelocation",
            "voip",
            "accountlogout",
            "hmsaccount",
            "ads",
            "liveviewlockscreen",
            "liveviewcard",
            "uiservice",
            "assetcache",
            "sysdialog",
            "syspicker",
            "syscommonui",
            "sysvisual",
            "recentphoto",
            "awcwebpage",
            "awcnewsfeed",
            "embeddedcashier",
            "contentembed",
            "insightintentui",
            "fence",
            "callerinfoquery",
            "assetacceleration",
            "formedit",
            "distributed",
            "appservice",
            "liveform",
            "selection",
            "webnativemessaging",
            "faultlog",
            "notificationsubscriber",
            "crypto",
            "partneragent",
            "agent",
            "modularobject",
            "ukeyauth",
            "fileaccess",
            "thumbnail",
            "preview",
            "enterpriseadmin",
            "driver",
            "action",
            "adsservice"
        };

        private static bool IsUiAbilityField(string key)
        {
            string normalized = NormalizePropertyName(key);
            if (NonUiAbilityFieldMarkers.Any(marker =>
                normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
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
            IEnumerable<HarmonyLaunchEntryInfo> harmonyLaunchEntries = null,
            int harmonyAppIndex = -1,
            bool hasSystemAppEvidence = false,
            bool isSystemApp = false,
            bool isPreInstallApp = false,
            string installSource = "")
        {
            bundle = (bundle ?? "").Trim().Trim('"', '\'', ',', ';');
            if (!ValidHarmonyApplicationBundle(bundle)) return null;
            AppInfo existing = apps.FirstOrDefault(delegate(AppInfo app)
            {
                return app != null
                    && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                    && app.HarmonyAppIndex == harmonyAppIndex;
            });
            if (existing == null && harmonyAppIndex >= 0)
            {
                // The flat text fallback may see the Bundle before the
                // enclosing appIndex line. Upgrade that still-unscoped,
                // metadata-only row when the structured pass later supplies
                // the concrete clone index instead of leaving a phantom
                // unindexed duplicate beside the real target.
                existing = apps.FirstOrDefault(delegate(AppInfo app)
                {
                    return app != null
                        && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                        && app.HarmonyAppIndex < 0
                        && !app.IsRunning
                        && (app.HarmonyLaunchEntries == null || app.HarmonyLaunchEntries.Count == 0)
                        && (harmonyUserId < 0 || app.HarmonyUserId < 0 || app.HarmonyUserId == harmonyUserId
                            || (app.HarmonyUserIds ?? new List<int>()).Contains(harmonyUserId));
                });
                if (existing != null) existing.HarmonyAppIndex = harmonyAppIndex;
            }
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
                if (hasSystemAppEvidence)
                {
                    existing.IsSystemApp = existing.IsSystemApp || isSystemApp;
                    existing.IsPreInstallApp = existing.IsPreInstallApp || isPreInstallApp;
                    if (string.IsNullOrWhiteSpace(existing.InstallSource)) existing.InstallSource = (installSource ?? "").Trim();
                }
                if (existing.HarmonyUserId < 0 && harmonyUserId >= 0) existing.HarmonyUserId = harmonyUserId;
                if (existing.HarmonyAppIndex < 0 && harmonyAppIndex >= 0) existing.HarmonyAppIndex = harmonyAppIndex;
                AddHarmonyUser(existing, harmonyUserId);
                AddHarmonyLaunchEntries(existing, harmonyLaunchEntries, harmonyAppIndex);
                return existing;
            }
            if (!seen.Add(HarmonyApplicationScopeKey(bundle, harmonyUserId, harmonyAppIndex))) return null;
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
                IsSystemApp = hasSystemAppEvidence && isSystemApp,
                IsPreInstallApp = hasSystemAppEvidence && isPreInstallApp,
                InstallSource = hasSystemAppEvidence ? (installSource ?? "").Trim() : "",
                HarmonyUserId = harmonyUserId,
                HarmonyAppIndex = harmonyAppIndex
            };
            AddHarmonyUser(app, harmonyUserId);
            AddHarmonyLaunchEntries(app, harmonyLaunchEntries, harmonyAppIndex);
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
                    .Concat((app.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                        .Where(delegate(HarmonyLaunchEntryInfo entry)
                        {
                            return entry != null && entry.HarmonyUserId >= 0;
                        })
                        .Select(delegate(HarmonyLaunchEntryInfo entry) { return entry.HarmonyUserId; }))
                    .Distinct()
                    .OrderBy(delegate(int userId) { return userId; })
                    .ToList();
                if (users.Count == 0 && app.HarmonyUserId >= 0) users.Add(app.HarmonyUserId);
                List<int> appIndices = (app.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                    .Where(delegate(HarmonyLaunchEntryInfo entry) { return entry != null && entry.HarmonyAppIndex >= 0; })
                    .Select(delegate(HarmonyLaunchEntryInfo entry) { return entry.HarmonyAppIndex; })
                    .Concat(app.HarmonyAppIndex >= 0 ? new[] { app.HarmonyAppIndex } : Enumerable.Empty<int>())
                    .Distinct()
                    .OrderBy(delegate(int index) { return index; })
                    .ToList();
                if (appIndices.Count == 0) appIndices.Add(-1);
                List<HarmonyInstanceScope> explicitScopes = new List<HarmonyInstanceScope>();
                foreach (HarmonyLaunchEntryInfo entry in app.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                {
                    if (entry == null || entry.HarmonyUserId < 0 || entry.HarmonyAppIndex < 0) continue;
                    AddHarmonyInstanceScope(explicitScopes, entry.HarmonyUserId, entry.HarmonyAppIndex);
                }
                if (app.HarmonyUserId >= 0 && app.HarmonyAppIndex >= 0)
                    AddHarmonyInstanceScope(explicitScopes, app.HarmonyUserId, app.HarmonyAppIndex);
                if (app.HarmonyAppIndex >= 0)
                {
                    foreach (int userId in users)
                        AddHarmonyInstanceScope(explicitScopes, userId, app.HarmonyAppIndex);
                }

                List<HarmonyInstanceScope> scopes = new List<HarmonyInstanceScope>();
                if (explicitScopes.Count > 0)
                {
                    scopes.AddRange(explicitScopes);
                    // One concrete app index with several explicit users is
                    // safe to apply to every user. With multiple indices,
                    // however, only preserve observed user/index pairs so a
                    // cross-product cannot invent a profile/clone target.
                    if (appIndices.Count == 1)
                    {
                        foreach (int userId in users)
                            AddHarmonyInstanceScope(scopes, userId, appIndices[0]);
                    }
                }
                else if (users.Count == 0)
                {
                    // A vendor may expose clone indexes without exposing a
                    // profile. Keep each real clone visible with an unknown
                    // user instead of dropping the entire application.
                    foreach (int appIndex in appIndices)
                        AddHarmonyInstanceScope(scopes, -1, appIndex);
                }
                else if (appIndices.Count == 0)
                {
                    foreach (int userId in users)
                        AddHarmonyInstanceScope(scopes, userId, -1);
                }
                else if (users.Count == 1)
                {
                    foreach (int appIndex in appIndices)
                        AddHarmonyInstanceScope(scopes, users[0], appIndex);
                }
                else if (appIndices.Count == 1)
                {
                    foreach (int userId in users)
                        AddHarmonyInstanceScope(scopes, userId, appIndices[0]);
                }
                else
                {
                    // No source preserved a user/index relationship. Keep
                    // the historical full projection only in this genuinely
                    // unpaired case; as soon as paired evidence exists above,
                    // unobserved combinations are suppressed.
                    foreach (int appIndex in appIndices)
                        foreach (int userId in users)
                            AddHarmonyInstanceScope(scopes, userId, appIndex);
                }

                if (scopes.Count == 0)
                {
                    expanded.Add(app);
                    continue;
                }

                if (scopes.Count == 1
                    && app.HarmonyUserId == scopes[0].HarmonyUserId
                    && app.HarmonyAppIndex == scopes[0].HarmonyAppIndex)
                {
                    expanded.Add(app);
                    continue;
                }

                // The same Bundle ID can be installed in owner, work, guest,
                // or secondary profiles. Keep one selectable row per real
                // profile so launch and process matching cannot silently use
                // the first user returned by HDC.
                foreach (HarmonyInstanceScope scope in scopes)
                {
                    AppInfo instance = CloneHarmonyApp(app, scope.HarmonyUserId, scope.HarmonyAppIndex);
                    instance.HarmonyUserId = scope.HarmonyUserId;
                    instance.HarmonyAppIndex = scope.HarmonyAppIndex;
                    instance.HarmonyUserIds = scope.HarmonyUserId >= 0
                        ? new List<int> { scope.HarmonyUserId }
                        : new List<int>();
                    expanded.Add(instance);
                }
            }
            return expanded;
        }

        private static void AddHarmonyInstanceScope(
            IList<HarmonyInstanceScope> scopes, int harmonyUserId, int harmonyAppIndex)
        {
            if (scopes == null || harmonyUserId < -1 || harmonyAppIndex < -1) return;
            if (scopes.Any(delegate(HarmonyInstanceScope scope)
            {
                return scope.HarmonyUserId == harmonyUserId
                    && scope.HarmonyAppIndex == harmonyAppIndex;
            })) return;
            scopes.Add(new HarmonyInstanceScope(harmonyUserId, harmonyAppIndex));
        }

        private static AppInfo CloneHarmonyApp(AppInfo source, int userId, int appIndex = -1)
        {
            bool preserveLiveProcess = source.HarmonyUserId == userId
                && source.HarmonyAppIndex == appIndex;
            List<HarmonyLaunchEntryInfo> entries = (source.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                .Where(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return entry != null
                        && (entry.HarmonyUserId < 0 || entry.HarmonyUserId == userId)
                        && (appIndex < 0
                            || (appIndex == 0
                                ? entry.HarmonyAppIndex < 0 || entry.HarmonyAppIndex == 0
                                : entry.HarmonyAppIndex == appIndex));
                })
                .Select(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return new HarmonyLaunchEntryInfo
                    {
                        Module = entry.Module,
                        Ability = entry.Ability,
                        IsUiEntry = entry.IsUiEntry,
                        HarmonyUserId = entry.HarmonyUserId,
                        HarmonyAppIndex = entry.HarmonyAppIndex
                    };
                })
                .ToList();
            bool hasUiEntry = entries.Any(delegate(HarmonyLaunchEntryInfo entry) { return entry.IsUiEntry; });
            bool hasNonUiEntry = entries.Any(delegate(HarmonyLaunchEntryInfo entry) { return !entry.IsUiEntry; });
            AppInfo copy = new AppInfo
            {
                BundleId = source.BundleId,
                Name = source.Name,
                Version = source.Version,
                Platform = source.Platform,
                Recommended = preserveLiveProcess ? source.Recommended : false,
                Reason = source.Reason,
                IconKey = source.IconKey,
                IconPath = preserveLiveProcess ? source.IconPath : "",
                ApkPath = source.ApkPath,
                ProcessPid = 0,
                ProcessName = "",
                IsRunning = preserveLiveProcess && source.IsRunning,
                IsSystemApp = source.IsSystemApp,
                IsPreInstallApp = source.IsPreInstallApp,
                InstallSource = source.InstallSource,
                IsProcessOnly = entries.Count > 0 ? !hasUiEntry : source.IsProcessOnly,
                HasLaunchEntry = hasUiEntry,
                HasNonUiLaunchEntry = hasNonUiEntry && !hasUiEntry,
                HarmonyUserId = source.HarmonyUserId,
                HarmonyAppIndex = source.HarmonyAppIndex
            };
            if (preserveLiveProcess)
            {
                copy.ProcessPid = source.ProcessPid;
                copy.ProcessName = source.ProcessName;
            }
            copy.HarmonyUserIds = new List<int>(source.HarmonyUserIds ?? new List<int>());
            copy.HarmonyLaunchEntries = entries;
            return copy;
        }

        private static List<HarmonyLaunchEntryInfo> ToHarmonyLaunchEntries(IEnumerable<HarmonyLaunchEntryPoint> entries)
        {
            return ToHarmonyLaunchEntries(entries, -1);
        }

        private static List<HarmonyLaunchEntryInfo> ToHarmonyLaunchEntries(
            IEnumerable<HarmonyLaunchEntryPoint> entries,
            int harmonyUserId,
            int harmonyAppIndex = -1)
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
                        IsUiEntry = entry.IsUiEntry,
                        HarmonyUserId = entry.HarmonyUserId >= 0 ? entry.HarmonyUserId : harmonyUserId,
                        HarmonyAppIndex = entry.HarmonyAppIndex >= 0 ? entry.HarmonyAppIndex : harmonyAppIndex
                    };
                })
                .ToList();
        }

        private static List<HarmonyLaunchEntryInfo> AnnotateHarmonyLaunchEntries(
            IEnumerable<HarmonyLaunchEntryInfo> entries,
            int harmonyUserId,
            int harmonyAppIndex = -1)
        {
            return (entries ?? Enumerable.Empty<HarmonyLaunchEntryInfo>())
                .Where(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return entry != null && !string.IsNullOrWhiteSpace(entry.Ability);
                })
                .Select(delegate(HarmonyLaunchEntryInfo entry)
                {
                    return new HarmonyLaunchEntryInfo
                    {
                        Module = entry.Module ?? "",
                        Ability = entry.Ability,
                        IsUiEntry = entry.IsUiEntry,
                        HarmonyUserId = entry.HarmonyUserId >= 0 ? entry.HarmonyUserId : harmonyUserId,
                        HarmonyAppIndex = entry.HarmonyAppIndex >= 0 ? entry.HarmonyAppIndex : harmonyAppIndex
                    };
                })
                .ToList();
        }

        private static void AddHarmonyLaunchEntries(
            AppInfo app,
            IEnumerable<HarmonyLaunchEntryInfo> entries,
            int harmonyAppIndex = -1)
        {
            if (app == null || entries == null) return;
            if (app.HarmonyLaunchEntries == null) app.HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>();
            foreach (HarmonyLaunchEntryInfo entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Ability)) continue;
                HarmonyLaunchEntryInfo existing = app.HarmonyLaunchEntries.FirstOrDefault(delegate(HarmonyLaunchEntryInfo current)
                {
                    return string.Equals(current.Module ?? "", entry.Module ?? "", StringComparison.Ordinal)
                        && string.Equals(current.Ability, entry.Ability, StringComparison.Ordinal)
                        && current.HarmonyUserId == entry.HarmonyUserId
                        && current.HarmonyAppIndex == entry.HarmonyAppIndex;
                });
                if (existing != null)
                {
                    existing.IsUiEntry = existing.IsUiEntry || entry.IsUiEntry;
                    AddHarmonyUser(app, entry.HarmonyUserId);
                    continue;
                }
                app.HarmonyLaunchEntries.Add(new HarmonyLaunchEntryInfo
                {
                    Module = (entry.Module ?? "").Trim(),
                    Ability = entry.Ability.Trim(),
                    IsUiEntry = entry.IsUiEntry,
                    HarmonyUserId = entry.HarmonyUserId,
                    HarmonyAppIndex = entry.HarmonyAppIndex >= 0 ? entry.HarmonyAppIndex : harmonyAppIndex
                });
                AddHarmonyUser(app, entry.HarmonyUserId);
            }
            RecomputeHarmonyLaunchFlags(app);
        }

        private static void RecomputeHarmonyLaunchFlags(AppInfo app)
        {
            if (app == null) return;
            bool hasUiEntry = app.HarmonyLaunchEntries != null
                && app.HarmonyLaunchEntries.Any(delegate(HarmonyLaunchEntryInfo entry) { return entry.IsUiEntry; });
            bool hasNonUiEntry = app.HarmonyLaunchEntries != null
                && app.HarmonyLaunchEntries.Any(delegate(HarmonyLaunchEntryInfo entry) { return !entry.IsUiEntry; });
            app.HasNonUiLaunchEntry = hasNonUiEntry && !hasUiEntry;
            if (hasUiEntry)
            {
                app.HasLaunchEntry = true;
                app.IsProcessOnly = false;
            }
            else if (app.HarmonyLaunchEntries != null && app.HarmonyLaunchEntries.Count > 0)
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

        private static void BindUnscopedInstalledAppsToSingleUser(
            IList<AppInfo> apps,
            IEnumerable<int> harmonyUserIds)
        {
            List<int> users = (harmonyUserIds ?? Enumerable.Empty<int>())
                .Where(delegate(int userId) { return userId >= 0; })
                .Distinct()
                .ToList();
            if (users.Count != 1) return;

            int userId = users[0];
            foreach (AppInfo app in apps ?? Enumerable.Empty<AppInfo>())
            {
                if (app == null
                    || !DevicePlatformNames.IsHarmony(app.Platform)
                    || app.HarmonyUserId >= 0
                    || (app.HarmonyUserIds ?? new List<int>()).Any(delegate(int value) { return value >= 0; })
                    || app.IsRunning
                    || string.IsNullOrWhiteSpace(app.BundleId)) continue;

                // A global installed-app record can be safely attributed when
                // account discovery proved that this device exposes exactly
                // one Harmony profile. Running process records stay unknown:
                // their PID/user identity still needs direct evidence.
                app.HarmonyUserId = userId;
                AddHarmonyUser(app, userId);
                foreach (HarmonyLaunchEntryInfo entry in app.HarmonyLaunchEntries ?? new List<HarmonyLaunchEntryInfo>())
                    if (entry != null && entry.HarmonyUserId < 0) entry.HarmonyUserId = userId;
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

        private async Task<ProcessResult> RunHarmonyScopedStartAsync(
            string serial,
            IEnumerable<string> command,
            CancellationToken token)
        {
            string[] requested = (command ?? Enumerable.Empty<string>()).ToArray();
            ProcessResult result = await RunShellAsync(serial, requested, 15000, token).ConfigureAwait(false);
            if (IsSuccessfulCommand(result) || !IsHarmonyUserOptionUnsupported(result)) return result;

            // A known Harmony profile must never fall through to an unscoped
            // start. Older vendor builds may reject `-u`, but an unscoped retry
            // can start the same Bundle in another profile and make the picker
            // bind the wrong process. Let the desktop layer keep the explicit
            // failure and use its same-Bundle/same-user PID fallback instead.
            return WithHarmonyScopedStartDiagnostic(result);
        }

        private static ProcessResult WithHarmonyScopedStartDiagnostic(ProcessResult result)
        {
            const string diagnostic = "设备的 aa start 不支持用户作用域参数 -u，已停止无范围重试；请直接选择该用户对应的真实进程，或更新设备工具。";
            string stderr = result == null ? "" : result.Stderr ?? "";
            if (stderr.IndexOf(diagnostic, StringComparison.Ordinal) >= 0) return result;
            string combined = string.IsNullOrWhiteSpace(stderr) ? diagnostic : stderr.TrimEnd() + "\n" + diagnostic;
            return new ProcessResult(result == null ? 1 : result.ExitCode,
                result == null ? "" : result.Stdout,
                combined);
        }

        private static bool IsHarmonyUserOptionUnsupported(ProcessResult result)
        {
            if (result == null || result.ExitCode == 0 || IsHdcFailure(result)) return false;
            string output = (result.Stdout ?? "") + "\n" + (result.Stderr ?? "");
            return output.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0
                && output.IndexOf("usage: aa start", StringComparison.OrdinalIgnoreCase) >= 0;
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
            int index = 1;
            string transport = parts[1].Trim().Trim(',', ';').ToLowerInvariant();
            if (transport == "usb" || transport == "tcp" || transport.StartsWith("tcp:", StringComparison.Ordinal)) index++;
            if (index >= parts.Length) return "";
            string candidate = parts[index].Trim().Trim(',', ';', ':').ToLowerInvariant();
            return IsTargetState(candidate) ? candidate : "";
        }

        private static bool IsTargetState(string candidate)
        {
            return IsConnectedState(candidate)
                || candidate == "unauthorized" || candidate == "un-authorized"
                || candidate == "unauthorised" || candidate == "un-authorised" || candidate == "offline"
                || candidate == "disconnected" || candidate == "disconnect" || candidate == "connecting"
                || candidate == "error" || candidate == "ready";
        }

        private static bool IsReservedDiscoveryToken(string token)
        {
            string value = (token ?? "").Trim().TrimEnd(':').ToLowerInvariant();
            return IsTargetState(value) || value == "warning" || value == "failed"
                || value == "failure" || value == "exception" || value == "usage" || value == "hdc";
        }

        private static bool IsDiscoveryDiagnosticLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;
            string normalized = line.Trim().ToLowerInvariant();
            if (normalized.Contains("command not found")
                || normalized.Contains("is not recognized as an internal or external command")
                || normalized.Contains("no such file or directory"))
                return true;
            return normalized.StartsWith("[fail]", StringComparison.Ordinal)
                || normalized.StartsWith("[error]", StringComparison.Ordinal)
                || Regex.IsMatch(normalized, @"^(?:warning|usage|error|failed|failure|exception)(?:\s|:|$)", RegexOptions.CultureInvariant);
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
            // Some vendor ps variants omit the header and expose a service
            // name without a dotted bundle. Skip only known metadata; scanning
            // ahead for a bundle can mistake an argument for the executable.
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

        private static bool IsGenericHarmonyProcessName(string name)
        {
            string value = NormalizeProcessToken(name);
            return string.Equals(value, "appspawn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "foundation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "init", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "system_server", StringComparison.OrdinalIgnoreCase);
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
                // OpenHarmony Constants::BASE_USER_RANGE is 200000, not the
                // Android 100000. Numeric ps USER values are UIDs too.
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid) && uid >= 10000)
                    return uid / 200000;
                return -1;
            }
            return ParseHarmonyUserId(text);
        }

        private static ProcessInfo CreateProcess(
            int pid,
            string name,
            string serial,
            int harmonyUserId = -1,
            string explicitBundle = "",
            bool nameIsComm = false,
            bool inferBundleFromProcessName = true,
            int harmonyAppIndex = -1)
        {
            bool hasExplicitBundle = ValidHarmonyApplicationBundle(explicitBundle);
            string processName = name ?? "";
            return new ProcessInfo
            {
                Pid = pid,
                Name = processName,
                DisplayName = processName,
                // A single-segment Bundle is accepted only when HDC labels the
                // field explicitly. Ordinary process names still use the
                // dotted executable heuristic so `foundation` and similar
                // system services do not become fake applications.
                BundleId = hasExplicitBundle
                    ? explicitBundle
                    : nameIsComm || !inferBundleFromProcessName ? "" : BundleFromProcess(processName),
                OwnerName = hasExplicitBundle ? explicitBundle : "",
                OwnerBundleId = hasExplicitBundle ? explicitBundle : "",
                OwnershipSource = hasExplicitBundle
                    ? "process-inventory"
                    : inferBundleFromProcessName ? "" : "proc-inventory",
                OwnershipVerified = hasExplicitBundle,
                DeviceUdid = serial ?? "",
                Platform = "harmony",
                HarmonyUserId = harmonyUserId,
                HarmonyAppIndex = harmonyAppIndex,
                HarmonyNameIsComm = nameIsComm,
                Recommended = false,
                ForegroundApplication = false
            };
        }

        private static bool IsMatchingHarmonyCommAlias(string commName, string fullName)
        {
            string comm = NormalizeProcessToken(commName);
            string full = NormalizeProcessToken(fullName);
            return ProcessTargetMatcher.IsHarmonyCommAlias(comm, full);
        }

        private static bool MatchesCompleteProcessName(ProcessInfo process, string fullName)
        {
            return string.Equals(NormalizeProcessToken(process.Name), NormalizeProcessToken(fullName), StringComparison.Ordinal)
                || (process.HarmonyNameIsComm && IsMatchingHarmonyCommAlias(process.Name, fullName));
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

        internal async Task PopulateDeviceDetailsAsync(DeviceInfo device, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(_deviceDetailBudgetMs);
                foreach (string key in new[]
                {
                    "const.product.model", "const.product.brand", "const.ohos.version", "const.product.name",
                    "const.product.cpu.model", "const.product.soc.model", "const.product.soc",
                    "const.product.gpu.model", "const.product.gpu", "const.hardware.egl",
                    "const.product.cpu.abilist"
                })
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    try
                    {
                        ProcessResult result = await RunShellAsync(device.Udid, new[] { "param", "get", key }, 5000, budget.Token).ConfigureAwait(false);
                        string value = FirstLine(result.Stdout);
                        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value, "fail|error|not found|not exist", RegexOptions.IgnoreCase)) properties[key] = value;
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { }
                }

                string model = Value(properties, "const.product.model");
                string product = Value(properties, "const.product.name");
                device.Name = FirstNonEmpty(model, product, device.Name);
                device.MarketName = device.Name;
                device.Brand = Value(properties, "const.product.brand");
                device.ProductVersion = Value(properties, "const.ohos.version");
                string cpuOutput = "";
                string renderOutput = "";
                foreach (string[] command in BuildDeviceDetailCommands())
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    try
                    {
                        ProcessResult detail = await RunShellAsync(device.Udid, command, 6000, budget.Token).ConfigureAwait(false);
                        if (detail == null || IsHdcFailure(detail)) continue;
                        string output = (detail.Stdout ?? "") + "\n" + (detail.Stderr ?? "");
                        if (command.Length >= 2 && string.Equals(command[0], "cat", StringComparison.Ordinal)
                            && string.Equals(command[1], "/proc/cpuinfo", StringComparison.Ordinal)) cpuOutput += "\n" + output;
                        else if (command.Length >= 1 && string.Equals(command[0], "uname", StringComparison.Ordinal)) cpuOutput += "\n" + output;
                        else if (command.Length >= 2 && string.Equals(command[0], "hidumper", StringComparison.Ordinal)
                            && command.Contains("--cpufreq")) cpuOutput += "\n" + output;
                        else if (command.Length >= 2 && string.Equals(command[0], "hidumper", StringComparison.Ordinal)
                            && command.Contains("RenderService")) renderOutput += "\n" + output;
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { }
                }

                device.CpuInfo = FirstNonEmpty(
                    ParseCpuInfo(cpuOutput),
                    Value(properties, "const.product.cpu.model"),
                    Value(properties, "const.product.soc.model"),
                    Value(properties, "const.product.soc"),
                    Value(properties, "const.product.cpu.abilist"));
                device.GpuInfo = FirstNonEmpty(
                    ParseGpuInfo(renderOutput),
                    Value(properties, "const.product.gpu.model"),
                    Value(properties, "const.product.gpu"),
                    Value(properties, "const.hardware.egl"));
                string parsedResolution = ParseResolution(renderOutput);
                if (parsedResolution.Length > 0) device.Resolution = parsedResolution;
                foreach (string[] command in BuildRenderServiceInfoCommands())
                {
                    if (budget.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    if (device.Resolution.Length > 0) break;
                    try
                    {
                        ProcessResult resolution = await RunShellAsync(device.Udid, command, 5000, budget.Token).ConfigureAwait(false);
                        if (resolution == null || IsHdcFailure(resolution)) continue;
                        string parsed = ParseResolution((resolution.Stdout ?? "") + "\n" + (resolution.Stderr ?? ""));
                        if (parsed.Length > 0)
                        {
                            device.Resolution = parsed;
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        break;
                    }
                    catch { }
                }
            }
        }

        internal static IReadOnlyList<string[]> BuildDeviceDetailCommands()
        {
            return new[]
            {
                new[] { "cat", "/proc/cpuinfo" },
                new[] { "uname", "-m" },
                new[] { "hidumper", "--cpufreq" },
                new[] { "hidumper", "-s", "RenderService", "-a", "gles" },
                new[] { "hidumper", "-s", "RenderService", "-a", "screen" }
            };
        }

        internal static string ParseCpuInfo(string text)
        {
            string architecture = "";
            HashSet<int> cores = new HashSet<int>();
            List<long> maxFrequencies = new List<long>();
            foreach (string line in Lines(text))
            {
                Match match = Regex.Match(line ?? "",
                    @"(?i)^\s*(?:hardware|model\s+name|processor|cpu\s+model|soc|chip)\s*[:=]\s*(.+?)\s*$",
                    RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                string value = CleanDeviceDetail(match.Groups[1].Value);
                if (value.Length == 0 || Regex.IsMatch(value, @"^(?:\d+|0x[0-9a-f]+)$", RegexOptions.IgnoreCase)) continue;
                return value;
            }
            Match architectureMatch = Regex.Match(text ?? "",
                @"(?im)^\s*(?:aarch64|arm64|arm64-v8a|armv8(?:-a)?|x86_64|x64|x86)\s*$",
                RegexOptions.CultureInvariant);
            if (architectureMatch.Success)
            {
                string value = architectureMatch.Value.Trim().ToLowerInvariant();
                architecture = value.StartsWith("x86", StringComparison.Ordinal) || value == "x64"
                    ? "x64"
                    : "ARM64";
            }
            else if (Regex.IsMatch(text ?? "", @"(?i)\barm64-v8a\b"))
            {
                architecture = "ARM64";
            }
            foreach (Match core in Regex.Matches(text ?? "",
                @"(?i)/cpu(?<id>\d+)/cpufreq/cpuinfo_max_freq\s*\r?\n\s*(?<freq>\d+)",
                RegexOptions.CultureInvariant))
            {
                int id;
                long frequency;
                if (int.TryParse(core.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) cores.Add(id);
                if (long.TryParse(core.Groups["freq"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frequency))
                    maxFrequencies.Add(frequency);
            }
            if (architecture.Length == 0 && cores.Count == 0) return "";
            List<string> parts = new List<string>();
            if (architecture.Length > 0) parts.Add(architecture);
            if (cores.Count > 0) parts.Add(cores.Count.ToString(CultureInfo.InvariantCulture) + "核");
            if (maxFrequencies.Count > 0)
            {
                double ghz = maxFrequencies.Max() / 1000000.0;
                parts.Add(ghz.ToString("0.##", CultureInfo.InvariantCulture) + " GHz");
            }
            return string.Join(" / ", parts);
        }

        internal static string ParseGpuInfo(string text)
        {
            string vendor = "";
            foreach (string line in Lines(text))
            {
                Match match = Regex.Match(line ?? "",
                    @"(?i)^\s*(?:gl_renderer|gles|gpu(?:\s+model)?|renderer|opengl[_ ]+renderer|vulkan(?:[_ ]+renderer)?)\s*[:=]\s*(.+?)\s*$",
                    RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    string value = CleanDeviceDetail(match.Groups[1].Value);
                    if (value.Length > 0) return value;
                }
                Match vendorMatch = Regex.Match(line ?? "",
                    @"(?i)^\s*gl_vendor\s*[:=]\s*(.+?)\s*$",
                    RegexOptions.CultureInvariant);
                if (vendorMatch.Success) vendor = CleanDeviceDetail(vendorMatch.Groups[1].Value);
            }
            return vendor;
        }

        private static string CleanDeviceDetail(string value)
        {
            string text = FirstLine(value).Trim().Trim('"', '\'');
            if (text.Length == 0 || Regex.IsMatch(text,
                @"(?i)^(?:error|failed|failure|permission\s+denied|not\s+found|unknown|none|null)\b")) return "";
            return Sanitize(text, 160);
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
