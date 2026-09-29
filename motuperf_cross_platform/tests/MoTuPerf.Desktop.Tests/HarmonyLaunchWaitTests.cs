using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class HarmonyLaunchWaitTests
    {
        private const string Bundle = "com.example.target";

        private static HarmonyTargetInventory Snapshot(int user, string suffix = "")
        {
            var snapshot = new HarmonyTargetInventory();
            snapshot.Apps.Add(new AppInfo { BundleId = Bundle, HarmonyUserId = user, Platform = "harmony" });
            snapshot.Processes.Add(new ProcessInfo
            {
                Pid = 501, Name = Bundle + suffix, BundleId = Bundle, HarmonyUserId = user,
                HarmonyStartTimeTicks = 42, Platform = "harmony"
            });
            return snapshot;
        }

        [Fact]
        public async Task AlreadyMatchedSnapshotKeepsDiagnosticsWithoutAnotherRead()
        {
            var initial = Snapshot(100);
            initial.UserInventoryError = "account query denied";
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(initial, Bundle, 100,
                _ => throw new InvalidOperationException("Unexpected read"), () => true, CancellationToken.None);
            Assert.Same(initial, result);
            Assert.Equal("account query denied", result.UserInventoryError);
        }

        [Fact]
        public async Task FailedRefreshDiscardsOlderTargetsAndPreservesReason()
        {
            int reads = 0;
            var initial = Snapshot(0);
            initial.UserInventoryError = "account query denied";
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(initial, Bundle, 100,
                _ => { reads++; throw new IOException("HDC disconnected"); }, () => true,
                CancellationToken.None, retryDelayMs: 0);
            Assert.Equal(8, reads);
            Assert.Empty(result.Apps);
            Assert.Empty(result.Processes);
            Assert.Contains("HDC disconnected", result.ProcessInventoryError);
            Assert.Equal(initial.UserInventoryError, result.UserInventoryError);
        }

        [Fact]
        public async Task NullRefreshHasNoStaleTargetsAndHasDiagnostic()
        {
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                _ => Task.FromResult<HarmonyTargetInventory>(null), () => true,
                CancellationToken.None, retryDelayMs: 0);
            Assert.NotNull(result);
            Assert.Empty(result.Processes);
            Assert.False(string.IsNullOrWhiteSpace(result.ProcessInventoryError));
        }

        [Fact]
        public async Task RecoveryUsesOneCompleteMatchingSnapshotIncludingLastAttempt()
        {
            int reads = 0;
            var matched = Snapshot(100, ":service");
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                _ =>
                {
                    if (++reads < 8) throw new IOException("temporarily unavailable");
                    return Task.FromResult(matched);
                }, () => true, CancellationToken.None, retryDelayMs: 0);
            Assert.Equal(8, reads);
            Assert.Same(matched, result);
            Assert.Same(matched.Processes[0], DevicePickerWindow.FindHarmonyProcessForBundle(result.Processes, Bundle, 100));
            Assert.Empty(result.ProcessInventoryError);
        }

        [Fact]
        public async Task AmbiguousAndOtherUserProcessesDoNotStopPolling()
        {
            int reads = 0;
            var ambiguous = Snapshot(100, ":worker");
            ambiguous.Processes.Add(new ProcessInfo
            {
                Pid = 502, Name = Bundle + ":renderer", BundleId = Bundle, HarmonyUserId = 100, Platform = "harmony"
            });
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                _ => { reads++; return Task.FromResult(ambiguous); }, () => true,
                CancellationToken.None, retryDelayMs: 0);
            Assert.Equal(8, reads);
            Assert.Same(ambiguous, result);
            Assert.Null(DevicePickerWindow.FindHarmonyProcessForBundle(result.Processes, Bundle, 100));
            Assert.Same(ambiguous.Processes[1], DevicePickerWindow.ResolveHarmonyConfirmedProcess(
                false, ambiguous.Apps[0], ambiguous.Processes[1], result.Processes));
        }

        [Fact]
        public async Task TotalDeadlineCancelsTheActiveReadAndDiscardsOlderTargets()
        {
            bool readCancelled = false;
            async Task<HarmonyTargetInventory> Read(CancellationToken token)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { readCancelled = token.IsCancellationRequested; }
                return Snapshot(100);
            }
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                Read, () => true, CancellationToken.None, maxWaitMs: 100, retryDelayMs: 0)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(readCancelled);
            Assert.Empty(result.Apps);
            Assert.Empty(result.Processes);
            Assert.Contains("超时", result.ProcessInventoryError);
        }

        [Fact]
        public async Task ResultReturnedAfterDeadlineCannotRestoreASelectablePid()
        {
            async Task<HarmonyTargetInventory> Read(CancellationToken token)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { }
                return Snapshot(100);
            }
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                Read, () => true, CancellationToken.None, maxWaitMs: 100, retryDelayMs: 0)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(result.Processes);
            Assert.Contains("超时", result.ProcessInventoryError);
        }

        [Fact]
        public async Task LastFailedReadStillHonorsUserCancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                int reads = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevicePickerWindow.WaitForHarmonyProcessAsync(
                    Snapshot(0), Bundle, 100, _ =>
                    {
                        if (++reads == 8) cancellation.Cancel();
                        throw new IOException("HDC exited");
                    }, () => true, cancellation.Token, retryDelayMs: 0));
                Assert.Equal(8, reads);
            }
        }

        [Fact]
        public async Task UnexpectedReadCancellationIsNotReportedAsDeadline()
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevicePickerWindow.WaitForHarmonyProcessAsync(
                Snapshot(0), Bundle, 100, _ => throw new OperationCanceledException(),
                () => true, CancellationToken.None, retryDelayMs: 0));
        }

        [Fact]
        public async Task SnapshotDiagnosticsSurviveWhenProcessLookupIsDenied()
        {
            var denied = new HarmonyTargetInventory
            {
                ProcessInventoryError = "process access denied",
                UserInventoryError = "account access denied"
            };
            denied.Apps.Add(Snapshot(100).Apps[0]);
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                _ => Task.FromResult(denied), () => true, CancellationToken.None, retryDelayMs: 0);
            Assert.Same(denied, result);
            Assert.Single(result.Apps);
            Assert.Empty(result.Processes);
            Assert.Equal("process access denied", result.ProcessInventoryError);
            Assert.Equal("account access denied", result.UserInventoryError);
        }

        [Fact]
        public async Task CancelledRequestCannotReturnEvenAnAlreadyMatchedSnapshot()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevicePickerWindow.WaitForHarmonyProcessAsync(
                    Snapshot(100), Bundle, 100, _ => Task.FromResult(Snapshot(100)), () => true, cancellation.Token));
            }
        }

        [Fact]
        public async Task UserCancellationDuringReadPropagatesInsteadOfBecomingTimeout()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevicePickerWindow.WaitForHarmonyProcessAsync(
                    Snapshot(0), Bundle, 100, token =>
                    {
                        cancellation.Cancel();
                        return Task.FromResult(Snapshot(100));
                    }, () => true, cancellation.Token, retryDelayMs: 0));
            }
        }

        [Fact]
        public async Task SupersededRequestDoesNotExposeLateMatchingResult()
        {
            bool current = true;
            var result = await DevicePickerWindow.WaitForHarmonyProcessAsync(Snapshot(0), Bundle, 100,
                _ => { current = false; return Task.FromResult(Snapshot(100)); }, () => current,
                CancellationToken.None, retryDelayMs: 0);
            Assert.Null(result);
        }

        [Fact]
        public void RefreshSelectionUsesOnlyTheCurrentSnapshotAndProfile()
        {
            var requested = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                ProcessPid = 701,
                ProcessName = Bundle
            };
            var refreshed = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                ProcessPid = 802,
                ProcessName = Bundle
            };
            var otherProfile = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 0,
                ProcessPid = 803,
                ProcessName = Bundle
            };

            Assert.Same(refreshed, DevicePickerWindow.ResolveHarmonyRefreshApp(
                new[] { otherProfile, refreshed }, requested));
            Assert.Null(DevicePickerWindow.ResolveHarmonyRefreshApp(
                new[] { otherProfile }, requested));
        }

        [Fact]
        public void RefreshSelectionKeepsTheSelectedHarmonyCloneIdentity()
        {
            var requested = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyAppIndex = 2,
                ProcessPid = 701
            };
            var otherClone = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyAppIndex = 1,
                ProcessPid = 702
            };
            var refreshed = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyAppIndex = 2,
                ProcessPid = 803
            };
            var otherProcess = new ProcessInfo
            {
                Pid = 702,
                Name = Bundle,
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyAppIndex = 1
            };
            var matchingProcess = new ProcessInfo
            {
                Pid = 803,
                Name = Bundle,
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyAppIndex = 2
            };

            Assert.Same(refreshed, DevicePickerWindow.ResolveHarmonyRefreshApp(
                new[] { otherClone, refreshed }, requested));
            Assert.Null(DevicePickerWindow.ResolveHarmonyRefreshApp(
                new[] { otherClone }, requested));
            Assert.Same(matchingProcess, DevicePickerWindow.FindHarmonyProcessForApp(
                new[] { otherProcess, matchingProcess }, refreshed));
            Assert.Null(DevicePickerWindow.FindHarmonyProcessForApp(
                new[] { otherProcess }, refreshed));
        }

        [Fact]
        public void RefreshSelectionDoesNotReuseMissingRequestedApp()
        {
            var requested = new AppInfo
            {
                BundleId = Bundle,
                Platform = "harmony",
                HarmonyUserId = 100,
                ProcessPid = 701
            };

            Assert.Null(DevicePickerWindow.ResolveHarmonyRefreshApp(
                Array.Empty<AppInfo>(), requested));
        }

        [Theory]
        [InlineData(true, true, "", true)]
        [InlineData(false, true, "", false)]
        [InlineData(true, false, "", false)]
        [InlineData(true, true, Bundle, false)]
        public void CancelledOrFailedLaunchRefreshCannotFinalizeStaleTarget(
            bool refreshSucceeded,
            bool refreshIsCurrent,
            string preferredBundleId,
            bool expected)
        {
            Assert.Equal(expected, DevicePickerWindow.CanFinalizeHarmonyLaunchRefresh(
                refreshSucceeded, refreshIsCurrent, preferredBundleId));
        }
    }
}
