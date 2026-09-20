using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 介面語言地（M57）：目前語言由 app_settings `ui.lang`（權威來源）決定；
/// 提供 `T(key)` 查詢現況語言的字串。App 啟動時以 `Init` 載入，切換時以 `SetLang` 寫回並更新。
/// </summary>
public static class Localizer
{
    public static string Lang { get; private set; } = I18n.DefaultLang;

    /// <summary>依 app_settings 載入目前語言（未設定→繁中）。</summary>
    public static void Init(SettingsRepository settings)
    {
        Lang = I18n.Load(settings);
    }

    /// <summary>現況語言下的字串。</summary>
    public static string T(string key)
        => I18n.Get(Lang, key);

    /// <summary>切換語言並寫回 app_settings（權威來源）。</summary>
    public static void SetLang(SettingsRepository? settings, string lang)
    {
        Lang = I18n.Normalize(lang);
        settings?.Set(I18n.SettingKey, Lang);
    }
}