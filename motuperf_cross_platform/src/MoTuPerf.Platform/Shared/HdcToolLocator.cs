using System;
using System.Collections.Generic;
using System.IO;

namespace CSharpIosPerfMonitor
{
    public static class HdcToolLocator
    {
        public static string Resolve(string bundled)
        {
            bool isWindows = OperatingSystem.IsWindows();
            string configured = Environment.GetEnvironmentVariable("MOTUPERF_HDC");
            string path = Environment.GetEnvironmentVariable("PATH");
            return ResolveFrom(
                bundled,
                configured,
                path,
                isWindows,
                KnownSdkRoots(isWindows));
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
                    "MOTUPERF_HDC 必须指向已安装的 HDC 可执行文件。",
                    configured);
            }

            string bundledPath = ExistingFile(bundled);
            if (!string.IsNullOrWhiteSpace(bundledPath)) return bundledPath;

            string pathMatch = FindInPath(path, name);
            if (!string.IsNullOrWhiteSpace(pathMatch)) return pathMatch;

            foreach (string root in knownSdkRoots ?? new string[0])
            {
                foreach (string candidate in ExpandRoot(root, name))
                {
                    string match = ExistingFile(candidate);
                    if (!string.IsNullOrWhiteSpace(match)) return match;
                }
            }

            throw new FileNotFoundException(
                "未找到 HDC。已检查安装包、PATH 以及常见 HarmonyOS/OpenHarmony SDK 目录；请安装官方 SDK 的 toolchains，或设置 MOTUPERF_HDC 指向 HDC 可执行文件。",
                bundled);
        }

        private static string ExistingFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? Path.GetFullPath(path)
                : "";
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
                Path.Combine("openharmony", "toolchains", name)
            };
            yield return normalized;
            foreach (string relativePath in relativePaths)
                yield return string.IsNullOrWhiteSpace(relativePath)
                    ? Path.Combine(normalized, name)
                    : Path.Combine(normalized, relativePath);
        }

        private static IEnumerable<string> KnownSdkRoots(bool isWindows)
        {
            List<string> roots = new List<string>();
            AddEnvironmentRoots(roots, "MOTUPERF_HDC_SDK", "HDC_HOME", "DEVECO_SDK_HOME", "OHOS_SDK_HOME", "HARMONYOS_SDK_HOME", "HOS_SDK_HOME");

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (isWindows)
            {
                Add(roots, Path.Combine(programFiles, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(programFilesX86, "Huawei", "DevEco Studio"));
                Add(roots, Path.Combine(localAppData, "OpenHarmony", "Sdk"));
                Add(roots, Path.Combine(localAppData, "Huawei", "Sdk"));
                Add(roots, Path.Combine(home, "OpenHarmony", "Sdk"));
                Add(roots, Path.Combine(home, ".openharmony", "sdk"));
            }
            else
            {
                Add(roots, Path.Combine(home, "Library", "OpenHarmony", "Sdk"));
                Add(roots, Path.Combine(home, "Library", "Huawei", "Sdk"));
                Add(roots, Path.Combine(home, "Library", "openharmony-sdk"));
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
    }
}
