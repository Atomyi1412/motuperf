using System;

namespace CSharpIosPerfMonitor
{
    internal static class DeviceLookupService
    {
        public static bool IsAndroid(DeviceInfo device)
        {
            return IsAndroid(device == null ? "" : device.Platform);
        }

        public static bool IsAndroid(string platform)
        {
            return string.Equals(platform ?? "", "android", StringComparison.OrdinalIgnoreCase);
        }
    }
}
