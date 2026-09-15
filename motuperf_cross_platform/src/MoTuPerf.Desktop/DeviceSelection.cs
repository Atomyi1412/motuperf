using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public sealed class DeviceSelection
    {
        public DeviceSelection(DeviceInfo device, AppInfo app, ProcessInfo process, bool autoStart = false)
        {
            Device = device;
            App = app;
            Process = process;
            AutoStart = autoStart;
        }

        public DeviceInfo Device { get; private set; }
        public AppInfo App { get; private set; }
        public ProcessInfo Process { get; private set; }
        public bool AutoStart { get; private set; }
    }
}
