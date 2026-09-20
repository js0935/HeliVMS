namespace HeliVMS.Storage;

/// <summary>
/// 多語言字串表與語言工具（M57，§14.7 P1「多國語言」）。
/// 支援 zh-Hant／zh-Hans／en；缺 key 時 fallback 繁中。設定鍵＝app_settings `ui.lang`（權威來源）。
/// </summary>
public static class I18n
{
    public const string SettingKey = "ui.lang";

    public const string DefaultLang = "zh-Hant";

    public static readonly string[] Languages = ["zh-Hant", "zh-Hans", "en"];

    private static readonly IReadOnlyDictionary<string, string> zhHant = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Brand.Title"] = "HeliVMS 監控中心",
        ["EventCenter.Title"] = "HeliVMS 事件中心",
        ["AlarmManager.Title"] = "HeliVMS 警報管理器",
        ["Settings.Title"] = "HeliVMS 設定中心",
        ["EventCenter.Channel"] = "頻道",
        ["EventCenter.Range"] = "範圍",
        ["EventCenter.Type"] = "類型",
        ["EventCenter.Keyword"] = "關鍵字",
        ["EventCenter.Apply"] = "套用",
        ["EventCenter.Refresh"] = "重新整理",
        ["EventCenter.ExportCsv"] = "匯出 CSV",
        ["EventCenter.Today"] = "今天",
        ["EventCenter.Hours24"] = "24 小時",
        ["EventCenter.Days7"] = "7 天",
        ["EventCenter.All"] = "全部",
        ["EventCenter.AllChannels"] = "全部頻道",
        ["EventCenter.AllTypes"] = "全部類型",
        ["Settings.Language"] = "介面語言",
        ["Settings.LangHint"] = "切換即寫入，新開視窗以新語言顯示。",
    };

    private static readonly IReadOnlyDictionary<string, string> zhHans = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Brand.Title"] = "HeliVMS 监控中心",
        ["EventCenter.Title"] = "HeliVMS 事件中心",
        ["AlarmManager.Title"] = "HeliVMS 警报管理器",
        ["Settings.Title"] = "HeliVMS 设置中心",
        ["EventCenter.Channel"] = "频道",
        ["EventCenter.Range"] = "范围",
        ["EventCenter.Type"] = "类型",
        ["EventCenter.Keyword"] = "关键字",
        ["EventCenter.Apply"] = "应用",
        ["EventCenter.Refresh"] = "刷新",
        ["EventCenter.ExportCsv"] = "导出 CSV",
        ["EventCenter.Today"] = "今天",
        ["EventCenter.Hours24"] = "24 小时",
        ["EventCenter.Days7"] = "7 天",
        ["EventCenter.All"] = "全部",
        ["EventCenter.AllChannels"] = "全部频道",
        ["EventCenter.AllTypes"] = "全部类型",
        ["Settings.Language"] = "界面语言",
        ["Settings.LangHint"] = "切换即写入，新开窗口以新语言显示。",
    };

    private static readonly IReadOnlyDictionary<string, string> en = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Brand.Title"] = "HeliVMS Surveillance Center",
        ["EventCenter.Title"] = "HeliVMS Event Center",
        ["AlarmManager.Title"] = "HeliVMS Alarm Manager",
        ["Settings.Title"] = "HeliVMS Settings",
        ["EventCenter.Channel"] = "Channel",
        ["EventCenter.Range"] = "Range",
        ["EventCenter.Type"] = "Type",
        ["EventCenter.Keyword"] = "Keyword",
        ["EventCenter.Apply"] = "Apply",
        ["EventCenter.Refresh"] = "Refresh",
        ["EventCenter.ExportCsv"] = "Export CSV",
        ["EventCenter.Today"] = "Today",
        ["EventCenter.Hours24"] = "24 hours",
        ["EventCenter.Days7"] = "7 days",
        ["EventCenter.All"] = "All",
        ["EventCenter.AllChannels"] = "All channels",
        ["EventCenter.AllTypes"] = "All types",
        ["Settings.Language"] = "Language",
        ["Settings.LangHint"] = "Saved; newly opened windows use the new language.",
    };

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.Ordinal)
    {
        ["zh-Hant"] = zhHant,
        ["zh-Hans"] = zhHans,
        ["en"] = en,
    };

    /// <summary>語言代碼是否受支援。</summary>
    public static bool IsSupported(string? lang)
        => !string.IsNullOrEmpty(lang) && Tables.ContainsKey(lang);

    /// <summary>正規化語言代碼；未知或空白時回傳繁中。</summary>
    public static string Normalize(string? lang)
        => IsSupported(lang) ? lang! : DefaultLang;

    /// <summary>於資料根目錄依鍵讀取語言（未設定→繁中）。</summary>
    public static string Load(SettingsRepository settings)
        => Normalize(settings.Get(SettingKey));

    /// <summary>取得指定語言的字串；缺 key 時 fallback 繁中，再缺時回傳 key。</summary>
    public static string Get(string lang, string key)
    {
        var normalized = Normalize(lang);
        if (Tables.TryGetValue(normalized, out var table) && table.TryGetValue(key, out var value))
        {
            return value;
        }

        if (zhHant.TryGetValue(key, out var zh))
        {
            return zh;
        }

        return key;
    }

    /// <summary>指定語言的字串表（僅含既有 key；每語言應與 zh-Hant 鍵集合相同）。</summary>
    public static IReadOnlyDictionary<string, string> ForLang(string lang)
        => Tables.TryGetValue(Normalize(lang), out var table) ? table : zhHant;
}