using System;
using System.IO;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    [CollectionDefinition("Global language state", DisableParallelization = true)]
    public sealed class GlobalLanguageStateCollection
    {
    }

    [Collection("Global language state")]
    public sealed class AppLanguageManagerTests
    {
        [Fact]
        public void SettingsPersistLanguageSeparatelyAndCorruptContentFallsBackToEnglish()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motuperf-language-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "language-settings.json");
            try
            {
                LanguageSettingsStore store = new LanguageSettingsStore(path);
                store.SaveChinese(true);

                Assert.True(store.LoadChinese());
                string json = File.ReadAllText(path);
                Assert.Contains("\"chinese\": true", json);
                Assert.DoesNotContain("themeId", json);

                File.WriteAllText(path, "{not-json");
                Assert.False(store.LoadChinese());
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void PresentationCatalogSwitchesLabelsWithoutChangingCanonicalIds()
        {
            bool previous = AppLanguageManager.IsChinese;
            try
            {
                AppLanguageManager.SetChinese(false, false);
                Assert.Equal("中文", AppLanguageManager.SelectorText);
                Assert.Equal("FPS", AppLanguageManager.MetricDisplayName("FPS"));
                Assert.Equal("PID", AppLanguageManager.DataTileLabel("pid", "PID"));
                Assert.Equal("FPS (帧/s)", AppLanguageManager.AnalysisLabel("FPS", "帧/s"));

                AppLanguageManager.SetChinese(true, false);
                Assert.Equal("English", AppLanguageManager.SelectorText);
                Assert.Equal("帧率", AppLanguageManager.MetricDisplayName("FPS"));
                Assert.Equal("显示帧耗时", AppLanguageManager.MetricDisplayName("Display FrameTime"));
                Assert.Equal("原始 CPU", AppLanguageManager.DataTileLabel("cpu", "CPU Raw"));
                Assert.Equal("帧率 (帧/秒)", AppLanguageManager.AnalysisLabel("FPS", "帧/s"));
            }
            finally
            {
                AppLanguageManager.SetChinese(previous, false);
            }
        }

        [Fact]
        public void DefaultSettingsPathLivesBesideThemeSettingsInApplicationDataDirectory()
        {
            string languagePath = Path.GetFullPath(LanguageSettingsStore.DefaultPath);
            string applicationPath = Path.GetFullPath(AppContext.BaseDirectory);

            Assert.StartsWith(applicationPath, languagePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("data", "language-settings.json"), languagePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
