using System;
using System.Collections.Generic;
using System.IO;

namespace MoTuPerf.Platform
{
    internal static class DevelopmentExecutableResolver
    {
        internal static string ResolvePython(
            string bundledPath,
            string configuredPath,
            string operatingSystem,
            string localApplicationData,
            string programFiles,
            string programFilesX86,
            IEnumerable<string> pathDirectories,
            Func<string, bool> fileExists,
            IEnumerable<string> additionalLocalApplicationDataRoots = null,
            IEnumerable<string> registeredExecutablePaths = null)
        {
            string bundled = NormalizePath(bundledPath);
            if (!string.IsNullOrWhiteSpace(bundled) && fileExists(bundled)) return bundled;

            string configured = NormalizePath(configuredPath);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return ResolveConfiguredExecutable(
                    configured,
                    "MOTUPERF_PYTHON",
                    "Python",
                    IsWindowsOperatingSystem(operatingSystem) ? "python.exe" : "python3",
                    pathDirectories,
                    fileExists);
            }

            List<string> candidates = new List<string>();
            List<string> localApplicationDataRoots = UniqueRoots(localApplicationData, additionalLocalApplicationDataRoots);
            string[] versions = { "314", "313", "312", "311", "310", "39", "38" };
            foreach (string version in versions)
            {
                foreach (string root in localApplicationDataRoots)
                {
                    AddCandidate(candidates, root, "Programs", "Python", "Python" + version, "python.exe");
                }
                AddCandidate(candidates, programFiles, "Python" + version, "python.exe");
                AddCandidate(candidates, programFilesX86, "Python" + version, "python.exe");
            }

            if (string.Equals(operatingSystem, "macos", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("/usr/bin/python3");
                candidates.Add("/opt/homebrew/bin/python3");
                candidates.Add("/usr/local/bin/python3");
            }

            AddPathCandidates(candidates, pathDirectories, "python.exe", "python3", "python");
            string resolved = FirstExisting(candidates, fileExists);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;

            // Python installed by the official Windows installer is also recorded
            // in HKCU/HKLM. Treat that OS-owned location as authoritative when a
            // restricted desktop process cannot observe the file through the
            // normal directory scan. The actual process launch remains the final
            // executable validation.
            string registered = FirstRegisteredCandidate(registeredExecutablePaths);
            if (!string.IsNullOrWhiteSpace(registered)) return registered;

            throw new FileNotFoundException(
                "未找到可用的 Python 解释器。开发版请将 MOTUPERF_PYTHON 设置为真实 python.exe 的完整路径，或使用包含内置运行时的安装包。",
                bundledPath ?? "");
        }

        internal static string ResolveAdb(
            string bundledPath,
            string configuredPath,
            string operatingSystem,
            string localApplicationData,
            string androidHome,
            string androidSdkRoot,
            IEnumerable<string> pathDirectories,
            Func<string, bool> fileExists)
        {
            string bundled = NormalizePath(bundledPath);
            if (!string.IsNullOrWhiteSpace(bundled) && fileExists(bundled)) return bundled;

            string configured = NormalizePath(configuredPath);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return ResolveConfiguredExecutable(
                    configured,
                    "MOTUPERF_ADB",
                    "ADB",
                    IsWindowsOperatingSystem(operatingSystem) ? "adb.exe" : "adb",
                    pathDirectories,
                    fileExists);
            }

            List<string> candidates = new List<string>();
            AddAdbCandidate(candidates, androidHome);
            AddAdbCandidate(candidates, androidSdkRoot);
            AddCandidate(candidates, localApplicationData, "Android", "Sdk", "platform-tools", "adb.exe");
            AddPathCandidates(candidates, pathDirectories, "adb.exe", "adb");
            string resolved = FirstExisting(candidates, fileExists);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;

            throw new FileNotFoundException(
                "未找到可用的 Android ADB。开发版请将 MOTUPERF_ADB 设置为真实 adb.exe 的完整路径，或使用包含内置运行时的安装包。",
                bundledPath ?? "");
        }

