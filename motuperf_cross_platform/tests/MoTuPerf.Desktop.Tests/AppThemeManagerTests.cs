using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class AppThemeManagerTests
    {
        [Fact]
        public void RegistryContainsCompleteThemesWithUniqueIds()
        {
            IReadOnlyList<AppThemeDefinition> themes = AppThemeManager.Themes;

            Assert.Equal(new[] { "dark", "light", "retro-sage" }, themes.Select(theme => theme.Id));
            Assert.Equal(new[] { "黑暗", "明亮", "绿色" }, themes.Select(theme => theme.DisplayName));
            Assert.Equal(new[] { "#171822", "#F3F5F8", "#3F6C4B" }, themes.Select(theme => theme.PreviewColor));
            Assert.Equal(themes.Count, themes.Select(theme => theme.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(themes, theme => Assert.All(AppThemeManager.RequiredResourceKeys,
                key => Assert.True(theme.Resources.ContainsKey(key), theme.Id + " is missing " + key)));
        }

        [Fact]
        public void UnknownThemeFallsBackToDark()
        {
            Assert.Equal("dark", AppThemeManager.Resolve("removed-theme").Id);
        }

        [Fact]
        public void SettingsPersistOnlyThemeIdAndCorruptContentDoesNotCrash()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motuperf-theme-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            try
            {
                ThemeSettingsStore store = new ThemeSettingsStore(path);
                store.SaveThemeId("light");

                Assert.Equal("light", store.LoadThemeId());
                string json = File.ReadAllText(path);
                Assert.Contains("\"themeId\"", json);
                Assert.DoesNotContain("Resources", json);

                File.WriteAllText(path, "{not-json");
                Assert.Equal("dark", AppThemeManager.Resolve(store.LoadThemeId()).Id);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void DefaultSettingsPathLivesInApplicationDataDirectory()
        {
            string settingsPath = Path.GetFullPath(ThemeSettingsStore.DefaultPath);
            string applicationPath = Path.GetFullPath(AppContext.BaseDirectory);

            Assert.StartsWith(applicationPath, settingsPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("data", "settings.json"), settingsPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PrimaryAndSecondaryTextMeetReadableContrastOnMainSurfaces()
        {
            foreach (AppThemeDefinition theme in AppThemeManager.Themes)
            {
                foreach (string surface in new[]
                {
                    "Theme.WindowBackground", "Theme.PanelBackground", "Theme.CardBackground",
                    "Theme.DialogBackground", "Theme.DialogSurface", "Theme.DialogFooterBackground"
                })
                {
                    Assert.True(Contrast(theme.Resources["Theme.TextPrimary"], theme.Resources[surface]) >= 4.5,
                        theme.Id + " primary text contrast failed on " + surface);
                    Assert.True(Contrast(theme.Resources["Theme.TextSecondary"], theme.Resources[surface]) >= 4.5,
                        theme.Id + " secondary text contrast failed on " + surface);
                }
            }
        }

        [Fact]
        public void TitleBarUsesAQuietDistinctSurfaceInEveryTheme()
        {
            AppThemeDefinition dark = AppThemeManager.Resolve("dark");
            AppThemeDefinition light = AppThemeManager.Resolve("light");
            AppThemeDefinition retro = AppThemeManager.Resolve("retro-sage");

            Assert.Equal("#181A24", dark.Resources["Theme.TitleBarBackground"]);
            Assert.Equal("#EAF0F6", light.Resources["Theme.TitleBarBackground"]);
            Assert.Equal("#B0C3AE", retro.Resources["Theme.TitleBarBackground"]);
            Assert.All(AppThemeManager.Themes, theme =>
            {
                Assert.NotEqual(theme.Resources["Theme.WindowBackground"], theme.Resources["Theme.TitleBarBackground"]);
                Assert.True(Contrast(theme.Resources["Theme.TextPrimary"], theme.Resources["Theme.TitleBarBackground"]) >= 4.5,
                    theme.Id + " primary text contrast failed on the title bar");
            });
        }

        [Fact]
        public void RetroSageUsesGreenAccentAndExclusiveWorkspaceTexture()
        {
            AppThemeDefinition retro = AppThemeManager.Resolve("retro-sage");

            Assert.Equal(0, AppThemeManager.Resolve("dark").WorkspaceTextureOpacity);
            Assert.Equal(0, AppThemeManager.Resolve("light").WorkspaceTextureOpacity);
            Assert.InRange(retro.WorkspaceTextureOpacity, 0.1, 0.25);
            Assert.Equal("#3F6C4B", retro.Resources["Theme.Accent"]);
            Assert.NotEqual(AppThemeManager.Resolve("light").Resources["Theme.Accent"], retro.Resources["Theme.Accent"]);
            Assert.Equal("#DCE7DA", retro.Resources["Theme.DialogBackground"]);
            Assert.Equal("#EEF3E8", retro.Resources["Theme.DialogSurface"]);
            Assert.Equal("#CAD8C7", retro.Resources["Theme.DialogFooterBackground"]);
            Assert.NotEqual(retro.Resources["Theme.DialogBackground"], retro.Resources["Theme.DialogSurface"]);
        }

        private static double Contrast(string first, string second)
        {
            double lighter = Math.Max(Luminance(first), Luminance(second));
            double darker = Math.Min(Luminance(first), Luminance(second));
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double Luminance(string hex)
        {
            int value = Convert.ToInt32(hex.TrimStart('#'), 16);
            double r = Channel((value >> 16) & 255);
            double g = Channel((value >> 8) & 255);
            double b = Channel(value & 255);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static double Channel(int value)
        {
            double normalized = value / 255.0;
            return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
    }
}
