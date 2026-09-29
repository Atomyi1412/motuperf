using System;

namespace CSharpIosPerfMonitor
{
    public static class ProcessTargetMatcher
    {
        public static bool IsValidTarget(ProcessInfo process)
        {
            if (process == null || process.Pid <= 0) return false;
            if (!string.IsNullOrWhiteSpace(process.Name)) return true;

            // Ability Manager can prove a Harmony Bundle owns a real PID even
            // when /proc/<pid>/cmdline and ps expose no executable name. Keep
            // that target selectable by its PID and Bundle evidence, while
            // refusing unnamed Android/iOS rows or ambiguous ownership.
            return DevicePlatformNames.IsHarmony(process.Platform)
                && process.OwnershipVerified
                && !process.OwnershipAmbiguous
                && !string.IsNullOrWhiteSpace(FirstNonEmpty(process.BundleId, process.OwnerBundleId));
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

        public static bool SameHarmonyProcessInstance(ProcessInfo current, ProcessInfo selected)
        {
            if (current == null || selected == null || current.Pid != selected.Pid) return false;
            if (selected.HarmonyStartTimeTicks > 0
                && (current.HarmonyStartTimeTicks <= 0
                    || current.HarmonyStartTimeTicks != selected.HarmonyStartTimeTicks))
            {
                return false;
            }
            // Unknown profile scope is distinct from every concrete Harmony
            // user. Do not reuse an unscoped row for a known-user process (or
            // the reverse), even when PID/name/start-time happen to match.
            if ((selected.HarmonyUserId >= 0 || current.HarmonyUserId >= 0)
                && (selected.HarmonyUserId < 0 || current.HarmonyUserId < 0
                    || current.HarmonyUserId != selected.HarmonyUserId))
            {
                return false;
            }
            if ((selected.HarmonyAppIndex >= 0 || current.HarmonyAppIndex >= 0)
                && (selected.HarmonyAppIndex < 0 || current.HarmonyAppIndex < 0
                    || current.HarmonyAppIndex != selected.HarmonyAppIndex))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(selected.Name)
                && !string.Equals(current.Name, selected.Name, StringComparison.Ordinal))
            {
                if (selected.HarmonyStartTimeTicks <= 0 || current.OwnershipAmbiguous || selected.OwnershipAmbiguous)
                    return false;
                bool commAlias = selected.HarmonyNameIsComm && !current.HarmonyNameIsComm
                    && IsHarmonyCommAlias(selected.Name, current.Name);
                commAlias |= current.HarmonyNameIsComm && !selected.HarmonyNameIsComm
                    && IsHarmonyCommAlias(current.Name, selected.Name);
                if (!commAlias) return false;
            }
            string selectedBundle = FirstNonEmpty(selected.BundleId, selected.OwnerBundleId);
            string currentBundle = FirstNonEmpty(current.BundleId, current.OwnerBundleId);
            if (!string.IsNullOrWhiteSpace(selectedBundle))
            {
                return !string.IsNullOrWhiteSpace(currentBundle)
                    && string.Equals(currentBundle, selectedBundle, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        public static bool CanReuseAndroidSelectionForCapture(ProcessInfo process, string udid)
        {
            return IsValidTarget(process)
                && process.AndroidStartTimeTicks > 0
                && BelongsToDevice(process, udid);
        }

        public static bool IsHarmonyCommAlias(string comm, string fullName)
        {
            if (string.IsNullOrEmpty(comm) || string.IsNullOrEmpty(fullName)
                || System.Text.Encoding.UTF8.GetByteCount(comm) != 15
                || fullName.Length <= comm.Length)
                return false;

            // Some Harmony vendors expose the native process COMM without
            // the common `com.` bundle prefix while ARGS/NAME retains the
            // complete executable identity. Keep this narrowly scoped to
            // the observed prefix form. Some vendor `ps COMM` columns also
            // drop the first character when a 16-byte ASCII Bundle exceeds
            // the 15-byte kernel limit (for example `om.m2.xzlr.hwhm` for
            // `com.m2.xzlr.hwhm`). Keep that exact one-character form here;
            // arbitrary suffix matching would merge unrelated processes that
            // happen to share a label.
            return fullName.StartsWith(comm, StringComparison.Ordinal)
                || fullName.StartsWith("com." + comm, StringComparison.Ordinal)
                || (fullName.Length == comm.Length + 1
                    && fullName.StartsWith("com.", StringComparison.Ordinal)
                    && string.Equals(fullName.Substring(1), comm, StringComparison.Ordinal));
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
