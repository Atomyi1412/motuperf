using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    public sealed class AppThemeDefinition
    {
        public AppThemeDefinition(string id, string displayName, string previewColor, ThemeVariant baseVariant, double workspaceTextureOpacity, IReadOnlyDictionary<string, string> resources)
        {
            Id = id;
            DisplayName = displayName;
            PreviewColor = previewColor;
            BaseVariant = baseVariant;
            WorkspaceTextureOpacity = workspaceTextureOpacity;
            Resources = resources;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string PreviewColor { get; }
        public ThemeVariant BaseVariant { get; }
        public double WorkspaceTextureOpacity { get; }
        public IReadOnlyDictionary<string, string> Resources { get; }
    }

    public sealed class ThemeSettingsStore
    {
        private sealed class SettingsDocument
        {
            public string ThemeId { get; set; }
        }

        private readonly string _path;

        public ThemeSettingsStore(string path = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(RuntimeTools.DataDirectory, "settings.json");
            }
        }

        public string LoadThemeId()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                SettingsDocument document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_path), JsonOptions);
                return document?.ThemeId;
            }
            catch
            {
                return null;
            }
        }

        public void SaveThemeId(string themeId)
        {
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_path, JsonSerializer.Serialize(new SettingsDocument { ThemeId = themeId }, JsonOptions));
            }
            catch
            {
                // Theme persistence must never prevent the monitor from running.
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public static class AppThemeManager
    {
        public static readonly IReadOnlyList<string> RequiredResourceKeys = new[]
        {
            "Theme.WindowBackground", "Theme.TitleBarBackground", "Theme.ToolbarBackground",
            "Theme.PanelBackground", "Theme.PanelHeaderBackground", "Theme.CardBackground",
            "Theme.InputBackground", "Theme.HoverBackground", "Theme.PressedBackground",
            "Theme.SelectedBackground", "Theme.ChartBackground", "Theme.ChartGrid",
            "Theme.TextPrimary", "Theme.TextSecondary", "Theme.TextMuted", "Theme.TextOnAccent",
            "Theme.Border", "Theme.BorderStrong", "Theme.ScrollTrack", "Theme.ScrollThumb",
            "Theme.ScrollThumbHover", "Theme.Cursor", "Theme.OverlayBackground",
            "Theme.CheckboxBackground", "Theme.CheckboxGlyph", "Theme.CheckboxHover",
            "Theme.CheckboxPressed", "Theme.CheckboxDisabled", "Theme.Accent",
            "Theme.DialogBackground", "Theme.DialogSurface", "Theme.DialogFooterBackground"
        };

        public static readonly IReadOnlyList<AppThemeDefinition> Themes = new[]
        {
            Create("dark", "黑暗", "#171822", ThemeVariant.Dark, 0,
                "#0F1018", "#181A24", "#181820", "#181923", "#191A24", "#1B1C27",
                "#31313F", "#242532", "#303240", "#2A2C3C", "#161720", "#383A4A",
                "#EBECF4", "#ACAEBE", "#777A8C", "#FFFFFF", "#292B3A", "#424456",
                "#0D0E15", "#5B5C60", "#7B7D84", "#FFFFFF", "#08090D",
                 "#FFFFFF", "#1A1B22", "#F4F4F5", "#E3E4E8", "#B8B9BE", "#1476FF",
                 "#151620", "#1D1F2B", "#171923"),
            Create("light", "明亮", "#F3F5F8", ThemeVariant.Light, 0,
                "#F3F5F8", "#EAF0F6", "#FFFFFF", "#F7F8FA", "#F0F2F5", "#FFFFFF",
                "#FFFFFF", "#E9EDF3", "#DDE3EB", "#E7F0FF", "#FFFFFF", "#D7DDE6",
                "#20232A", "#596170", "#7A8290", "#FFFFFF", "#D8DEE7", "#B8C0CC",
                "#E4E8EE", "#AAB2BF", "#858E9D", "#1476FF", "#EEF1F5",
                 "#FFFFFF", "#20232A", "#F4F4F5", "#E3E4E8", "#D1D5DC", "#1476FF",
                 "#F4F6F9", "#FFFFFF", "#EDF1F5"),
            Create("retro-sage", "绿色", "#3F6C4B", ThemeVariant.Light, 0.18,
                "#E4EDE3", "#B0C3AE", "#F2EEDD", "#D7E3D5", "#C9D8C6", "#F7F2E4",
                "#FBF7EA", "#E0E6D6", "#CCD6C3", "#E9E3CE", "#EDF2E8", "#BBC7B5",
                "#1D2B22", "#405146", "#5A6B5E", "#FFFDF3", "#B9B9A4", "#8E9278",
                "#CBD5C5", "#71856F", "#596E58", "#2F7652", "#E9E3D3",
                 "#FBF8EA", "#365A42", "#F0ECD9", "#E5DFC8", "#C8C7B8", "#3F6C4B",
                 "#DCE7DA", "#EEF3E8", "#CAD8C7")
        };

        private static readonly ThemeSettingsStore Settings = new ThemeSettingsStore();
        private static AppThemeDefinition _current = Themes[0];

        public static event Action ThemeChanged;
        public static AppThemeDefinition Current { get { return _current; } }

        public static AppThemeDefinition Resolve(string themeId)
        {
            return Themes.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
        }

        public static string ColorValue(string resourceKey)
        {
            string value;
            return _current.Resources.TryGetValue(resourceKey, out value) ? value : "#FF00FF";
        }

        public static void Initialize(Application application)
        {
            Apply(application, Settings.LoadThemeId(), false);
        }

        public static void Select(Application application, string themeId)
        {
            Apply(application, themeId, true);
        }

        public static void Apply(Application application, string themeId, bool persist = true)
        {
            if (application == null) return;
            AppThemeDefinition theme = Resolve(themeId);
            application.RequestedThemeVariant = theme.BaseVariant;
            foreach (KeyValuePair<string, string> item in theme.Resources)
            {
                application.Resources[item.Key] = new SolidColorBrush(Color.Parse(item.Value));
            }
            application.Resources["Theme.WorkspaceTextureOpacity"] = theme.WorkspaceTextureOpacity;
            _current = theme;
            if (persist) Settings.SaveThemeId(theme.Id);
            ThemeChanged?.Invoke();
        }

        private static AppThemeDefinition Create(string id, string displayName, string previewColor, ThemeVariant variant, double workspaceTextureOpacity, params string[] values)
        {
            if (values.Length != RequiredResourceKeys.Count) throw new InvalidOperationException("Theme palette is incomplete: " + id);
            Dictionary<string, string> resources = RequiredResourceKeys
                .Select((key, index) => new KeyValuePair<string, string>(key, values[index]))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            return new AppThemeDefinition(id, displayName, previewColor, variant, workspaceTextureOpacity, resources);
        }
    }
}
