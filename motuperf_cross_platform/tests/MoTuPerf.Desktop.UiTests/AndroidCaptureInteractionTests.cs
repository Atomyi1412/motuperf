using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.UiTests
{
    public sealed class ConnectedAndroidCaptureFactAttribute : FactAttribute
    {
        public ConnectedAndroidCaptureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MOTUPERF_TEST_ANDROID_SERIAL"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MOTUPERF_TEST_ANDROID_PACKAGE")))
                Skip = "Requires an explicitly selected connected Android device and running app.";
        }
    }

    public sealed partial class DevicePickerInteractionTests
    {
        [ConnectedAndroidCaptureFact]
        public Task ConnectedAndroidSupportsDesktopCaptureStopImportAndRestart() => OnUi(async () =>
        {
            string serial = Environment.GetEnvironmentVariable("MOTUPERF_TEST_ANDROID_SERIAL");
            string package = Environment.GetEnvironmentVariable("MOTUPERF_TEST_ANDROID_PACKAGE");
            if (Environment.GetEnvironmentVariable("MOTUPERF_TEST_PACKAGED") == "1")
            {
                Assert.True(RuntimeTools.IsPackagedBuild);
                Assert.StartsWith(AppContext.BaseDirectory, RuntimeTools.PythonExecutable, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(AppContext.BaseDirectory, RuntimeTools.AdbExecutable, StringComparison.OrdinalIgnoreCase);
            }
            var lookup = new AndroidLookupService();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var viewModel = new MainWindowViewModel();
            for (int round = 0; round < 3; round++)
            {
                var processes = await lookup.ListProcessesAsync(serial, deadline.Token);
                var target = processes.Single(process => process.BundleId == package && process.Name == package);
                viewModel.ApplySelection(new DeviceSelection(
                    new DeviceInfo { Udid = serial, Platform = "android", Name = serial },
                    new AppInfo { BundleId = package, Name = package, Platform = "android" }, target));
                viewModel.ToggleCapture();
                Assert.True(viewModel.IsCapturing, viewModel.Status);
                DateTime sampleDeadline = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < sampleDeadline && viewModel.IsCapturing
                    && !(viewModel.Samples.Any(sample => sample.HasCpu && sample.CpuUpdated)
                        && viewModel.Samples.Any(sample => sample.HasMemory && sample.MemoryUpdated)
                        && viewModel.Samples.Count(sample => sample.HasFps && sample.FpsUpdated) >= 3))
                    await Task.Delay(100, deadline.Token);
                Assert.True(viewModel.IsCapturing, viewModel.Status);
                Assert.Contains(viewModel.Samples, sample => sample.HasCpu && sample.CpuUpdated);
                Assert.Contains(viewModel.Samples, sample => sample.HasMemory && sample.MemoryUpdated);
                Assert.True(viewModel.Samples.Count(sample => sample.HasFps && sample.FpsUpdated) >= 3, viewModel.Status);
                DateTime screenshotDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < screenshotDeadline && viewModel.IsCapturing && viewModel.Screenshots.Count == 0)
                    await Task.Delay(100, deadline.Token);
                Assert.True(viewModel.IsCapturing, viewModel.Status);
                Assert.NotEmpty(viewModel.Screenshots);
                viewModel.ToggleCapture();
                Assert.False(viewModel.IsCapturing);
                Assert.NotEmpty(viewModel.Samples);
                if (round == 0)
                {
                    var saved = viewModel.BuildSessionDocument();
                    await viewModel.ApplySessionDocumentAsync(saved);
                    Assert.True(viewModel.CaptureSelectionNeedsPicker);
                    viewModel.ToggleCapture();
                    Assert.False(viewModel.IsCapturing);
                    Assert.NotEmpty(viewModel.Samples);
                }
            }
        });
    }
}
