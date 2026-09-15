using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public sealed class IosLookupService
    {
        public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken token)
        {
            PlatformDeviceDiscovery discovery = await DiscoverDevicesAsync(token);
            return discovery.Devices;
        }

        public async Task<PlatformDeviceDiscovery> DiscoverDevicesAsync(CancellationToken token)
        {
            PlatformDeviceDiscovery discovery = new PlatformDeviceDiscovery();
            try
            {
                ProcessResult result = await RuntimeTools.RunTideviceAsync(new[] { "list", "--json" }, 12000, token);
                if (result.ExitCode != 0)
                {
                    discovery.Diagnostic = RuntimeTools.DescribeIosFailure(result);
                    await PopulateAppleDriverStatusAsync(discovery, token);
                    return discovery;
                }
                List<object> parsed = SimpleJson.Parse(result.Stdout) as List<object>;
                if (parsed == null)
                {
                    discovery.Diagnostic = "iOS 设备列表返回格式异常，请重新安装 MoTuPerf 后重试。";
                    await PopulateAppleDriverStatusAsync(discovery, token);
                    return discovery;
                }
                for (int i = 0; i < parsed.Count; i++)
                {
                    Dictionary<string, object> item = parsed[i] as Dictionary<string, object>;
                    if (item == null) continue;
                    string udid = Value(item, "udid");
                    if (string.IsNullOrWhiteSpace(udid)) continue;
                    DeviceInfo device = new DeviceInfo
                    {
                        Udid = udid,
                        Name = Clean(Value(item, "name")),
                        MarketName = Clean(Value(item, "market_name")),
                        ProductVersion = Value(item, "product_version"),
                        ConnType = Value(item, "conn_type"),
                        Platform = "ios",
                        Recommended = i == 0
                    };
                    await EnrichDeviceDetailsAsync(device, token);
                    discovery.Devices.Add(device);
                }
                if (discovery.Devices.Count == 0)
                {
                    await PopulateAppleDriverStatusAsync(discovery, token);
                    discovery.Diagnostic = RuntimeTools.NoIosDeviceMessage();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await PopulateAppleDriverStatusAsync(discovery, token);
                discovery.Diagnostic = RuntimeTools.DescribeIosException(ex);
            }
            return discovery;
        }

        private static async Task EnrichDeviceDetailsAsync(DeviceInfo device, CancellationToken token)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Udid)) return;
            try
            {
                ProcessResult result = await RuntimeTools.RunPythonAsync(
                    new[] { DeviceInfoToolPath(), "--udid", device.Udid },
                    15000,
                    token);
                if (result.ExitCode == 0) ApplyDeviceDetails(device, result.Stdout);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Basic list discovery remains usable when optional details are unavailable.
            }
        }

        internal static void ApplyDeviceDetails(DeviceInfo device, string output)
        {
            if (device == null) return;
            Dictionary<string, object> details = SimpleJson.Parse(output ?? "") as Dictionary<string, object>;
            if (details == null) return;

            string productType = FirstNonEmpty(Value(details, "product_type"));
            string marketName = Clean(FirstNonEmpty(Value(details, "market_name")));
            string deviceName = Clean(FirstNonEmpty(Value(details, "device_name")));
            if (!string.IsNullOrWhiteSpace(marketName)) device.MarketName = marketName;
            if (string.IsNullOrWhiteSpace(device.Name) || device.Name == "-")
            {
                device.Name = FirstNonEmpty(deviceName, device.MarketName, productType, device.Name);
            }
            if (string.IsNullOrWhiteSpace(device.MarketName))
            {
                device.MarketName = FirstNonEmpty(device.Name, productType);
            }

            string hardwarePlatform = Clean(Value(details, "hardware_platform"));
            string architecture = Clean(Value(details, "cpu_architecture"));
            if (string.IsNullOrWhiteSpace(device.CpuInfo))
            {
                device.CpuInfo = AppleCpuInfo(hardwarePlatform, architecture);
            }
            if (string.IsNullOrWhiteSpace(device.Resolution))
            {
                device.Resolution = Clean(Value(details, "resolution"));
                if (string.IsNullOrWhiteSpace(device.Resolution))
                {
                    int width = ToInt(Get(details, "screen_width"));
                    int height = ToInt(Get(details, "screen_height"));
                    if (width > 0 && height > 0)
                    {
                        device.Resolution = FormatResolution(width, height);
                    }
                }
            }
        }

        private static string DeviceInfoToolPath()
        {
            return RuntimeTools.ResolveToolPath("ios_device_info.py");
        }

        private static string AppleCpuInfo(string hardwarePlatform, string architecture)
        {
            string platform = (hardwarePlatform ?? "").Trim().ToLowerInvariant();
            string family = "";
            switch (platform)
            {
                case "t8000": family = "Apple A9"; break;
                case "t8010": family = "Apple A10"; break;
                case "t8011": family = "Apple A10X"; break;
                case "t8015": family = "Apple A11"; break;
                case "t8020": family = "Apple A12"; break;
                case "t8027": family = "Apple A12Z"; break;
                case "t8030": family = "Apple A13"; break;
                case "t8101":
                case "t8103": family = "Apple A14"; break;
                case "t8110":
                case "t8112": family = "Apple A15"; break;
                case "t8120": family = "Apple A16"; break;
                case "t8130": family = "Apple A17 Pro"; break;
                case "t8140": family = "Apple A18"; break;
                case "t8150": family = "Apple A19"; break;
                case "t6000":
                case "t6001": family = "Apple M1"; break;
                case "t6020": family = "Apple M2"; break;
                case "t6030": family = "Apple M3"; break;
                case "t6040": family = "Apple M4"; break;
            }
            if (!string.IsNullOrWhiteSpace(family)) return family + " (" + hardwarePlatform + ")";
            if (!string.IsNullOrWhiteSpace(hardwarePlatform))
            {
                return "Apple SoC (" + hardwarePlatform + (string.IsNullOrWhiteSpace(architecture) ? "" : ", " + architecture) + ")";
            }
            return string.IsNullOrWhiteSpace(architecture) ? "" : "Apple SoC (" + architecture + ")";
        }

        private static string FormatResolution(int width, int height)
        {
            return Math.Max(width, height) + "x" + Math.Min(width, height);
        }

        private static async Task PopulateAppleDriverStatusAsync(PlatformDeviceDiscovery discovery, CancellationToken token)
        {
            discovery.AppleDriverMissing = !RuntimeTools.HasAppleMobileDeviceSupport();
            discovery.AppleDriverActionAvailable = RuntimeTools.CanOfferAppleDriverRepair()
                && (discovery.AppleDriverMissing || await RuntimeTools.IsAppleUsbDeviceConnectedAsync(token));
        }

        public async Task<bool?> ProbeOnlineAsync(string udid, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(udid)) return false;
            try
            {
                ProcessResult result = await RuntimeTools.RunTideviceAsync(new[] { "list", "--json" }, 5000, token);
                if (result.ExitCode != 0) return null;
                List<object> parsed = SimpleJson.Parse(result.Stdout) as List<object>;
                if (parsed == null) return null;
                foreach (object value in parsed)
                {
                    Dictionary<string, object> item = value as Dictionary<string, object>;
                    if (item != null && string.Equals(Value(item, "udid"), udid, StringComparison.Ordinal)) return true;
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

        public async Task<ProcessResult> LaunchAppAsync(string udid, string productVersion, string bundleId, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(udid) || string.IsNullOrWhiteSpace(bundleId))
            {
                return new ProcessResult(1, "", "未选择有效的 iOS 设备或 APP。");
            }
            if (UsesRsd(productVersion))
            {
                return await RuntimeTools.RunPythonAsync(Pymobiledevice3LaunchArgs(udid, bundleId), 45000, token);
            }
            return await RuntimeTools.RunTideviceAsync(new[] { "-u", udid, "launch", "--skip-running", bundleId }, 30000, token);
        }

        public static bool IsDeveloperModeDisabled(ProcessResult result)
        {
            if (result == null) return false;
            string output = (result.Stdout ?? "") + "\n" + (result.Stderr ?? "");
            return output.IndexOf("developer mode is disabled", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("developer mode is not enabled", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("developer mode is turned off", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("developer mode isn't enabled", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("device is not in developer mode", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static IEnumerable<string> Pymobiledevice3LaunchArgs(string udid, string bundleId)
        {
            return new[]
            {
                "-m", "pymobiledevice3", "developer", "dvt", "launch", bundleId,
                "--no-kill-existing", "--userspace", "--udid", udid
            };
        }

        public Task<List<AppInfo>> ListAppsAsync(string udid, CancellationToken token)
        {
            return ListAppsAsync(udid, "", token);
        }

        public async Task<List<AppInfo>> ListAppsAsync(string udid, string productVersion, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(udid))
            {
                List<DeviceInfo> devices = await ListDevicesAsync(token);
                if (devices.Count == 0) return new List<AppInfo>();
            }
            List<AppInfo> apps = new List<AppInfo>();
            bool modernIos = UsesRsd(productVersion);
            if (modernIos)
            {
                apps = await ListAppsWithPymobiledevice3Async(udid, token);
            }
            if (apps.Count == 0)
            {
                try
                {
                    ProcessResult result = await RuntimeTools.RunTideviceAsync(TideviceArguments(udid, "applist"), 20000, token);
                    apps = ParseApplist(result.Stdout);
                }
                catch (TimeoutException)
                {
                }
            }
            if (apps.Count == 0 && !modernIos)
            {
                apps = await ListAppsWithPymobiledevice3Async(udid, token);
            }
            // An empty result means enumeration failed or the device has not
            // granted the required access. Never present a fabricated app as
            // a real installable target.
            if (apps.Count == 0) return new List<AppInfo>();
            EnsureWechat(apps);
            return apps.OrderBy(AppRank).ThenBy(delegate(AppInfo app) { return app.Name; }).ThenBy(delegate(AppInfo app) { return app.BundleId; }).ToList();
        }

        public Task<List<ProcessInfo>> ListProcessesAsync(string udid, CancellationToken token)
        {
            return ListProcessesAsync(udid, "", token);
        }

        public async Task<List<ProcessInfo>> ListProcessesAsync(string udid, string productVersion, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(udid))
            {
                List<DeviceInfo> devices = await ListDevicesAsync(token);
                if (devices.Count == 0) return new List<ProcessInfo>();
            }
            List<ProcessInfo> processes = new List<ProcessInfo>();
            bool modernIos = UsesRsd(productVersion);
            if (modernIos)
            {
                processes = await ListProcessesWithPymobiledevice3Async(udid, token);
            }
            if (processes.Count == 0)
            {
                try
                {
                    ProcessResult result = await RuntimeTools.RunTideviceAsync(TideviceArguments(udid, "ps", "--json", "-A"), 12000, token);
                    if (result.ExitCode == 0) processes = ParseTideviceProcesses(result.Stdout);
                }
                catch (TimeoutException)
                {
                }
            }
            if (processes.Count == 0 && !modernIos)
            {
                processes = await ListProcessesWithPymobiledevice3Async(udid, token);
            }
            foreach (ProcessInfo process in processes)
            {
                process.DeviceUdid = udid ?? "";
            }
            return processes
                .Where(ProcessTargetMatcher.IsValidTarget)
                .OrderBy(ProcessRank)
                .ThenByDescending(delegate(ProcessInfo process) { return process.Pid; })
                .ThenBy(delegate(ProcessInfo process) { return process.Name; })
                .ToList();
        }

        public static string TideviceArgs(string udid, string rest)
        {
            return string.IsNullOrWhiteSpace(udid) ? rest : "-u \"" + udid + "\" " + rest;
        }

        private static IEnumerable<string> TideviceArguments(string udid, params string[] rest)
        {
            List<string> arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(udid))
            {
                arguments.Add("-u");
                arguments.Add(udid);
            }
            if (rest != null) arguments.AddRange(rest);
            return arguments;
        }

        public static bool UsesRsd(string productVersion)
        {
            if (string.IsNullOrWhiteSpace(productVersion)) return false;
            int separator = productVersion.IndexOf('.');
            string majorText = separator < 0 ? productVersion : productVersion.Substring(0, separator);
            int major;
            return int.TryParse(majorText, out major) && major >= 17;
        }

        private static List<AppInfo> ParseApplist(string output)
        {
            List<AppInfo> apps = new List<AppInfo>();
            string[] lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int lastSpace = line.LastIndexOf(' ');
                if (lastSpace <= 0) continue;
                string left = line.Substring(0, lastSpace).Trim();
                string version = line.Substring(lastSpace + 1).Trim();
                int firstSpace = left.IndexOf(' ');
                string bundle = firstSpace >= 0 ? left.Substring(0, firstSpace).Trim() : left;
                if (!bundle.Contains(".")) continue;
                string name = firstSpace >= 0 ? left.Substring(firstSpace + 1).Trim() : bundle;
                Tuple<bool, string> rec = ClassifyApp(bundle, name);
                apps.Add(new AppInfo
                {
                    BundleId = bundle,
                    Name = Clean(name),
                    Version = version,
                    Platform = "ios",
                    Recommended = rec.Item1,
                    Reason = rec.Item2
                });
            }
            return apps;
        }

        private static async Task<List<AppInfo>> ListAppsWithPymobiledevice3Async(string udid, CancellationToken token)
        {
            try
            {
                ProcessResult result = await RuntimeTools.RunPythonAsync(
                    Pymobiledevice3Args(udid, "apps list --type User"),
                    45000,
                    token);
                return result.ExitCode == 0 ? ParsePymobiledevice3Apps(result.Stdout) : new List<AppInfo>();
            }
            catch (TimeoutException)
            {
                return new List<AppInfo>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new List<AppInfo>();
            }
        }

        private static async Task<List<ProcessInfo>> ListProcessesWithPymobiledevice3Async(string udid, CancellationToken token)
        {
            try
            {
                string processTool = ModernProcessListToolPath();
                ProcessResult result = await RuntimeTools.RunPythonAsync(
                    new[] { processTool, "--udid", udid, "--interval", "1000" },
                    45000,
                    token);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(DescribeProcessListFailure(result));
                }
                List<ProcessInfo> processes = ParsePymobiledevice3Processes(result.Stdout);
                if (processes.Count == 0)
                {
                    throw new InvalidOperationException("iOS 进程列表返回了空结果，请确认设备已解锁、已开启开发者模式后重试。");
                }
                return processes;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("iOS 进程读取超时，请保持设备解锁，重新插拔 USB 后再试。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        private static string DescribeProcessListFailure(ProcessResult result)
        {
            string output = ((result == null ? "" : result.Stderr) + "\n" + (result == null ? "" : result.Stdout)).Trim();
            string lower = output.ToLowerInvariant();
            if (lower.Contains("dataclassfielderror") || lower.Contains("construct_typed"))
            {
                return "iOS 运行组件版本不兼容，请重新安装最新版 MoTuPerf。";
            }
            if (lower.Contains("developer mode") || lower.Contains("invalidservice"))
            {
                return "iOS 开发者服务不可用，请开启开发者模式、解锁设备并重新插拔 USB。";
            }
            string detail = FirstNonEmptyLine(output);
            return string.IsNullOrWhiteSpace(detail)
                ? "iOS 进程读取失败，请保持设备解锁并重试。"
                : "iOS 进程读取失败：" + detail;
        }

        private static string FirstNonEmptyLine(string value)
        {
            string[] lines = (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return "";
            string line = lines[0].Trim();
            return line.Length <= 180 ? line : line.Substring(0, 180) + "...";
        }

        private static string Pymobiledevice3Args(string udid, string command)
        {
            return "-m pymobiledevice3 " + command + " --userspace --udid \"" + udid + "\"";
        }

        private static string ModernProcessListToolPath()
        {
            return RuntimeTools.ResolveToolPath("ios_process_list.py");
        }

        private static List<AppInfo> ParsePymobiledevice3Apps(string output)
        {
            Dictionary<string, object> parsed = SimpleJson.Parse(output) as Dictionary<string, object>;
            List<AppInfo> apps = new List<AppInfo>();
            if (parsed == null) return apps;
            foreach (KeyValuePair<string, object> pair in parsed)
            {
                Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                if (item == null) continue;
                string bundle = FirstNonEmpty(Value(item, "CFBundleIdentifier"), pair.Key);
                if (string.IsNullOrWhiteSpace(bundle)) continue;
                string name = Clean(FirstNonEmpty(Value(item, "CFBundleDisplayName"), Value(item, "CFBundleName"), bundle));
                Tuple<bool, string> rec = ClassifyApp(bundle, name);
                apps.Add(new AppInfo
                {
                    BundleId = bundle,
                    Name = name,
                    Version = FirstNonEmpty(Value(item, "CFBundleShortVersionString"), Value(item, "CFBundleVersion")),
                    Platform = "ios",
                    Recommended = rec.Item1,
                    Reason = rec.Item2
                });
            }
            return apps;
        }

        private static List<ProcessInfo> ParseTideviceProcesses(string output)
        {
            List<object> parsed = SimpleJson.Parse(output) as List<object>;
            List<ProcessInfo> processes = new List<ProcessInfo>();
            if (parsed == null) return processes;
            foreach (object obj in parsed)
            {
                Dictionary<string, object> item = obj as Dictionary<string, object>;
                if (item == null) continue;
                AddProcess(processes, item, "name", "bundle_id", "display_name");
            }
            return processes;
        }

        private static List<ProcessInfo> ParsePymobiledevice3Processes(string output)
        {
            List<object> parsed = SimpleJson.Parse(output) as List<object>;
            List<ProcessInfo> processes = new List<ProcessInfo>();
            if (parsed == null) return processes;
            foreach (object obj in parsed)
            {
                Dictionary<string, object> item = obj as Dictionary<string, object>;
                if (item == null) continue;
                string name = Value(item, "name");
                bool isApplication = ToBool(Get(item, "isApplication"));
                if (!isApplication && !IsIosWebKitProcess(name)) continue;
                AddProcess(processes, item, "name", "bundleIdentifier", "displayLocalizedAppName");
            }
            return processes;
        }

        private static bool IsIosWebKitProcess(string name)
        {
            return name == "com.apple.WebKit.WebContent" ||
                name == "com.apple.WebKit.GPU" ||
                name == "com.apple.WebKit.Networking";
        }

        private static void AddProcess(
            List<ProcessInfo> processes,
            Dictionary<string, object> item,
            string nameKey,
            string bundleKey,
            string displayNameKey)
        {
            int pid = ToInt(Get(item, "pid"));
            string name = Value(item, nameKey);
            if (pid <= 0 || string.IsNullOrWhiteSpace(name)) return;
            ProcessInfo process = new ProcessInfo
            {
                Pid = pid,
                Name = name,
                BundleId = Value(item, bundleKey),
                DisplayName = Clean(Value(item, displayNameKey)),
                StartedAt = Value(item, "startDate"),
                ResponsiblePid = ToInt(Get(item, "responsiblePID")),
                CoalitionId = ToLong(Get(item, "coalitionID")),
                StartAbsTime = ToLong(Get(item, "startAbsTime")),
                ProcessUniqueId = ToLong(Get(item, "processUniqueID")),
                OwnerPid = ToInt(Get(item, "ownerPID")),
                OwnerName = Clean(Value(item, "ownerName")),
                OwnerBundleId = Value(item, "ownerBundleIdentifier"),
                OwnerDisplayName = Clean(Value(item, "ownerDisplayLocalizedAppName")),
                OwnershipSource = Value(item, "ownershipSource"),
                OwnershipVerified = ToBool(Get(item, "ownershipVerified")),
                OwnershipAmbiguous = ToBool(Get(item, "ownershipAmbiguous")),
                ApplicationState = Value(item, "applicationState"),
                ApplicationExecutablePath = Value(item, "applicationExecutablePath"),
                ForegroundApplication = ToBool(Get(item, "foregroundApplication")),
                Platform = "ios"
            };
            if (IsIosWebKitProcess(process.Name) && process.OwnershipVerified)
            {
                string owner = FirstNonEmpty(process.OwnerDisplayName, process.OwnerBundleId, process.OwnerName);
                process.DisplayName = owner + " · " + WebKitRoleLabel(process.Name);
            }
            Tuple<bool, string> rec = ClassifyProcess(process);
            process.Recommended = rec.Item1;
            process.Reason = rec.Item2;
            processes.Add(process);
        }

        private static void EnsureWechat(List<AppInfo> apps)
        {
            AppInfo existing = apps.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == "com.tencent.xin"; });
            if (existing == null) return;
            existing.Recommended = true;
            if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = "WeChat";
            if (string.IsNullOrWhiteSpace(existing.Reason)) existing.Reason = "微信宿主应用";
        }

        private static List<AppInfo> KnownHostApps()
        {
            return new List<AppInfo>();
        }

        private static Tuple<bool, string> ClassifyApp(string bundle, string name)
        {
            if (bundle == "com.tencent.xin") return Tuple.Create(true, "微信宿主应用");
            if (bundle == "com.tencent.mqq" || name == "QQ" || name == "WeChat")
            {
                return Tuple.Create(true, "腾讯宿主应用");
            }
            return Tuple.Create(false, "");
        }

        private static Tuple<bool, string> ClassifyProcess(ProcessInfo process)
        {
            string name = process == null ? "" : process.Name;
            string bundle = process == null ? "" : process.BundleId;
            if (process != null && process.ForegroundApplication)
            {
                return Tuple.Create(true, "当前前台 iOS APP 主进程");
            }
            if (bundle == "com.tencent.xin" || name == "WeChat") return Tuple.Create(true, "微信主进程");
            if (!string.IsNullOrWhiteSpace(bundle) && !bundle.StartsWith("com.apple.", StringComparison.Ordinal))
            {
                return Tuple.Create(false, "第三方 APP 进程");
            }
            if (IsIosWebKitProcess(name))
            {
                if (process.OwnershipVerified && process.OwnerBundleId == "com.tencent.xin")
                {
                    string role = WebKitRoleLabel(name);
                    return Tuple.Create(name != "com.apple.WebKit.Networking", "微信 " + role + "（宿主 pid " + process.OwnerPid + "）");
                }
                if (process.OwnershipVerified)
                {
                    return Tuple.Create(false, WebKitRoleLabel(name) + "（宿主 " + FirstNonEmpty(process.OwnerDisplayName, process.OwnerBundleId, process.OwnerName) + "）");
                }
                return Tuple.Create(false, WebKitRoleLabel(name) + (process.OwnershipAmbiguous ? "（宿主归属冲突）" : "（宿主未确认）"));
            }
            return Tuple.Create(false, "");
        }

        private static string WebKitRoleLabel(string name)
        {
            if (name == "com.apple.WebKit.WebContent") return "WebContent";
            if (name == "com.apple.WebKit.GPU") return "WebKit GPU";
            if (name == "com.apple.WebKit.Networking") return "WebKit 网络";
            return "WebKit";
        }

        private static int AppRank(AppInfo app)
        {
            if (app.BundleId == "com.tencent.xin") return 0;
            return app.Recommended ? 1 : 2;
        }

        private static int ProcessRank(ProcessInfo process)
        {
            if (process.BundleId == "com.tencent.xin" || process.Name == "WeChat") return 0;
            if (process.OwnerBundleId == "com.tencent.xin" && process.Name == "com.apple.WebKit.WebContent") return 1;
            if (process.OwnerBundleId == "com.tencent.xin" && process.Name == "com.apple.WebKit.GPU") return 2;
            if (!string.IsNullOrWhiteSpace(process.BundleId) && !process.BundleId.StartsWith("com.apple.", StringComparison.Ordinal)) return 3;
            if (process.OwnershipVerified && process.Name.StartsWith("com.apple.WebKit", StringComparison.Ordinal)) return 4;
            if (!string.IsNullOrWhiteSpace(process.BundleId)) return 5;
            return 6;
        }

        private static object Get(Dictionary<string, object> item, string key)
        {
            return item.ContainsKey(key) ? item[key] : null;
        }

        private static string Value(Dictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value == null ? "" : Convert.ToString(value);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static int ToInt(object value)
        {
            if (value == null) return 0;
            if (value is long) return (int)(long)value;
            if (value is double) return (int)(double)value;
            int parsed;
            int.TryParse(Convert.ToString(value), out parsed);
            return parsed;
        }

        private static long ToLong(object value)
        {
            if (value == null) return 0;
            if (value is long) return (long)value;
            if (value is double) return (long)(double)value;
            long parsed;
            long.TryParse(Convert.ToString(value), out parsed);
            return parsed;
        }

        private static bool ToBool(object value)
        {
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) && parsed;
        }

        private static string Clean(string value)
        {
            return value != null && value.Contains("\ufffd") ? "" : value ?? "";
        }
    }
}
