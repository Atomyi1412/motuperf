using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSharpIosPerfMonitor
{
    public static class HdcToolLocator
    {
        internal const string UserConfigurationFileName = "hdc-settings.json";

        public static string Resolve(string bundled)
        {
            return Resolve(bundled, "");
        }

        public static string Resolve(string bundled, string persistedConfigured)
        {
            bool isWindows = OperatingSystem.IsWindows();
            string configured = Environment.GetEnvironmentVariable("MOTUPERF_HDC");
            if (string.IsNullOrWhiteSpace(configured) && IsPlatformExecutablePath(persistedConfigured, isWindows))
                configured = persistedConfigured;
            string path = Environment.GetEnvironmentVariable("PATH");
            return ResolveFrom(
                bundled,
                configured,
                path,
                isWindows,
                KnownSdkRoots(isWindows));
        }

        internal static string ReadConfiguredPath(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) return "";
            try
            {
                string path = Path.Combine(dataDirectory, UserConfigurationFileName);
                if (!File.Exists(path)) return "";
                HdcSettingsDocument document = JsonSerializer.Deserialize<HdcSettingsDocument>(File.ReadAllText(path));
                return document == null ? "" : (document.ExecutablePath ?? "").Trim();
            }
            catch { return ""; }
        }

        internal static void SaveConfiguredPath(string dataDirectory, string executablePath, bool isWindows)
        {
            if (!IsPlatformExecutablePath(executablePath, isWindows))
                throw new FileNotFoundException(
                    isWindows ? "请选择解压后的 hdc.exe 文件。" : "请选择解压后的 hdc 文件。",
                    executablePath);
            string directory = Path.GetFullPath(dataDirectory ?? "");
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("数据目录不能为空。", "dataDirectory");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, UserConfigurationFileName);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string json = JsonSerializer.Serialize(
                    new HdcSettingsDocument { ExecutablePath = Path.GetFullPath(executablePath) },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        internal static bool IsPlatformExecutablePath(string path, bool isWindows)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path)) return false;
            string expectedName = isWindows ? "hdc.exe" : "hdc";
            StringComparison comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFileName(path), expectedName, comparison);
        }

        internal static string FindInSelectedDirectory(string directory, bool isWindows)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return "";

            string name = isWindows ? "hdc.exe" : "hdc";
            string normalized = Path.GetFullPath(directory.Trim().Trim('"'));
            foreach (string candidate in SelectedDirectoryCandidates(normalized, name))
            {
                string match = ExistingPlatformFile(candidate, name, isWindows);
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            return "";
        }

        // Keep the lookup order deterministic. An explicit executable and the
        // packaged runtime always win over a developer machine SDK.
        internal static string ResolveFrom(
            string bundled,
            string configured,
            string path,
            bool isWindows,
            IEnumerable<string> knownSdkRoots)
        {
            string name = isWindows ? "hdc.exe" : "hdc";
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (Path.IsPathRooted(configured) && File.Exists(configured))
                    return Path.GetFullPath(configured);
                throw new FileNotFoundException(
                    "保存的 HDC 文件不存在，请重新选择解压后的 HDC 文件。",
                    configured);
            }

            string bundledPath = ExistingPlatformFile(bundled, name, isWindows);
            if (!string.IsNullOrWhiteSpace(bundledPath)) return bundledPath;

            string pathMatch = FindInPath(path, name);
            if (!string.IsNullOrWhiteSpace(pathMatch)) return pathMatch;

            foreach (string root in knownSdkRoots ?? new string[0])
            {
                foreach (string candidate in ExpandRoot(root, name))
                {
                    string match = ExistingPlatformFile(candidate, name, isWindows);
                    if (!string.IsNullOrWhiteSpace(match)) return match;
                }
            }

            throw new FileNotFoundException(
                "未找到 HDC。请在设备选择页点击“下载鸿蒙连接工具”，只下载官方 Command Line Tools，解压后选择最外层的 command-line-tools 文件夹。",
                bundled);
        }

        private static string ExistingFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? Path.GetFullPath(path)
                : "";
        }

        private static string ExistingPlatformFile(string path, string expectedName, bool isWindows)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedName)) return "";
            string actualName = Path.GetFileName(path);
            StringComparison comparison = isWindows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(actualName, expectedName, comparison)) return "";
            return ExistingFile(path);
        }

        private static string FindInPath(string path, string name)
        {
            foreach (string entry in (path ?? "").Split(Path.PathSeparator))
            {
                string directory = entry.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) continue;
                string match = ExistingFile(Path.Combine(directory, name));
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            return "";
        }

        private static IEnumerable<string> ExpandRoot(string root, string name)
        {
            if (string.IsNullOrWhiteSpace(root)) yield break;
            string normalized = root.Trim().Trim('"');
            if (!Path.IsPathRooted(normalized)) yield break;

            // Environment variables may point at the executable, its
            // toolchains directory, an SDK root, or a DevEco installation.
            string[] relativePaths =
            {
                "",
                Path.Combine("toolchains", name),
                Path.Combine("default", "openharmony", "toolchains", name),
                Path.Combine("default", "toolchains", name),
                Path.Combine("sdk", "default", "openharmony", "toolchains", name),
                Path.Combine("sdk", "default", "toolchains", name),
                Path.Combine("openharmony", "toolchains", name),
                Path.Combine("tools", name),
                Path.Combine("bin", name),
                Path.Combine("sdk", "tools", name),
                Path.Combine("sdk", "bin", name)
            };
            yield return normalized;
            foreach (string relativePath in relativePaths)
                yield return string.IsNullOrWhiteSpace(relativePath)
                    ? Path.Combine(normalized, name)
                    : Path.Combine(normalized, relativePath);
        }

        private static IEnumerable<string> SelectedDirectoryCandidates(string directory, string name)
        {
            // Command Line Tools normally contains sdk/default/openharmony/toolchains.
            // Also accept the SDK/toolchains folder itself so the fallback remains useful
            // when the user has already opened one level in the folder picker.
            string[] relativePaths =
            {
                name,
                Path.Combine("toolchains", name),
                Path.Combine("sdk", "default", "openharmony", "toolchains", name),
                Path.Combine("default", "openharmony", "toolchains", name),
                Path.Combine("sdk", "default", "toolchains", name),
                Path.Combine("default", "toolchains", name),
                Path.Combine("openharmony", "toolchains", name),
                Path.Combine("sdk", "toolchains", name),
                Path.Combine("tools", name),
                Path.Combine("bin", name)
            };
            foreach (string relativePath in relativePaths)
                yield return Path.Combine(directory, relativePath);
        }

        internal static IEnumerable<string> KnownSdkRoots(bool isWindows)
        {
            List<string> roots = new List<string>();
            AddEnvironmentRoots(roots, "MOTUPERF_HDC_SDK", "HDC_HOME", "DEVECO_SDK_HOME", "OHOS_SDK_HOME", "HARMONYOS_SDK_HOME", "HOS_SDK_HOME");

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (isWindows)
            {
                Add(roots, Path.Combine(programFiles, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(programFiles, "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(programFilesX86, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(programFilesX86, "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(localAppData, "OpenHarmony", "Sdk"));
                Add(roots, Path.Combine(localAppData, "Huawei", "Sdk"));
                Add(roots, Path.Combine(localAppData, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(localAppData, "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(roamingAppData, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(roamingAppData, "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(documents, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(documents, "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(home, "OpenHarmony", "Sdk"));
                Add(roots, Path.Combine(home, ".openharmony", "sdk"));
            }
            else
            {
                Add(roots, Path.Combine(home, "Library", "OpenHarmony", "Sdk"));
                Add(roots, Path.Combine(home, "Library", "Huawei", "Sdk"));
                Add(roots, Path.Combine(home, "Library", "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(home, "Library", "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(home, "Library", "openharmony-sdk"));
                Add(roots, Path.Combine(roamingAppData, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(roamingAppData, "Huawei", "DevEco-Studio"));
                Add(roots, Path.Combine(documents, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(documents, "Huawei", "DevEco-Studio"));
                Add(roots, "/Applications/DevEco-Studio.app/Contents/sdk");
                Add(roots, "/Applications/DevEco Studio.app/Contents/sdk");
                Add(roots, Path.Combine(home, "Applications", "DevEco-Studio.app", "Contents", "sdk"));
                Add(roots, Path.Combine(home, "Applications", "DevEco Studio.app", "Contents", "sdk"));
            }
            return roots;
        }

        private static void AddEnvironmentRoots(List<string> roots, params string[] names)
        {
            foreach (string name in names)
            {
                string value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) Add(roots, value);
            }
        }

        private static void Add(List<string> roots, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = value.Trim().Trim('"');
            if (!Path.IsPathRooted(normalized)) return;
            foreach (string existing in roots)
            {
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) return;
            }
            roots.Add(normalized);
        }

        private sealed class HdcSettingsDocument
        {
            public string ExecutablePath { get; set; }
        }
    }
}
