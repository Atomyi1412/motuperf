using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class IosProcessDiagnosticsTests
    {
        [Fact]
        public void PythonTracebackUsesTheFinalExceptionInsteadOfTheTracebackHeader()
        {
            string message = IosLookupService.DescribeProcessListFailure(new ProcessResult(
                1,
                "",
                "Traceback (most recent call last):\n  File \"ios_process_list.py\", line 88, in emit_processes\n    await device_info.proclist()\nRuntimeError: process parser exploded"));

            Assert.Contains("process parser exploded", message);
            Assert.DoesNotContain("Traceback (most recent call last)", message);
        }

        [Theory]
        [InlineData("Developer mode is disabled", "开发者模式")]
        [InlineData("InvalidHostID: device is locked", "信任")]
        [InlineData("UserspaceRsdTunnel connection refused", "开发者隧道")]
        [InlineData("No module named pymobiledevice3", "运行组件不完整")]
        public void KnownModernIosFailuresProvideActionableGuidance(string error, string expected)
        {
            string message = IosLookupService.DescribeProcessListFailure(new ProcessResult(1, "", error));

            Assert.Contains(expected, message);
        }

        [Fact]
        public void ModernAndFallbackFailuresAreBothKeptForDiagnosis()
        {
            string message = IosLookupService.CombineProcessListFailures(
                "iOS 开发者隧道建立失败。",
                "备用进程通道返回了空结果。");

            Assert.Contains("开发者隧道", message);
            Assert.Contains("备用通道", message);
            Assert.Contains("空结果", message);
        }
    }
}
