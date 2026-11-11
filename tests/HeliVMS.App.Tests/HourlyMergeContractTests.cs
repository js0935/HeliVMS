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

    /// <summary>
    /// 合併失敗也要講出來，而且要指得出是哪一個資料夾。
    /// </summary>
    /// <remarks>
    /// 舊版只把失敗數加一，而那個數字沒有任何地方讀它：檔案數沒有變少、日誌乾淨、提示列沉默，
    /// 於是「這個小時永遠不會被併起來」在現場完全是隱形的（實測 33/59 個可合併小時）。
    /// </remarks>
    [Fact]
    public void 清理輪合併失敗會在主畫面說明並指出資料夾()
    {
        var source = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        Assert.Contains("Main.HourlyMergeFailed", source);
        Assert.Contains("sweep.MergeFailures", source);
        Assert.Contains("ShortFolderLabel", source);
    }

    /// <summary>
    /// Headless 服務模式沒有提示列：合併失敗只能寫進 log，否則維運完全看不到。
    /// </summary>
    /// <remarks>
    /// 這裡刻意不用「原始碼裡有『小時合併失敗』這幾個字」當守門條件：那個字串上面的
    /// 說明註解本身就含有同樣的字，於是整段 log 被刪掉、測試還是綠的（本 repo 已經發生過
    /// 一次：磁碟滿的守門條件被自己的註解滿足）。所以改釘「訊息模板（含預留位置）」與
    /// 「真的有把資料夾寫出來」——這兩者只可能出現在程式碼裡。
    /// </remarks>
    [Fact]
    public void Agent的保留清理會把合併失敗寫進log()
    {
        var source = SourceContract.Read("src/HeliVMS.Agent/RetentionWorker.cs");
        Assert.Contains("sweep.MergeFailures", source);
        Assert.Contains("小時合併失敗 {Failed}", source);
        Assert.Contains("mergeFailures[0].FolderPath", source);
    }
}
