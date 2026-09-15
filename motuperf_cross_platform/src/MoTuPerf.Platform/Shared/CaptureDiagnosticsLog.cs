using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CSharpIosPerfMonitor
{
    internal sealed class CaptureDiagnosticsLog : IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _sessionId;
        private readonly string _path;
        private bool _disposed;
        private int _stderrEventCount;

        internal CaptureDiagnosticsLog(string path, string sessionId)
        {
            _path = path ?? "";
            _sessionId = sessionId ?? "";
            string directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        internal string Path { get { return _path; } }

        internal static CaptureDiagnosticsLog TryCreate()
        {
            try
            {
                string root = System.IO.Path.Combine(RuntimeTools.DataDirectory, "logs");
                Directory.CreateDirectory(root);
                DiagnosticLogMaintenance.Run(root);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
                string path = System.IO.Path.Combine(root, "collector-" + stamp + ".log");
                int suffix = 0;
                while (File.Exists(path))
                {
                    suffix++;
                    path = System.IO.Path.Combine(root, "collector-" + stamp + "-" + suffix + ".log");
                }
                return new CaptureDiagnosticsLog(path, Guid.NewGuid().ToString("N"));
            }
            catch
            {
                return null;
            }
        }

        internal void WriteStart(CaptureConfig config, string runner, string runnerPath)
        {
            Dictionary<string, object> fields = new Dictionary<string, object>
            {
                { "platform", config == null ? "" : config.Platform ?? "" },
                { "device", MaskDeviceId(config == null ? "" : config.Udid) },
                { "bundle_id", config == null ? "" : config.BundleId ?? "" },
                { "target_name", config == null ? "" : config.TargetName ?? "" },
                { "target_pid", config == null || !config.TargetPid.HasValue ? 0 : config.TargetPid.Value },
                { "target_owner_pid", config == null ? 0 : config.TargetOwnerPid },
                { "runner", runner ?? "" },
                { "runner_path", runnerPath ?? "" },
                { "screenshot_interval_sec", config == null ? 0 : config.ScreenshotIntervalSec },
                { "collect_fps", config != null && config.CollectFps },
                { "collect_memory", config != null && config.CollectMemory },
                { "collect_cpu", config != null && config.CollectCpu },
                { "collect_temperature", config != null && config.CollectTemperature },
                { "collect_thermal_state", config != null && config.CollectThermalState },
                { "capture_screenshots", config != null && config.CaptureScreenshots }
            };
            Write("capture_started", fields);
        }

        internal void WriteRunnerStderr(string label, string line)
        {
            lock (_lock)
            {
                if (_stderrEventCount >= 100) return;
                _stderrEventCount++;
            }
            Write("runner_stderr", new Dictionary<string, object>
            {
                { "runner", label ?? "" },
                { "message", Sanitize(line, 2000) }
            });
        }

        internal void WriteRunnerExit(string label, int exitCode, string stderr)
        {
            Write("runner_exit", new Dictionary<string, object>
            {
                { "runner", label ?? "" },
                { "exit_code", exitCode },
                { "stderr_tail", Sanitize(stderr, 6000) }
            });
        }

        internal void WriteEvent(string eventName, string message, string code = "")
        {
            Write(eventName, new Dictionary<string, object>
            {
                { "code", code ?? "" },
                { "message", Sanitize(message, 4000) }
            });
        }

        internal void WritePidRebound(int oldPid, int newPid, string bundleId, string reason)
        {
            Write("target_pid_rebound", new Dictionary<string, object>
            {
                { "old_pid", oldPid },
                { "new_pid", newPid },
                { "bundle_id", bundleId ?? "" },
                { "reason", reason ?? "" }
            });
        }

        internal void WriteStop(string reason, string message = "")
        {
            Write("capture_stopped", new Dictionary<string, object>
            {
                { "reason", reason ?? "" },
                { "message", Sanitize(message, 4000) }
            });
        }

        private void Write(string eventName, IDictionary<string, object> fields)
        {
            lock (_lock)
            {
                if (_disposed || string.IsNullOrWhiteSpace(_path)) return;
                Dictionary<string, object> record = new Dictionary<string, object>
                {
                    { "timestamp", DateTime.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
                    { "session_id", _sessionId },
                    { "event", eventName ?? "" },
                    { "fields", fields ?? new Dictionary<string, object>() }
                };
                try
                {
                    File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Diagnostics must never interrupt or stop performance collection.
                }
            }
        }

        internal static string MaskDeviceId(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length <= 8) return string.IsNullOrWhiteSpace(text) ? "" : "***";
            return text.Substring(0, 4) + "..." + text.Substring(text.Length - 4);
        }

        internal static string Sanitize(string value, int maxLength)
        {
            string text = string.Join(" ", (value ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                "(?i)(bearer)\\s+[^\\s;&]+",
                "$1 [redacted]");
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                "(?i)(password|passwd|token|secret|authorization|api[_-]?key)\\s*[=:]\\s*[^\\s;&]+",
                "$1=[redacted]");
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                "(?i)([?&](?:password|passwd|token|secret|authorization|api[_-]?key)=)[^&\\s]+",
                "$1[redacted]");
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        public void Dispose()
        {
            lock (_lock) _disposed = true;
        }
    }

    public static class DiagnosticLogMaintenance
    {
        private const int DefaultMaximumFiles = 60;
        private const long DefaultMaximumBytes = 64L * 1024L * 1024L;
        private static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(30);

        public static void Run(string directory)
        {
            Run(directory, DateTime.UtcNow, DefaultMaximumAge, DefaultMaximumFiles, DefaultMaximumBytes);
        }

        internal static void Run(string directory, DateTime nowUtc, TimeSpan maximumAge, int maximumFiles, long maximumBytes)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            List<FileInfo> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                    .Select(delegate(string path) { return new FileInfo(path); })
                    .OrderByDescending(delegate(FileInfo file) { return file.LastWriteTimeUtc; })
                    .ToList();
            }
            catch
            {
                return;
            }

            foreach (FileInfo file in files.Where(delegate(FileInfo item) { return nowUtc - item.LastWriteTimeUtc > maximumAge; }).ToList())
            {
                TryDelete(file.FullName);
                files.Remove(file);
            }

            long retainedBytes = 0;
            int retainedFiles = 0;
            foreach (FileInfo file in files)
            {
                long length;
                try { length = Math.Max(0, file.Length); }
                catch { length = 0; }
                long byteLimit = Math.Max(0, maximumBytes);
                if (retainedFiles >= Math.Max(1, maximumFiles)
                    || length > byteLimit
                    || retainedBytes > byteLimit - length)
                {
                    TryDelete(file.FullName);
                    continue;
                }
                retainedFiles++;
                retainedBytes += length;
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { }
        }
    }
}
