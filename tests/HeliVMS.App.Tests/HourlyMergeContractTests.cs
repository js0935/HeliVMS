namespace HeliVMS.App.Tests;

/// <summary>
/// 每小時合併（<c>recording.hourly_merge</c>）的設定面契約：開關在設定頁儲存頁籤、
/// 讀寫都走共用的 <c>HourlyMergePolicy</c>，桌面與 Headless Agent 才會用同一個事實來源。
/// </summary>
public sealed class HourlyMergeContractTests
{
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";

    [Fact]
    public void 設定頁有每小時合併的開關並寫入共用設定()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        // 開關存在且切換／載入都接同一個處理常式。
        Assert.Matches(
            @"<CheckBox[^>]*x:Name=""HourlyMergeCheckBox""[^>]*Checked=""OnHourlyMergeToggled""",
            xaml);
        Assert.Contains("Unchecked=\"OnHourlyMergeToggled\"", xaml, StringComparison.Ordinal);

        // 讀寫走共用政策，不在 UI 端另存一份。
        Assert.Contains("HourlyMergePolicy.SettingKey", source);
        Assert.Contains("HourlyMergePolicy.Read(_store)", source);

        // 載入時要抑制事件回寫（IsChecked 指派會觸發 Toggled）；切換時要真的寫入設定。
        var reload = SourceContract.BodyOf(source, "ReloadHourlyMerge");
        Assert.Contains("_suppressHourlyMergeWrite = true", reload);
        var toggle = SourceContract.BodyOf(source, "OnHourlyMergeToggled");
        Assert.Contains("_suppressHourlyMergeWrite", toggle);
        Assert.Contains("_settings.Set(", toggle);
    }

    /// <summary>
    /// 合併會重寫磁碟上的檔案與索引列，主視窗必須把「合併了什麼」講出來，
    /// 否則操作員對備份清單對帳時會以為程式亂刪檔。
    /// </summary>
    [Fact]
    public void 清理輪合併後會在主畫面說明()
    {
        var source = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        Assert.Contains("HourlyMergePolicy.Read(_store!)", source);
        Assert.Contains("Main.MergedHourlySegments", source);
    }
}
