using System;
using System.IO;
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
                text.AppendLine("Version: v0.21.7");
                text.AppendLine("BaseDirectory: " + AppDomain.CurrentDomain.BaseDirectory);
                text.AppendLine();
                text.AppendLine(Convert.ToString(exception));
                lock (Gate) File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch { return ""; }
        }
    }
}
