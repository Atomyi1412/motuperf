using System;

namespace CSharpIosPerfMonitor
{
    public static class ProcessTargetMatcher
    {
        public static bool IsValidTarget(ProcessInfo process)
        {
            return process != null
                && process.Pid > 0
                && !string.IsNullOrWhiteSpace(process.Name);
        }

        public static bool BelongsToDevice(ProcessInfo process, string udid)
        {
            return IsValidTarget(process)
                && !string.IsNullOrWhiteSpace(udid)
                && string.Equals(process.DeviceUdid, udid, StringComparison.Ordinal);
        }

        public static bool IsIosWebKitProcessRole(string processName)
        {
            return string.Equals(processName, "com.apple.WebKit.WebContent", StringComparison.Ordinal)
                || string.Equals(processName, "com.apple.WebKit.GPU", StringComparison.Ordinal)
                || string.Equals(processName, "com.apple.WebKit.Networking", StringComparison.Ordinal);
        }

        public static bool IsIosOwnedWebProcess(ProcessInfo process, string bundleId)
        {
            return process != null
                && process.OwnershipVerified
                && process.OwnerPid > 0
                && IsIosWebKitProcessRole(process.Name)
                && !string.IsNullOrWhiteSpace(bundleId)
                && string.Equals(process.OwnerBundleId, bundleId, StringComparison.Ordinal);
        }

        public static bool IsIosDefaultPickerProcess(ProcessInfo process, string selectedBundleId)
        {
            if (process == null) return false;
            if (IsIosWebKitProcessRole(process.Name))
            {
                return string.Equals(selectedBundleId, "com.tencent.xin", StringComparison.Ordinal)
                    && process.Name != "com.apple.WebKit.Networking"
                    && IsIosOwnedWebProcess(process, selectedBundleId);
            }

            if (!string.IsNullOrWhiteSpace(selectedBundleId)
                && (string.Equals(process.BundleId, selectedBundleId, StringComparison.Ordinal)
                    || string.Equals(ProcessBundleFromName(process.Name), selectedBundleId, StringComparison.Ordinal)))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(process.BundleId);
        }

        public static bool SameAndroidTarget(ProcessInfo current, ProcessInfo selected, string bundleId)
        {
            if (current == null || selected == null) return false;
            if (IsAndroidAppBrandProcessName(selected.Name))
            {
                return IsAndroidAppBrandProcessName(current.Name)
                    && string.Equals(current.Reason, "当前前台微信小游戏进程", StringComparison.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(selected.Name)
                && string.Equals(current.Name, selected.Name, StringComparison.Ordinal))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(selected.Name) && selected.Name.IndexOf(':') >= 0)
            {
                return false;
            }

            string targetBundle = FirstNonEmpty(selected.BundleId, bundleId, ProcessBundleFromName(selected.Name));
            string currentBundle = FirstNonEmpty(current.BundleId, ProcessBundleFromName(current.Name));
            return !string.IsNullOrWhiteSpace(targetBundle)
                && string.Equals(currentBundle, targetBundle, StringComparison.Ordinal);
        }

        public static bool IsAndroidProcessCompatibleWithBundle(ProcessInfo process, string bundleId)
        {
            if (process == null || string.IsNullOrWhiteSpace(bundleId)) return false;
            string processBundle = FirstNonEmpty(process.BundleId, ProcessBundleFromName(process.Name));
            return string.Equals(processBundle, bundleId, StringComparison.Ordinal);
        }

        public static bool SameAndroidProcessInstance(ProcessInfo current, ProcessInfo selected)
        {
            if (current == null || selected == null || current.Pid != selected.Pid) return false;
            if (selected.AndroidStartTimeTicks > 0
                && (current.AndroidStartTimeTicks <= 0
                    || current.AndroidStartTimeTicks != selected.AndroidStartTimeTicks))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(current.Name) && !string.IsNullOrWhiteSpace(selected.Name))
            {
                return string.Equals(current.Name, selected.Name, StringComparison.Ordinal);
            }
            if (!string.IsNullOrWhiteSpace(current.BundleId) && !string.IsNullOrWhiteSpace(selected.BundleId))
            {
                return string.Equals(current.BundleId, selected.BundleId, StringComparison.Ordinal);
            }
            return string.Equals(
                ProcessBundleFromName(current.Name),
                ProcessBundleFromName(selected.Name),
                StringComparison.Ordinal);
        }

        public static bool CanReuseAndroidSelectionForCapture(ProcessInfo process, string udid)
        {
            return IsValidTarget(process)
                && process.AndroidStartTimeTicks > 0
                && BelongsToDevice(process, udid);
        }

        private static bool IsAndroidAppBrandProcessName(string processName)
        {
            return !string.IsNullOrWhiteSpace(processName)
                && processName.StartsWith("com.tencent.mm:appbrand", StringComparison.Ordinal);
        }

        private static string ProcessBundleFromName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return "";
            int colon = processName.IndexOf(':');
            return colon > 0 ? processName.Substring(0, colon) : processName;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }
    }
}
