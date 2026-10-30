using Avalonia;
using System;

namespace MoTuPerf.Desktop
{
    internal static class Program
    {
        public static void Main(string[] args)
        {
            // Process-scoped so the production build keeps its original Android frame source.
            Environment.SetEnvironmentVariable("MOTUPERF_ANDROID_FRAME_BACKEND", "device_perfetto");
            Environment.SetEnvironmentVariable("MOTUPERF_EXPERIMENT_BUILD", "android_frame");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
    }
}
