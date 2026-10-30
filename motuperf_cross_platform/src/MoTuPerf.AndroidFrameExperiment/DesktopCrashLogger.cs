using System;
using System.IO;
using System.Reflection;
using System.Text;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    internal static class DesktopCrashLogger
    {
        private static readonly object Gate = new object();

        public static string Write(string title, Exception exception)
        {
            try
            {
                string root = Path.Combine(RuntimeTools.DataDirectory, "logs");
                Directory.CreateDirectory(root);
                DiagnosticLogMaintenance.Run(root);
                string path = Path.Combine(root, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                StringBuilder text = new StringBuilder();
                text.AppendLine("Title: " + (title ?? ""));
                text.AppendLine("Time: " + DateTime.Now.ToString("O"));
                text.AppendLine("Version: v" + ResolveVersion());
                text.AppendLine("BaseDirectory: " + AppDomain.CurrentDomain.BaseDirectory);
                text.AppendLine();
                text.AppendLine(Convert.ToString(exception));
                lock (Gate) File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch { return ""; }
        }

        private static string ResolveVersion()
        {
            Assembly assembly = typeof(DesktopCrashLogger).Assembly;
            AssemblyInformationalVersionAttribute informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = informational == null ? "" : informational.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                Version assemblyVersion = assembly.GetName().Version;
                version = assemblyVersion == null ? "0.0.0" : assemblyVersion.ToString(3);
            }
            int suffix = version.IndexOf('+');
            return (suffix >= 0 ? version.Substring(0, suffix) : version).TrimStart('v');
        }
    }
}
