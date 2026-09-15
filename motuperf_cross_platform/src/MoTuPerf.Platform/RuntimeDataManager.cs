using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MoTuPerf.Platform
{
    public sealed class RuntimeDataCategory
    {
        public RuntimeDataCategory(string id, string displayName, string description, string location, long sizeBytes)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Location = location;
            SizeBytes = sizeBytes;
        }

        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public string Location { get; private set; }
        public long SizeBytes { get; private set; }
    }

    public sealed class RuntimeDataCleanupResult
    {
        public RuntimeDataCleanupResult(long deletedBytes, int deletedFiles, int failedFiles, IReadOnlyList<RuntimeDataCleanupFailure> failures)
        {
            DeletedBytes = deletedBytes;
            DeletedFiles = deletedFiles;
            FailedFiles = failedFiles;
            Failures = failures ?? Array.Empty<RuntimeDataCleanupFailure>();
        }

        public long DeletedBytes { get; private set; }
        public int DeletedFiles { get; private set; }
        public int FailedFiles { get; private set; }
        public IReadOnlyList<RuntimeDataCleanupFailure> Failures { get; private set; }
    }

    public sealed class RuntimeDataCleanupFailure
    {
        public RuntimeDataCleanupFailure(string path, string reason)
        {
            Path = path ?? "";
            Reason = reason ?? "未知原因";
        }

        public string Path { get; private set; }
        public string Reason { get; private set; }
    }

    public static class RuntimeDataManager
    {
        private static readonly DataCategoryDefinition[] Definitions =
        {
            new DataCategoryDefinition("settings", "软件设置", "主题和本地偏好设置", "settings.json", false),
            new DataCategoryDefinition("logs", "日志与崩溃记录", "采集日志、错误日志和崩溃记录", "logs", true),
            new DataCategoryDefinition("screenshots", "实时截图", "采集过程中保存的设备截图", "screenshots", true),
            new DataCategoryDefinition("opened", "现场文件缓存", "打开现场文件时生成的解压缓存", "opened", true),
            new DataCategoryDefinition("app-icons", "应用图标缓存", "设备应用列表中的图标缓存", "app-icons", true),
            new DataCategoryDefinition("downloads", "下载文件", "通过软件下载的支持文件", "downloads", true),
            new DataCategoryDefinition("other", "其他数据", "数据目录下未归类的文件", "", false)
        };

        public static IReadOnlyList<RuntimeDataCategory> GetCategories(string root)
        {
            string normalized = NormalizeRoot(root);
            List<RuntimeDataCategory> result = new List<RuntimeDataCategory>();
            foreach (DataCategoryDefinition definition in Definitions)
            {
                string location = string.IsNullOrWhiteSpace(definition.RelativePath)
                    ? normalized
                    : Path.Combine(normalized, definition.RelativePath);
                long size = definition.Id == "other"
                    ? GetOtherFilesSize(normalized)
                    : GetPathSize(location);
                result.Add(new RuntimeDataCategory(definition.Id, definition.DisplayName, definition.Description, location, size));
            }
            return result;
        }

        public static RuntimeDataCleanupResult Delete(string root, IEnumerable<string> categoryIds, IProgress<RuntimeDataCleanupProgress> progress = null)
        {
            string normalized = NormalizeRoot(root);
            HashSet<string> requested = new HashSet<string>(categoryIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            List<CleanupFile> files = new List<CleanupFile>();
            List<string> directories = new List<string>();
            CollectCleanupTargets(normalized, requested, files, directories);
            CleanupProgressReporter reporter = new CleanupProgressReporter(files, progress);
            reporter.Report();
            long deletedBytes = 0;
            int deletedFiles = 0;
            int failedFiles = 0;
            List<RuntimeDataCleanupFailure> failures = new List<RuntimeDataCleanupFailure>();
            foreach (CleanupFile target in files)
            {
                DeleteFile(target.Path, target.Length, ref deletedBytes, ref deletedFiles, ref failedFiles, failures, reporter);
            }
            foreach (string directory in directories) TryDeleteDirectory(directory);
            reporter.Report();
            return new RuntimeDataCleanupResult(deletedBytes, deletedFiles, failedFiles, failures);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.00") + " GB";
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("数据目录不能为空。", "root");
            return Path.GetFullPath(root.Trim());
        }

        private static long GetPathSize(string path)
        {
            if (File.Exists(path))
            {
                try { return new FileInfo(path).Length; } catch { return 0; }
            }
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (string file in EnumerateFiles(path))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }

        private static long GetOtherFilesSize(string root)
        {
            long total = 0;
            foreach (string file in EnumerateRootFiles(root))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }

        private static IEnumerable<string> EnumerateRootFiles(string root)
        {
            if (!Directory.Exists(root)) yield break;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(root); } catch { yield break; }
            foreach (string file in entries)
            {
                if (!string.Equals(Path.GetFileName(file), "settings.json", StringComparison.OrdinalIgnoreCase)) yield return file;
            }
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(current); } catch { continue; }
                foreach (string entry in entries)
                {
                    bool isDirectory = false;
                    bool isFile = false;
                    try
                    {
                        isDirectory = Directory.Exists(entry);
                        isFile = !isDirectory && File.Exists(entry);
                        if (isDirectory)
                        {
                            DirectoryInfo info = new DirectoryInfo(entry);
                            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                        }
                    }
                    catch { }
                    if (isFile) yield return entry;
                }
            }
        }

        private static void CollectCleanupTargets(string root, HashSet<string> requested, List<CleanupFile> files, List<string> directories)
        {
            foreach (DataCategoryDefinition definition in Definitions)
            {
                if (!requested.Contains(definition.Id)) continue;
                if (definition.Id == "other")
                {
                    foreach (string file in EnumerateRootFiles(root)) AddCleanupFile(file, files);
                    continue;
                }

                string location = Path.Combine(root, definition.RelativePath);
                if (File.Exists(location)) AddCleanupFile(location, files);
                else if (Directory.Exists(location))
                {
                    foreach (string file in EnumerateFiles(location)) AddCleanupFile(file, files);
                    directories.Add(location);
                }
            }
        }

        private static void AddCleanupFile(string file, List<CleanupFile> files)
        {
            long length = 0;
            try { length = new FileInfo(file).Length; } catch { }
            files.Add(new CleanupFile(file, length));
        }

        private static void TryDeleteDirectory(string directory)
        {
            try { Directory.Delete(directory, true); } catch { }
        }

        private static void DeleteFile(string file, long knownLength, ref long bytes, ref int deleted, ref int failed, List<RuntimeDataCleanupFailure> failures, CleanupProgressReporter reporter)
        {
            long length = knownLength;
            try
            {
                File.Delete(file);
                if (!File.Exists(file))
                {
                    bytes += length;
                    deleted++;
                    reporter.Record(length, length, false);
                    return;
                }
            }
            catch (Exception exception)
            {
                AddFailure(failures, file, exception.Message);
            }
            failed++;
            if (!failures.Any(item => string.Equals(item.Path, file, StringComparison.OrdinalIgnoreCase)))
                AddFailure(failures, file, "文件仍存在或无法删除");
            reporter.Record(length, 0, true);
        }

        private static void AddFailure(List<RuntimeDataCleanupFailure> failures, string file, string reason)
        {
            if (failures.Count >= 20) return;
            failures.Add(new RuntimeDataCleanupFailure(file, string.IsNullOrWhiteSpace(reason) ? "无法删除" : reason));
        }

        private sealed class CleanupFile
        {
            public CleanupFile(string path, long length) { Path = path; Length = length; }
            public string Path { get; private set; }
            public long Length { get; private set; }
        }

        private sealed class CleanupProgressReporter
        {
            private readonly long _totalBytes;
            private readonly int _totalFiles;
            private readonly IProgress<RuntimeDataCleanupProgress> _progress;
            private long _processedBytes;
            private long _deletedBytes;
            private int _processedFiles;
            private int _failedFiles;

            public CleanupProgressReporter(IReadOnlyList<CleanupFile> files, IProgress<RuntimeDataCleanupProgress> progress)
            {
                _totalBytes = files.Sum(item => item.Length);
                _totalFiles = files.Count;
                _progress = progress;
            }

            public void Record(long processedBytes, long deletedBytes, bool failed)
            {
                _processedBytes = Math.Min(_totalBytes, _processedBytes + Math.Max(0, processedBytes));
                _deletedBytes = Math.Min(_processedBytes, _deletedBytes + Math.Max(0, deletedBytes));
                _processedFiles++;
                if (failed) _failedFiles++;
                Report();
            }

            public void Report()
            {
                if (_progress != null)
                {
                    _progress.Report(new RuntimeDataCleanupProgress(
                        _totalBytes, _processedBytes, _deletedBytes, _totalFiles, _processedFiles, _failedFiles));
                }
            }
        }

        private sealed class DataCategoryDefinition
        {
            public DataCategoryDefinition(string id, string displayName, string description, string relativePath, bool directory)
            {
                Id = id;
                DisplayName = displayName;
                Description = description;
                RelativePath = relativePath;
                Directory = directory;
            }

            public string Id { get; private set; }
            public string DisplayName { get; private set; }
            public string Description { get; private set; }
            public string RelativePath { get; private set; }
            public bool Directory { get; private set; }
        }
    }
}
