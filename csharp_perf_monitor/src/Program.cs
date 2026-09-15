using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CSharpIosPerfMonitor
{
    public static class Program
    {
        public static readonly string AppVersion = "v" + ResolveAppVersion();
        public const string AppTitle = "MoTuPerf";

        [STAThread]
        public static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                Exception exception = args.ExceptionObject as Exception;
                if (exception != null) CrashLogger.Write("未处理异常", exception);
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs args)
            {
                CrashLogger.Write("后台任务异常", args.Exception);
                args.SetObserved();
            };

            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;

            try
            {
                var window = new MainWindow();
                app.Run(window);
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("启动失败", ex);
                MessageBox.Show(
                    "程序启动失败，已写入日志：\n" + path,
                    AppTitle + " 运行错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            string path = CrashLogger.Write("界面异常", args.Exception);
            MessageBox.Show(
                "程序遇到异常但已拦截，日志位置：\n" + path,
                AppTitle + " 运行错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        }

        private static string ResolveAppVersion()
        {
            AssemblyInformationalVersionAttribute attr = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = attr == null ? "" : attr.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                Version assemblyVersion = typeof(Program).Assembly.GetName().Version;
                version = assemblyVersion == null ? "0.0.0" : assemblyVersion.ToString(3);
            }
            int metadataIndex = version.IndexOf('+');
            if (metadataIndex >= 0) version = version.Substring(0, metadataIndex);
            return version;
        }
    }
}
