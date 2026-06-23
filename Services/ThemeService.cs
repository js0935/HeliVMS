using System.Windows;

namespace HeliVMS.Services;

public sealed class ThemeService(ISettingsService settings) {
    private const string DarkKey = "Dark";
    private const string LightKey = "Light";

    public string CurrentTheme { get; private set; } = DarkKey;

    public void Initialize() {
        var saved = settings.Settings.Theme;
        if (saved is LightKey or DarkKey)
            ApplyTheme(saved);
    }

    public void Toggle() {
        ApplyTheme(CurrentTheme == DarkKey ? LightKey : DarkKey);
    }

    private void ApplyTheme(string theme) {
        if (Application.Current is not Application app) return;
        var dict = app.Resources.MergedDictionaries;
        var colorsIdx = -1;
        for (var i = 0; i < dict.Count; i++) {
            if (dict[i].Source?.ToString().Contains("Colors") == true) {
                colorsIdx = i;
                break;
            }
        }
        if (colorsIdx < 0) return;

        var uri = theme == LightKey
            ? new Uri("pack://application:,,,/Styles/ColorsLight.xaml", UriKind.Absolute)
            : new Uri("pack://application:,,,/Styles/Colors.xaml", UriKind.Absolute);
        dict[colorsIdx] = new ResourceDictionary { Source = uri };

        CurrentTheme = theme;
        settings.Settings.Theme = theme;
        settings.Save();
    }
}
