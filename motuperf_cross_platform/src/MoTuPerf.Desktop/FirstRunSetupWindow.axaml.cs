using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MoTuPerf.Desktop
{
    public partial class FirstRunSetupWindow : Window
    {
        private readonly string _originalTheme;
        private bool _confirmed;

        public FirstRunSetupWindow()
        {
            _originalTheme = AppThemeManager.Current.Id;
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            foreach (string name in new[] { "DarkTheme", "LightTheme", "GreenTheme" })
                this.FindControl<RadioButton>(name).IsCheckedChanged += PreviewTheme;
            this.FindControl<RadioButton>(_originalTheme == "light" ? "LightTheme" : _originalTheme == "retro-sage" ? "GreenTheme" : "DarkTheme").IsChecked = true;
            Closed += delegate { if (!_confirmed) ApplyPreview(_originalTheme); };
        }

        private void PreviewTheme(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            if (button?.IsChecked != true) return;
            ApplyPreview(button.Name == "LightTheme" ? "light" : button.Name == "GreenTheme" ? "retro-sage" : "dark");
        }

        private string SelectedTheme()
        {
            return this.FindControl<RadioButton>("LightTheme").IsChecked == true ? "light"
                : this.FindControl<RadioButton>("GreenTheme").IsChecked == true ? "retro-sage" : "dark";
        }

        private void ApplyPreview(string theme)
        {
            AppThemeManager.Apply(Application.Current, theme, false);
            PerformanceChartControl.RefreshThemeResources();
            ComparisonChartControl.RefreshThemeResources();
            (Owner?.DataContext as MainWindowViewModel)?.RefreshThemePresentation();
        }

        private void Confirm(object sender, RoutedEventArgs e)
        {
            string theme = SelectedTheme();
            bool chinese = this.FindControl<RadioButton>("ChineseLanguage").IsChecked == true;
            AppThemeManager.Select(Application.Current, theme);
            AppLanguageManager.SetChinese(chinese);
            bool saved = System.IO.File.Exists(LanguageSettingsStore.DefaultPath)
                && new ThemeSettingsStore().LoadThemeId() == theme
                && new LanguageSettingsStore().LoadChinese() == chinese;
            _confirmed = saved;
            Close(saved);
        }

        private void Cancel(object sender, RoutedEventArgs e) { Close(false); }
    }
}
