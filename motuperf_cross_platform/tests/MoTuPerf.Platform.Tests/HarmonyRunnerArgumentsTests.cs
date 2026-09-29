using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyRunnerArgumentsTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PreservesSelectedIdentityAndPassesCommFlagOnlyForActualComm(bool isComm)
        {
            var config = new CaptureConfig
            {
                Udid = "harmony-1", TargetName = "game; echo test", BundleId = "",
                TargetHarmonyStartTimeTicks = 900, TargetHarmonyUserId = 100,
                TargetHarmonyNameIsComm = isComm
            };
            var args = PerfCollector.BuildHarmonyRunnerArguments(config, 42, "runner.py", "tools/hdc");
            Assert.Equal("game; echo test", args[args.IndexOf("--target-name") + 1]);
            Assert.Equal("", args[args.IndexOf("--target-bundle-id") + 1]);
            Assert.Equal("42", args[args.IndexOf("--pid") + 1]);
            Assert.Equal("900", args[args.IndexOf("--target-start-time-ticks") + 1]);
            Assert.Equal("100", args[args.IndexOf("--target-user-id") + 1]);
            Assert.Equal(isComm, args.Contains("--target-name-is-comm"));
        }

        [Fact]
        public void KeepsBundleAndIdentityEvidenceWhenSelectedProcessNameIsUnreadable()
        {
            var config = new CaptureConfig
            {
                Udid = "harmony-1", TargetName = "", BundleId = "com.example.game",
                TargetHarmonyStartTimeTicks = 900, TargetHarmonyUserId = 100
            };
            var args = PerfCollector.BuildHarmonyRunnerArguments(config, 42, "runner.py", "tools/hdc");
            Assert.Equal("", args[args.IndexOf("--target-name") + 1]);
            Assert.Equal("com.example.game", args[args.IndexOf("--target-bundle-id") + 1]);
            Assert.Equal("42", args[args.IndexOf("--pid") + 1]);
            Assert.Equal("900", args[args.IndexOf("--target-start-time-ticks") + 1]);
            Assert.Equal("100", args[args.IndexOf("--target-user-id") + 1]);
            Assert.DoesNotContain("--target-name-is-comm", args);
        }
    }
}
