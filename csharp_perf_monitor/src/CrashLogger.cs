using System;
using System.IO;
using System.Text;

namespace CSharpIosPerfMonitor
{
    public static class CrashLogger
    {
        private static readonly object Gate = new object();

        public static string Write(string title, Exception exception)
        {
            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "logs");
                Directory.CreateDirectory(root);
                DiagnosticLogMaintenance.Run(root);
                string path = Path.Combine(root, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                StringBuilder text = new StringBuilder();
                text.AppendLine("Title: " + title);
                text.AppendLine("Time: " + DateTime.Now.ToString("O"));
                text.AppendLine("Version: " + Program.AppVersion);
                text.AppendLine("BaseDirectory: " + AppDomain.CurrentDomain.BaseDirectory);
                text.AppendLine();
                text.AppendLine(Convert.ToString(exception));

                lock (Gate)
                {
                    File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                }
                return path;
            }
            catch
            {
                return "";
            }
        }
    }
}
