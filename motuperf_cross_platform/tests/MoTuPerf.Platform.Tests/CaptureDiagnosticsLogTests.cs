using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class CaptureDiagnosticsLogTests
    {
        [Theory]
        [InlineData("harmony", "harmony_hdc_timeout")]
        [InlineData("harmony", "harmony_hdc_permission_denied")]
        [InlineData("harmony", "")]
        [InlineData("harmony", "   ")]
        [InlineData("ios", "")]
        [InlineData("android", "")]
        public void RunnerReasonReachesLogWithoutChangingStopCode(string platform, string reason)
        {
            string directory = Directory.CreateTempSubdirectory("motuperf-runner-reason-").FullName;
            string path = Path.Combine(directory, "capture.log");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                using var log = new CaptureDiagnosticsLog(path, "reason-session");
                using var collector = new PerfCollector();
                typeof(PerfCollector).GetField("_captureGeneration", flags).SetValue(collector, 1);
                typeof(PerfCollector).GetField("_diagnosticsLog", flags).SetValue(collector, log);
                MethodInfo parse = typeof(PerfCollector).GetMethod("ParseLine", flags);
                var messages = new List<string>();
                var failures = new List<string>();
                collector.Message += messages.Add;
                collector.Failed += failures.Add;
                void Send(string kind, object payload, int generation = 1) => parse.Invoke(collector,
                    new object[] { kind + " " + JsonSerializer.Serialize(payload), generation, platform });

                Send("status", new { code = "stale", message = "ignored" }, 0);
                Send("status", new { code = "harmony_hdc_timeout", message = "采集失败（第 1/3 次）：HDC 超时" });
                Assert.Empty(failures);
                Send("status", new { code = "harmony_probe_recovered", message = "目标校验已恢复" });
                var fatal = new Dictionary<string, object>
                {
                    ["code"] = "harmony_target_unavailable", ["message"] = "连续三次失败 token=secret-value"
                };
                if (reason.Length > 0) fatal["reason_code"] = reason;
                Send("fatal", fatal);
                Send("fatal", fatal); // Late output cannot stop the session twice.

                Assert.Equal(2, messages.Count);
                Assert.Contains("第 1/3 次", messages[0]);
                Assert.Equal("目标校验已恢复", messages[1]);
                Assert.Contains("连续三次失败", Assert.Single(failures));
                var records = File.ReadAllLines(path).Select(line =>
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    return doc.RootElement.Clone();
                }).ToArray();
                JsonElement Event(string name) => Assert.Single(records, row => row.GetProperty("event").GetString() == name);
                Assert.Equal("harmony_target_unavailable", Event("runner_fatal").GetProperty("fields").GetProperty("code").GetString());
                Assert.Equal("harmony_target_unavailable", Event("capture_failure").GetProperty("fields").GetProperty("code").GetString());
                Assert.Equal("harmony_target_unavailable", Event("capture_stopped").GetProperty("fields").GetProperty("reason").GetString());
                if (!string.IsNullOrWhiteSpace(reason))
                    Assert.Equal(reason, Event("runner_failure_reason").GetProperty("fields").GetProperty("code").GetString());
                else
                    Assert.DoesNotContain(records, row => row.GetProperty("event").GetString() == "runner_failure_reason");
                Assert.DoesNotContain("secret-value", File.ReadAllText(path));
                Assert.DoesNotContain("ignored", File.ReadAllText(path));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void StopReportsTrueMetricFreshnessOnlyOnceAndTaskExitRemainsLoggable()
        {
            string directory = Directory.CreateTempSubdirectory("motuperf-lifecycle-").FullName;
            string path = Path.Combine(directory, "capture.log");
            try
            {
                using (CaptureDiagnosticsLog log = new CaptureDiagnosticsLog(path, "first-session"))
                {
                    log.ObserveOutput();
                    log.ObserveSample(new PerfSample { HasCpu = true, CpuUpdated = true, HasFps = true, FpsUpdated = false });
                    log.WriteStop("runner_exit");
                    log.WriteStop("collector_failed");
                    log.WriteTask("samples", "cancelled", 123);
                }
                string[] lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length);
                using JsonDocument stop = JsonDocument.Parse(lines[0]);
                var fields = stop.RootElement.GetProperty("fields");
                Assert.Equal("runner_exit", fields.GetProperty("reason").GetString());
                Assert.Equal(1, fields.GetProperty("delivered_samples").GetInt32());
                Assert.Equal(JsonValueKind.Null, fields.GetProperty("last_metric_age_sec").GetProperty("fps").ValueKind);
                Assert.Equal(JsonValueKind.Number, fields.GetProperty("last_metric_age_sec").GetProperty("cpu").ValueKind);
                Assert.Contains("first-session", lines[1]);
                Assert.Contains("cancelled", lines[1]);
            }
            finally { Directory.Delete(directory, true); }
        }

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
                        TargetHarmonyUserId = 100,
                        TargetHarmonyStartTimeTicks = 987654,
                        TargetHarmonyNameIsComm = true,
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
                Assert.Equal(100, start.RootElement.GetProperty("fields").GetProperty("target_harmony_user_id").GetInt32());
                Assert.Equal(987654, start.RootElement.GetProperty("fields").GetProperty("target_harmony_start_time_ticks").GetInt64());
                Assert.True(start.RootElement.GetProperty("fields").GetProperty("target_harmony_name_is_comm").GetBoolean());
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

        [Fact]
        public void UnspecifiedHarmonyUserIsRecordedAsUnknown()
        {
            CaptureConfig config = new CaptureConfig { Platform = "harmony" };

            Assert.Equal(-1, config.TargetHarmonyUserId);
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
