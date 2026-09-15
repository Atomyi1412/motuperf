using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoTuPerf.Platform
{
    public sealed class RuntimeToolResolver
    {
        public RuntimeToolResolver(string baseDirectory, bool packaged)
            : this(baseDirectory, packaged, ResolveOperatingSystem(), ResolveArchitecture())
        {
        }

        internal RuntimeToolResolver(string baseDirectory, bool packaged, string operatingSystem, string architecture, string personalDirectory = null, string localApplicationData = null)
        {
            BaseDirectory = Path.GetFullPath(baseDirectory ?? "");
            IsPackaged = packaged;
            OperatingSystem = (operatingSystem ?? "unknown").Trim().ToLowerInvariant();
            Architecture = (architecture ?? "unknown").Trim().ToLowerInvariant();
            _personalDirectory = personalDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            _localApplicationData = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            BundleDirectory = ResolveBundleDirectory(BaseDirectory, OperatingSystem);
            ResourceDirectory = OperatingSystem == "macos"
                ? Path.Combine(BundleDirectory, "Contents", "Resources")
                : BaseDirectory;
        }

        public string BaseDirectory { get; private set; }
        public bool IsPackaged { get; private set; }
        public string OperatingSystem { get; private set; }
        public string Architecture { get; private set; }
        public string BundleDirectory { get; private set; }
        public string ResourceDirectory { get; private set; }
        public string RuntimeDirectory { get { return Path.Combine(ResourceDirectory, "runtime"); } }
        public string ToolsDirectory { get { return Path.Combine(ResourceDirectory, "tools"); } }
        public string PackageManifestPath { get { return Path.Combine(ResourceDirectory, "motuperf-package.json"); } }
        public string DataLocationConfigPath { get { return Path.Combine(OperatingSystem == "macos" ? MacSupportDirectory : BaseDirectory, "data-location.json"); } }
        private readonly string _personalDirectory;
        private readonly string _localApplicationData;
        private string MacSupportDirectory { get { return Path.Combine(_personalDirectory, "Library", "Application Support", "MoTuPerf"); } }
        private readonly SemaphoreSlim _migrationGate = new SemaphoreSlim(1, 1);

        public string PythonExecutable
        {
            get
            {
                return OperatingSystem == "macos"
                    ? Path.Combine(RuntimeDirectory, "python", "bin", "python3")
                    : Path.Combine(RuntimeDirectory, "python", "python.exe");
            }
        }

        public string AdbExecutable
        {
            get
            {
                return OperatingSystem == "macos"
                    ? Path.Combine(RuntimeDirectory, "android", "adb")
                    : Path.Combine(RuntimeDirectory, "android", "adb.exe");
            }
        }

        public string UserDataDirectory
        {
            get
            {
                PrepareUserDataDirectory();
                return _userDataDirectory;
            }
        }

        private readonly object _dataDirectoryGate = new object();
        private string _userDataDirectory;

        public void PrepareUserDataDirectory()
        {
            lock (_dataDirectoryGate)
            {
                if (!string.IsNullOrWhiteSpace(_userDataDirectory)) return;

                string desired = GetDesiredUserDataDirectory();
                string legacy = GetLegacyUserDataDirectory();
                try { Directory.CreateDirectory(desired); }
                catch (Exception exception)
                {
                    throw new IOException("无法写入 MoTuPerf 安装目录的数据目录，请将软件安装到可写目录或使用管理员权限运行：" + desired, exception);
                }
                RuntimeDataMigration.MoveSettings(legacy, desired);
                _userDataDirectory = desired;
            }
        }

        public Task MigrateLegacyDataAsync(CancellationToken token)
        {
            PrepareUserDataDirectory();
            string target = _userDataDirectory;
            string legacy = GetLegacyUserDataDirectory();
            if (string.Equals(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }
            return Task.Run(async delegate
            {
                await _migrationGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (_dataDirectoryGate) target = _userDataDirectory;
                    if (OperatingSystem == "macos")
                    {
                        // The support root also owns persistent configuration and the default data directory.
                        // Only migrate known legacy categories, never the root containing our target/config.
                        foreach (string name in new[] { "logs", "screenshots", "opened", "app-icons", "downloads", "cache", "sessions", "exports" })
                        {
                            string source = Path.Combine(legacy, name);
                            if (AreSamePath(source, target) || IsPathInside(target, source)) continue;
                            RuntimeDataMigration.MoveTree(source, Path.Combine(target, name), token);
                        }
                    }
                    else RuntimeDataMigration.MoveTree(legacy, target, token);
                }
                finally { _migrationGate.Release(); }
            }, token);
        }

        public async Task<string> ChangeUserDataDirectoryAsync(string directory, CancellationToken token, IProgress<RuntimeDataMigrationProgress> progress = null)
        {
            if (OperatingSystem == "macos" && string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("数据目录不能为空。", "directory");
            string target = NormalizeDirectory(directory);
            PrepareUserDataDirectory();
            await _migrationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string current;
                lock (_dataDirectoryGate) current = _userDataDirectory;
                if (AreSamePath(current, target)) return current;
                if (IsPathInside(target, current) || IsPathInside(current, target))
                    throw new IOException("新数据目录不能位于当前数据目录内部，也不能包含当前数据目录。");
                if (IsPathInside(DataLocationConfigPath, target))
                    throw new IOException("新数据目录不能包含数据位置配置文件。");
                await Task.Run(delegate
                {
                    RuntimeDataMigration.EnsureNoLinks(target);
                    Directory.CreateDirectory(target);
                    EnsureWritableDirectory(target);
                    RuntimeDataMigration.CopyTreeAndCommit(current, target, delegate
                    {
                        WriteDataLocation(target);
                        lock (_dataDirectoryGate) _userDataDirectory = target;
                    }, token, progress);
                }, token).ConfigureAwait(false);
            }
            finally { _migrationGate.Release(); }
            return target;
        }

        public bool IsSupportedTarget
        {
            get
            {
                return (OperatingSystem == "windows" && Architecture == "x64")
                    || (OperatingSystem == "macos" && Architecture == "arm64");
            }
        }

        public string DescribeTarget()
        {
            return OperatingSystem + "-" + Architecture;
        }

        private string GetDesiredUserDataDirectory()
        {
            if (IsPackaged || OperatingSystem == "macos")
            {
                string configured = ReadConfiguredDataDirectory();
                if (!string.IsNullOrWhiteSpace(configured)) return configured;
            }
            if (OperatingSystem == "macos")
            {
                return Path.Combine(MacSupportDirectory, "data");
            }
            return Path.Combine(BaseDirectory, "data");
        }

        private string ReadConfiguredDataDirectory()
        {
            try
            {
                string config = DataLocationConfigPath;
                if (!File.Exists(config) && OperatingSystem == "macos") config = Path.Combine(BaseDirectory, "data-location.json");
                if (!File.Exists(config)) return "";
                StorageLocationDocument document = JsonSerializer.Deserialize<StorageLocationDocument>(File.ReadAllText(config));
                string value = document == null ? "" : document.Directory;
                if (string.IsNullOrWhiteSpace(value)) return "";
                string normalized = NormalizeDirectory(value);
                if (!AreSamePath(config, DataLocationConfigPath)) WriteDataLocation(normalized);
                return normalized;
            }
            catch
            {
                return "";
            }
        }

        private void WriteDataLocation(string directory)
        {
            string temporary = DataLocationConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                RuntimeDataMigration.EnsureNoLinks(DataLocationConfigPath);
                Directory.CreateDirectory(Path.GetDirectoryName(DataLocationConfigPath));
                string json = JsonSerializer.Serialize(new StorageLocationDocument { Directory = directory }, new JsonSerializerOptions { WriteIndented = true });
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, DataLocationConfigPath, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("数据目录不能为空。", "directory");
            return Path.GetFullPath(directory.Trim());
        }

        private bool AreSamePath(string first, string second)
        {
            return string.Equals(
                (first ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                (second ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem == "windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private bool IsPathInside(string possibleChild, string parent)
        {
            string child = Path.GetFullPath(possibleChild).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(root, OperatingSystem == "windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static void EnsureWritableDirectory(string directory)
        {
            string probe = Path.Combine(directory, ".motuperf-write-test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllText(probe, "ok"); }
            finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
        }

        private sealed class StorageLocationDocument
        {
            public string Directory { get; set; }
        }

        private string GetLegacyUserDataDirectory()
        {
            if (OperatingSystem == "macos")
            {
                return MacSupportDirectory;
            }
            return Path.Combine(_localApplicationData, "MoTuPerf");
        }

        public IReadOnlyList<string> ValidatePackagedRuntime()
        {
            List<string> issues = new List<string>();
            if (!IsSupportedTarget)
            {
                issues.Add("当前版本仅支持 Windows x64 和 Apple Silicon macOS。");
                return issues;
            }
            if (!IsPackaged) return issues;
            if (!File.Exists(PackageManifestPath)) issues.Add("缺少运行时清单 motuperf-package.json。");
            if (!File.Exists(PythonExecutable)) issues.Add("缺少内置 Python 运行环境。");
            if (!File.Exists(AdbExecutable)) issues.Add("缺少内置 Android ADB。");
            if (!Directory.Exists(ToolsDirectory)) issues.Add("缺少性能采集脚本目录。");
            return issues;
        }

        private static string ResolveBundleDirectory(string baseDirectory, string operatingSystem)
        {
            if (operatingSystem != "macos") return baseDirectory;

            DirectoryInfo directory = new DirectoryInfo(baseDirectory);
            if (string.Equals(directory.Name, "MacOS", StringComparison.OrdinalIgnoreCase)
                && directory.Parent != null
                && string.Equals(directory.Parent.Name, "Contents", StringComparison.OrdinalIgnoreCase)
                && directory.Parent.Parent != null)
            {
                return directory.Parent.Parent.FullName;
            }
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
            return directory.FullName;
        }

        private static string ResolveArchitecture()
        {
            return RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        }

        private static string ResolveOperatingSystem()
        {
            if (System.OperatingSystem.IsMacOS()) return "macos";
            if (System.OperatingSystem.IsWindows()) return "windows";
            if (System.OperatingSystem.IsLinux()) return "linux";
            return "unknown";
        }
    }
}
