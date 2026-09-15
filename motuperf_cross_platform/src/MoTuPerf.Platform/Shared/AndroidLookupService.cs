using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public sealed class AndroidLookupService
    {
        public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken token)
        {
            ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, "devices -l", 12000, token);
            if (result.ExitCode != 0) return new List<DeviceInfo>();
            List<DeviceInfo> devices = new List<DeviceInfo>();
            string[] lines = (result.Stdout ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                Match match = Regex.Match(line, @"^(\S+)\s+(\S+)(.*)$");
                if (!match.Success) continue;
                string serial = match.Groups[1].Value;
                string state = match.Groups[2].Value;
                string rest = match.Groups[3].Value;
                if (state != "device") continue;
                string model = Meta(rest, "model");
                string product = Meta(rest, "product");
                string manufacturer = await GetPropAsync(serial, "ro.product.manufacturer", token);
                string brand = await GetPropAsync(serial, "ro.product.brand", token);
                string version = await GetPropAsync(serial, "ro.build.version.release", token);
                string cpuInfo = await CpuInfoAsync(serial, token);
                string gpuInfo = await GpuInfoAsync(serial, token);
                string resolution = await ResolutionAsync(serial, token);
                string displayName = DisplayModelName(model, product, manufacturer, brand);
                devices.Add(new DeviceInfo
                {
                    Udid = serial,
                    Name = displayName,
                    MarketName = displayName,
                    Brand = FirstNonEmpty(Clean(manufacturer), Clean(brand)),
                    ProductVersion = version,
                    ConnType = "ADB",
                    CpuInfo = cpuInfo,
                    GpuInfo = gpuInfo,
                    Resolution = resolution,
                    Platform = "android",
                    Recommended = devices.Count == 0
                });
            }
            return devices;
        }

        public async Task<bool?> ProbeOnlineAsync(string serial, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, "devices", 5000, token);
                if (result.ExitCode != 0) return null;
                foreach (string raw in Lines(result.Stdout))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = Regex.Split(line, @"\s+");
                    if (parts.Length < 2 || !string.Equals(parts[0], serial, StringComparison.Ordinal)) continue;
                    return string.Equals(parts[1], "device", StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<AppInfo>> ListAppsAsync(string serial, CancellationToken token)
        {
            string adb = SerialArgs(serial, "shell pm list packages -3 -f");
            ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, adb, 20000, token);
            if (result.ExitCode != 0) return new List<AppInfo>();
            List<AppInfo> apps = new List<AppInfo>();
            string[] lines = (result.Stdout ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("package:", StringComparison.Ordinal)) line = line.Substring("package:".Length);
                string apkPath = "";
                string packageName = line;
                int split = line.LastIndexOf('=');
                if (split > 0)
                {
                    apkPath = line.Substring(0, split).Trim();
                    packageName = line.Substring(split + 1).Trim();
                }
                if (!packageName.Contains(".")) continue;
                Tuple<bool, string> rec = ClassifyApp(packageName);
                apps.Add(new AppInfo
                {
                    BundleId = packageName,
                    Name = FriendlyAppName(packageName),
                    Version = "",
                    Platform = "android",
                    Recommended = rec.Item1,
                    Reason = rec.Item2,
                    IconKey = AppIconKey(packageName),
                    IconPath = CachedIconPath(packageName),
                    ApkPath = apkPath
                });
            }
            EnsureWechat(apps);
            return apps.OrderBy(AppRank).ThenBy(delegate(AppInfo app) { return app.BundleId; }).ToList();
        }

        public async Task<List<ProcessInfo>> ListProcessesAsync(string serial, CancellationToken token)
        {
            Task<int> foregroundPidTask = ForegroundActivityPidAsync(serial, token);
            Task<string> defaultHomePackageTask = DefaultHomePackageAsync(serial, token);
            ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell ps -A -o PID,NAME,ARGS"), 12000, token);
            if (result.ExitCode != 0)
            {
                result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell ps"), 12000, token);
            }
            if (result.ExitCode != 0) return new List<ProcessInfo>();
            List<ProcessInfo> processes = ParsePs(result.Stdout);
            string defaultHomePackage = await defaultHomePackageTask;
            processes.RemoveAll(delegate(ProcessInfo process) { return IsHomeProcess(process, defaultHomePackage); });
            Dictionary<int, long> startTimes = await ReadProcessStartTimesAsync(serial, processes, token);
            MarkForegroundProcess(processes, await foregroundPidTask);
            foreach (ProcessInfo process in processes)
            {
                process.DeviceUdid = serial ?? "";
                long startTimeTicks;
                if (startTimes.TryGetValue(process.Pid, out startTimeTicks))
                {
                    process.AndroidStartTimeTicks = startTimeTicks;
                }
            }
            return processes
                .Where(ProcessTargetMatcher.IsValidTarget)
                .OrderBy(ProcessRank)
                .ThenByDescending(delegate(ProcessInfo p) { return p.Pid; })
                .ThenBy(delegate(ProcessInfo p) { return p.Name; })
                .ToList();
        }

        public async Task<bool> IsHomeProcessAsync(string serial, ProcessInfo process, CancellationToken token)
        {
            if (IsHomeProcess(process, "")) return true;
            return IsHomeProcess(process, await DefaultHomePackageAsync(serial, token));
        }

        public async Task<ProcessResult> LaunchAppAsync(string serial, string packageName, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return new ProcessResult(1, "", "Package name is empty.");
            }
            string escaped = ShellEscape(packageName.Trim());
            string args = SerialArgs(serial, "shell monkey -p " + escaped + " -c android.intent.category.LAUNCHER 1");
            return await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, args, 10000, token);
        }

        public async Task HydrateAppIconsAsync(string serial, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token)
        {
            if (apps == null || apps.Count == 0 || maxIcons <= 0) return;
            Directory.CreateDirectory(IconCacheDir());
            Dictionary<string, string> iconsByPackage = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (AppInfo app in apps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.BundleId)) continue;
                string cached = CachedIconPath(app.BundleId);
                if (string.IsNullOrWhiteSpace(cached)) continue;
                app.IconPath = cached;
                iconsByPackage[app.BundleId] = cached;
            }
            ApplyIconPathsToProcesses(iconsByPackage, processes);

            int extracted = 0;
            foreach (AppInfo app in apps.OrderBy(IconHydrationRank).ThenBy(delegate(AppInfo item) { return item.BundleId; }))
            {
                token.ThrowIfCancellationRequested();
                if (extracted >= maxIcons) break;
                if (app == null || string.IsNullOrWhiteSpace(app.BundleId) || string.IsNullOrWhiteSpace(app.ApkPath)) continue;
                if (!string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath)) continue;
                string iconPath = await ExtractAppIconAsync(serial, app.BundleId, app.ApkPath, token);
                if (string.IsNullOrWhiteSpace(iconPath)) continue;
                app.IconPath = iconPath;
                iconsByPackage[app.BundleId] = iconPath;
                extracted++;
            }
            ApplyIconPathsToProcesses(iconsByPackage, processes);
        }

        public static string SerialArgs(string serial, string rest)
        {
            return string.IsNullOrWhiteSpace(serial) ? rest : "-s \"" + serial + "\" " + rest;
        }

        public static IEnumerable<string> AdbExecOutArgs(string serial, params string[] rest)
        {
            if (!string.IsNullOrWhiteSpace(serial))
            {
                yield return "-s";
                yield return serial;
            }
            yield return "exec-out";
            foreach (string item in rest)
            {
                yield return item;
            }
        }

        private static void ApplyIconPathsToProcesses(Dictionary<string, string> iconsByPackage, IList<ProcessInfo> processes)
        {
            if (iconsByPackage == null || processes == null) return;
            foreach (ProcessInfo process in processes)
            {
                if (process == null || string.IsNullOrWhiteSpace(process.BundleId)) continue;
                string iconPath;
                if (iconsByPackage.TryGetValue(process.BundleId, out iconPath))
                {
                    process.IconPath = iconPath;
                }
            }
        }

        private static async Task<string> ExtractAppIconAsync(string serial, string packageName, string apkPath, CancellationToken token)
        {
            try
            {
                string outputPath = IconCachePath(packageName, ".png");
                string tempPath = outputPath + ".tmp";
                foreach (string entry in PreferredIconEntries(packageName))
                {
                    if (await TryExtractIconEntryAsync(serial, apkPath, entry, outputPath, tempPath, token)) return outputPath;
                }

                ProcessResult list = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell unzip -l " + ShellEscape(apkPath)), 8000, token);
                if (list.ExitCode != 0) return "";
                foreach (IconCandidate candidate in BestIconCandidates(list.Stdout).Take(8))
                {
                    if (await TryExtractIconEntryAsync(serial, apkPath, candidate.Entry, outputPath, tempPath, token)) return outputPath;
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        private static async Task<bool> TryExtractIconEntryAsync(string serial, string apkPath, string entry, string outputPath, string tempPath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(entry)) return false;
            try
            {
                TryDelete(tempPath);
                ProcessResult extract = await ProcessRunner.RunToFileAsync(
                    RuntimeTools.AdbExecutable,
                    AdbExecOutArgs(serial, "unzip", "-p", apkPath, entry),
                    tempPath,
                    5000,
                    token);
                if (extract.ExitCode != 0 || !IsValidPng(tempPath))
                {
                    TryDelete(tempPath);
                    return false;
                }
                TryDelete(outputPath);
                File.Move(tempPath, outputPath);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        public static bool IsValidPng(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                FileInfo file = new FileInfo(path);
                if (file.Length < 16) return false;
                byte[] header = new byte[8];
                using (FileStream stream = File.OpenRead(path))
                {
                    int read = stream.Read(header, 0, header.Length);
                    if (read != header.Length) return false;
                }
                return header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                    header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
            }
            catch
            {
                return false;
            }
        }

        private static IconCandidate BestIconCandidate(string unzipList)
        {
            return BestIconCandidates(unzipList).FirstOrDefault();
        }

        private static List<IconCandidate> BestIconCandidates(string unzipList)
        {
            List<IconCandidate> candidates = new List<IconCandidate>();
            string[] lines = (unzipList ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                Match match = Regex.Match(raw ?? "", @"^\s*(\d+)\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s+(.+\.(?:png|webp))\s*$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                long size = 0;
                long.TryParse(match.Groups[1].Value, out size);
                string entry = match.Groups[2].Value.Trim();
                int score = IconEntryScore(entry, size);
                if (score <= 0) continue;
                candidates.Add(new IconCandidate { Entry = entry, Score = score, Size = size });
            }
            return candidates
                .OrderByDescending(delegate(IconCandidate item) { return item.Score; })
                .ThenByDescending(delegate(IconCandidate item) { return item.Size; })
                .ToList();
        }

        private static int IconEntryScore(string entry, long size)
        {
            if (string.IsNullOrWhiteSpace(entry)) return 0;
            string path = entry.Replace('\\', '/').ToLowerInvariant();
            string name = Path.GetFileName(path);
            if (path.EndsWith(".9.png", StringComparison.Ordinal)) return 0;
            if (!path.EndsWith(".png", StringComparison.Ordinal)) return 0;
            if (path.Contains("/raw/") || path.Contains("notification") || path.Contains("notify") ||
                path.Contains("status") || path.Contains("share") || path.Contains("search") ||
                path.Contains("loading") || path.Contains("arrow") || path.Contains("back") ||
                path.Contains("close") || path.Contains("play") || path.Contains("pause") ||
                path.Contains("qrcode") || path.Contains("login") || path.Contains("comment") ||
                path.Contains("message") || path.Contains("white") || path.Contains("grey") ||
                path.Contains("gray") || path.Contains("small"))
            {
                return 0;
            }

            int score = 0;
            if (path.Contains("/mipmap-xxxhdpi")) score += 140;
            else if (path.Contains("/mipmap-xxhdpi")) score += 130;
            else if (path.Contains("/mipmap-xhdpi")) score += 110;
            else if (path.Contains("/mipmap-hdpi")) score += 90;
            else if (path.Contains("/mipmap")) score += 80;
            else if (path.Contains("/drawable-xxxhdpi")) score += 80;
            else if (path.Contains("/drawable-xxhdpi")) score += 70;
            else if (path.Contains("/drawable")) score += 35;

            if (name == "ic_launcher.png") score += 140;
            else if (name.StartsWith("ic_launcher", StringComparison.Ordinal)) score += 120;
            if (name == "icon.png") score += 110;
            if (name == "app_icon.png" || name == "icon_app.png") score += 105;
            if (name.Contains("launcher")) score += 75;
            if (name.Contains("qiyi_icon") || name.Contains("qiyi")) score += 80;
            if (name.Contains("meituan") || name.StartsWith("mt_", StringComparison.Ordinal)) score += 65;
            if (name.Contains("xhs") || name.Contains("xiaohongshu")) score += 65;
            if (name.Contains("ele") || name.Contains("eleme")) score += 60;
            if (name.Contains("wechat") || name.Contains("weixin") || name.Contains("wx")) score += 50;
            if (name.Contains("logo")) score += 30;
            if (name.Contains("round")) score -= 20;
            if (size >= 4000 && size <= 250000) score += 30;
            else if (size >= 1000) score += 10;
            return score;
        }

        private static IEnumerable<string> PreferredIconEntries(string packageName)
        {
            if (packageName == "com.qiyi.video")
            {
                yield return "r/x/qiyi_icon.png";
                yield return "r/aa/qiyi_icon.png";
                yield return "r/u/qiyi_icon.png";
            }
            else if (packageName == "me.ele" || packageName == "com.tencent.reading" || packageName == "com.tencent.qqlive" ||
                packageName == "com.tencent.mtt" || packageName == "com.xunmeng.pinduoduo" || packageName == "com.smile.gifmaker")
            {
                yield return "res/mipmap-xxxhdpi-v4/icon.png";
                yield return "res/mipmap-xxhdpi-v4/icon.png";
                yield return "res/mipmap-xhdpi-v4/icon.png";
                yield return "res/mipmap/icon.png";
            }
            else if (packageName == "com.xingin.xhs")
            {
                yield return "res/mipmap-xxxhdpi-v4/ic_launcher.png";
                yield return "res/mipmap-xxhdpi-v4/ic_launcher.png";
                yield return "res/mipmap-xhdpi-v4/ic_launcher.png";
                yield return "res/mipmap-xxxhdpi-v4/icon.png";
                yield return "res/mipmap-xxhdpi-v4/icon.png";
            }
            else if (packageName == "com.sankuai.meituan")
            {
                yield return "res/mipmap-xxxhdpi-v4/ic_launcher.png";
                yield return "res/mipmap-xxhdpi-v4/ic_launcher.png";
                yield return "res/mipmap-xxxhdpi-v4/icon.png";
                yield return "res/mipmap-xxhdpi-v4/icon.png";
            }
            else if (packageName == "com.tencent.mm")
            {
                yield return "res/mipmap-xxxhdpi-v4/icon.png";
                yield return "res/mipmap-xxhdpi-v4/icon.png";
                yield return "r/a8/kinda_icon_card.png";
            }
        }

        private static List<ProcessInfo> ParsePs(string output)
        {
            List<ProcessInfo> processes = new List<ProcessInfo>();
            string[] lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("PID ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("USER ", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = Regex.Split(line, @"\s+");
                int pid = 0;
                string name = "";
                if (parts.Length >= 2 && int.TryParse(parts[0], out pid))
                {
                    // `ps -o PID,NAME,ARGS` keeps the full process command in the
                    // first ARGS token; NAME may be truncated and later ARGS are not identity.
                    name = parts.Length >= 3 ? parts[2] : parts[1];
                }
                else if (parts.Length >= 9 && int.TryParse(parts[1], out pid))
                {
                    name = parts[parts.Length - 1];
                }
                if (pid <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                if (!IsUserVisibleProcessName(name)) continue;
                Tuple<bool, string> rec = ClassifyProcess(name);
                string packageName = ProcessPackageName(name);
                processes.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    BundleId = packageName,
                    DisplayName = name,
                    Platform = "android",
                    Recommended = rec.Item1,
                    Reason = rec.Item2,
                    IconKey = AppIconKey(packageName),
                    IconPath = CachedIconPath(packageName)
                });
            }
            return processes
                .GroupBy(delegate(ProcessInfo p) { return p.Pid.ToString() + "|" + p.Name; })
                .Select(delegate(IGrouping<string, ProcessInfo> group) { return group.First(); })
                .ToList();
        }

        private static async Task<Dictionary<int, long>> ReadProcessStartTimesAsync(
            string serial,
            IList<ProcessInfo> processes,
            CancellationToken token)
        {
            Dictionary<int, long> empty = new Dictionary<int, long>();
            if (processes == null || processes.Count == 0) return empty;
            string pidList = string.Join(
                " ",
                processes.Where(delegate(ProcessInfo process) { return process != null && process.Pid > 0; })
                    .Select(delegate(ProcessInfo process) { return process.Pid.ToString(CultureInfo.InvariantCulture); })
                    .Distinct());
            if (string.IsNullOrWhiteSpace(pidList)) return empty;

            string command = "for p in " + pidList + "; do cat /proc/$p/stat 2>/dev/null; done";
            List<string> arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(serial))
            {
                arguments.Add("-s");
                arguments.Add(serial);
            }
            arguments.Add("shell");
            arguments.Add("sh");
            arguments.Add("-c");
            arguments.Add(command);
            ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, arguments, 12000, token);
            return result.ExitCode == 0 ? ParseProcessStartTimeTicks(result.Stdout) : empty;
        }

        public static Dictionary<int, long> ParseProcessStartTimeTicks(string output)
        {
            Dictionary<int, long> result = new Dictionary<int, long>();
            string[] lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int nameStart = line.IndexOf(" (", StringComparison.Ordinal);
                int nameEnd = line.LastIndexOf(')');
                if (nameStart <= 0 || nameEnd <= nameStart) continue;
                int pid;
                if (!int.TryParse(line.Substring(0, nameStart), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) continue;
                string[] fields = line.Substring(nameEnd + 1).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                long startTimeTicks;
                if (fields.Length <= 19 || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out startTimeTicks)) continue;
                if (pid > 0 && startTimeTicks > 0) result[pid] = startTimeTicks;
            }
            return result;
        }

        private static async Task<int> ForegroundActivityPidAsync(string serial, CancellationToken token)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell dumpsys activity top"), 10000, token);
                if (result.ExitCode != 0) return 0;
                return ParseForegroundActivityPid(result.Stdout);
            }
            catch
            {
                return 0;
            }
        }

        private static async Task<string> DefaultHomePackageAsync(string serial, CancellationToken token)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    RuntimeTools.AdbExecutable,
                    SerialArgs(serial, "shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME"),
                    8000,
                    token);
                return result.ExitCode == 0 ? ParseDefaultHomePackage(result.Stdout) : "";
            }
            catch
            {
                return "";
            }
        }

        public static string ParseDefaultHomePackage(string output)
        {
            foreach (string raw in (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                int slash = line.IndexOf('/');
                if (slash <= 0) continue;
                string packageName = line.Substring(0, slash).Trim();
                if (packageName.IndexOf('.') <= 0 || packageName.IndexOf(' ') >= 0) continue;
                return packageName;
            }
            return "";
        }

        public static bool IsHomeProcess(ProcessInfo process, string defaultHomePackage)
        {
            if (process == null) return false;
            string packageName = FirstNonEmpty(process.BundleId, ProcessPackageName(process.Name));
            return IsKnownHomePackage(packageName)
                || (!string.IsNullOrWhiteSpace(defaultHomePackage)
                    && string.Equals(packageName, defaultHomePackage, StringComparison.Ordinal));
        }

        public static bool IsKnownHomePackage(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return false;
            return string.Equals(packageName, "com.bbk.launcher2", StringComparison.Ordinal)
                || string.Equals(packageName, "com.vivo.launcher", StringComparison.Ordinal)
                || string.Equals(packageName, "com.android.systemui", StringComparison.Ordinal)
                || packageName.StartsWith("com.android.launcher", StringComparison.Ordinal)
                || packageName.StartsWith("com.google.android.apps.nexuslauncher", StringComparison.Ordinal)
                || packageName.StartsWith("com.miui.home", StringComparison.Ordinal)
                || packageName.StartsWith("com.huawei.android.launcher", StringComparison.Ordinal)
                || packageName.StartsWith("com.oppo.launcher", StringComparison.Ordinal)
                || packageName.StartsWith("com.coloros.launcher", StringComparison.Ordinal);
        }

        private static int ParseForegroundActivityPid(string output)
        {
            int bestPid = 0;
            int bestScore = int.MinValue;
            int order = 0;
            ActivityCandidate current = null;
            string[] lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("ACTIVITY ", StringComparison.Ordinal))
                {
                    CommitActivityCandidate(current, ref bestPid, ref bestScore);
                    current = ActivityCandidate.FromLine(line, ++order);
                    continue;
                }
                if (current == null) continue;
                if (line.Contains("mResumed=true")) current.Resumed = true;
                if (line.Contains("mStopped=false")) current.StoppedFalse = true;
                if (line.IndexOf("AppBrand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("MagicBrush", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    current.AppBrand = true;
                }
            }
            CommitActivityCandidate(current, ref bestPid, ref bestScore);
            return bestPid;
        }

        private static void CommitActivityCandidate(ActivityCandidate candidate, ref int bestPid, ref int bestScore)
        {
            if (candidate == null || candidate.Pid <= 0) return;
            int score = candidate.Score();
            if (score <= bestScore) return;
            bestScore = score;
            bestPid = candidate.Pid;
        }

        private static void MarkForegroundProcess(List<ProcessInfo> processes, int foregroundPid)
        {
            if (foregroundPid <= 0) return;
            ProcessInfo process = processes.FirstOrDefault(delegate(ProcessInfo item) { return item.Pid == foregroundPid; });
            if (process == null) return;
            process.Recommended = true;
            if (process.Name.StartsWith("com.tencent.mm:appbrand", StringComparison.Ordinal))
            {
                process.Reason = "当前前台微信小游戏进程";
            }
            else if (process.Name.StartsWith("com.tencent.mm", StringComparison.Ordinal))
            {
                process.Reason = "当前前台微信相关进程";
            }
            else if (string.IsNullOrWhiteSpace(process.Reason))
            {
                process.Reason = "当前前台 Activity 进程";
            }
        }

        private static async Task<string> GetPropAsync(string serial, string key, CancellationToken token)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell getprop " + key), 6000, token);
                return result.ExitCode == 0 ? Clean(result.Stdout.Trim()) : "";
            }
            catch
            {
                return "";
            }
        }

        private static async Task<string> CpuInfoAsync(string serial, CancellationToken token)
        {
            string cpuInfoText = "";
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell cat /proc/cpuinfo"), 6000, token);
                if (result.ExitCode == 0)
                {
                    cpuInfoText = result.Stdout;
                    foreach (string raw in Lines(result.Stdout))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                        {
                            return Clean(AfterColon(line));
                        }
                    }
                }
            }
            catch
            {
            }
            string manufacturer = await GetPropAsync(serial, "ro.soc.manufacturer", token);
            string model = FirstNonEmpty(
                await GetPropAsync(serial, "ro.soc.model", token),
                await GetPropAsync(serial, "ro.vendor.soc.model", token));
            string platform = FirstNonEmpty(
                await GetPropAsync(serial, "ro.board.platform", token),
                await GetPropAsync(serial, "ro.product.board", token),
                await GetPropAsync(serial, "ro.hardware", token));
            string soc = FirstNonEmpty(JoinParts(" ", manufacturer, model), model, platform);
            if (!string.IsNullOrWhiteSpace(soc) && !string.IsNullOrWhiteSpace(platform) &&
                soc.IndexOf(platform, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return soc + " (" + platform + ")";
            }
            if (!string.IsNullOrWhiteSpace(soc)) return soc;
            return CpuPartsSummary(cpuInfoText);
        }

        private static async Task<string> GpuInfoAsync(string serial, CancellationToken token)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell dumpsys SurfaceFlinger"), 10000, token);
                if (result.ExitCode == 0)
                {
                    foreach (string raw in Lines(result.Stdout))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("GLES:", StringComparison.OrdinalIgnoreCase))
                        {
                            return Clean(AfterColon(line));
                        }
                    }
                }
            }
            catch
            {
            }
            string egl = await GetPropAsync(serial, "ro.hardware.egl", token);
            string vulkan = await GetPropAsync(serial, "ro.hardware.vulkan", token);
            string driver = await GetPropAsync(serial, "ro.gfx.driver.1", token);
            return FirstNonEmpty(JoinParts(" / ", egl, vulkan, driver), egl, vulkan, driver);
        }

        private static string CpuPartsSummary(string cpuInfoText)
        {
            List<string> processors = new List<string>();
            List<string> parts = new List<string>();
            foreach (string raw in Lines(cpuInfoText))
            {
                string line = raw.Trim();
                if (line.StartsWith("processor", StringComparison.OrdinalIgnoreCase))
                {
                    processors.Add(AfterColon(line).Trim());
                }
                else if (line.StartsWith("CPU part", StringComparison.OrdinalIgnoreCase))
                {
                    string part = Clean(AfterColon(line).Trim());
                    if (!string.IsNullOrWhiteSpace(part) && !parts.Contains(part)) parts.Add(part);
                }
            }
            if (processors.Count == 0 && parts.Count == 0) return "";
            string coreText = processors.Count > 0 ? processors.Count.ToString() + " cores" : "";
            string partText = parts.Count > 0 ? "ARM parts " + string.Join("/", parts) : "";
            return JoinParts(" ", coreText, partText);
        }

        private static string JoinParts(string separator, params string[] parts)
        {
            return string.Join(separator, (parts ?? new string[0]).Select(Clean).Where(delegate(string value) { return !string.IsNullOrWhiteSpace(value); }));
        }

        private static async Task<string> ResolutionAsync(string serial, CancellationToken token)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, SerialArgs(serial, "shell wm size"), 6000, token);
                if (result.ExitCode != 0) return "";
                foreach (string raw in Lines(result.Stdout))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("Physical size:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Clean(AfterColon(line));
                    }
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        private static string AfterColon(string text)
        {
            int index = (text ?? "").IndexOf(':');
            return index < 0 ? text ?? "" : text.Substring(index + 1);
        }

        private static IEnumerable<string> Lines(string text)
        {
            return (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        private static string Meta(string text, string key)
        {
            Match match = Regex.Match(text ?? "", @"(?:^|\s)" + Regex.Escape(key) + @":([^\s]+)");
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string DisplayModelName(string model, string product, string manufacturer, string brand)
        {
            string cleanedModel = FirstNonEmpty(Clean(model), Clean(product), "Android 设备");
            string prefix = FirstNonEmpty(Clean(manufacturer), Clean(brand));
            if (string.IsNullOrWhiteSpace(prefix)) return cleanedModel;
            if (cleanedModel.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return cleanedModel;
            if (cleanedModel.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)) return prefix + "-" + cleanedModel.Substring(prefix.Length + 1);
            if (cleanedModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return cleanedModel;
            return prefix + "-" + cleanedModel;
        }

        private static void EnsureWechat(List<AppInfo> apps)
        {
            if (apps.Any(delegate(AppInfo app) { return app.BundleId == "com.tencent.mm"; })) return;
            apps.Add(new AppInfo
            {
                BundleId = "com.tencent.mm",
                Name = "微信",
                Platform = "android",
                Recommended = true,
                Reason = "微信宿主应用",
                IconKey = "wechat",
                IconPath = CachedIconPath("com.tencent.mm")
            });
        }

        private static Tuple<bool, string> ClassifyApp(string packageName)
        {
            if (packageName == "com.tencent.mm") return Tuple.Create(true, "微信宿主应用");
            if (packageName.StartsWith("com.tencent.", StringComparison.Ordinal)) return Tuple.Create(true, "腾讯应用");
            return Tuple.Create(false, "");
        }

        private static Tuple<bool, string> ClassifyProcess(string name)
        {
            if (name == "com.tencent.mm") return Tuple.Create(true, "微信主进程");
            if (name.StartsWith("com.tencent.mm:appbrand", StringComparison.Ordinal)) return Tuple.Create(true, "微信小游戏进程");
            if (name.StartsWith("com.tencent.mm:", StringComparison.Ordinal)) return Tuple.Create(true, "微信子进程");
            if (name.StartsWith("com.tencent.", StringComparison.Ordinal)) return Tuple.Create(true, "腾讯应用进程");
            return Tuple.Create(false, "");
        }

        private static bool IsUserVisibleProcessName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.StartsWith("[", StringComparison.Ordinal) || name.StartsWith("/", StringComparison.Ordinal)) return false;
            if (name.IndexOf('@') >= 0) return false;
            if (name.StartsWith("android.hardware.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("android.hidl.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("android.system.", StringComparison.Ordinal)) return false;
            if (name.IndexOf('.') < 0) return false;
            if (name.StartsWith("com.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("cn.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("me.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("tv.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("android.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("org.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("ctrip.", StringComparison.Ordinal)) return true;
            return false;
        }

        private static string ProcessPackageName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return "";
            int colon = processName.IndexOf(':');
            return colon > 0 ? processName.Substring(0, colon) : processName;
        }

        private static string FriendlyAppName(string packageName)
        {
            if (packageName == "com.tencent.mm") return "微信";
            if (packageName == "com.qiyi.video") return "爱奇艺";
            if (packageName == "com.sankuai.meituan") return "美团";
            if (packageName == "com.tencent.reading") return "天天快报";
            if (packageName == "me.ele") return "饿了么";
            if (packageName == "com.xingin.xhs") return "小红书";
            if (packageName == "com.tencent.qqlive") return "腾讯视频";
            if (packageName == "com.tencent.mtt") return "QQ浏览器";
            if (packageName == "com.xunmeng.pinduoduo") return "拼多多";
            if (packageName == "com.ss.android.ugc.aweme") return "抖音";
            if (packageName == "com.taobao.taobao") return "淘宝";
            if (packageName == "com.eg.android.AlipayGphone") return "支付宝";
            if (packageName == "com.autonavi.minimap") return "高德地图";
            if (packageName == "com.sina.weibo") return "微博";
            if (packageName == "com.smile.gifmaker") return "快手";
            return packageName;
        }

        private static string AppIconKey(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return "android";
            if (packageName == "com.tencent.mm") return "wechat";
            if (packageName.StartsWith("com.tencent.", StringComparison.Ordinal)) return "tencent";
            if (packageName.StartsWith("com.android.", StringComparison.Ordinal) || packageName.StartsWith("android.", StringComparison.Ordinal)) return "android";
            return "app";
        }

        private static string CachedIconPath(string packageName)
        {
            string path = IconCachePath(packageName, ".png");
            if (!File.Exists(path)) return "";
            if (IsValidPng(path)) return path;
            TryDelete(path);
            return "";
        }

        private static string IconCachePath(string packageName, string extension)
        {
            string safe = Regex.Replace(packageName ?? "", @"[^A-Za-z0-9._-]+", "_");
            if (string.IsNullOrWhiteSpace(safe)) safe = "app";
            return Path.Combine(IconCacheDir(), safe + extension);
        }

        private static string IconCacheDir()
        {
            return Path.Combine(RuntimeTools.DataDirectory, "app-icons");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static string ShellEscape(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static int AppRank(AppInfo app)
        {
            if (app.BundleId == "com.tencent.mm") return 0;
            return app.Recommended ? 1 : 2;
        }

        private static int IconHydrationRank(AppInfo app)
        {
            if (app == null) return 99;
            string packageName = app.BundleId ?? "";
            if (packageName == "com.tencent.mm") return 0;
            if (app.Recommended) return 1;
            if (packageName == "com.qiyi.video") return 2;
            if (packageName == "com.sankuai.meituan") return 3;
            if (packageName == "com.tencent.reading") return 4;
            if (packageName == "me.ele") return 5;
            if (packageName == "com.xingin.xhs") return 6;
            if (packageName.StartsWith("com.tencent.", StringComparison.Ordinal)) return 7;
            if (packageName.StartsWith("com.vivo.", StringComparison.Ordinal) || packageName.Contains(".widget")) return 30;
            return 20;
        }

        private static int ProcessRank(ProcessInfo process)
        {
            if (process.Reason == "当前前台微信小游戏进程") return 0;
            if (process.Reason == "当前前台微信相关进程") return 1;
            if (process.Reason == "当前前台 Activity 进程") return 2;
            if (process.Name.StartsWith("com.tencent.mm:appbrand", StringComparison.Ordinal)) return 2;
            if (process.Name == "com.tencent.mm") return 3;
            if (process.Name.StartsWith("com.tencent.mm:", StringComparison.Ordinal)) return 4;
            if (process.Recommended) return 2;
            if (!string.IsNullOrWhiteSpace(process.BundleId)) return 3;
            return 4;
        }

        private sealed class ActivityCandidate
        {
            public int Pid;
            public int Order;
            public bool WeChat;
            public bool AppBrand;
            public bool Resumed;
            public bool StoppedFalse;

            public static ActivityCandidate FromLine(string line, int order)
            {
                Match match = Regex.Match(line ?? "", @"\bpid=(\d+)\b");
                int pid = 0;
                if (match.Success)
                {
                    int.TryParse(match.Groups[1].Value, out pid);
                }
                return new ActivityCandidate
                {
                    Pid = pid,
                    Order = order,
                    WeChat = (line ?? "").Contains("com.tencent.mm"),
                    AppBrand = (line ?? "").IndexOf("AppBrand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (line ?? "").Contains("com.tencent.mm:appbrand")
                };
            }

            public int Score()
            {
                int score = Order;
                if (WeChat) score += 1000;
                if (AppBrand) score += 2000;
                if (StoppedFalse) score += 5000;
                if (Resumed) score += 10000;
                return score;
            }
        }

        private sealed class IconCandidate
        {
            public string Entry;
            public int Score;
            public long Size;
        }

        private static string Clean(string value)
        {
            return value != null && value.Contains("\ufffd") ? "" : value ?? "";
        }
    }
}
