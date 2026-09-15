using System;
using System.IO;
using System.Text.Json;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public sealed class UpdateSettingsStore
    {
        private sealed class SettingsDocument
        {
            public bool CheckForUpdates { get; set; } = true;
            public string SkippedVersion { get; set; }
        }

        private readonly string _path;

        public UpdateSettingsStore(string path = null)
        {
            _path = path;
        }

        private string SettingsPath { get { return string.IsNullOrWhiteSpace(_path) ? Path.Combine(RuntimeTools.DataDirectory, "update-settings.json") : _path; } }

        public bool LoadCheckForUpdates()
        {
            SettingsDocument document = Load();
            return document == null || document.CheckForUpdates;
        }

        public string LoadSkippedVersion()
        {
            return Load()?.SkippedVersion;
        }

        public void Save(bool checkForUpdates, string skippedVersion)
        {
            string temporary = null;
            try
            {
                string path = SettingsPath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new SettingsDocument { CheckForUpdates = checkForUpdates, SkippedVersion = skippedVersion }, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, true);
            }
            catch
            {
                // Update preferences are optional and must never block data collection.
            }
            finally { try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private SettingsDocument Load()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path));
            }
            catch { return null; }
        }
    }
}
