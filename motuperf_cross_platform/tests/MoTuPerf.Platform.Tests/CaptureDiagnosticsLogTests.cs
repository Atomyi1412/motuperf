using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class CaptureDiagnosticsLogTests
    {
        [Fact]
        public void WritesStructuredSessionEventsAndRedactsSensitiveValues()
        {
            string directory = Directory.CreateTempSubdirectory("motuperf-collector-log-").FullName;
            string path = Path.Combine(directory, "collector-test.log");
            try
            {
                using (CaptureDiagnosticsLog log = new CaptureDiagnosticsLog(path, "session-test"))
                {
                    log.WriteStart(new CaptureConfig
                    {
                        Platform = "ios",
                        Udid = "1234567890abcdef",
                        BundleId = "com.example.game",
                        TargetName = "GameWebContent",
                        TargetPid = 42,
                        ScreenshotIntervalSec = 3
                    }, "pyidevice metrics runner", "pid_perf_runner.py");
                    log.WriteEvent("runner_fatal", "token=super-secret; device communication failed", "pid_lost");
                    log.WriteStop("runner_fatal", "采集异常停止");
                }

                string[] lines = File.ReadAllLines(path);
                Assert.Equal(3, lines.Length);
                JsonDocument start = JsonDocument.Parse(lines[0]);
                JsonDocument fatal = JsonDocument.Parse(lines[1]);
                Assert.Equal("capture_started", start.RootElement.GetProperty("event").GetString());
                Assert.Equal("1234...cdef", start.RootElement.GetProperty("fields").GetProperty("device").GetString());
                Assert.Equal(3, start.RootElement.GetProperty("fields").GetProperty("screenshot_interval_sec").GetInt32());
                Assert.Equal("runner_fatal", fatal.RootElement.GetProperty("event").GetString());
                string message = fatal.RootElement.GetProperty("fields").GetProperty("message").GetString();
                Assert.Contains("token=[redacted]", message, StringComparison.Ordinal);
                Assert.DoesNotContain("super-secret", lines[1], StringComparison.Ordinal);
                Assert.Equal("capture_stopped", JsonDocument.Parse(lines.Last()).RootElement.GetProperty("event").GetString());
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("12345678", "***")]
        [InlineData("1234567890abcdef", "1234...cdef")]
        public void MasksDeviceIdentifiers(string value, string expected)
        {
            Assert.Equal(expected, CaptureDiagnosticsLog.MaskDeviceId(value));
        }

        [Fact]
        public void SanitizesBearerApiKeysAndUrlSecrets()
        {
            string sanitized = CaptureDiagnosticsLog.Sanitize(
                "Authorization: Bearer abc123 api_key=qwerty https://example.test/?token=hidden&name=ok",
                2000);

            Assert.DoesNotContain("abc123", sanitized);
            Assert.DoesNotContain("qwerty", sanitized);
            Assert.DoesNotContain("hidden", sanitized);
            Assert.Contains("[redacted]", sanitized);
        }

        [Fact]
        public void LogMaintenanceRemovesExpiredAndOldestExcessFiles()
        {
            string directory = Directory.CreateTempSubdirectory("motuperf-log-retention-").FullName;
            try
            {
                DateTime now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
                string expired = Path.Combine(directory, "expired.log");
                string oldest = Path.Combine(directory, "oldest.log");
                string newest = Path.Combine(directory, "newest.log");
                File.WriteAllText(expired, "old");
                File.WriteAllText(oldest, "12345");
                File.WriteAllText(newest, "12345");
                File.SetLastWriteTimeUtc(expired, now.AddDays(-31));
                File.SetLastWriteTimeUtc(oldest, now.AddMinutes(-2));
                File.SetLastWriteTimeUtc(newest, now.AddMinutes(-1));

                DiagnosticLogMaintenance.Run(directory, now, TimeSpan.FromDays(30), 1, 1024);

                Assert.False(File.Exists(expired));
                Assert.False(File.Exists(oldest));
                Assert.True(File.Exists(newest));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
