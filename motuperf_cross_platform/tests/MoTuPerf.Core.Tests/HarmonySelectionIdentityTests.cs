using CSharpIosPerfMonitor;
using System.Text.Json;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class HarmonySelectionIdentityTests
    {
        [Theory]
        [InlineData(true, 900, 100, true)]
        [InlineData(false, 900, 100, false)]
        [InlineData(true, 901, 100, false)]
        [InlineData(true, 900, 101, false)]
        [InlineData(true, 0, 100, false)]
        public void RefreshCanCompleteOnlyExplicitCommWithMatchingInstance(bool isComm, long ticks, int user, bool expected)
        {
            var selected = new ProcessInfo { Pid = 42, Name = "com.example.gam", HarmonyNameIsComm = isComm,
                HarmonyUserId = 100, HarmonyStartTimeTicks = 900 };
            var current = new ProcessInfo { Pid = 42, Name = "com.example.game:renderer",
                HarmonyUserId = user, HarmonyStartTimeTicks = ticks };
            Assert.Equal(expected, ProcessTargetMatcher.SameHarmonyProcessInstance(current, selected));
        }

        [Theory]
        [InlineData(900, false, true)]
        [InlineData(0, false, false)]
        [InlineData(900, true, false)]
        public void TemporaryCommOnlyRefreshRequiresConfirmedStartAndUnambiguousIdentity(long ticks, bool ambiguous, bool expected)
        {
            var selected = new ProcessInfo { Pid = 42, Name = "com.example.game:renderer", HarmonyStartTimeTicks = ticks };
            var current = new ProcessInfo { Pid = 42, Name = "com.example.gam", HarmonyStartTimeTicks = 900,
                HarmonyNameIsComm = true, OwnershipAmbiguous = ambiguous };
            Assert.Equal(expected, ProcessTargetMatcher.SameHarmonyProcessInstance(current, selected));
        }

        [Fact]
        public void CompleteNamesKeepCaseSensitiveIdentity()
        {
            Assert.False(ProcessTargetMatcher.SameHarmonyProcessInstance(
                new ProcessInfo { Pid = 42, Name = "Game", HarmonyStartTimeTicks = 900 },
                new ProcessInfo { Pid = 42, Name = "game", HarmonyStartTimeTicks = 900 }));
        }

        [Theory]
        [InlineData("abc界面游戏", "abc界面游戏worker", true)]
        [InlineData("界面abcdefghijklm", "界面abcdefghijklmworker", false)]
        [InlineData("com.example.gam", "com.example.gam", false)]
        [InlineData("short", "shortworker", false)]
        public void AliasesUseBytesAndDoNotBroadenShortNames(string comm, string full, bool expected)
        {
            Assert.Equal(expected, ProcessTargetMatcher.IsHarmonyCommAlias(comm, full));
        }

        [Fact]
        public void HarmonyVendorCommMayOmitComPrefixOnly()
        {
            Assert.True(ProcessTargetMatcher.IsHarmonyCommAlias(
                "more2.fkmj.hwhm", "com.more2.fkmj.hwhm"));
            Assert.False(ProcessTargetMatcher.IsHarmonyCommAlias(
                "more2.fkmj.hwhm", "org.more2.fkmj.hwhm"));
        }

        [Fact]
        public void HarmonyVendorCommMayDropTheLeadingCharacterAtTheKernelLimit()
        {
            Assert.True(ProcessTargetMatcher.IsHarmonyCommAlias(
                "om.m2.xzlr.hwhm", "com.m2.xzlr.hwhm"));
            Assert.False(ProcessTargetMatcher.IsHarmonyCommAlias(
                "om.m2.xzlr.hwhm", "com.other.xzlr.hwhm"));
        }

        [Fact]
        public void NameProvenanceRoundTripsWithoutGrantingLegacySelectionsPrefixMatching()
        {
            Assert.False(JsonSerializer.Deserialize<ProcessInfo>("{}").HarmonyNameIsComm);
            Assert.False(JsonSerializer.Deserialize<CaptureConfig>("{}").TargetHarmonyNameIsComm);
            var process = new ProcessInfo { HarmonyNameIsComm = true };
            var config = new CaptureConfig { TargetHarmonyNameIsComm = true };
            Assert.True(JsonSerializer.Deserialize<ProcessInfo>(JsonSerializer.Serialize(process)).HarmonyNameIsComm);
            Assert.True(JsonSerializer.Deserialize<CaptureConfig>(JsonSerializer.Serialize(config)).TargetHarmonyNameIsComm);
        }

        [Fact]
        public void UnnamedHarmonyTargetRequiresBundleEvidenceDuringRefresh()
        {
            var selected = new ProcessInfo
            {
                Pid = 42,
                Platform = "harmony",
                BundleId = "com.example.game",
                OwnerBundleId = "com.example.game",
                OwnershipVerified = true,
                HarmonyUserId = 100,
                HarmonyStartTimeTicks = 900
            };

            Assert.False(ProcessTargetMatcher.SameHarmonyProcessInstance(
                new ProcessInfo
                {
                    Pid = 42,
                    Platform = "harmony",
                    HarmonyUserId = 100,
                    HarmonyStartTimeTicks = 900
                }, selected));
            Assert.True(ProcessTargetMatcher.SameHarmonyProcessInstance(
                new ProcessInfo
                {
                    Pid = 42,
                    Platform = "harmony",
                    BundleId = "com.example.game",
                    HarmonyUserId = 100,
                    HarmonyStartTimeTicks = 900
                }, selected));
        }

        [Fact]
        public void UnknownHarmonyUserScopeCannotReuseKnownUserProcess()
        {
            var selectedUnknown = new ProcessInfo
            {
                Pid = 42,
                Name = "com.example.game",
                HarmonyUserId = -1,
                HarmonyStartTimeTicks = 900
            };
            var currentKnown = new ProcessInfo
            {
                Pid = 42,
                Name = "com.example.game",
                HarmonyUserId = 100,
                HarmonyStartTimeTicks = 900
            };

            Assert.False(ProcessTargetMatcher.SameHarmonyProcessInstance(currentKnown, selectedUnknown));
            Assert.False(ProcessTargetMatcher.SameHarmonyProcessInstance(
                new ProcessInfo
                {
                    Pid = 42,
                    Name = "com.example.game",
                    HarmonyUserId = -1,
                    HarmonyStartTimeTicks = 900
                },
                new ProcessInfo
                {
                    Pid = 42,
                    Name = "com.example.game",
                    HarmonyUserId = 100,
                    HarmonyStartTimeTicks = 900
                }));
        }
    }
}
