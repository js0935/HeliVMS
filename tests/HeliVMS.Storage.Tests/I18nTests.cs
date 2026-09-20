namespace HeliVMS.Storage.Tests;

/// <summary>M57（§14.7 P1 多國語言）：字串表、fallback 與 ui.lang 持久化。</summary>
public class I18nTests : IDisposable
{
    private readonly string _dbPath;
    private SqliteStore _store;

    public I18nTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-i18n-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Normalize_UnknownOrBlank_FallsBackToTraditionalChinese()
    {
        Assert.Equal(I18n.DefaultLang, I18n.Normalize(null));
        Assert.Equal(I18n.DefaultLang, I18n.Normalize(""));
        Assert.Equal(I18n.DefaultLang, I18n.Normalize("fr"));
        Assert.Equal(I18n.DefaultLang, I18n.Normalize("zh"));
    }

    [Fact]
    public void Get_TraditionalChinese_ReturnsBrandTitle()
    {
        Assert.Equal("HeliVMS 監控中心", I18n.Get("zh-Hant", "Brand.Title"));
        Assert.Contains("設定中心", I18n.Get("zh-Hant", "Settings.Title"));
    }

    [Fact]
    public void Get_English_ReturnsLocalizedValue()
    {
        Assert.Equal("HeliVMS Surveillance Center", I18n.Get("en", "Brand.Title"));
        Assert.Equal("Refresh", I18n.Get("en", "EventCenter.Refresh"));
        Assert.Equal("Export CSV", I18n.Get("en", "EventCenter.ExportCsv"));
    }

    [Fact]
    public void Get_Simplified_ReturnsSimplifiedValue()
    {
        Assert.Equal("HeliVMS 监控中心", I18n.Get("zh-Hans", "Brand.Title"));
        Assert.Equal("刷新", I18n.Get("zh-Hans", "EventCenter.Refresh"));
        Assert.NotEqual(
            I18n.Get("zh-Hant", "EventCenter.Apply"),
            I18n.Get("zh-Hans", "EventCenter.Apply"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsKeyItself()
    {
        Assert.Equal("Brand.MissingKey", I18n.Get("en", "Brand.MissingKey"));
        Assert.Equal("Nope", I18n.Get("zh-Hant", "Nope"));
    }

    [Fact]
    public void ForLang_EveryLanguageCoversTraditionalKeys()
    {
        var baseKeys = new HashSet<string>(I18n.ForLang("zh-Hant").Keys);
        foreach (var lang in I18n.Languages)
        {
            var langKeys = new HashSet<string>(I18n.ForLang(lang).Keys);
            Assert.Superset(baseKeys, langKeys);
        }
    }

    [Fact]
    public void UiLang_PersistsViaSettingsRepository()
    {
        var settings = new SettingsRepository(_store);
        Assert.Equal(I18n.DefaultLang, I18n.Load(settings));
        settings.Set(I18n.SettingKey, "en");
        Assert.Equal("en", I18n.Load(settings));
        settings.Set(I18n.SettingKey, "zh-Hans");
        Assert.Equal("zh-Hans", I18n.Load(settings));
    }
}