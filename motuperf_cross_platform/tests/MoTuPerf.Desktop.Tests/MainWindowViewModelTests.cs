using System.Linq;
using System.Collections.Generic;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class MainWindowViewModelTests
    {
        [Fact]
        public void NewWorkstationHasRealMetricRowsAndNoSelection()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                Assert.Null(viewModel.CurrentSelection);
                Assert.Equal("未选择设备", viewModel.DeviceSummary);
                Assert.Equal(
                    new[]
                    {
                        "Screenshot",
                        "FPS",
                        "Display FrameTime",
                        "Process Memory",
                        "Process CPU Raw",
                        "Device Temperature",
                        "Thermal State"
                    },
                    viewModel.Metrics.Select(delegate(MetricRowViewModel metric) { return metric.Name; }).ToArray());
                string[] expectedDataTiles =
                {
                    "PID",
                    "时长",
                    "CPU Raw",
                    "内存",
                    "FPS",
                    "FrameTime",
                    "Jank",
                    "BigJank",
                    "Temperature",
                    "Thermal State"
                };
                Assert.Equal(expectedDataTiles, viewModel.LiveDataTiles.Select(delegate(DataMetricTileViewModel tile) { return tile.Label; }).ToArray());
                Assert.Equal(expectedDataTiles, viewModel.SelectedDataTiles.Select(delegate(DataMetricTileViewModel tile) { return tile.Label; }).ToArray());
                Assert.All(viewModel.Metrics, delegate(MetricRowViewModel metric) { Assert.True(metric.IsEnabled); });
                Assert.True(viewModel.ShowFrameMetrics);
                Assert.True(viewModel.ShowCoreCpu);
                Assert.False(viewModel.ShowLiveData);
                Assert.False(viewModel.ShowSelectedData);
                Assert.True(viewModel.ShowAnalysisData);
                Assert.True(viewModel.CanShowAnalysisData);
                Assert.NotEmpty(viewModel.AnalysisDataRows);
                Assert.All(viewModel.AnalysisDataRows, delegate(AnalysisMetricRowViewModel row)
                {
                    Assert.Equal("--", row.Maximum);
                    Assert.Equal("--", row.Minimum);
                    Assert.Equal("--", row.Average);
                });
            }
        }

        [Fact]
        public void AnalysisDataUsesCurrentIntervalAndPreservesMissingAndZeroValues()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 1, HasFps = true, Fps = 0, HasCpu = true, CpuPercent = 20, HasMemory = true, MemoryMb = 100, MemoryMetric = "rss" });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 2, HasFps = true, Fps = 60, HasCpu = true, CpuPercent = 40, HasMemory = true, MemoryMb = 200, MemoryMetric = "rss" });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 90, HasFps = true, Fps = 30, HasCpu = true, CpuPercent = 80, HasMemory = true, MemoryMb = 300, MemoryMetric = "rss" });

                AnalysisMetricRowViewModel fps = viewModel.AnalysisDataRows.First(delegate(AnalysisMetricRowViewModel row) { return row.Label == "FPS"; });
                Assert.Equal("60", fps.Maximum);
                Assert.Equal("0", fps.Minimum);
                Assert.Equal("30", fps.Average);

                viewModel.IsChartZoomEnabled = true;
                viewModel.ApplyChartZoom(1, 2);
                fps = viewModel.AnalysisDataRows.First(delegate(AnalysisMetricRowViewModel row) { return row.Label == "FPS"; });
                Assert.Equal("60", fps.Maximum);
                Assert.Equal("0", fps.Minimum);
                Assert.Equal("30", fps.Average);

                AnalysisMetricRowViewModel cpu = viewModel.AnalysisDataRows.First(delegate(AnalysisMetricRowViewModel row) { return row.Label == "CPU Raw"; });
                Assert.Equal("40", cpu.Maximum);
                Assert.Equal("20", cpu.Minimum);
                Assert.Equal("30", cpu.Average);
            }
        }

        [Fact]
        public void SelectedTimeDoesNotRestrictAnalysisToSelectedSample()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 1, HasFps = true, Fps = 10 });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 2, HasFps = true, Fps = 30 });
                viewModel.SelectTimeFromChart(1);

                AnalysisMetricRowViewModel fps = viewModel.AnalysisDataRows.First(delegate(AnalysisMetricRowViewModel row) { return row.Label == "FPS"; });
                Assert.Equal("30", fps.Maximum);
                Assert.Equal("10", fps.Minimum);
                Assert.Equal("20", fps.Average);
            }
        }

        [Fact]
        public void DataPanelDefaultsFollowCaptureLifecycle()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ShowLiveDataTab();
                Assert.True(viewModel.ShowLiveData);
                Assert.False(viewModel.ShowAnalysisData);
                viewModel.ShowAnalysisDataTab();
                Assert.True(viewModel.ShowAnalysisData);
                Assert.False(viewModel.ShowSelectedData);
            }
        }

        [Fact]
        public void DisablingFpsHidesTheWholeFrameMetricGroup()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                MetricRowViewModel fps = viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "FPS"; });
                fps.IsEnabled = false;

                Assert.False(viewModel.ShowFps);
                Assert.False(viewModel.ShowJank);
                Assert.False(viewModel.ShowBigJank);
                Assert.False(viewModel.ShowFrameMetrics);
                Assert.True(viewModel.ShowFrameTime);
            }
        }

        [Fact]
        public void DisablingProcessCpuHidesAllCpuChartRows()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                MetricRowViewModel cpu = viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Process CPU Raw"; });
                cpu.IsEnabled = false;

                Assert.False(viewModel.ShowProcessCpu);
                Assert.False(viewModel.ShowNormalizedCpu);
                Assert.False(viewModel.ShowCoreCpu);
                Assert.True(viewModel.ShowMemory);
            }
        }

        [Fact]
        public void IosSelectionHidesAndroidOnlyCpuChartsAndAndroidRestoresThem()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo { Udid = "ios-1", Name = "iPhone", Platform = "ios" },
                    new AppInfo { Name = "Game", BundleId = "com.example.ios" },
                    new ProcessInfo { Pid = 43, Name = "Game", BundleId = "com.example.ios" }));

                Assert.False(viewModel.ShowNormalizedCpu);
                Assert.False(viewModel.ShowCoreCpu);
                Assert.True(viewModel.ShowProcessCpu);

                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo { Udid = "android-1", Name = "Android", Platform = "android" },
                    new AppInfo { Name = "Game", BundleId = "com.example.game" },
                    new ProcessInfo { Pid = 44, Name = "Game", BundleId = "com.example.game" }));

                Assert.True(viewModel.ShowNormalizedCpu);
                Assert.True(viewModel.ShowCoreCpu);
            }
        }

        [Fact]
        public void CaptureRequiresASelectedPid()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ToggleCaptureCommand.Execute(null);

                Assert.False(viewModel.IsCapturing);
                Assert.Equal("请先选择设备、应用和有效进程", viewModel.Status);
            }
        }

        [Fact]
        public void HarmonyProcessInventoryCanProvideFallbackAppsWhenAppInventoryFails()
        {
            List<AppInfo> apps = DevicePickerWindow.HarmonyAppsFromProcesses(new[]
            {
                new ProcessInfo
                {
                    Pid = 701,
                    Name = "com.example.game:render",
                    DisplayName = "com.example.game:render",
                    BundleId = "com.example.game",
                    Platform = "harmony",
                    Recommended = true,
                    HarmonyUserId = 100
                },
                new ProcessInfo
                {
                    Pid = 702,
                    Name = "com.example.game:worker",
                    DisplayName = "com.example.game:worker",
                    BundleId = "com.example.game",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            });

            Assert.Single(apps);
            Assert.Equal("com.example.game", apps[0].BundleId);
            Assert.Equal("可尝试启动", apps[0].LaunchAvailability);
            Assert.Equal(new[] { 100 }, apps[0].HarmonyUserIds);
            Assert.True(apps[0].Recommended);
            Assert.Equal(0, apps[0].ProcessPid);
            Assert.Empty(apps[0].ProcessName);
        }

        [Fact]
        public void HarmonyProcessFallbackKeepsSameBundlePerUserSelectable()
        {
            List<AppInfo> apps = DevicePickerWindow.HarmonyAppsFromProcesses(new[]
            {
                new ProcessInfo
                {
                    Pid = 711,
                    Name = "com.example.game",
                    DisplayName = "com.example.game",
                    BundleId = "com.example.game",
                    Platform = "harmony",
                    HarmonyUserId = 0
                },
                new ProcessInfo
                {
                    Pid = 712,
                    Name = "com.example.game",
                    DisplayName = "com.example.game",
                    BundleId = "com.example.game",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            });

            Assert.Equal(2, apps.Count);
            Assert.Contains(apps, app => app.BundleId == "com.example.game" && app.HarmonyUserId == 0);
            Assert.Contains(apps, app => app.BundleId == "com.example.game" && app.HarmonyUserId == 100);
        }

        [Fact]
        public void HarmonyProcessFallbackBindsPidOnlyWhenBundleProfileIsUnique()
        {
            List<AppInfo> apps = DevicePickerWindow.HarmonyAppsFromProcesses(new[]
            {
                new ProcessInfo
                {
                    Pid = 713,
                    Name = "com.example.single",
                    DisplayName = "Single App",
                    BundleId = "com.example.single",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            });

            AppInfo app = Assert.Single(apps);
            Assert.Equal(713, app.ProcessPid);
            Assert.Equal("com.example.single", app.ProcessName);
            Assert.Equal(100, app.HarmonyUserId);
        }

        [Fact]
        public void HarmonyProcessFallbackKeepsUnbundledServicesSelectable()
        {
            List<AppInfo> apps = DevicePickerWindow.HarmonyAppsFromProcesses(new[]
            {
                new ProcessInfo
                {
                    Pid = 801,
                    Name = "foundation",
                    DisplayName = "foundation",
                    Platform = "harmony",
                    HarmonyUserId = 0
                }
            });

            var app = Assert.Single(apps);
            Assert.Empty(app.BundleId);
            Assert.Equal(801, app.ProcessPid);
            Assert.Equal("PID 801", app.TargetIdentifier);
            Assert.Equal("仅运行中可采集", app.LaunchAvailability);
        }

        [Fact]
        public void HarmonyProcessWithUnreadableNameUsesBundleAndPidInPicker()
        {
            var process = new ProcessInfo
            {
                Pid = 802,
                Platform = "harmony",
                BundleId = "com.example.game",
                OwnerBundleId = "com.example.game",
                OwnershipVerified = true,
                HarmonyUserId = 100,
                DisplayName = "com.example.game"
            };

            Assert.True(ProcessTargetMatcher.IsValidTarget(process));
            Assert.Equal("com.example.game", process.PickerName);
            Assert.Contains("进程名不可读", process.PickerSubtitle);
            var app = Assert.Single(DevicePickerWindow.HarmonyAppsFromProcesses(new[] { process }));
            Assert.Equal(802, app.ProcessPid);
            Assert.Empty(app.ProcessName);
            Assert.False(app.CanAttemptLaunch);
        }

        [Fact]
        public void HarmonyAppDoesNotAutoBindAmbiguousWorkerProcesses()
        {
            ProcessInfo[] processes =
            {
                new ProcessInfo
                {
                    Pid = 901,
                    Name = "com.example.game:render",
                    BundleId = "com.example.game",
                    Platform = "harmony"
                },
                new ProcessInfo
                {
                    Pid = 902,
                    Name = "com.example.game:worker",
                    BundleId = "com.example.game",
                    Platform = "harmony"
                }
            };

            Assert.Null(DevicePickerWindow.FindHarmonyProcessForBundle(processes, "com.example.game"));
        }

        [Fact]
        public void HarmonyAppPrefersTheRealMainOrUniqueRecommendedProcess()
        {
            ProcessInfo[] withMain =
            {
                new ProcessInfo
                {
                    Pid = 903,
                    Name = "com.example.game:render",
                    BundleId = "com.example.game",
                    Platform = "harmony",
                    Recommended = true
                },
                new ProcessInfo
                {
                    Pid = 904,
                    Name = "com.example.game",
                    BundleId = "com.example.game",
                    Platform = "harmony"
                }
            };
            Assert.Equal(904, DevicePickerWindow.FindHarmonyProcessForBundle(withMain, "com.example.game").Pid);

            ProcessInfo[] withRecommendation =
            {
                new ProcessInfo
                {
                    Pid = 905,
                    Name = "com.example.game:render",
                    BundleId = "com.example.game",
                    Platform = "harmony",
                    Recommended = true
                },
                new ProcessInfo
                {
                    Pid = 906,
                    Name = "com.example.game:worker",
                    BundleId = "com.example.game",
                    Platform = "harmony"
                }
            };
            Assert.Equal(905, DevicePickerWindow.FindHarmonyProcessForBundle(withRecommendation, "com.example.game").Pid);
        }

        [Fact]
        public void HarmonyAppBindsProcessFromTheSelectedUserOnly()
        {
            ProcessInfo[] processes =
            {
                new ProcessInfo
                {
                    Pid = 1001,
                    Name = "com.example.shared",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 0
                },
                new ProcessInfo
                {
                    Pid = 1101,
                    Name = "com.example.shared",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            };
            AppInfo workApp = new AppInfo
            {
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = 100
            };

            Assert.Equal(1101, DevicePickerWindow.FindHarmonyProcessForApp(processes, workApp).Pid);
            Assert.Null(DevicePickerWindow.FindHarmonyProcessForBundle(processes, "com.example.shared", 200));
        }

        [Fact]
        public void HarmonyAppDoesNotBindUnknownProcessUserToASelectedProfile()
        {
            ProcessInfo[] processes =
            {
                new ProcessInfo
                {
                    Pid = 1201,
                    Name = "com.example.shared",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = -1
                }
            };
            AppInfo workApp = new AppInfo
            {
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = 100
            };

            Assert.Null(DevicePickerWindow.FindHarmonyProcessForApp(processes, workApp));
        }

        [Fact]
        public void UnknownHarmonyProcessDoesNotSynchronizeToKnownProfileApp()
        {
            var apps = new[]
            {
                new AppInfo { BundleId = "com.example.shared", Platform = "harmony", HarmonyUserId = 100 }
            };
            var process = new ProcessInfo
            {
                Pid = 1202,
                Name = "com.example.shared",
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = -1
            };

            Assert.Null(DevicePickerWindow.FindHarmonyAppForProcess(apps, process));
        }

        [Theory]
        [InlineData(100, -1, false)]
        [InlineData(-1, 100, false)]
        [InlineData(100, 0, false)]
        [InlineData(100, 100, true)]
        [InlineData(-1, -1, true)]
        public void HarmonyPidShortcutRequiresTheSameProfileInBothDirections(int appUser, int processUser, bool expected)
        {
            var app = new AppInfo
            {
                BundleId = "com.example.shared", Platform = "harmony", HarmonyUserId = appUser,
                ProcessPid = 501, ProcessName = "com.example.shared"
            };
            var process = new ProcessInfo
            {
                Pid = 501, Name = "com.example.shared", BundleId = "com.example.shared",
                Platform = "harmony", HarmonyUserId = processUser
            };
            Assert.Equal(expected, DevicePickerWindow.FindHarmonyAppForProcess(new[] { app }, process) == app);
            Assert.Equal(expected, DevicePickerWindow.FindHarmonyProcessForApp(new[] { process }, app) == process);
            var restoredApp = new AppInfo
            {
                BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = processUser,
                ProcessPid = app.ProcessPid, ProcessName = app.ProcessName
            };
            Assert.Equal(expected, DevicePickerWindow.SameAppSelection(app, restoredApp));
        }

        [Fact]
        public void HarmonyUnbundledProcessAndExactProfileRemainSelectable()
        {
            var process = new ProcessInfo { Pid = 501, Name = "foundation", Platform = "harmony", HarmonyUserId = 0 };
            var app = Assert.Single(DevicePickerWindow.HarmonyAppsFromProcesses(new[] { process }));
            Assert.Same(process, DevicePickerWindow.FindHarmonyProcessForApp(new[] { process }, app));
            Assert.Same(app, DevicePickerWindow.FindHarmonyAppForProcess(new[] { app }, process));
            process.Name = "different-service";
            Assert.Null(DevicePickerWindow.FindHarmonyProcessForApp(new[] { process }, app));
            Assert.Null(DevicePickerWindow.FindHarmonyAppForProcess(new[] { app }, process));
        }

        [Fact]
        public void UnknownHarmonyAppCannotChooseAKnownUserEvenWhenRecommended()
        {
            var app = new AppInfo { BundleId = "com.example.shared", Platform = "harmony" };
            var known = new ProcessInfo
            {
                Pid = 501, Name = app.BundleId, BundleId = app.BundleId, Platform = "harmony",
                HarmonyUserId = 100, Recommended = true
            };
            var unknown = new ProcessInfo
            {
                Pid = 502, Name = app.BundleId + ":worker", BundleId = app.BundleId, Platform = "harmony"
            };
            Assert.Null(DevicePickerWindow.FindHarmonyProcessForApp(new[] { known }, app));
            Assert.Same(unknown, DevicePickerWindow.FindHarmonyProcessForApp(new[] { known, unknown }, app));
            var knownApp = new AppInfo { BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 100 };
            Assert.Null(DevicePickerWindow.FindHarmonyAppByBundle(new[] { knownApp }, app.BundleId, -1));
            Assert.Same(app, DevicePickerWindow.FindHarmonyAppByBundle(new[] { knownApp, app }, app.BundleId, -1));
            Assert.Same(knownApp, DevicePickerWindow.FindHarmonyAppForProcess(new[] { app, knownApp }, known));
        }

        [Fact]
        public void HarmonyViewPreservesManualWorkerAndRejectsPidReuse()
        {
            var app = new AppInfo { BundleId = "com.example.shared", Platform = "harmony", HarmonyUserId = 100 };
            var main = new ProcessInfo
            {
                Pid = 501, Name = app.BundleId, BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 100
            };
            var worker = new ProcessInfo
            {
                Pid = 502, Name = app.BundleId + ":worker", BundleId = app.BundleId, Platform = "harmony",
                HarmonyUserId = 100, HarmonyStartTimeTicks = 42
            };
            Assert.Same(worker, DevicePickerWindow.SelectHarmonyProcessForView(new[] { main, worker }, worker, app));
            Assert.Same(main, DevicePickerWindow.SelectHarmonyProcessForView(new[] { main, worker }, null, app));
            Assert.Null(DevicePickerWindow.SelectHarmonyProcessForView(new ProcessInfo[0], worker, app));
            var reused = new ProcessInfo
            {
                Pid = worker.Pid, Name = worker.Name, BundleId = worker.BundleId, Platform = "harmony",
                HarmonyUserId = 100, HarmonyStartTimeTicks = 43
            };
            Assert.Null(DevicePickerWindow.SelectHarmonyProcessForView(new[] { main, reused }, worker, app));
        }

        [Fact]
        public void HarmonyAppRestoreDoesNotFallBackAcrossUsers()
        {
            var apps = new AppInfo[]
            {
                new AppInfo { BundleId = "com.example.shared", HarmonyUserId = 0, Platform = "harmony" },
                new AppInfo { BundleId = "com.example.shared", HarmonyUserId = 100, Platform = "harmony" }
            };

            Assert.Equal(100, DevicePickerWindow.FindHarmonyAppByBundle(apps, "com.example.shared", 100).HarmonyUserId);
            Assert.Null(DevicePickerWindow.FindHarmonyAppByBundle(apps, "com.example.shared", 200));
            Assert.Null(DevicePickerWindow.FindHarmonyAppByBundle(apps, "com.example.shared", -1));

            var singleProfile = new[]
            {
                new AppInfo { BundleId = "com.example.single", HarmonyUserId = -1, Platform = "harmony" }
            };
            Assert.NotNull(DevicePickerWindow.FindHarmonyAppByBundle(singleProfile, "com.example.single", -1));
        }

        [Theory]
        [InlineData(true, true, false, false)]
        [InlineData(true, true, true, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        public void HarmonyLaunchDoesNotFallbackToAnotherProcessWhenTargetIsMissing(
            bool isHarmony,
            bool preserveHarmonyAppPreference,
            bool hasSelectedApp,
            bool expected)
        {
            AppInfo app = hasSelectedApp
                ? new AppInfo { BundleId = "com.example.target", Platform = "harmony" }
                : null;
            Assert.Equal(
                expected,
                DevicePickerWindow.ShouldAutoSelectFallbackProcess(
                    isHarmony,
                    preserveHarmonyAppPreference,
                    app,
                    null));
        }

        [Fact]
        public void HarmonyLaunchModeKeepsTheSelectedAppWhenProcessBelongsToAnotherTarget()
        {
            var selectedApp = new AppInfo
            {
                BundleId = "com.example.selected",
                Platform = "harmony",
                HarmonyUserId = 100
            };
            var otherApp = new AppInfo
            {
                BundleId = "com.example.other",
                Platform = "harmony",
                HarmonyUserId = 100
            };
            var otherProcess = new ProcessInfo
            {
                Pid = 901,
                Name = otherApp.BundleId,
                BundleId = otherApp.BundleId,
                Platform = "harmony",
                HarmonyUserId = 100
            };

            Assert.Same(
                selectedApp,
                DevicePickerWindow.ResolveHarmonyConfirmedApp(
                    true,
                    selectedApp,
                    otherProcess,
                    new[] { selectedApp, otherApp }));
            Assert.Same(
                otherApp,
                DevicePickerWindow.ResolveHarmonyConfirmedApp(
                    false,
                    selectedApp,
                    otherProcess,
                    new[] { selectedApp, otherApp }));
            Assert.Null(DevicePickerWindow.ResolveHarmonyConfirmedProcess(true, selectedApp, otherProcess, new[] { otherProcess }));
            Assert.Same(otherProcess, DevicePickerWindow.ResolveHarmonyConfirmedProcess(false, selectedApp, otherProcess, new[] { otherProcess }));
        }

        [Fact]
        public void PendingHarmonyLaunchSurvivesMissingAppRowsAndRepeatedFiltering()
        {
            var other = new ProcessInfo
            {
                Pid = 900, Name = "com.example.other", BundleId = "com.example.other",
                Platform = "harmony", HarmonyUserId = 100, Recommended = true
            };
            var otherProfile = new ProcessInfo
            {
                Pid = 901, Name = "com.example.target", BundleId = "com.example.target",
                Platform = "harmony", HarmonyUserId = 0, Recommended = true
            };
            var items = new[] { other, otherProfile };
            ProcessInfo selected = other;
            for (int refresh = 0; refresh < 3; refresh++)
            {
                selected = DevicePickerWindow.SelectHarmonyProcessForView(items, selected, null, "com.example.target", 100);
                Assert.Null(selected);
            }
            var target = new ProcessInfo
            {
                Pid = 902, Name = "com.example.target", BundleId = "com.example.target",
                Platform = "harmony", HarmonyUserId = 100
            };
            Assert.Same(target, DevicePickerWindow.SelectHarmonyProcessForView(
                new[] { other, otherProfile, target }, null, null, "com.example.target", 100));
            Assert.Same(other, DevicePickerWindow.SelectHarmonyProcessForView(items, other, null));
        }

        [Fact]
        public void LaunchConfirmationResolvesOnlyTheSelectedProfileAndRequiresARealPid()
        {
            var app = new AppInfo { BundleId = "com.example.target", Platform = "harmony", HarmonyUserId = 100 };
            var foreign = new ProcessInfo
            {
                Pid = 1, Name = app.BundleId, BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 0
            };
            var target = new ProcessInfo
            {
                Pid = 2, Name = app.BundleId, BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 100,
                HarmonyStartTimeTicks = 42
            };
            Assert.Null(DevicePickerWindow.ResolveHarmonyConfirmedProcess(true, app, foreign, new[] { foreign }));
            Assert.Same(target, DevicePickerWindow.ResolveHarmonyConfirmedProcess(true, app, foreign, new[] { foreign, target }));
            Assert.Null(DevicePickerWindow.ResolveHarmonyConfirmedProcess(true, null, target, new[] { target }));
            Assert.Null(DevicePickerWindow.ResolveHarmonyConfirmedProcess(true, app, target, new ProcessInfo[0]));
            var reused = new ProcessInfo
            {
                Pid = target.Pid, Name = target.Name, BundleId = target.BundleId, Platform = "harmony",
                HarmonyUserId = 100, HarmonyStartTimeTicks = 43
            };
            Assert.Null(DevicePickerWindow.ResolveHarmonyConfirmedProcess(false, app, target, new[] { reused }));
            var worker = new ProcessInfo
            {
                Pid = 3, Name = app.BundleId + ":worker", BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 100
            };
            Assert.Same(worker, DevicePickerWindow.ResolveHarmonyConfirmedProcess(false, app, worker, new[] { target, worker }));
        }

        [Fact]
        public void PendingHarmonyLaunchDoesNotGuessAmongMultipleWorkers()
        {
            var app = new AppInfo { BundleId = "com.example.target", Platform = "harmony", HarmonyUserId = 100 };
            var workers = new[]
            {
                new ProcessInfo { Pid = 10, Name = app.BundleId + ":worker", BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 100 },
                new ProcessInfo { Pid = 11, Name = app.BundleId + ":render", BundleId = app.BundleId, Platform = "harmony", HarmonyUserId = 100 }
            };
            Assert.Null(DevicePickerWindow.SelectHarmonyProcessForView(workers, workers[0], app, app.BundleId, 100));
            Assert.Null(DevicePickerWindow.ResolveHarmonyConfirmedProcess(true, app, workers[0], workers));
            Assert.Same(workers[1], DevicePickerWindow.ResolveHarmonyConfirmedProcess(false, app, workers[1], workers));
        }

        [Fact]
        public void RestoredSessionSelectionMustBeConfirmedBeforeCapture()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                DeviceSelection current = new DeviceSelection(
                    new DeviceInfo { Udid = "device-current", Name = "Android", Platform = "android" },
                    new AppInfo { Name = "Game", BundleId = "com.example.game" },
                    new ProcessInfo { Pid = 42, Name = "com.example.game", BundleId = "com.example.game" });
                viewModel.ApplySelection(current);
                Assert.False(viewModel.CaptureSelectionNeedsPicker);

                viewModel.ApplySessionDocument(new SessionDocument
                {
                    Format = "motuperf-session",
                    Version = 5,
                    Device = current.Device,
                    App = current.App,
                    Process = current.Process
                });

                Assert.NotNull(viewModel.CurrentSelection);
                Assert.True(viewModel.CaptureSelectionNeedsPicker);
                viewModel.ApplySelection(current);
                Assert.False(viewModel.CaptureSelectionNeedsPicker);
            }
        }

        [Fact]
        public void SessionLoadingStateCanBeShownAndClearedExplicitly()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                Assert.False(viewModel.IsSessionLoading);
                Assert.True(viewModel.BeginSessionLoading());
                Assert.True(viewModel.IsSessionLoading);
                Assert.Equal("正在打开现场文件，请稍候...", viewModel.SessionLoadingText);
                Assert.False(viewModel.BeginSessionLoading());

                viewModel.EndSessionLoading();

                Assert.False(viewModel.IsSessionLoading);
            }
        }

        [Fact]
        public void FileOperationStateIsDynamicIdempotentAndCompatibleWithSessionLoading()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                Assert.False(viewModel.IsFileOperationInProgress);
                Assert.True(viewModel.BeginFileOperation("正在保存现场文件，请稍候...", "正在写入截图，请勿关闭软件"));
                Assert.True(viewModel.IsFileOperationInProgress);
                Assert.False(viewModel.CanUseFileActions);
                Assert.False(viewModel.CanChangeDevice);
                Assert.False(viewModel.CanToggleCapture);
                Assert.True(viewModel.IsSessionLoading);
                Assert.Equal("正在保存现场文件，请稍候...", viewModel.FileOperationText);
                Assert.Equal("正在写入截图，请勿关闭软件", viewModel.FileOperationDetailText);
                Assert.Equal(viewModel.FileOperationText, viewModel.SessionLoadingText);
                Assert.False(viewModel.BeginFileOperation("另一个操作", "不应覆盖"));

                viewModel.SetDataMigrationProgress(new MoTuPerf.Platform.RuntimeDataMigrationProgress(100, 25, 4, 1, 0));
                Assert.False(viewModel.IsFileOperationIndeterminate);
                Assert.Equal(25, viewModel.FileOperationProgress);
                Assert.Contains("25.0%", viewModel.FileOperationProgressText);
                Assert.Contains("预计剩余", viewModel.FileOperationProgressText);

                viewModel.EndFileOperation();
                viewModel.EndFileOperation();

                Assert.False(viewModel.IsFileOperationInProgress);
                Assert.False(viewModel.IsSessionLoading);
                Assert.True(viewModel.CanUseFileActions);
                Assert.True(viewModel.CanChangeDevice);
                Assert.True(viewModel.CanToggleCapture);
            }
        }

        [Fact]
        public void InvalidProcessSelectionDoesNotReplaceCurrentSelection()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo { Udid = "ios-1", Name = "iPhone", Platform = "ios" },
                    new AppInfo { Name = "WeChat", BundleId = "com.tencent.xin" },
                    new ProcessInfo { Pid = 0, Name = "kernel_task" }));

                Assert.Null(viewModel.CurrentSelection);
                Assert.Equal("当前进程 PID 无效，请刷新进程列表后重新选择。", viewModel.Status);
            }
        }

        [Fact]
        public void DeviceInfoPanelUsesTheCurrentDeviceAppAndProcessDetails()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo
                    {
                        Udid = "android-device-32dba6",
                        Name = "V1938T",
                        MarketName = "vivo V1938T",
                        ProductVersion = "10",
                        ConnType = "ADB",
                        CpuInfo = "Samsung EXYNOS980",
                        GpuInfo = "ARM Mali-G76",
                        Resolution = "1080x2400",
                        Platform = "android"
                    },
                    new AppInfo { Name = "Game", BundleId = "com.example.game" },
                    new ProcessInfo { Pid = 16551, Name = "com.example.game", BundleId = "com.example.game" }));

                Assert.False(viewModel.IsDeviceInfoVisible);
                viewModel.ToggleDeviceInfo();

                Assert.True(viewModel.IsDeviceInfoVisible);
                Assert.Contains("设备名称：\nvivo V1938T", viewModel.DeviceInfoText);
                Assert.Contains("当前应用：\ncom.example.game\nGame", viewModel.DeviceInfoText);
                Assert.Contains("当前进程：\ncom.example.game\npid 16551", viewModel.DeviceInfoText);
                Assert.Contains("CPU信息：Samsung EXYNOS980", viewModel.DeviceInfoText);
                Assert.Contains("GPU信息：ARM Mali-G76", viewModel.DeviceInfoText);
                Assert.Contains("系统信息：Android 10", viewModel.DeviceInfoText);
                Assert.Contains("分辨率：1080x2400", viewModel.DeviceInfoText);
                Assert.Contains("平台：Android", viewModel.DeviceInfoText);
                Assert.Contains("连接方式：ADB", viewModel.DeviceInfoText);
            }
        }

        [Fact]
        public void ThermalMetricUsesPlatformSpecificNameRangeAndLegend()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                MetricRowViewModel thermal = viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Thermal State"; });
                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo { Udid = "android-1", Name = "Android", Platform = "android" },
                    new AppInfo { Name = "Game", BundleId = "com.example.game" },
                    new ProcessInfo { Pid = 42, Name = "com.example.game", BundleId = "com.example.game" }));

                Assert.Equal("Thermal Status", viewModel.ThermalMetricTitle);
                Assert.Equal("0正常 / 1轻微 / 2中度 / 3严重 / 4临界 / 5紧急 / 6关机", viewModel.ThermalMetricLegend);
                Assert.Equal(6, viewModel.ThermalAxisMax);
                Assert.Equal("Thermal Status", thermal.DisplayName);
                Assert.Equal("系统热状态", thermal.Description);
                Assert.Equal("0-6", thermal.Unit);

                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo { Udid = "ios-1", Name = "iPhone", Platform = "ios" },
                    new AppInfo { Name = "Game", BundleId = "com.example.ios" },
                    new ProcessInfo { Pid = 43, Name = "Game", BundleId = "com.example.ios" }));

                Assert.Equal("Thermal State", viewModel.ThermalMetricTitle);
                Assert.Equal("0正常 / 1升温 / 2严重 / 3临界", viewModel.ThermalMetricLegend);
                Assert.Equal(3, viewModel.ThermalAxisMax);
                Assert.Equal("Thermal State", thermal.DisplayName);
                Assert.Equal("系统热状态", thermal.Description);
                Assert.Equal("0-3", thermal.Unit);
            }
        }

        [Fact]
        public void ChartTimeSelectionPinsCursorWithoutChangingViewRange()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.SelectTimeFromChart(2.0);

                Assert.False(viewModel.FollowLatest);
                Assert.Equal(2.0, viewModel.SelectedTime);
                Assert.Equal(0, viewModel.ViewStartTime);
                Assert.Equal(0, viewModel.ViewEndTime);
            }
        }

        [Fact]
        public void ChartZoomIsViewOnlyAndKeepsItsWindowDuringNewSamples()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 0, HasFps = true, Fps = 60 });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 300, HasFps = true, Fps = 45 });
                viewModel.IsChartZoomEnabled = true;

                viewModel.ApplyChartZoom(120, 130);

                Assert.True(viewModel.IsChartZoomed);
                Assert.True(viewModel.ShowChartZoomReset);
                Assert.Equal(95, viewModel.ViewStartTime);
                Assert.Equal(155, viewModel.ViewEndTime);
                viewModel.ApplySample(new PerfSample { ElapsedSec = 301, HasFps = true, Fps = 30 });
                Assert.Equal(95, viewModel.ViewStartTime);
                Assert.Equal(155, viewModel.ViewEndTime);

                SessionDocument session = viewModel.BuildSessionDocument();
                Assert.Equal(0, session.ViewStartTime);
                Assert.Equal(0, session.ViewEndTime);
                Assert.Equal(3, session.Samples.Count);
            }
        }

        [Fact]
        public void DisablingChartZoomRestoresViewWithoutMovingSelectedTime()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 0 });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 180 });
                viewModel.SelectTimeFromChart(120);
                viewModel.IsChartZoomEnabled = true;
                viewModel.ApplyChartZoom(90, 150);

                viewModel.IsChartZoomEnabled = false;

                Assert.False(viewModel.IsChartZoomed);
                Assert.False(viewModel.ShowChartZoomReset);
                Assert.Equal(0, viewModel.ViewStartTime);
                Assert.Equal(0, viewModel.ViewEndTime);
                Assert.Equal(120, viewModel.SelectedTime);
            }
        }

        [Fact]
        public void IncomingSamplesUpdateLiveDataWithoutAutoSelectingLatest()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 1, TargetPid = 42, HasFps = true, Fps = 45, HasMemory = true, MemoryMb = 512 });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 2, TargetPid = 42, HasFps = true, Fps = 58, HasMemory = true, MemoryMb = 520 });

                Assert.Null(viewModel.SelectedTime);
                Assert.True(viewModel.FollowLatest);
                Assert.Equal("58.0", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "FPS"; }).Value);
                Assert.All(viewModel.SelectedDataTiles, delegate(DataMetricTileViewModel tile) { Assert.Equal("--", tile.Value); });
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "FPS"; }).SelectedValue);
            }
        }

        [Fact]
        public void UserSelectedTimeRemainsPinnedWhenNewSamplesArrive()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 1, HasFps = true, Fps = 45 });
                viewModel.SelectTimeFromChart(1);
                viewModel.ApplySample(new PerfSample { ElapsedSec = 2, HasFps = true, Fps = 60 });

                Assert.Equal(1, viewModel.SelectedTime);
                Assert.False(viewModel.FollowLatest);
                Assert.Equal("60.0", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "FPS"; }).Value);
                Assert.Equal("45.0", viewModel.SelectedDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "FPS"; }).Value);
                Assert.Equal("45", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "FPS"; }).SelectedValue);
            }
        }

        [Fact]
        public void SelectingScreenshotUpdatesSelectedTimeAndMetricPanel()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            using (ScreenshotItemViewModel screenshot = new ScreenshotItemViewModel(
                new ScreenshotInfo { ElapsedSec = 2, Path = "" }, false))
            {
                viewModel.ApplySample(new PerfSample { ElapsedSec = 2, HasFps = true, Fps = 48 });

                viewModel.SelectScreenshot(screenshot);

                Assert.Equal(2, viewModel.SelectedTime);
                Assert.False(viewModel.FollowLatest);
                Assert.Equal("48.0", viewModel.SelectedDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "FPS"; }).Value);
                Assert.Equal("48", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "FPS"; }).SelectedValue);
            }
        }

        [Fact]
        public void DataTilesDoNotShowExpiredMetricValues()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample
                {
                    ElapsedSec = 0,
                    TargetPid = 42,
                    HasFps = true,
                    Fps = 60,
                    HasMemory = true,
                    MemoryMb = 512,
                    HasTemperature = true,
                    TemperatureCelsius = new Dictionary<string, double> { { "battery", 35.0 } },
                    HasThermalState = true,
                    ThermalStateLevel = 1,
                    ThermalStateName = "fair"
                });
                viewModel.ApplySample(new PerfSample
                {
                    ElapsedSec = 10,
                    TargetPid = 42,
                    HasFps = true,
                    Fps = 60
                });

                Assert.Equal("--", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "内存"; }).Value);
                Assert.Equal("--", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "Temperature"; }).Value);
                Assert.Equal("--", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "Thermal State"; }).Value);
                Assert.Equal("60.0", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "FPS"; }).Value);
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Process Memory"; }).Value);
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Device Temperature"; }).Value);
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Thermal State"; }).Value);
            }
        }

        [Fact]
        public void DataTilesKeepARecentMetricValueWithoutChangingMissingSemantics()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySample(new PerfSample
                {
                    ElapsedSec = 0,
                    TargetPid = 42,
                    HasFps = true,
                    Fps = 60,
                    HasMemory = true,
                    MemoryMb = 512,
                    HasTemperature = true,
                    TemperatureCelsius = new Dictionary<string, double> { { "battery", 35.0 } },
                    HasThermalState = true,
                    ThermalStateLevel = 1,
                    ThermalStateName = "fair"
                });
                viewModel.ApplySample(new PerfSample { ElapsedSec = 2, TargetPid = 42, HasFps = true, Fps = 60 });

                Assert.Equal("512 MB", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "内存"; }).Value);
                Assert.Equal("battery 35.0°C", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "Temperature"; }).Value);
                Assert.Equal("1 升温", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "Thermal State"; }).Value);
            }
        }

        [Fact]
        public void SessionDocumentRoundTripRestoresSamplesSelectionAndCursor()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                SessionDocument input = new SessionDocument
                {
                    Format = "motuperf-session",
                    Version = 5,
                    Device = new DeviceInfo { Udid = "device-1", Name = "Test", Platform = "ios" },
                    App = new AppInfo { BundleId = "com.example.game", Name = "Game" },
                    Process = new ProcessInfo { Pid = 42, Name = "Game" },
                    SelectedTime = 1.0,
                    FollowLatest = false,
                    ViewStartTime = 0.9,
                    ViewEndTime = 1.1,
                    Samples = new List<PerfSample> { new PerfSample { ElapsedSec = 1, HasFps = true, Fps = 60 } }
                };

                viewModel.ApplySessionDocument(input);
                SessionDocument output = viewModel.BuildSessionDocument();

                Assert.Single(viewModel.Samples);
                Assert.Equal(60, viewModel.Samples[0].Fps);
                Assert.Equal(42, viewModel.CurrentSelection.Process.Pid);
                Assert.Equal(1.0, viewModel.SelectedTime);
                Assert.Equal(0, viewModel.ViewStartTime);
                Assert.Equal(0, viewModel.ViewEndTime);
                Assert.Equal("com.example.game", output.SelectedBundleId);
                Assert.Equal(0, output.ViewStartTime);
                Assert.Equal(0, output.ViewEndTime);
                Assert.Single(output.Samples);
                Assert.Equal("42", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "PID"; }).Value);
                Assert.Equal("60.0", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "FPS"; }).Value);
            }
        }

        [Fact]
        public void RestoredSessionDoesNotPresentExpiredMetricsAsLatestValues()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySessionDocument(new SessionDocument
                {
                    Format = "motuperf-session",
                    Version = 5,
                    Device = new DeviceInfo { Udid = "ios-1", Name = "iPhone", Platform = "ios" },
                    App = new AppInfo { BundleId = "com.example.game", Name = "Game" },
                    Process = new ProcessInfo { Pid = 42, Name = "Game", BundleId = "com.example.game" },
                    Samples = new List<PerfSample>
                    {
                        new PerfSample
                        {
                            ElapsedSec = 0,
                            HasFps = true,
                            Fps = 60,
                            HasMemory = true,
                            MemoryMb = 512,
                            HasTemperature = true,
                            TemperatureCelsius = new Dictionary<string, double> { { "battery", 35.0 } },
                            HasThermalState = true,
                            ThermalStateLevel = 1,
                            ThermalStateName = "fair"
                        },
                        new PerfSample { ElapsedSec = 10 }
                    }
                });

                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "FPS"; }).Value);
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Process Memory"; }).Value);
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Device Temperature"; }).Value);
                Assert.Equal("--", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Thermal State"; }).Value);
                Assert.Equal("--", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "内存"; }).Value);
                Assert.Equal("--", viewModel.LiveDataTiles.First(delegate(DataMetricTileViewModel tile) { return tile.Label == "Temperature"; }).Value);
            }
        }

        [Fact]
        public void RestoredHarmonySessionUsesHarmonyFrameFreshness()
        {
            using (MainWindowViewModel viewModel = new MainWindowViewModel())
            {
                viewModel.ApplySessionDocument(new SessionDocument
                {
                    Format = "motuperf-session",
                    Version = 5,
                    Device = new DeviceInfo { Udid = "harmony-1", Name = "Harmony", Platform = "harmony" },
                    App = new AppInfo { BundleId = "com.example.harmony", Name = "Game" },
                    Process = new ProcessInfo { Pid = 42, Name = "Game", BundleId = "com.example.harmony", Platform = "harmony" },
                    Samples = new List<PerfSample>
                    {
                        new PerfSample { ElapsedSec = 0, HasFps = true, Fps = 60, HasFrameTimeMax = true, FrameTimeMaxMs = 16.7 },
                        new PerfSample { ElapsedSec = 3.8, TargetPid = 42 }
                    }
                });

                Assert.Equal("60", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "FPS"; }).Value);
                Assert.Equal("16.7", viewModel.Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Display FrameTime"; }).Value);
            }
        }

        [Fact]
        public void HarmonyDefaultPickerHidesSystemOnlyTargetsButKeepsUserAndForegroundTargets()
        {
            AppInfo system = new AppInfo
            {
                BundleId = "com.example.system",
                Platform = "harmony",
                IsSystemApp = true,
                IsPreInstallApp = true
            };
            AppInfo user = new AppInfo
            {
                BundleId = "com.example.user",
                Platform = "harmony"
            };
            AppInfo foreground = new AppInfo
            {
                BundleId = "com.example.foreground",
                Platform = "harmony",
                Recommended = true,
                IsSystemApp = true
            };

            Assert.False(DevicePickerWindow.IsHarmonyDefaultAppTarget(system));
            Assert.True(DevicePickerWindow.IsHarmonyDefaultAppTarget(user));
            Assert.True(DevicePickerWindow.IsHarmonyDefaultAppTarget(foreground));
        }

        [Fact]
        public void HarmonyDefaultProcessFilterKeepsForegroundAndUserBundleButHidesOrphanSystemProcess()
        {
            AppInfo user = new AppInfo
            {
                BundleId = "com.example.user",
                Platform = "harmony"
            };
            AppInfo system = new AppInfo
            {
                BundleId = "com.example.system",
                Platform = "harmony",
                IsSystemApp = true,
                IsPreInstallApp = true
            };
            ProcessInfo userProcess = new ProcessInfo
            {
                Pid = 101,
                Name = user.BundleId,
                BundleId = user.BundleId,
                OwnerBundleId = user.BundleId,
                Platform = "harmony"
            };
            ProcessInfo systemProcess = new ProcessInfo
            {
                Pid = 102,
                Name = system.BundleId,
                BundleId = system.BundleId,
                OwnerBundleId = system.BundleId,
                Platform = "harmony"
            };
            ProcessInfo foregroundSystemProcess = new ProcessInfo
            {
                Pid = 103,
                Name = system.BundleId,
                BundleId = system.BundleId,
                OwnerBundleId = system.BundleId,
                Platform = "harmony",
                ForegroundApplication = true
            };

            Assert.True(DevicePickerWindow.IsHarmonyDefaultProcessTarget(userProcess, new[] { user, system }));
            Assert.False(DevicePickerWindow.IsHarmonyDefaultProcessTarget(systemProcess, new[] { user, system }));
            Assert.True(DevicePickerWindow.IsHarmonyDefaultProcessTarget(foregroundSystemProcess, new[] { user, system }));
            Assert.False(DevicePickerWindow.IsHarmonyDefaultProcessTarget(new ProcessInfo
            {
                Pid = 104,
                BundleId = user.BundleId,
                Platform = "harmony",
                ForegroundApplication = true
            }, new[] { user, system }));
        }

        [Fact]
        public void HarmonyPickerShowsUserScopeOnlyWhenBundleHasMultipleProfiles()
        {
            AppInfo singleProfile = new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.single",
                HarmonyUserId = 100
            };
            AppInfo profileZero = new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.shared",
                HarmonyUserId = 0
            };
            AppInfo profileOneHundred = new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.shared",
                HarmonyUserId = 100
            };
            ProcessInfo processZero = new ProcessInfo
            {
                Pid = 10,
                Platform = "harmony",
                BundleId = "com.example.shared",
                Name = "com.example.shared",
                HarmonyUserId = 0
            };
            ProcessInfo processOneHundred = new ProcessInfo
            {
                Pid = 11,
                Platform = "harmony",
                BundleId = "com.example.shared",
                Name = "com.example.shared",
                HarmonyUserId = 100
            };

            DevicePickerWindow.MarkHarmonyPickerUserScopeVisibility(
                new[] { singleProfile, profileZero, profileOneHundred },
                new[] { processZero, processOneHundred });

            Assert.False(singleProfile.ShowHarmonyUserScope);
            Assert.True(profileZero.ShowHarmonyUserScope);
            Assert.True(profileOneHundred.ShowHarmonyUserScope);
            Assert.True(processZero.ShowHarmonyUserScope);
            Assert.True(processOneHundred.ShowHarmonyUserScope);
            Assert.Equal("com.example.single", singleProfile.PickerTargetIdentifier);
            Assert.Equal("com.example.shared · 用户 0", profileZero.PickerTargetIdentifier);
        }

        [Fact]
        public void CleanupFailureStatusIncludesTheFirstFileAndReason()
        {
            MoTuPerf.Platform.RuntimeDataCleanupResult result = new MoTuPerf.Platform.RuntimeDataCleanupResult(
                1024,
                1,
                2,
                new[]
                {
                    new MoTuPerf.Platform.RuntimeDataCleanupFailure(@"D:\data\collector.log", "文件正在使用")
                });

            string status = DataCleanupWindow.BuildFailureStatus(result);

            Assert.Contains("2 个文件未能删除", status);
            Assert.Contains("collector.log", status);
            Assert.Contains("文件正在使用", status);
        }
    }
}