        internal static bool IsWindowsAppsAlias(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            normalized = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string marker = Path.DirectorySeparatorChar + "Microsoft" + Path.DirectorySeparatorChar + "WindowsApps" + Path.DirectorySeparatorChar;
            string storeMarker = Path.DirectorySeparatorChar + "WindowsApps" + Path.DirectorySeparatorChar;
            return normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf(storeMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveConfiguredExecutable(
            string configured,
            string environmentVariable,
            string displayName,
            string fallbackFileName,
            IEnumerable<string> pathDirectories,
            Func<string, bool> fileExists)
        {
            if (IsWindowsAppsAlias(configured))
            {
                throw new FileNotFoundException(
                    environmentVariable + " 指向了 WindowsApps 占位程序，该程序无法启动采集。请改为真实 " + displayName + " 可执行文件的完整路径。",
                    configured);
            }

            string fileName = Path.GetFileName(configured);
            bool simpleName = string.Equals(configured, fileName, StringComparison.Ordinal);
            if (simpleName)
            {
                List<string> pathCandidates = new List<string>();
                AddPathCandidates(pathCandidates, pathDirectories, fileName, fallbackFileName);
                string found = FirstExisting(pathCandidates, fileExists);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            else if (fileExists(configured))
            {
                return Path.GetFullPath(configured);
            }

            throw new FileNotFoundException(
                environmentVariable + " 指定的 " + displayName + " 不存在：" + configured,
                configured);
        }

        private static void AddAdbCandidate(List<string> candidates, string sdkRoot)
        {
            string root = NormalizePath(sdkRoot);
            if (string.IsNullOrWhiteSpace(root)) return;
            candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
            candidates.Add(Path.Combine(root, "adb.exe"));
        }

        private static void AddCandidate(List<string> candidates, string root, params string[] parts)
        {
            string normalizedRoot = NormalizePath(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot)) return;
            List<string> all = new List<string> { normalizedRoot };
            all.AddRange(parts);
            candidates.Add(Path.Combine(all.ToArray()));
        }

        private static void AddPathCandidates(List<string> candidates, IEnumerable<string> pathDirectories, params string[] fileNames)
        {
            if (pathDirectories == null) return;
            foreach (string directory in pathDirectories)
            {
                string root = NormalizePath(directory);
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (string fileName in fileNames)
                {
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    candidates.Add(Path.Combine(root, fileName));
                }
            }
        }

        private static string FirstExisting(IEnumerable<string> candidates, Func<string, bool> fileExists)
        {
            foreach (string candidate in candidates)
            {
                string normalized = NormalizePath(candidate);
                if (string.IsNullOrWhiteSpace(normalized) || IsWindowsAppsAlias(normalized)) continue;
                if (fileExists(normalized)) return normalized;
            }
            return "";
        }

        private static string FirstRegisteredCandidate(IEnumerable<string> candidates)
        {
            if (candidates == null) return "";
            foreach (string candidate in candidates)
            {
                string normalized = NormalizePath(candidate);
                if (!string.IsNullOrWhiteSpace(normalized) && !IsWindowsAppsAlias(normalized)) return normalized;
            }
            return "";
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(value)) return "";
            try { return Path.GetFullPath(value); }
            catch { return value; }
        }

        private static List<string> UniqueRoots(string primaryRoot, IEnumerable<string> additionalRoots)
        {
            List<string> roots = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddUniqueRoot(roots, seen, primaryRoot);
            if (additionalRoots != null)
            {
                foreach (string root in additionalRoots) AddUniqueRoot(roots, seen, root);
            }
            return roots;
        }

        private static void AddUniqueRoot(List<string> roots, HashSet<string> seen, string root)
        {
            string normalized = NormalizePath(root);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized)) roots.Add(normalized);
        }

        internal static bool IsWindowsOperatingSystem(string operatingSystem)
        {
            return string.Equals(operatingSystem, "windows", StringComparison.OrdinalIgnoreCase);
        }
    }
}
