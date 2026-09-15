using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--emit-large-output")
        {
            for (int index = 0; index < 5000; index++)
            {
                Console.WriteLine("process-row-" + index.ToString("00000") + "-abcdefghijklmnopqrstuvwxyz");
            }
            Console.WriteLine("process-output-complete");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--echo-args")
        {
            Console.WriteLine(args[1]);
            Console.WriteLine(args[2]);
            return 0;
        }
        ExactWindowsUseFrameCountOverObservationTime();
        MixedWindowsPreserveExactObservationWeights();
        PerfDogFpsStatisticsExposeMinimumAndMedianRange();
        PerfDogDropUsesStrictThresholdAndRealSourceContinuity();
        OrderedZeroTransitionCountsAsRealDrop();
        ExactTargetFpsDoesNotMixUnverifiedDisplayFallback();
        VerifiedGfxinfoOrderedSampleEntersExactStatistics();
        FrameSeriesIdentityRejectsSourceAndTargetSwitches();
        LegacyAndroidDisplayFallbackWithoutVerificationDoesNotPolluteExactTargetStats();
        ExactIosFpsDoesNotMixGraphicsFallback();
        FallbackFpsRemainsAvailableWhenNoExactSourceExists();
        MissingSamplesDoNotWeightALongDisconnectGap();
        JankRatesUseOnlyObservedCapabilityDuration();
        JankStatsExcludeUnorderedAndUnverifiedLegacyRows();
        MissingJankDurationDoesNotFabricateRates();
        MissingJankTimeDoesNotFabricateZeroStutter();
        ResumeGapSupersedesOverlappingIdleWindows();
        PrimaryMemoryMetricDoesNotMixWithRssFallback();
        RssRemainsAvailableWhenItIsTheOnlyMemoryMetric();
        NonSurfaceFrameVerificationSurvivesSessionNormalization();
        SurfaceFrameVerificationRequiresAnOwnerPid();
        CaptureMetricSourcesDefaultToEnabled();
        AppBrandRespawnRequiresTheCurrentForegroundProcess();
        AndroidProcessInstanceRequiresMatchingStartTime();
        BoundAndroidCaptureSelectionRequiresDeviceAndStartIdentity();
        AndroidProcessStartTimesParseFromProcStat();
        AndroidDefaultHomeProcessIsExcluded();
        AndroidSelectedAppDoesNotAdoptAnotherPackage();
        ProcessDeviceBindingRejectsAnotherDevice();
        IosWebProcessRequiresVerifiedOwnerBundle();
        IosDefaultPickerShowsOnlyActionableTargets();
        ProcessRunnerDrainsLargeOutputAfterExit();
        ProcessRunnerPreservesStructuredArguments();
        RuntimeToolsUsesOnlyExplicitTestOverrides();
        IosRuntimeDiagnosticsRemainActionable();
        EmptyDeviceDiscoveryPreservesPlatformDiagnostics();
        IosLookupParsesVerifiedAndUnverifiedWebOwnership();
        IosLookupPreservesForegroundApplicationState();
        ProcessMetricQueuePreservesEveryBurstSample();
        TemperatureEventSurvivesCollectorParsing();
        TemperatureEventWakesSampleLoopPromptly();
        TemperatureChartProjectionKeepsLatestValueLiveWithoutMutatingSamples();
        ThermalStateEventSurvivesCollectorParsing();
        ThermalStateChartUsesStepTransitionsWithoutMutatingSamples();
        FrameDiagnosticsSurviveCollectorParsing();
        ResumeGapMetadataSurvivesCollectorParsing();
        FrameSourceSequenceDiscontinuitySurvivesCollectorParsing();
        FrameTimeChartExcludesNonFrameStateSnapshots();
        CollectorTargetConfirmationEventIsGenerationBound();
        Console.WriteLine("C# metric statistics tests passed.");
        return 0;
    }

    private static void ExactWindowsUseFrameCountOverObservationTime()
    {
        FpsStatistics stats = MetricStatistics.ComputeFps(new List<PerfSample>
        {
            ExactSample(0.0, 60.0, 60, 1000.0),
            ExactSample(1.0, 30.0, 15, 500.0),
        });

        AssertTrue(stats.HasData, "exact stats availability");
        AssertNear(stats.Average, 50.0, "exact weighted average");
        AssertNear(stats.TotalDurationSec, 1.5, "exact observation duration");
        AssertTrue(!stats.HasEstimatedWeights, "exact stats must not be estimated");
    }

    private static void MixedWindowsPreserveExactObservationWeights()
    {
        FpsStatistics stats = MetricStatistics.ComputeFps(new List<PerfSample>
        {
            ExactSample(0.0, 60.0, 120, 2000.0),
            ExactSample(2.0, 30.0, 6, 200.0),
            FallbackSample(3.0, 10.0),
        });

        AssertNear(stats.Average, 42.5, "mixed weighted average");
        AssertNear(stats.TotalDurationSec, 3.2, "mixed duration");
        AssertNear(stats.Median, 60.0, "mixed weighted median");
        AssertNear(stats.FpsGe18Percent, 68.75, "mixed >=18 duration ratio");
        AssertNear(stats.FpsGe25Percent, 68.75, "mixed >=25 duration ratio");
        AssertTrue(stats.HasEstimatedWeights, "mixed stats identify estimated fallback duration");
    }

    private static void PerfDogFpsStatisticsExposeMinimumAndMedianRange()
    {
        FpsStatistics stats = MetricStatistics.ComputeFps(new[]
        {
            FallbackSample(0.0, 40.0),
            FallbackSample(1.0, 50.0),
            FallbackSample(2.0, 50.0),
            FallbackSample(3.0, 60.0),
            FallbackSample(4.0, 80.0),
        });

        AssertNear(stats.Minimum, 40.0, "PerfDog minimum FPS");
        AssertNear(stats.Median, 50.0, "PerfDog median FPS");
        AssertNear(stats.MedianRangePercent, 80.0, "PerfDog median range percent");
    }

    private static void PerfDogDropUsesStrictThresholdAndRealSourceContinuity()
    {
        PerfSample first = FallbackSample(0.0, 60.0);
        SetFrameContinuity(first, "ordered-a", 1);
        PerfSample exactEight = FallbackSample(1.0, 52.0);
        SetFrameContinuity(exactEight, "ordered-a", 2);
        PerfSample realDrop = FallbackSample(2.0, 43.0);
        SetFrameContinuity(realDrop, "ordered-a", 3);
        PerfSample sourceSwitch = FallbackSample(3.0, 20.0);
        SetFrameContinuity(sourceSwitch, "ordered-b", 1);
        PerfSample sequenceGap = FallbackSample(4.0, 0.0);
        SetFrameContinuity(sequenceGap, "ordered-b", 3);
        sequenceGap.FrameSourceSequenceDiscontinuity = true;
        sequenceGap.MissingFrameSourceWindows = 1;
        PerfSample longGap = FallbackSample(10.0, 0.0);
        SetFrameContinuity(longGap, "ordered-b", 4);

        FpsStatistics stats = MetricStatistics.ComputeFps(new[]
        {
            first,
            exactEight,
            realDrop,
            sourceSwitch,
            sequenceGap,
            longGap,
        });

        AssertNear(stats.TotalDurationSec, 6.0, "Drop FPS observed duration");
        AssertNear(stats.DropPerHour, 600.0, "only one continuous >8 FPS drop per hour");
    }

    private static void OrderedZeroTransitionCountsAsRealDrop()
    {
        PerfSample active = FallbackSample(0.0, 60.0);
        SetFrameContinuity(active, "ordered-layer", 1);
        PerfSample idle = FallbackSample(1.0, 0.0);
        SetFrameContinuity(idle, "ordered-layer", 2);

        FpsStatistics stats = MetricStatistics.ComputeFps(new[] { active, idle });

        AssertNear(stats.DropPerHour, 1800.0, "continuous ordered 60 to zero transition is one real drop");
    }

    private static void SetFrameContinuity(PerfSample sample, string source, long sequence)
    {
        sample.Source = source;
        sample.FpsScope = "screen";
        sample.FrameSource = source;
        sample.HasFrameSourceSequence = true;
        sample.FrameSourceSequence = sequence;
    }

    private static void ExactTargetFpsDoesNotMixUnverifiedDisplayFallback()
    {
        PerfSample exact = ExactSample(1.0, 30.0, 30, 1000.0);
        exact.OrderedFrames = true;
        exact.HasFrameTargetVerification = true;
        exact.FrameTargetVerified = true;
        PerfSample fallback = FallbackSample(2.0, 120.0);
        fallback.HasFrameTargetVerification = true;
        fallback.FrameTargetVerified = false;

        FpsStatistics stats = MetricStatistics.ComputeFps(new[] { exact, fallback });

        AssertNear(stats.Average, 30.0, "verified exact target FPS average");
        AssertNear(stats.TotalDurationSec, 1.0, "verified exact target FPS duration");
        AssertTrue(stats.SourceTier == "exact_ordered", "verified target exact source tier");
        AssertTrue(stats.SampleCount == 1 && stats.ExcludedSampleCount == 1, "verified target source cohort counts");
    }

    private static void ExactIosFpsDoesNotMixGraphicsFallback()
    {
        PerfSample exact = ExactSample(1.0, 60.0, 60, 1000.0);
        exact.OrderedFrames = true;
        PerfSample graphicsFallback = FallbackSample(2.0, 10.0);

        FpsStatistics stats = MetricStatistics.ComputeFps(new[] { exact, graphicsFallback });

        AssertNear(stats.Average, 60.0, "exact iOS ordered FPS average");
        AssertNear(stats.TotalDurationSec, 1.0, "exact iOS ordered FPS duration");
        AssertTrue(stats.SourceTier == "exact_ordered", "iOS exact source tier");
    }

    private static void VerifiedGfxinfoOrderedSampleEntersExactStatistics()
    {
        PerfSample sample = ExactSample(1.0, 60.0, 60, 1000.0);
        sample.OrderedFrames = true;
        sample.Source = "adb-gfxinfo-framestats";
        sample.FpsScope = "app";
        sample.FrameSource = "gfxinfo-display-present";
        sample.HasFrameTargetVerification = true;
        sample.FrameTargetVerified = true;
        sample.TargetPid = 42;

        FpsStatistics fps = MetricStatistics.ComputeFps(new[] { sample });
        JankStatistics jank = MetricStatistics.ComputeJank(new[] { JankFrom(sample) });

        AssertTrue(fps.SourceTier == "exact_ordered", "verified gfxinfo belongs to exact FPS tier");
        AssertTrue(jank.HasRateData, "verified gfxinfo ordered Jank remains statistically available");
    }

    private static void FrameSeriesIdentityRejectsSourceAndTargetSwitches()
    {
        PerfSample first = FallbackSample(1.0, 60.0);
        first.Source = "adb-gfxinfo-framestats";
        first.FpsScope = "app";
        first.FrameSource = "gfxinfo-display-present";
        first.TargetPid = 42;
        first.HasFrameTargetVerification = true;
        first.FrameTargetVerified = true;

        PerfSample same = FallbackSample(2.0, 59.0);
        same.Source = first.Source;
        same.FpsScope = first.FpsScope;
        same.FrameSource = first.FrameSource;
        same.TargetPid = first.TargetPid;
        same.HasFrameTargetVerification = true;
        same.FrameTargetVerified = true;

        AssertTrue(MetricStatistics.IsSameFrameSeries(first, same), "same frame source and target remain one series");
        same.Source = "adb-surfaceflinger-layer-latency";
        AssertTrue(!MetricStatistics.IsSameFrameSeries(first, same), "source switch must break the chart series");
        same.Source = first.Source;
        same.TargetPid = 99;
        AssertTrue(!MetricStatistics.IsSameFrameSeries(first, same), "pid switch must break the chart series");
    }

    private static void LegacyAndroidDisplayFallbackWithoutVerificationDoesNotPolluteExactTargetStats()
    {
        PerfSample exact = ExactSample(1.0, 60.0, 60, 1000.0);
        exact.OrderedFrames = true;
        exact.Source = "adb-surfaceflinger-layer-latency";
        exact.FpsScope = "surface";
        exact.HasFrameTargetVerification = true;
        exact.FrameTargetVerified = true;

        PerfSample legacyDisplayFallback = ExactSample(2.0, 120.0, 120, 1000.0);
        legacyDisplayFallback.OrderedFrames = true;
        legacyDisplayFallback.Source = "adb-surfaceflinger-display-latency";
        legacyDisplayFallback.FpsScope = "display";

        FpsStatistics stats = MetricStatistics.ComputeFps(new[] { exact, legacyDisplayFallback });

        AssertNear(stats.Average, 60.0, "legacy Android display fallback must not pollute exact target FPS");
        AssertTrue(stats.SourceTier == "exact_ordered", "verified Android target remains exact source tier");
        AssertTrue(stats.SampleCount == 1 && stats.ExcludedSampleCount == 1, "legacy Android display fallback cohort counts");
    }

    private static void FallbackFpsRemainsAvailableWhenNoExactSourceExists()
    {
        PerfSample first = FallbackSample(0.0, 20.0);
        first.HasFrameTargetVerification = true;
        first.FrameTargetVerified = false;
        PerfSample second = FallbackSample(1.0, 40.0);
        second.HasFrameTargetVerification = true;
        second.FrameTargetVerified = false;

        FpsStatistics stats = MetricStatistics.ComputeFps(new[] { first, second });

        AssertTrue(stats.HasData, "fallback-only FPS remains available");
        AssertNear(stats.Average, 30.0, "fallback-only FPS average");
        AssertTrue(stats.SourceTier == "fallback", "fallback-only source tier");
    }

    private static void MissingSamplesDoNotWeightALongDisconnectGap()
    {
        FpsStatistics stats = MetricStatistics.ComputeFps(new List<PerfSample>
        {
            ExactSample(0.0, 60.0, 120, 2000.0),
            FallbackSample(10.0, 10.0),
        });

        AssertNear(stats.TotalDurationSec, 3.0, "disconnect gap must not become ten seconds of fallback FPS");
        AssertNear(stats.Average, 130.0 / 3.0, "disconnect-safe mixed average");
    }

    private static void JankRatesUseOnlyObservedCapabilityDuration()
    {
        JankStatistics stats = MetricStatistics.ComputeJank(new List<PerfSample>
        {
            JankSample(0.0, 1.0, 0.0, 1000.0, 90.0),
            JankSample(1.0, 2.0, 1.0, 2000.0, 210.0),
        });

        AssertTrue(stats.HasRateData, "jank rate availability");
        AssertNear(stats.ObservationSec, 3.0, "jank observation duration");
        AssertNear(stats.JankPer10Min, 600.0, "jank per ten minutes");
        AssertNear(stats.BigJankPer10Min, 200.0, "big jank per ten minutes");
        AssertTrue(stats.HasStutterData, "stutter availability");
        AssertNear(stats.StutterPercent, 10.0, "stutter duration ratio");
    }

    private static void JankStatsExcludeUnorderedAndUnverifiedLegacyRows()
    {
        PerfSample verified = JankSample(0.0, 1.0, 0.0, 1000.0, 100.0);
        PerfSample unverifiedAndroid = JankSample(1.0, 100.0, 50.0, 1000.0, 900.0);
        unverifiedAndroid.Source = "adb-surfaceflinger-display-latency";
        unverifiedAndroid.FpsScope = "display";
        unverifiedAndroid.HasFrameTargetVerification = false;
        PerfSample unorderedLegacy = JankSample(2.0, 100.0, 50.0, 1000.0, 900.0);
        unorderedLegacy.Source = "legacy-sampled-fps";
        unorderedLegacy.OrderedFrames = false;

        JankStatistics stats = MetricStatistics.ComputeJank(new[]
        {
            verified,
            unverifiedAndroid,
            unorderedLegacy,
        });

        AssertTrue(stats.HasRateData, "verified ordered Jank remains available");
        AssertNear(stats.ObservationSec, 1.0, "only verified ordered Jank observation duration");
        AssertNear(stats.JankPer10Min, 600.0, "unverified and unordered Jank rows are excluded");
        AssertNear(stats.BigJankPer10Min, 0.0, "unverified BigJank rows are excluded");
        AssertNear(stats.StutterPercent, 10.0, "stutter uses only verified ordered duration");
        AssertTrue(stats.SampleCount == 1 && stats.ExcludedSampleCount == 2, "Jank provenance cohort counts");
    }

    private static void MissingJankDurationDoesNotFabricateRates()
    {
        JankStatistics stats = MetricStatistics.ComputeJank(new List<PerfSample>
        {
            new PerfSample { ElapsedSec = 0.0, HasJank = true, FpsUpdated = true, Jank = 1.0 },
            new PerfSample { ElapsedSec = 10.0, HasJank = true, FpsUpdated = true, Jank = 2.0 },
        });

        AssertTrue(!stats.HasRateData, "missing capability duration must keep jank rate unavailable");
        AssertTrue(!stats.HasStutterData, "missing capability duration must keep stutter unavailable");
    }

    private static void MissingJankTimeDoesNotFabricateZeroStutter()
    {
        PerfSample sample = JankSample(0.0, 1.0, 0.0, 1000.0, 0.0);
        sample.HasStutter = false;
        JankStatistics stats = MetricStatistics.ComputeJank(new[] { sample });

        AssertTrue(stats.HasRateData, "jank count with duration remains available");
        AssertTrue(!stats.HasStutterData, "missing jank time provenance must not become zero stutter");
    }

    private static void ResumeGapSupersedesOverlappingIdleWindows()
    {
        PerfSample active = VerifiedSurfaceSample(0.0, 60.0, 60, 1000.0);
        PerfSample idle1 = VerifiedSurfaceSample(1.0, 0.0, 0, 1000.0);
        idle1.NoPresentFrames = true;
        PerfSample idle2 = VerifiedSurfaceSample(2.0, 0.0, 0, 1000.0);
        idle2.NoPresentFrames = true;
        PerfSample resumed = VerifiedSurfaceSample(3.0, 20.0, 60, 3000.0);
        resumed.ResumeGapMs = 2000.0;
        resumed.Jank = 1.0;
        resumed.BigJank = 1.0;
        resumed.JankTimeMs = 2000.0;
        resumed.StutterPercent = 2000.0 * 100.0 / 3000.0;

        FpsStatistics fps = MetricStatistics.ComputeFps(new[] { active, idle1, idle2, resumed });
        JankStatistics jank = MetricStatistics.ComputeJank(new[] { active, idle1, idle2, resumed });

        AssertNear(fps.TotalDurationSec, 4.0, "resume gap must cover idle duration once in FPS stats");
        AssertNear(fps.Average, 30.0, "resume gap weighted FPS must exclude superseded zero windows");
        AssertTrue(fps.SampleCount == 2 && fps.ExcludedSampleCount == 2, "resume gap FPS cohort counts");
        AssertNear(jank.ObservationSec, 4.0, "resume gap must cover idle duration once in Jank stats");
        AssertNear(jank.JankPer10Min, 150.0, "resume gap Jank rate");
        AssertNear(jank.BigJankPer10Min, 150.0, "resume gap BigJank rate");
        AssertNear(jank.StutterPercent, 50.0, "resume gap Stutter duration ratio");
        AssertTrue(jank.SampleCount == 2 && jank.ExcludedSampleCount == 2, "resume gap Jank cohort counts");
    }

    private static void PrimaryMemoryMetricDoesNotMixWithRssFallback()
    {
        MemoryStatistics stats = MetricStatistics.ComputeMemory(new List<PerfSample>
        {
            MemorySample(100.0, "pss"),
            MemorySample(500.0, "rss"),
            MemorySample(110.0, "pss"),
        });

        AssertTrue(stats.HasData, "primary memory stats availability");
        AssertTrue(stats.Metric == "pss", "PSS must remain the selected Android memory metric");
        AssertNear(stats.AverageMb, 105.0, "PSS average must not include RSS");
        AssertNear(stats.PeakMb, 110.0, "PSS peak must not include RSS");
        AssertTrue(stats.SampleCount == 2, "only primary PSS samples should be summarized");
    }

    private static void RssRemainsAvailableWhenItIsTheOnlyMemoryMetric()
    {
        MemoryStatistics stats = MetricStatistics.ComputeMemory(new List<PerfSample>
        {
            MemorySample(200.0, "rss"),
            MemorySample(220.0, "rss"),
        });

        AssertTrue(stats.HasData, "RSS-only stats availability");
        AssertTrue(stats.Metric == "rss", "RSS should remain an explicit fallback metric");
        AssertNear(stats.AverageMb, 210.0, "RSS-only average");
        AssertNear(stats.PeakMb, 220.0, "RSS-only peak");
    }

    private static void NonSurfaceFrameVerificationSurvivesSessionNormalization()
    {
        PerfSample sample = new PerfSample
        {
            Source = "adb-gfxinfo-framestats",
            FrameSource = "display-present-time",
            HasFrameTargetVerification = true,
            FrameTargetVerified = true,
        };

        MetricStatistics.NormalizeFrameTargetVerification(sample);

        AssertTrue(sample.HasFrameTargetVerification, "gfxinfo verification availability");
        AssertTrue(sample.FrameTargetVerified, "verified gfxinfo must survive session normalization");
    }

    private static void SurfaceFrameVerificationRequiresAnOwnerPid()
    {
        PerfSample sample = new PerfSample
        {
            Source = "adb-surfaceflinger-layer-latency",
            FrameSource = "ordered-layer-present",
            HasFrameTargetVerification = true,
            FrameTargetVerified = true,
        };

        MetricStatistics.NormalizeFrameTargetVerification(sample);

        AssertTrue(sample.HasFrameTargetVerification, "surface verification availability");
        AssertTrue(!sample.FrameTargetVerified, "surface verification without an owner pid is invalid");
    }

    private static void CaptureMetricSourcesDefaultToEnabled()
    {
        CaptureConfig config = new CaptureConfig();

        AssertTrue(config.CollectFps, "FPS collection should default to enabled");
        AssertTrue(config.CollectMemory, "memory collection should default to enabled");
        AssertTrue(config.CollectCpu, "CPU collection should default to enabled");
    }

    private static void AppBrandRespawnRequiresTheCurrentForegroundProcess()
    {
        ProcessInfo selected = new ProcessInfo
        {
            Pid = 1690,
            Name = "com.tencent.mm:appbrand1",
            BundleId = "com.tencent.mm",
        };
        ProcessInfo sameNameBackground = new ProcessInfo
        {
            Pid = 2001,
            Name = "com.tencent.mm:appbrand1",
            BundleId = "com.tencent.mm",
            Reason = "微信小游戏进程",
        };
        ProcessInfo currentForeground = new ProcessInfo
        {
            Pid = 2002,
            Name = "com.tencent.mm:appbrand2",
            BundleId = "com.tencent.mm",
            Reason = "当前前台微信小游戏进程",
        };

        AssertTrue(
            !ProcessTargetMatcher.SameAndroidTarget(sameNameBackground, selected, "com.tencent.mm"),
            "background AppBrand must not replace the selected pid");
        AssertTrue(
            ProcessTargetMatcher.SameAndroidTarget(currentForeground, selected, "com.tencent.mm"),
            "current foreground AppBrand should replace a stale selected pid");
    }

    private static void ProcessDeviceBindingRejectsAnotherDevice()
    {
        ProcessInfo selected = new ProcessInfo
        {
            Pid = 597,
            Name = "ldt_global",
            BundleId = "com.ldt1.app",
            DeviceUdid = "ipad-mini-6",
        };

        AssertTrue(
            ProcessTargetMatcher.BelongsToDevice(selected, "ipad-mini-6"),
            "selected process should retain its source device");
        AssertTrue(
            !ProcessTargetMatcher.BelongsToDevice(selected, "another-device"),
            "a process snapshot must not cross devices");
    }

    private static void AndroidProcessInstanceRequiresMatchingStartTime()
    {
        ProcessInfo selected = new ProcessInfo
        {
            Pid = 8850,
            Name = "com.example.game",
            BundleId = "com.example.game",
            AndroidStartTimeTicks = 100,
        };
        ProcessInfo sameInstance = new ProcessInfo
        {
            Pid = 8850,
            Name = "com.example.game",
            BundleId = "com.example.game",
            AndroidStartTimeTicks = 100,
        };
        ProcessInfo reusedPid = new ProcessInfo
        {
            Pid = 8850,
            Name = "com.example.game",
            BundleId = "com.example.game",
            AndroidStartTimeTicks = 200,
        };
        ProcessInfo missingCurrentIdentity = new ProcessInfo
        {
            Pid = 8850,
            Name = "com.example.game",
            BundleId = "com.example.game",
            AndroidStartTimeTicks = 0,
        };

        AssertTrue(
            ProcessTargetMatcher.SameAndroidProcessInstance(sameInstance, selected),
            "matching Android starttime should retain the selected process instance");
        AssertTrue(
            !ProcessTargetMatcher.SameAndroidProcessInstance(reusedPid, selected),
            "same-name PID reuse must not retain the old selected process instance");
        AssertTrue(
            !ProcessTargetMatcher.SameAndroidProcessInstance(missingCurrentIdentity, selected),
            "a known selected starttime must not be downgraded by an unverified refresh");
    }

    private static void BoundAndroidCaptureSelectionRequiresDeviceAndStartIdentity()
    {
        ProcessInfo bound = new ProcessInfo
        {
            Pid = 8850,
            Name = "com.example.game",
            DeviceUdid = "android-device",
            AndroidStartTimeTicks = 100,
        };
        ProcessInfo missingIdentity = new ProcessInfo
        {
            Pid = 8850,
            Name = "com.example.game",
            DeviceUdid = "android-device",
            AndroidStartTimeTicks = 0,
        };

        AssertTrue(
            ProcessTargetMatcher.CanReuseAndroidSelectionForCapture(bound, "android-device"),
            "same-device Android selection with starttime may enter the real runner directly");
        AssertTrue(
            !ProcessTargetMatcher.CanReuseAndroidSelectionForCapture(bound, "another-device"),
            "Android selection must not cross devices");
        AssertTrue(
            !ProcessTargetMatcher.CanReuseAndroidSelectionForCapture(missingIdentity, "android-device"),
            "Android selection without starttime must retain the full preflight");
    }

    private static void AndroidProcessStartTimesParseFromProcStat()
    {
        string output =
            "8850 (more2.scientist) S 1 1 0 0 -1 0 0 0 0 0 10 20 0 0 20 0 5 0 5730017 0\n" +
            "10180 (WeChat render (gpu)) S 1 1 0 0 -1 0 0 0 0 0 10 20 0 0 20 0 5 0 7773 0\n";

        Dictionary<int, long> parsed = AndroidLookupService.ParseProcessStartTimeTicks(output);

        AssertTrue(parsed[8850] == 5730017, "native Android process starttime parse");
        AssertTrue(parsed[10180] == 7773, "nested Android process name starttime parse");
    }

    private static void AndroidDefaultHomeProcessIsExcluded()
    {
        string vivoHomeOutput =
            "priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n" +
            "com.bbk.launcher2/.Launcher\n";
        string homePackage = AndroidLookupService.ParseDefaultHomePackage(vivoHomeOutput);
        ProcessInfo launcher = new ProcessInfo
        {
            Pid = 5324,
            Name = "com.bbk.launcher2",
            BundleId = "com.bbk.launcher2",
        };
        ProcessInfo game = new ProcessInfo
        {
            Pid = 22655,
            Name = "com.motu.ldt3.gfmotu",
            BundleId = "com.motu.ldt3.gfmotu",
        };

        AssertTrue(homePackage == "com.bbk.launcher2", "default HOME package parse");
        AssertTrue(AndroidLookupService.IsHomeProcess(launcher, homePackage), "default HOME process must be excluded");
        AssertTrue(!AndroidLookupService.IsHomeProcess(game, homePackage), "game process must remain selectable");
        AssertTrue(AndroidLookupService.IsKnownHomePackage("com.bbk.launcher2"), "known vivo HOME fallback");
    }

    private static void AndroidSelectedAppDoesNotAdoptAnotherPackage()
    {
        ProcessInfo game = new ProcessInfo
        {
            Pid = 22655,
            Name = "com.motu.ldt3.gfmotu",
            BundleId = "com.motu.ldt3.gfmotu",
        };
        ProcessInfo launcher = new ProcessInfo
        {
            Pid = 5324,
            Name = "com.bbk.launcher2",
            BundleId = "com.bbk.launcher2",
        };

        AssertTrue(
            ProcessTargetMatcher.IsAndroidProcessCompatibleWithBundle(game, "com.motu.ldt3.gfmotu"),
            "selected Android game package accepts its own process");
        AssertTrue(
            !ProcessTargetMatcher.IsAndroidProcessCompatibleWithBundle(launcher, "com.motu.ldt3.gfmotu"),
            "selected Android game package must reject the launcher process");
    }

    private static void IosWebProcessRequiresVerifiedOwnerBundle()
    {
        ProcessInfo verified = new ProcessInfo
        {
            Pid = 700,
            Name = "com.apple.WebKit.WebContent",
            OwnerPid = 600,
            OwnerBundleId = "com.tencent.xin",
            OwnershipVerified = true,
        };
        ProcessInfo unverified = new ProcessInfo
        {
            Pid = 701,
            Name = "com.apple.WebKit.WebContent",
            OwnerPid = 600,
            OwnerBundleId = "com.tencent.xin",
            OwnershipVerified = false,
        };

        AssertTrue(
            ProcessTargetMatcher.IsIosOwnedWebProcess(verified, "com.tencent.xin"),
            "verified WeChat WebContent should match its owner bundle");
        AssertTrue(
            !ProcessTargetMatcher.IsIosOwnedWebProcess(unverified, "com.tencent.xin"),
            "unverified WebContent must not be classified as WeChat");
        AssertTrue(
            !ProcessTargetMatcher.IsIosOwnedWebProcess(verified, "com.apple.mobilesafari"),
            "owned WebContent must not match another bundle");
    }

    private static void IosDefaultPickerShowsOnlyActionableTargets()
    {
        ProcessInfo nativeMain = new ProcessInfo
        {
            Pid = 2508,
            Name = "PixelHeroes",
            BundleId = "com.more2.pixelheroes.cn",
        };
        ProcessInfo anotherNativeMain = new ProcessInfo
        {
            Pid = 2608,
            Name = "ChaosHeroes",
            BundleId = "com.ldt1.app",
        };
        ProcessInfo systemMain = new ProcessInfo
        {
            Pid = 2708,
            Name = "GameOverlayUI",
            BundleId = "com.apple.GameOverlayUI",
        };
        ProcessInfo systemDaemon = new ProcessInfo
        {
            Pid = 2709,
            Name = "runningboardd",
        };
        ProcessInfo nativeWebContent = new ProcessInfo
        {
            Pid = 2510,
            Name = "com.apple.WebKit.WebContent",
            OwnerPid = 2508,
            OwnerBundleId = "com.more2.pixelheroes.cn",
            OwnershipVerified = true,
        };
        ProcessInfo nativeNetworking = new ProcessInfo
        {
            Pid = 2514,
            Name = "com.apple.WebKit.Networking",
            OwnerPid = 2508,
            OwnerBundleId = "com.more2.pixelheroes.cn",
            OwnershipVerified = true,
        };
        ProcessInfo wechatWebContent = new ProcessInfo
        {
            Pid = 2601,
            Name = "com.apple.WebKit.WebContent",
            OwnerPid = 2600,
            OwnerBundleId = "com.tencent.xin",
            OwnershipVerified = true,
        };
        ProcessInfo wechatGpu = new ProcessInfo
        {
            Pid = 2602,
            Name = "com.apple.WebKit.GPU",
            OwnerPid = 2600,
            OwnerBundleId = "com.tencent.xin",
            OwnershipVerified = true,
        };
        ProcessInfo wechatNetworking = new ProcessInfo
        {
            Pid = 2603,
            Name = "com.apple.WebKit.Networking",
            OwnerPid = 2600,
            OwnerBundleId = "com.tencent.xin",
            OwnershipVerified = true,
        };

        AssertTrue(
            ProcessTargetMatcher.IsIosDefaultPickerProcess(nativeMain, "com.more2.pixelheroes.cn"),
            "native app main process should be visible by default");
        AssertTrue(
            ProcessTargetMatcher.IsIosDefaultPickerProcess(anotherNativeMain, "com.ownbook.notes"),
            "other running third-party app processes should remain visible when the selected app is not running");
        AssertTrue(
            ProcessTargetMatcher.IsIosDefaultPickerProcess(systemMain, "com.ownbook.notes"),
            "running iOS application processes should remain visible like Android package processes");
        AssertTrue(
            !ProcessTargetMatcher.IsIosDefaultPickerProcess(systemDaemon, "com.ownbook.notes"),
            "system daemons without an application bundle should stay out of the default picker");
        AssertTrue(
            !ProcessTargetMatcher.IsIosDefaultPickerProcess(nativeWebContent, "com.more2.pixelheroes.cn"),
            "native app WebContent should stay out of the default picker");
        AssertTrue(
            !ProcessTargetMatcher.IsIosDefaultPickerProcess(nativeNetworking, "com.more2.pixelheroes.cn"),
            "native app WebKit networking helper should stay out of the default picker");
        AssertTrue(
            ProcessTargetMatcher.IsIosDefaultPickerProcess(wechatWebContent, "com.tencent.xin"),
            "verified WeChat WebContent should remain selectable");
        AssertTrue(
            ProcessTargetMatcher.IsIosDefaultPickerProcess(wechatGpu, "com.tencent.xin"),
            "verified WeChat GPU process should remain selectable");
        AssertTrue(
            !ProcessTargetMatcher.IsIosDefaultPickerProcess(wechatNetworking, "com.tencent.xin"),
            "WebKit networking helper should not be a default performance target");
    }

    private static void ProcessRunnerDrainsLargeOutputAfterExit()
    {
        ProcessResult result = ProcessRunner.RunAsync(
            Environment.ProcessPath,
            "--emit-large-output",
            15000,
            CancellationToken.None).GetAwaiter().GetResult();

        AssertTrue(result.ExitCode == 0, "large-output child exit code");
        AssertTrue(result.Stdout.Contains("process-row-04999"), "large-output final row must be drained");
        AssertTrue(result.Stdout.Contains("process-output-complete"), "large-output EOF marker must be drained");
    }

    private static void ProcessRunnerPreservesStructuredArguments()
    {
        ProcessResult result = ProcessRunner.RunAsync(
            Environment.ProcessPath,
            new[] { "--echo-args", "value with spaces", "cat /proc/$p/stat" },
            15000,
            CancellationToken.None).GetAwaiter().GetResult();

        AssertTrue(result.ExitCode == 0, "structured-argument child exit code");
        AssertTrue(result.Stdout.Contains("value with spaces"), "structured argument with spaces");
        AssertTrue(result.Stdout.Contains("cat /proc/$p/stat"), "structured argument must preserve device shell variable");
    }

    private static void RuntimeToolsUsesOnlyExplicitTestOverrides()
    {
        string previousGate = Environment.GetEnvironmentVariable("MOTUPERF_TEST_TOOL_OVERRIDE");
        string previousPython = Environment.GetEnvironmentVariable("MOTUPERF_PYTHON");
        try
        {
            Environment.SetEnvironmentVariable("MOTUPERF_PYTHON", @"C:\test runtime\python.exe");
            Environment.SetEnvironmentVariable("MOTUPERF_TEST_TOOL_OVERRIDE", "1");
            AssertTrue(RuntimeTools.PythonExecutable == @"C:\test runtime\python.exe", "explicit test Python override");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOTUPERF_TEST_TOOL_OVERRIDE", previousGate);
            Environment.SetEnvironmentVariable("MOTUPERF_PYTHON", previousPython);
        }
    }

    private static void IosRuntimeDiagnosticsRemainActionable()
    {
        ProcessResult missingModule = new ProcessResult(1, "", "ModuleNotFoundError: No module named 'tidevice'");
        AssertTrue(RuntimeTools.DescribeIosFailure(missingModule) == "iOS 运行组件不完整，请重新安装 MoTuPerf。", "missing iOS module diagnostic");

        ProcessResult untrusted = new ProcessResult(1, "", "InvalidHostID: device is not paired or trusted");
        AssertTrue(RuntimeTools.DescribeIosFailure(untrusted) == "iOS 设备尚未信任或仍处于锁定状态，请解锁设备并在手机上选择“信任此电脑”。", "untrusted iOS diagnostic");
    }

    private static void EmptyDeviceDiscoveryPreservesPlatformDiagnostics()
    {
        DeviceDiscoveryReport report = new DeviceDiscoveryReport
        {
            IosDiagnostic = "缺少 Apple 驱动",
            AndroidDiagnostic = "未授权 USB 调试"
        };
        AssertTrue(report.StatusMessage == "未检测到设备，请连接设备、检查授权后刷新。", "platform-neutral device discovery status");
    }

    private static void IosLookupParsesVerifiedAndUnverifiedWebOwnership()
    {
        string json = "[" +
            "{\"pid\":700,\"name\":\"com.apple.WebKit.WebContent\",\"isApplication\":false," +
            "\"ownerPID\":600,\"ownerName\":\"WeChat\",\"ownerBundleIdentifier\":\"com.tencent.xin\"," +
            "\"ownerDisplayLocalizedAppName\":\"微信\",\"ownershipSource\":\"coalition\"," +
            "\"ownershipVerified\":true,\"coalitionID\":80,\"startAbsTime\":100}," +
            "{\"pid\":701,\"name\":\"com.apple.WebKit.WebContent\",\"isApplication\":false," +
            "\"ownershipSource\":\"unavailable\",\"ownershipVerified\":false}" +
            "]";
        MethodInfo parser = typeof(IosLookupService).GetMethod(
            "ParsePymobiledevice3Processes",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(parser != null, "modern iOS process parser should exist");
        List<ProcessInfo> processes = (List<ProcessInfo>)parser.Invoke(null, new object[] { json });

        AssertTrue(processes.Count == 2, "modern iOS process parser row count");
        AssertTrue(processes[0].Recommended, "verified WeChat WebContent should be recommended");
        AssertTrue(processes[0].OwnerBundleId == "com.tencent.xin", "verified owner bundle should survive parsing");
        AssertTrue(processes[0].StartAbsTime == 100, "process start identity should survive parsing");
        AssertTrue(!processes[1].Recommended, "unowned WebContent must not be recommended");
        AssertTrue(processes[1].Reason.Contains("宿主未确认"), "unowned WebContent should explain unavailable ownership");
    }

    private static void IosLookupPreservesForegroundApplicationState()
    {
        string json = "[{\"pid\":1594,\"name\":\"NativeGame\",\"bundleIdentifier\":\"com.example.game\"," +
            "\"displayLocalizedAppName\":\"Native Game\",\"isApplication\":true," +
            "\"applicationState\":\"Running\",\"applicationExecutablePath\":\"/private/var/containers/Bundle/Application/UUID/NativeGame.app\"," +
            "\"foregroundApplication\":true}]";
        MethodInfo parser = typeof(IosLookupService).GetMethod(
            "ParsePymobiledevice3Processes",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(parser != null, "modern iOS process parser should exist");

        List<ProcessInfo> processes = (List<ProcessInfo>)parser.Invoke(null, new object[] { json });

        AssertTrue(processes.Count == 1, "foreground iOS parser row count");
        AssertTrue(processes[0].ForegroundApplication, "foreground iOS flag should survive parsing");
        AssertTrue(processes[0].ApplicationState == "Running", "iOS application state should survive parsing");
        AssertTrue(processes[0].Recommended, "foreground iOS application should be recommended");
        AssertTrue(processes[0].Reason == "当前前台 iOS APP 主进程", "foreground iOS recommendation reason");
    }

    private static void ProcessMetricQueuePreservesEveryBurstSample()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo startedAt = type.GetField("_startedAt", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo pending = type.GetField("_pendingProcessSamples", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo sampleLoop = type.GetMethod("SampleLoop", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field");
        AssertTrue(startedAt != null, "collector start time field");
        AssertTrue(pending != null, "process metric queue field");
        AssertTrue(parse != null, "collector parser method");
        AssertTrue(sampleLoop != null, "collector sample loop method");

        generation.SetValue(collector, 7);
        startedAt.SetValue(collector, DateTime.Now);
        parse.Invoke(collector, new object[] { "memory {\"value\":100,\"unit\":\"MB\",\"metric\":\"physical_footprint\",\"source\":\"test-memory\"}", 7 });
        parse.Invoke(collector, new object[] { "memory {\"value\":120,\"unit\":\"MB\",\"metric\":\"physical_footprint\",\"source\":\"test-memory\"}", 7 });
        parse.Invoke(collector, new object[] { "cpu {\"value\":10,\"unit\":\"%\",\"source\":\"test-cpu\"}", 7 });
        parse.Invoke(collector, new object[] { "cpu {\"value\":80,\"unit\":\"%\",\"source\":\"test-cpu\"}", 7 });
        parse.Invoke(collector, new object[] { "cpu {\"value\":999,\"unit\":\"%\",\"source\":\"stale-generation\"}", 6 });

        Queue<PerfSample> queue = (Queue<PerfSample>)pending.GetValue(collector);
        PerfSample[] samples = queue.ToArray();
        AssertTrue(samples.Length == 4, "every current-generation process sample must be queued");
        AssertTrue(samples[0].MemoryUpdated && samples[0].MemoryMb == 100.0, "first memory burst sample");
        AssertTrue(samples[1].MemoryUpdated && samples[1].MemoryMb == 120.0, "second memory burst sample");
        AssertTrue(samples[2].CpuUpdated && samples[2].CpuPercent == 10.0, "first CPU burst sample");
        AssertTrue(samples[3].CpuUpdated && samples[3].CpuPercent == 80.0, "second CPU burst sample");

        List<PerfSample> emitted = new List<PerfSample>();
        collector.SampleReady += emitted.Add;
        using (CancellationTokenSource cancellation = new CancellationTokenSource(100))
        {
            CaptureConfig config = new CaptureConfig { TargetPid = 42 };
            Task task = (Task)sampleLoop.Invoke(collector, new object[] { config, 7, cancellation.Token });
            task.GetAwaiter().GetResult();
        }
        List<PerfSample> emittedMemory = emitted.FindAll(sample => sample.MemoryUpdated);
        List<PerfSample> emittedCpu = emitted.FindAll(sample => sample.CpuUpdated);
        AssertTrue(emittedMemory.Count == 2, "sample loop must emit every memory burst sample");
        AssertTrue(emittedMemory[0].MemoryMb == 100.0 && emittedMemory[1].MemoryMb == 120.0, "memory burst order and values");
        AssertTrue(emittedCpu.Count == 2, "sample loop must emit every CPU burst sample");
        AssertTrue(emittedCpu[0].CpuPercent == 10.0 && emittedCpu[1].CpuPercent == 80.0, "CPU burst order and values");
    }

    private static void TemperatureEventSurvivesCollectorParsing()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo pending = type.GetField("_pendingProcessSamples", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field for temperature");
        AssertTrue(pending != null, "temperature queue field");
        AssertTrue(parse != null, "collector parser for temperature");

        generation.SetValue(collector, 15);
        parse.Invoke(collector, new object[]
        {
            "temperature {\"values\":{\"CPU\":46.25,\"Battery\":32.89,\"invalid\":999}," +
            "\"source\":\"test-temperature\",\"scope\":\"device\"}",
            15
        });

        Queue<PerfSample> queue = (Queue<PerfSample>)pending.GetValue(collector);
        AssertTrue(queue.Count == 1, "one valid temperature event should be queued");
        PerfSample sample = queue.Dequeue();
        AssertTrue(sample.HasTemperature && sample.TemperatureUpdated, "temperature event availability and freshness");
        AssertTrue(sample.TemperatureCelsius.Count == 2, "invalid temperature value must be rejected");
        AssertNear(sample.TemperatureCelsius["CPU"], 46.25, "CPU temperature value");
        AssertNear(sample.TemperatureCelsius["Battery"], 32.89, "battery temperature value");
        AssertTrue(sample.TemperatureSource == "test-temperature" && sample.TemperatureScope == "device", "temperature provenance");

        string serialized = JsonSerializer.Serialize(sample);
        PerfSample roundTrip = JsonSerializer.Deserialize<PerfSample>(serialized);
        AssertTrue(roundTrip != null && roundTrip.HasTemperature && roundTrip.TemperatureCelsius.Count == 2, "temperature session round trip");
    }

    private static void TemperatureEventWakesSampleLoopPromptly()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo startedAt = type.GetField("_startedAt", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo sampleLoop = type.GetMethod("SampleLoop", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field for temperature wake");
        AssertTrue(startedAt != null, "collector start time field for temperature wake");
        AssertTrue(parse != null, "collector parser for temperature wake");
        AssertTrue(sampleLoop != null, "collector sample loop for temperature wake");

        generation.SetValue(collector, 16);
        startedAt.SetValue(collector, DateTime.Now);

        DateTime parsedAt = DateTime.MinValue;
        TaskCompletionSource<DateTime> seen = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleReady += delegate(PerfSample sample)
        {
            if (sample.TemperatureUpdated) seen.TrySetResult(DateTime.UtcNow);
        };

        using (CancellationTokenSource cancellation = new CancellationTokenSource(1500))
        {
            CaptureConfig config = new CaptureConfig { TargetPid = 42 };
            Task loopTask = (Task)sampleLoop.Invoke(collector, new object[] { config, 16, cancellation.Token });
            Task parseTask = Task.Run(async delegate
            {
                await Task.Delay(120);
                parsedAt = DateTime.UtcNow;
                parse.Invoke(collector, new object[]
                {
                    "temperature {\"values\":{\"Battery\":32.89},\"source\":\"test-temperature\",\"scope\":\"device\"}",
                    16
                });
            });

            DateTime seenAt = seen.Task.GetAwaiter().GetResult();
            parseTask.GetAwaiter().GetResult();
            cancellation.Cancel();
            loopTask.GetAwaiter().GetResult();

            AssertTrue(parsedAt != DateTime.MinValue, "temperature event parse time should be recorded");
            AssertTrue((seenAt - parsedAt).TotalMilliseconds < 750, "temperature sample should wake the sample loop promptly");
        }
    }

    private static void TemperatureChartProjectionKeepsLatestValueLiveWithoutMutatingSamples()
    {
        PerfSample first = new PerfSample
        {
            ElapsedSec = 1.0,
            HasTemperature = true,
            TemperatureUpdated = true,
            TemperatureCelsius = new Dictionary<string, double> { { "Battery", 32.5 } }
        };
        PerfSample second = new PerfSample
        {
            ElapsedSec = 6.0,
            HasTemperature = true,
            TemperatureUpdated = true,
            TemperatureCelsius = new Dictionary<string, double> { { "Battery", 33.0 } }
        };
        List<PerfSample> source = new List<PerfSample> { first, second };

        List<PerfSample> live = TemperatureChartProjection.ForSensor(source, "Battery", 9.0);
        AssertTrue(live.Count == 3, "temperature chart should append one presentation point");
        AssertNear(live[2].ElapsedSec, 9.0, "temperature chart should reach current timeline");
        AssertNear(live[2].TemperatureCelsius["Battery"], 33.0, "temperature chart should hold latest real value");
        AssertTrue(!live[2].TemperatureUpdated, "presentation point must not be a fresh temperature event");
        AssertTrue(source.Count == 2, "temperature chart projection must not mutate raw samples");

        List<PerfSample> stale = TemperatureChartProjection.ForSensor(source, "Battery", 14.0);
        AssertTrue(stale.Count == 3, "stale projection should stop at its freshness boundary");
        AssertNear(stale[2].ElapsedSec, 13.5, "stale projection boundary");

        List<PerfSample> firstOnly = TemperatureChartProjection.ForSensor(
            new List<PerfSample> { first },
            "Battery",
            2.0);
        AssertTrue(firstOnly.Count == 2, "one temperature event should be visible before the next poll");
        AssertNear(firstOnly[1].ElapsedSec, 2.0, "first temperature event should project to current time");
    }

    private static void ThermalStateEventSurvivesCollectorParsing()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo pending = type.GetField("_pendingProcessSamples", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null && pending != null && parse != null, "thermal state collector reflection contract");

        generation.SetValue(collector, 17);
        parse.Invoke(collector, new object[]
        {
            "thermal_state {\"platform\":\"ios\",\"value\":2,\"state\":\"serious\",\"source\":\"xcode-energy\",\"scope\":\"device\"}",
            17
        });
        parse.Invoke(collector, new object[]
        {
            "thermal_state {\"platform\":\"ios\",\"value\":4,\"state\":\"critical\",\"source\":\"xcode-energy\",\"scope\":\"device\"}",
            17
        });
        parse.Invoke(collector, new object[]
        {
            "thermal_state {\"platform\":\"android\",\"value\":4,\"state\":\"critical\",\"source\":\"adb-dumpsys-thermalservice\",\"scope\":\"device\"}",
            17
        });
        parse.Invoke(collector, new object[]
        {
            "thermal_state {\"platform\":\"android\",\"value\":6,\"state\":\"shutdown\",\"source\":\"adb-dumpsys-thermalservice\",\"scope\":\"device\"}",
            17
        });
        parse.Invoke(collector, new object[]
        {
            "thermal_state {\"platform\":\"android\",\"value\":7,\"state\":\"shutdown\",\"source\":\"adb-dumpsys-thermalservice\",\"scope\":\"device\"}",
            17
        });
        parse.Invoke(collector, new object[]
        {
            "thermal_state {\"platform\":\"android\",\"value\":2,\"state\":\"serious\",\"source\":\"adb-dumpsys-thermalservice\",\"scope\":\"device\"}",
            17
        });

        Queue<PerfSample> queue = (Queue<PerfSample>)pending.GetValue(collector);
        AssertTrue(queue.Count == 3, "only platform-valid thermal states should be queued");
        PerfSample sample = queue.Dequeue();
        AssertTrue(sample.HasThermalState && sample.ThermalStateUpdated, "thermal state availability and freshness");
        AssertTrue(sample.ThermalStateLevel == 2 && sample.ThermalStateName == "serious", "thermal state value and enum");
        AssertTrue(sample.ThermalStateSource == "xcode-energy" && sample.ThermalStateScope == "device", "thermal state provenance");

        string serialized = JsonSerializer.Serialize(sample);
        PerfSample roundTrip = JsonSerializer.Deserialize<PerfSample>(serialized);
        AssertTrue(roundTrip != null && roundTrip.HasThermalState && roundTrip.ThermalStateLevel == 2, "thermal state session round trip");

        PerfSample androidCritical = queue.Dequeue();
        AssertTrue(androidCritical.ThermalStateLevel == 4 && androidCritical.ThermalStateName == "critical", "Android critical thermal status");
        AssertTrue(androidCritical.ThermalStateSource == "adb-dumpsys-thermalservice", "Android thermal status provenance");
        PerfSample androidShutdown = queue.Dequeue();
        AssertTrue(androidShutdown.ThermalStateLevel == 6 && androidShutdown.ThermalStateName == "shutdown", "Android shutdown thermal status");
    }

    private static void ThermalStateChartUsesStepTransitionsWithoutMutatingSamples()
    {
        PerfSample nominal = new PerfSample
        {
            ElapsedSec = 1.0,
            HasThermalState = true,
            ThermalStateLevel = 0,
            ThermalStateName = "nominal",
            ThermalStateUpdated = true
        };
        PerfSample serious = new PerfSample
        {
            ElapsedSec = 3.0,
            HasThermalState = true,
            ThermalStateLevel = 2,
            ThermalStateName = "serious",
            ThermalStateUpdated = true
        };
        List<PerfSample> source = new List<PerfSample> { nominal, serious };

        List<PerfSample> projected = ThermalStateChartProjection.ForChart(source, 4.0);
        AssertTrue(projected.Count == 4, "thermal state step projection point count");
        AssertNear(projected[1].ElapsedSec, 3.0, "thermal state vertical transition time");
        AssertTrue(projected[1].ThermalStateLevel == 0 && projected[2].ThermalStateLevel == 2, "thermal state step transition levels");
        AssertNear(projected[3].ElapsedSec, 4.0, "thermal state projection reaches current timeline");
        AssertTrue(!projected[1].ThermalStateUpdated && !projected[3].ThermalStateUpdated, "thermal state projection points are presentation-only");
        AssertTrue(source.Count == 2, "thermal state projection must not mutate raw samples");

        PerfSample shutdown = new PerfSample
        {
            ElapsedSec = 5.0,
            HasThermalState = true,
            ThermalStateLevel = 6,
            ThermalStateName = "shutdown",
            ThermalStateUpdated = true
        };
        List<PerfSample> androidProjected = ThermalStateChartProjection.ForChart(
            new List<PerfSample> { serious, shutdown },
            11.0);
        AssertTrue(androidProjected.Find(delegate(PerfSample sample) { return sample.ThermalStateLevel == 6; }) != null, "Android level 6 should survive chart projection");
        AssertNear(androidProjected[androidProjected.Count - 1].ElapsedSec, 11.0, "Android thermal status should stay visible through the five-second polling interval");
    }

    private static void FrameDiagnosticsSurviveCollectorParsing()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo pending = type.GetField("_pendingFrameSamples", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field for frame diagnostics");
        AssertTrue(pending != null, "frame metric queue field");
        AssertTrue(parse != null, "collector frame parser method");

        generation.SetValue(collector, 9);
        parse.Invoke(collector, new object[]
        {
            "fps {\"fps\":58,\"frame_count\":58,\"window_sec\":1," +
            "\"jank\":7,\"big_jank\":3,\"jank_time_ms\":400,\"stutter_percent\":40," +
            "\"frame_time_ms\":120,\"frame_time_p95_ms\":120," +
            "\"ordered_frames\":true,\"approximate\":false,\"source_degraded\":false," +
            "\"duplicate_timestamps\":2,\"out_of_order_timestamps\":1," +
            "\"invalid_frame_intervals\":3,\"ring_buffer_overrun\":true}",
            9
        });

        Queue<PerfSample> queue = (Queue<PerfSample>)pending.GetValue(collector);
        AssertTrue(queue.Count == 1, "one frame diagnostics sample should be queued");
        PerfSample sample = queue.Dequeue();
        AssertTrue(sample.DuplicateFrameTimestamps == 2, "duplicate timestamp count");
        AssertTrue(sample.OutOfOrderFrameTimestamps == 1, "out-of-order timestamp count");
        AssertTrue(sample.InvalidFrameIntervals == 3, "invalid interval count");
        AssertTrue(sample.FrameRingBufferOverrun, "ring-buffer overrun flag");
        AssertTrue(sample.SourceDegraded, "integrity diagnostics force degraded source");
        AssertTrue(sample.ApproximateFrameMetrics, "degraded source cannot remain exact");
        AssertTrue(!sample.HasJank, "invalid ordered integrity must suppress Jank");
        AssertTrue(!sample.HasStutter, "invalid ordered integrity must suppress Stutter");
        AssertTrue(!sample.HasFrameTime, "invalid ordered integrity must suppress FrameTime");

        string serialized = JsonSerializer.Serialize(sample);
        PerfSample roundTrip = JsonSerializer.Deserialize<PerfSample>(serialized);
        AssertTrue(roundTrip != null, "frame diagnostics session round trip");
        AssertTrue(roundTrip.DuplicateFrameTimestamps == 2, "round-trip duplicate diagnostics");
        AssertTrue(roundTrip.OutOfOrderFrameTimestamps == 1, "round-trip out-of-order diagnostics");
        AssertTrue(roundTrip.InvalidFrameIntervals == 3, "round-trip invalid diagnostics");
        AssertTrue(roundTrip.FrameRingBufferOverrun, "round-trip ring-overrun diagnostics");
    }

    private static void ResumeGapMetadataSurvivesCollectorParsing()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo pending = type.GetField("_pendingFrameSamples", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field for resume gap");
        AssertTrue(pending != null, "frame queue field for resume gap");
        AssertTrue(parse != null, "collector parser for resume gap");

        generation.SetValue(collector, 12);
        parse.Invoke(collector, new object[]
        {
            "fps {\"fps\":0,\"frame_count\":0,\"window_sec\":1,\"jank\":0,\"big_jank\":0," +
            "\"ordered_frames\":true,\"no_present_frames\":true}",
            12
        });
        parse.Invoke(collector, new object[]
        {
            "fps {\"fps\":0.2,\"frame_count\":1,\"window_sec\":5,\"jank\":1,\"big_jank\":1," +
            "\"jank_time_ms\":5000,\"stutter_percent\":100,\"frame_time_ms\":5000," +
            "\"frame_time_p95_ms\":5000,\"frame_time_max_ms\":5000," +
            "\"ordered_frames\":true,\"resume_gap_ms\":5000}",
            12
        });

        Queue<PerfSample> queue = (Queue<PerfSample>)pending.GetValue(collector);
        AssertTrue(queue.Count == 2, "idle and resume samples should both be queued");
        PerfSample idle = queue.Dequeue();
        PerfSample resumed = queue.Dequeue();
        AssertTrue(idle.NoPresentFrames && idle.FrameCount == 0, "confirmed no-present metadata");
        AssertNear(resumed.ResumeGapMs, 5000.0, "resume gap metadata");
        AssertTrue(resumed.HasJank && resumed.Jank == 1.0 && resumed.BigJank == 1.0, "resume gap Jank metadata");
        AssertTrue(resumed.HasFrameTimeMax && resumed.FrameTimeMaxMs == 5000.0, "resume gap FrameTime metadata");

        string serialized = JsonSerializer.Serialize(resumed);
        PerfSample roundTrip = JsonSerializer.Deserialize<PerfSample>(serialized);
        AssertTrue(roundTrip != null && roundTrip.ResumeGapMs == 5000.0, "resume gap session round trip");
    }

    private static void FrameSourceSequenceDiscontinuitySurvivesCollectorParsing()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo pending = type.GetField("_pendingFrameSamples", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field for frame sequence");
        AssertTrue(pending != null, "frame queue field for source sequence");
        AssertTrue(parse != null, "collector parser for source sequence");

        generation.SetValue(collector, 10);
        parse.Invoke(collector, new object[]
        {
            "fps {\"fps\":60,\"source\":\"ordered-source\",\"frame_source\":\"display-present\"," +
            "\"source_elapsed_sec\":4,\"source_sequence\":4}",
            10
        });
        parse.Invoke(collector, new object[]
        {
            "fps {\"fps\":58,\"source\":\"ordered-source\",\"frame_source\":\"display-present\"," +
            "\"source_elapsed_sec\":6,\"source_sequence\":6}",
            10
        });

        Queue<PerfSample> queue = (Queue<PerfSample>)pending.GetValue(collector);
        AssertTrue(queue.Count == 2, "two source-sequenced samples should be queued");
        PerfSample first = queue.Dequeue();
        PerfSample second = queue.Dequeue();
        AssertTrue(first.HasFrameSourceSequence && first.FrameSourceSequence == 4, "first source sequence");
        AssertTrue(!first.FrameSourceSequenceDiscontinuity, "first source sequence establishes a baseline");
        AssertTrue(second.HasFrameSourceSequence && second.FrameSourceSequence == 6, "second source sequence");
        AssertTrue(second.FrameSourceSequenceDiscontinuity, "missing source sequence must break continuity");
        AssertTrue(second.MissingFrameSourceWindows == 1, "one missing source window must be recorded");

        string serialized = JsonSerializer.Serialize(second);
        PerfSample roundTrip = JsonSerializer.Deserialize<PerfSample>(serialized);
        AssertTrue(roundTrip != null, "frame source sequence session round trip");
        AssertTrue(roundTrip.FrameSourceSequence == 6, "round-trip source sequence");
        AssertTrue(roundTrip.FrameSourceSequenceDiscontinuity, "round-trip source discontinuity");
        AssertTrue(roundTrip.MissingFrameSourceWindows == 1, "round-trip missing source windows");
    }

    private static void FrameTimeChartExcludesNonFrameStateSnapshots()
    {
        PerfSample frameWindow = ExactSample(1.0, 60.0, 60, 1000.0);
        frameWindow.HasFrameTimeMax = true;
        frameWindow.FrameTimeMaxMs = 16.7;
        frameWindow.FpsUpdated = true;
        AssertTrue(FrameTimeChartProjection.IsChartSample(frameWindow), "real frame window should enter FrameTime chart");

        PerfSample thermalStateSnapshot = new PerfSample
        {
            ElapsedSec = 2.0,
            HasFrameTimeMax = true,
            FrameTimeMaxMs = 16.7,
            FpsUpdated = false,
            HasThermalState = true,
            ThermalStateLevel = 0,
            ThermalStateName = "nominal",
            ThermalStateUpdated = true,
            Source = "pymobiledevice3-xcode-energy-rsd"
        };
        AssertTrue(!FrameTimeChartProjection.IsChartSample(thermalStateSnapshot), "Thermal State snapshot must not split FrameTime chart");
    }

    private static void CollectorTargetConfirmationEventIsGenerationBound()
    {
        PerfCollector collector = new PerfCollector();
        Type type = typeof(PerfCollector);
        FieldInfo generation = type.GetField("_captureGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo parse = type.GetMethod("ParseLine", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertTrue(generation != null, "collector generation field for target confirmation");
        AssertTrue(parse != null, "collector parser for target confirmation");

        List<bool> confirmations = new List<bool>();
        collector.TargetConfirmationChanged += confirmations.Add;
        generation.SetValue(collector, 12);
        parse.Invoke(collector, new object[] { "target {\"confirmed\":true,\"pid\":42}", 11 });
        parse.Invoke(collector, new object[] { "target {\"confirmed\":true,\"pid\":42}", 12 });
        parse.Invoke(collector, new object[] { "target {\"confirmed\":false,\"pid\":42}", 12 });

        AssertTrue(confirmations.Count == 2, "stale target confirmations must be rejected");
        AssertTrue(confirmations[0] && !confirmations[1], "target confirmation transitions must preserve values");
    }

    private static PerfSample ExactSample(double elapsedSec, double fps, int frameCount, double observationMs)
    {
        return new PerfSample
        {
            ElapsedSec = elapsedSec,
            HasFps = true,
            FpsUpdated = true,
            Fps = fps,
            HasFrameObservation = true,
            FrameCount = frameCount,
            FrameObservationMs = observationMs,
        };
    }

    private static PerfSample VerifiedSurfaceSample(double elapsedSec, double fps, int frameCount, double observationMs)
    {
        return new PerfSample
        {
            ElapsedSec = elapsedSec,
            HasFps = true,
            FpsUpdated = true,
            Fps = fps,
            HasJank = true,
            Jank = 0.0,
            BigJank = 0.0,
            HasStutter = true,
            StutterPercent = 0.0,
            JankTimeMs = 0.0,
            HasFrameObservation = true,
            FrameCount = frameCount,
            FrameObservationMs = observationMs,
            OrderedFrames = true,
            HasFrameTargetVerification = true,
            FrameTargetVerified = true,
            SurfaceOwnerPid = 42,
            SurfaceOwnerUid = 10042,
            TargetPid = 42,
            Source = "adb-surfaceflinger-layer-latency",
            FpsScope = "surface",
            FrameSource = "ordered-layer-present",
        };
    }

    private static PerfSample FallbackSample(double elapsedSec, double fps)
    {
        return new PerfSample
        {
            ElapsedSec = elapsedSec,
            HasFps = true,
            FpsUpdated = true,
            Fps = fps,
        };
    }

    private static PerfSample MemorySample(double valueMb, string metric)
    {
        return new PerfSample
        {
            HasMemory = true,
            MemoryUpdated = true,
            MemoryMb = valueMb,
            MemoryMetric = metric,
        };
    }

    private static PerfSample JankSample(
        double elapsedSec,
        double jank,
        double bigJank,
        double observationMs,
        double jankTimeMs)
    {
        return new PerfSample
        {
            ElapsedSec = elapsedSec,
            HasFps = true,
            FpsUpdated = true,
            HasJank = true,
            Jank = jank,
            BigJank = bigJank,
            HasStutter = true,
            StutterPercent = jankTimeMs * 100.0 / observationMs,
            JankTimeMs = jankTimeMs,
            HasFrameObservation = true,
            FrameObservationMs = observationMs,
            OrderedFrames = true,
            Source = "pymobiledevice3-coreprofile-rsd-display",
            FpsScope = "screen",
            FrameSource = "iomfb-swap-on-glass",
        };
    }

    private static PerfSample JankFrom(PerfSample source)
    {
        PerfSample sample = JankSample(source.ElapsedSec, 0.0, 0.0, source.FrameObservationMs, 0.0);
        sample.Source = source.Source;
        sample.FpsScope = source.FpsScope;
        sample.FrameSource = source.FrameSource;
        sample.HasFrameTargetVerification = source.HasFrameTargetVerification;
        sample.FrameTargetVerified = source.FrameTargetVerified;
        sample.TargetPid = source.TargetPid;
        return sample;
    }

    private static void AssertNear(double actual, double expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.0001)
        {
            throw new InvalidOperationException(label + ": expected " + expected + ", actual " + actual);
        }
    }

    private static void AssertTrue(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }
}
