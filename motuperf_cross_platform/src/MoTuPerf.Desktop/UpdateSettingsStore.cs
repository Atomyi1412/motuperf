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
            _path = string.IsNullOrWhiteSpace(path) ? Path.Combine(RuntimeTools.DataDirectory, "update-settings.json") : path;
        }

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
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new SettingsDocument { CheckForUpdates = checkForUpdates, SkippedVersion = skippedVersion }, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, true);
            }
            catch
            {
                // Update preferences are optional and must never block data collection.
            }
        }

        private SettingsDocument Load()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                return JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_path));
            }
            catch { return null; }
        }
    }
}
