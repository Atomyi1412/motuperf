using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MoTuPerf.Platform
{
    [SupportedOSPlatform("windows")]
    internal static class WindowsPythonRegistry
    {
        private const int ErrorSuccess = 0;
        private const int KeyRead = 0x20019;
        private const int KeyWow6432 = 0x0200;
        private const int RegistrySize = 1;
        private const int RegistryExpandSize = 2;
        private static readonly IntPtr CurrentUser = unchecked((IntPtr)(-2147483647));
        private static readonly IntPtr LocalMachine = unchecked((IntPtr)(-2147483646));
        private static readonly string[] Versions = { "3.14", "3.13", "3.12", "3.11", "3.10", "3.9", "3.8" };

        internal static IEnumerable<string> FindExecutablePaths()
        {
            List<string> paths = new List<string>();
            AddInstallPaths(paths, CurrentUser, 0);
            if (Environment.Is64BitOperatingSystem)
            {
                AddInstallPaths(paths, CurrentUser, KeyWow6432);
            }
            AddInstallPaths(paths, LocalMachine, 0);
            if (Environment.Is64BitOperatingSystem)
            {
                AddInstallPaths(paths, LocalMachine, KeyWow6432);
            }
            return paths;
        }

        private static void AddInstallPaths(List<string> paths, IntPtr root, int registryView)
        {
            foreach (string version in Versions)
            {
                string subKey = @"Software\Python\PythonCore\" + version + @"\InstallPath";
                string executable = ReadStringValue(root, subKey, "ExecutablePath", registryView);
                AddDistinct(paths, executable);
            }
        }

        private static string ReadStringValue(IntPtr root, string subKey, string valueName, int registryView)
        {
            IntPtr key = IntPtr.Zero;
            try
            {
                int result = RegOpenKeyEx(root, subKey, 0, KeyRead | registryView, out key);
                if (result != ErrorSuccess) return "";

                int type;
                int byteCount = 0;
                result = RegQueryValueEx(key, valueName, IntPtr.Zero, out type, null, ref byteCount);
                if (result != ErrorSuccess || byteCount <= 0) return "";
                if (type != RegistrySize && type != RegistryExpandSize) return "";

                byte[] data = new byte[byteCount];
                result = RegQueryValueEx(key, valueName, IntPtr.Zero, out type, data, ref byteCount);
                if (result != ErrorSuccess || byteCount <= 0) return "";

                string value = Encoding.Unicode.GetString(data, 0, byteCount).TrimEnd('\0');
                return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            }
            finally
            {
                if (key != IntPtr.Zero) RegCloseKey(key);
            }
        }

        private static void AddDistinct(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            foreach (string existing in paths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
            }
            paths.Add(path);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
        private static extern int RegOpenKeyEx(IntPtr key, string subKey, int options, int desiredAccess, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
        private static extern int RegQueryValueEx(IntPtr key, string valueName, IntPtr reserved, out int type, byte[] data, ref int byteCount);

        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey")]
        private static extern int RegCloseKey(IntPtr key);
    }
}
