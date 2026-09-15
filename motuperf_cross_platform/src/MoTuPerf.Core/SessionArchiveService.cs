using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CSharpIosPerfMonitor
{
    public sealed class SessionDocument
    {
        public SessionDocument()
        {
            Format = "";
            AppVersion = "";
            SelectedBundleId = "";
            Samples = new List<PerfSample>();
            Screenshots = new List<SessionScreenshot>();
        }

        public string Format { get; set; }
        public int Version { get; set; }
        public string AppVersion { get; set; }
        public DateTime SavedAt { get; set; }
        public DateTime StartedAt { get; set; }
        public DeviceInfo Device { get; set; }
        public AppInfo App { get; set; }
        public ProcessInfo Process { get; set; }
        public string SelectedBundleId { get; set; }
        public double? SelectedTime { get; set; }
        public bool FollowLatest { get; set; }
        public double ViewStartTime { get; set; }
        public double ViewEndTime { get; set; }
        public bool CaptureScreenshots { get; set; }
        public bool CaptureTemperature { get; set; }
        public bool CaptureThermalState { get; set; }
        public List<PerfSample> Samples { get; set; }
        public List<SessionScreenshot> Screenshots { get; set; }
    }

    public sealed class SessionScreenshot
    {
        public SessionScreenshot()
        {
            OriginalPath = "";
            ArchivePath = "";
        }

        public DateTime Timestamp { get; set; }
        public double ElapsedSec { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string OriginalPath { get; set; }
        public string ArchivePath { get; set; }
        public ScreenshotOrientation Orientation { get; set; }
    }

    public static class SessionArchiveService
    {
        private const long MaximumManifestBytes = 256L * 1024 * 1024;
        private const long MaximumScreenshotBytes = 100L * 1024 * 1024;
        private const long MaximumUncompressedBytes = 8L * 1024 * 1024 * 1024;
        private const int MaximumScreenshots = 100000;
        private const int MaximumArchiveEntries = 100005;

        public static void Save(string path, SessionDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            string fullPath = Path.GetFullPath(path ?? "");
            string parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            if ((document.Screenshots ?? new List<SessionScreenshot>()).Count > MaximumScreenshots)
                throw new InvalidDataException("现场文件截图数量异常。");
            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (ZipArchive archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
                    if (manifestBytes.Length > MaximumManifestBytes)
                        throw new InvalidDataException("现场文件清单过大。");
                    ZipArchiveEntry manifest = archive.CreateEntry("session.json", CompressionLevel.Optimal);
                    using (Stream stream = manifest.Open())
                    {
                        stream.Write(manifestBytes, 0, manifestBytes.Length);
                    }

                    HashSet<string> entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "session.json" };
                    long uncompressedBytes = manifestBytes.Length;
                    foreach (SessionScreenshot screenshot in document.Screenshots ?? new List<SessionScreenshot>())
                    {
                        if (screenshot == null || string.IsNullOrWhiteSpace(screenshot.ArchivePath) || !File.Exists(screenshot.OriginalPath))
                            throw new InvalidDataException("截图文件缺失，现场未保存，请保留当前数据并检查截图目录。");
                        string entryName = NormalizeScreenshotEntry(screenshot.ArchivePath);
                        if (!entryNames.Add(entryName)) throw new InvalidDataException("现场文件包含重复的截图归档名称。");
                        long sourceLength = new FileInfo(screenshot.OriginalPath).Length;
                        if (sourceLength > MaximumScreenshotBytes || uncompressedBytes > MaximumUncompressedBytes - sourceLength)
                            throw new InvalidDataException("现场文件解压后的数据量过大。");
                        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        using (Stream input = File.OpenRead(screenshot.OriginalPath))
                        using (Stream output = entry.Open()) input.CopyTo(output);
                        uncompressedBytes += sourceLength;
                    }
                    if (entryNames.Count > MaximumArchiveEntries) throw new InvalidDataException("现场文件条目数量异常。");
                }
                File.Move(tempPath, fullPath, true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        public static SessionDocument Load(string path, string extractionRoot)
        {
            string fullPath = Path.GetFullPath(path ?? "");
            string targetRoot = Path.GetFullPath(extractionRoot ?? "");
            Directory.CreateDirectory(targetRoot);
            List<string> extractedFiles = new List<string>();
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(fullPath))
                {
                    if (archive.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException("现场文件条目数量异常。");
                    HashSet<string> archiveEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long archiveUncompressedBytes = 0;
                    foreach (ZipArchiveEntry archiveEntry in archive.Entries)
                    {
                        string archiveName = (archiveEntry.FullName ?? "").Replace('\\', '/');
                        if (string.IsNullOrWhiteSpace(archiveName) || archiveName.EndsWith("/", StringComparison.Ordinal)
                            || (archiveName != "session.json" && !archiveName.StartsWith("screenshots/", StringComparison.Ordinal)))
                            throw new InvalidDataException("现场文件包含不受支持的归档路径。");
                        if (!archiveEntryNames.Add(archiveName)) throw new InvalidDataException("现场文件包含重复的归档条目。");
                        if (archiveEntry.Length < 0 || archiveUncompressedBytes > MaximumUncompressedBytes - archiveEntry.Length)
                            throw new InvalidDataException("现场文件解压后的数据量过大。");
                        archiveUncompressedBytes += archiveEntry.Length;
                    }
                    ZipArchiveEntry manifest = archive.GetEntry("session.json");
                    if (manifest == null) throw new InvalidDataException("现场文件缺少 session.json。");
                    if (manifest.Length > MaximumManifestBytes) throw new InvalidDataException("现场文件清单过大。");
                    SessionDocument document;
                    using (Stream stream = manifest.Open()) document = JsonSerializer.Deserialize<SessionDocument>(stream);
                    if (document == null) throw new InvalidDataException("现场文件内容为空。");
                    if (!string.IsNullOrWhiteSpace(document.Format) && !string.Equals(document.Format, "motuperf-session", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("不是受支持的 MoTuPerf 现场文件。");
                    }
                    if (document.Version < 0 || document.Version > 5) throw new InvalidDataException("现场文件版本不受支持：" + document.Version);
                    List<SessionScreenshot> screenshots = document.Screenshots ?? new List<SessionScreenshot>();
                    if (screenshots.Count > MaximumScreenshots) throw new InvalidDataException("现场文件截图数量异常。");
                    HashSet<string> entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "session.json" };
                    long uncompressedBytes = manifest.Length;
                    foreach (SessionScreenshot screenshot in screenshots)
                    {
                        if (screenshot == null || string.IsNullOrWhiteSpace(screenshot.ArchivePath)) continue;
                        string entryName = NormalizeScreenshotEntry(screenshot.ArchivePath);
                        if (!entryNames.Add(entryName)) throw new InvalidDataException("现场文件包含重复的截图归档名称。");
                        ZipArchiveEntry entry = archive.GetEntry(entryName);
                        if (entry == null) throw new InvalidDataException("现场文件缺少截图：" + entryName);
                        if (entry.Length > MaximumScreenshotBytes || uncompressedBytes > MaximumUncompressedBytes - entry.Length)
                            throw new InvalidDataException("现场文件解压后的数据量过大。");
                        string target = Path.Combine(targetRoot, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(entryName));
                        using (Stream input = entry.Open())
                        using (Stream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            extractedFiles.Add(target);
                            input.CopyTo(output);
                        }
                        uncompressedBytes += entry.Length;
                        screenshot.OriginalPath = target;
                    }
                    document.Samples = document.Samples ?? new List<PerfSample>();
                    document.Screenshots = screenshots;
                    return document;
                }
            }
            catch
            {
                foreach (string file in extractedFiles) TryDelete(file);
                throw;
            }
        }

        private static string NormalizeScreenshotEntry(string path)
        {
            string fileName = Path.GetFileName((path ?? "").Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException("截图归档路径无效。");
            return "screenshots/" + fileName;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
