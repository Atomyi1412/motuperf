using System;
using System.IO;

namespace CSharpIosPerfMonitor
{
    public static class HdcToolLocator
    {
        public static string Resolve(string bundled)
        {
            string configured = Environment.GetEnvironmentVariable("MOTUPERF_HDC");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (Path.IsPathRooted(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
                throw new FileNotFoundException("MOTUPERF_HDC 必须指向已安装的 HDC 可执行文件。", configured);
            }
            if (File.Exists(bundled)) return bundled;
            string name = OperatingSystem.IsWindows() ? "hdc.exe" : "hdc";
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string directory = entry.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) continue;
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("未找到 HDC，请安装官方 SDK 的 toolchains，配置 PATH 或 MOTUPERF_HDC。", bundled);
        }
    }
}
