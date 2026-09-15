using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace MoTuPerf.Platform
{
    internal static class RuntimeDataMigration
    {
        // A user-requested relocation commits the location only after every copy is verified.
        internal static void CopyTreeAndCommit(string sourceRoot, string targetRoot, Action commit,
            CancellationToken token, IProgress<RuntimeDataMigrationProgress> progress = null)
        {
            EnsureNoLinks(sourceRoot);
            EnsureNoLinks(targetRoot);
            EnsureSeparateTrees(sourceRoot, targetRoot);
            List<MigrationFile> files = CollectFiles(sourceRoot, token);
            MigrationProgressReporter reporter = new MigrationProgressReporter(files);
            reporter.Attach(progress);
            List<Tuple<string, string, byte[]>> copies = new List<Tuple<string, string, byte[]>>();
            HashSet<string> created = new HashSet<string>(StringComparer.Ordinal);
            bool committed = false;
            try
            {
                foreach (MigrationFile file in files)
                {
                    token.ThrowIfCancellationRequested();
                    string target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, file.Source));
                    EnsureNoLinks(file.Source);
                    EnsureNoLinks(target);
                    byte[] hash = HashFile(file.Source, token);
                    if (Directory.Exists(target)) throw new IOException("迁移目标存在同名目录：" + target);
                    if (File.Exists(target))
                    {
                        if (!hash.SequenceEqual(HashFile(target, token))) throw new IOException("迁移目标存在不同内容的同名文件：" + target);
                        reporter.AddBytes(file.Length, file.Source);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string temporary = target + ".motuperf-copy-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            using (FileStream input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[1024 * 1024];
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    output.Write(buffer, 0, read);
                                    reporter.AddBytes(read, file.Source);
                                }
                                output.Flush(true);
                            }
                            if (!hash.SequenceEqual(HashFile(temporary, token))) throw new IOException("迁移期间源文件已变化：" + file.Source);
                            EnsureNoLinks(target);
                            File.Move(temporary, target, false);
                            created.Add(target);
                        }
                        finally { TryDeleteFile(temporary); }
                    }
                    copies.Add(Tuple.Create(file.Source, target, hash));
                    reporter.Complete(file.Source, false, 0);
                }
                // Detect files added or changed while the copy was in progress before committing.
                reporter.ReportStage("校验文件");
                List<MigrationFile> finalFiles = CollectFiles(sourceRoot, token);
                if (!files.Select(f => f.Source).OrderBy(p => p, StringComparer.Ordinal)
                    .SequenceEqual(finalFiles.Select(f => f.Source).OrderBy(p => p, StringComparer.Ordinal)))
                    throw new IOException("迁移期间原目录文件发生变化，请停止使用数据后重试。");
                foreach (var copy in copies)
                {
                    EnsureNoLinks(copy.Item1);
                    EnsureNoLinks(copy.Item2);
                    if (!copy.Item3.SequenceEqual(HashFile(copy.Item1, token)) || !copy.Item3.SequenceEqual(HashFile(copy.Item2, token)))
                        throw new IOException("迁移期间文件发生变化：" + copy.Item1);
                }
                token.ThrowIfCancellationRequested();
                reporter.ReportStage("保存目录设置");
                commit();
                committed = true;
            }
            finally
            {
                reporter.ReportStage(committed ? "清理原目录" : "保留原数据并回退");
                foreach (var copy in copies)
                {
                    string remove = committed ? copy.Item1 : created.Contains(copy.Item2) ? copy.Item2 : null;
                    if (remove == null) continue;
                    try
                    {
                        EnsureNoLinks(remove);
                        if (copy.Item3.SequenceEqual(HashFile(remove, CancellationToken.None))) File.Delete(remove);
                    }
                    catch { /* Keep redundant copies if cleanup cannot safely complete. */ }
                }
            }
        }

        private static void EnsureSeparateTrees(string sourceRoot, string targetRoot)
        {
            // Check filesystem identity too: macOS casing and Windows short names can alias a directory.
            foreach (var pair in new[] { Tuple.Create(sourceRoot, targetRoot), Tuple.Create(targetRoot, sourceRoot) })
            {
                Directory.CreateDirectory(pair.Item1);
                string name = ".motuperf-boundary-" + Guid.NewGuid().ToString("N");
                string marker = Path.Combine(pair.Item1, name);
                try
                {
                    using (FileStream stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        string current = Path.GetFullPath(pair.Item2);
                        while (!string.IsNullOrEmpty(current))
                        {
                            if (File.Exists(Path.Combine(current, name)))
                                throw new IOException("迁移源目录和目标目录实际重合或互相包含。");
                            current = Path.GetDirectoryName(current);
                        }
                    }
                }
                finally { TryDeleteFile(marker); }
            }
        }

        internal static void EnsureNoLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { current = Path.GetDirectoryName(current); continue; }
                catch (DirectoryNotFoundException) { current = Path.GetDirectoryName(current); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("数据迁移不支持目录链接或符号链接：" + current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static byte[] HashFile(string path, CancellationToken token)
        {
            using (FileStream stream = File.OpenRead(path))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[1024 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, count);
                }
                return hash.GetHashAndReset();
            }
        }

        internal static void MoveSettings(string legacyRoot, string targetRoot)
        {
            if (string.IsNullOrWhiteSpace(legacyRoot) || string.IsNullOrWhiteSpace(targetRoot)) return;
            try { MoveFile(Path.Combine(legacyRoot, "settings.json"), Path.Combine(targetRoot, "settings.json"), null, CancellationToken.None); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool MoveTree(string legacyRoot, string targetRoot, CancellationToken token, IProgress<RuntimeDataMigrationProgress> progress = null)
        {
            if (string.IsNullOrWhiteSpace(legacyRoot) || string.IsNullOrWhiteSpace(targetRoot)
                || !Directory.Exists(legacyRoot)) return true;
            List<MigrationFile> files;
            try
            {
                EnsureNoLinks(targetRoot);
                files = CollectFiles(legacyRoot, token);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            MigrationProgressReporter reporter = new MigrationProgressReporter(files);
            reporter.Attach(progress);
            reporter.Report();
            bool completed = true;
            try
            {
                Directory.CreateDirectory(targetRoot);
                foreach (string source in Directory.EnumerateFileSystemEntries(legacyRoot))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(source);
                    if (Directory.Exists(source))
                    {
                        string sourceFullPath = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string targetFullPath = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            MoveDirectoryContents(source, targetRoot, token, reporter);
                        }
                        else
                        {
                            MoveDirectory(source, Path.Combine(targetRoot, name), token, reporter);
                        }
                    }
                    else if (File.Exists(source)) MoveFile(source, Path.Combine(targetRoot, name), reporter, token);
                }
                TryDeleteIfEmpty(legacyRoot);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                completed = false;
            }
            finally
            {
                reporter.CompletePendingFiles();
                reporter.Report();
            }
            return completed && reporter.FailedFiles == 0;
        }

        private static List<MigrationFile> CollectFiles(string sourceRoot, CancellationToken token)
        {
            EnsureNoLinks(sourceRoot);
            List<MigrationFile> files = new List<MigrationFile>();
            foreach (string source in EnumerateFiles(sourceRoot, token))
            {
                long length = new FileInfo(source).Length;
                files.Add(new MigrationFile(source, length));
            }
            return files;
        }

        private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = pending.Pop();
                EnsureNoLinks(current);
                IEnumerable<string> entries = Directory.EnumerateFileSystemEntries(current);
                foreach (string entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureNoLinks(entry);
                    bool isFile = !Directory.Exists(entry);
                    if (!isFile) pending.Push(entry);
                    if (isFile) yield return entry;
                }
            }
        }

        private static void MoveDirectory(string source, string target, CancellationToken token, MigrationProgressReporter reporter)
        {
            try
            {
                EnsureNoLinks(source);
                EnsureNoLinks(target);
                Directory.CreateDirectory(target);
                foreach (string child in Directory.EnumerateFileSystemEntries(source))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(child);
                    if (Directory.Exists(child)) MoveDirectory(child, Path.Combine(target, name), token, reporter);
                    else if (File.Exists(child)) MoveFile(child, Path.Combine(target, name), reporter, token);
                }
                TryDeleteIfEmpty(source);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Continue with other top-level entries when one folder is unavailable.
            }
        }

        private static void MoveDirectoryContents(string source, string target, CancellationToken token, MigrationProgressReporter reporter)
        {
            EnsureNoLinks(source);
            EnsureNoLinks(target);
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(child);
                if (Directory.Exists(child)) MoveDirectory(child, Path.Combine(target, name), token, reporter);
                else if (File.Exists(child)) MoveFile(child, Path.Combine(target, name), reporter, token);
            }
            TryDeleteIfEmpty(source);
        }

        private static void MoveFile(string source, string target, MigrationProgressReporter reporter, CancellationToken token)
        {
            EnsureNoLinks(source);
            EnsureNoLinks(target);
            MigrationFile plan = reporter == null ? null : reporter.Find(source);
            long length = plan == null ? GetFileLength(source) : plan.Length;
            if (!File.Exists(source) || File.Exists(target) || Directory.Exists(target))
            {
                if (reporter != null) reporter.Skip(length, source, File.Exists(target) || Directory.Exists(target));
                return;
            }
            string temporary = target + ".motuperf-copy-" + Guid.NewGuid().ToString("N");
            long copied = 0;
            try
            {
                string directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        copied += read;
                        if (reporter != null) reporter.AddBytes(read, source);
                    }
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, false);
                File.Delete(source);
                if (reporter != null) reporter.Complete(source, false, Math.Max(0, length - copied));
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(temporary);
                if (reporter != null) reporter.Complete(source, true, Math.Max(0, length - copied));
                throw;
            }
            catch
            {
                TryDeleteFile(temporary);
                if (reporter != null) reporter.Complete(source, true, Math.Max(0, length - copied));
            }
        }

        private static long GetFileLength(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteIfEmpty(string directory)
        {
            try
            {
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }

        private sealed class MigrationFile
        {
            public MigrationFile(string source, long length)
            {
                Source = source;
                Length = length;
            }
            public string Source { get; private set; }
            public long Length { get; private set; }
        }

        private sealed class MigrationProgressReporter
        {
            private readonly Dictionary<string, MigrationFile> _files;
            private readonly long _totalBytes;
            private readonly int _totalFiles;
            private long _processedBytes;
            private int _processedFiles;
            private int _failedFiles;
            private IProgress<RuntimeDataMigrationProgress> _progress;

            public MigrationProgressReporter(IReadOnlyList<MigrationFile> files)
            {
                _files = files.ToDictionary(item => Path.GetFullPath(item.Source), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                _totalBytes = files.Sum(item => item.Length);
                _totalFiles = files.Count;
            }

            public void Attach(IProgress<RuntimeDataMigrationProgress> progress) { _progress = progress; }
            public int FailedFiles { get { return _failedFiles; } }
            public MigrationFile Find(string source)
            {
                MigrationFile file;
                return _files.TryGetValue(Path.GetFullPath(source), out file) ? file : null;
            }
            public void AddBytes(long bytes, string source)
            {
                _processedBytes = Math.Min(_totalBytes, _processedBytes + Math.Max(0, bytes));
                Report();
            }
            public void Skip(long bytes, string source, bool failed)
            {
                _processedBytes = Math.Min(_totalBytes, _processedBytes + Math.Max(0, bytes));
                _processedFiles++;
                if (failed) _failedFiles++;
                Report();
            }
            public void Complete(string source, bool failed, long remainingBytes)
            {
                _processedBytes = Math.Min(_totalBytes, _processedBytes + Math.Max(0, remainingBytes));
                _processedFiles++;
                if (failed) _failedFiles++;
                Report();
            }
            public void CompletePendingFiles()
            {
                while (_processedFiles < _totalFiles)
                {
                    _processedFiles++;
                    _failedFiles++;
                }
            }
            public void Report()
            {
                if (_progress != null) _progress.Report(new RuntimeDataMigrationProgress(_totalBytes, _processedBytes, _totalFiles, _processedFiles, _failedFiles));
            }
            public void ReportStage(string stage)
            {
                if (_progress != null) _progress.Report(new RuntimeDataMigrationProgress(_totalBytes, _processedBytes, _totalFiles, _processedFiles, _failedFiles, stage));
            }
        }
    }
}
