using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public partial class App : Avalonia.Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            RuntimeTools.PrepareUserDataDirectory();
            AppThemeManager.Initialize(this);
            _ = RuntimeTools.MigrateLegacyDataAsync(System.Threading.CancellationToken.None);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += UnobservedTaskException;
            Dispatcher.UIThread.UnhandledException += DispatcherUnhandledException;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }

        private static void CurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            DesktopCrashLogger.Write("未处理异常", args.ExceptionObject as Exception);
        }
        private static void UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            DesktopCrashLogger.Write("后台任务异常", args.Exception);
            args.SetObserved();
        }
        private static void DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            string path = DesktopCrashLogger.Write("界面异常", args.Exception);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Status = string.IsNullOrWhiteSpace(path) ? "界面处理异常，已拦截" : "界面处理异常，日志：" + path;
            }
            args.Handled = true;
        }
    }
}
