namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心「儲存」頁的「掃描磁碟錄影」入口（<see cref="HeliVMS.Storage.RecordingDiskImporter"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 症狀：索引被重建、還原成舊備份、頻道刪掉又重加之後，磁碟上滿是錄影，回放卻一片空白，
/// 日誌乾淨得像什麼都沒發生。匯入服務本身在 Storage 有行為測試（<c>RecordingDiskImporterTests</c>），
/// 這裡只守桌面端最容易漏掉的接線：
/// </para>
/// <list type="number">
/// <item>按鈕真的接到處理常式（XAML 少了 <c>Click=</c> 就只是一個按了沒反應的方塊）。</item>
/// <item>掃描根目錄包含 <c>&lt;dataRoot&gt;\recordings</c>——實機的舊錄影（例如 ch072）就在那裡，
/// 只掃目前根目錄等於永遠看不到它們。</item>
/// <item>掃描在背景執行緒：整顆碟的列舉會讓設定中心「沒有回應」。</item>
/// <item>匯入成功後重新整理用量，否則畫面仍顯示舊的段數與容量。</item>
/// </list>
/// </remarks>
public sealed class RecordingImportContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string ImportCode = "src/HeliVMS.App/RecordingImportWindow.xaml.cs";

    [Fact]
    public void 儲存頁的掃描按鈕接上了處理常式()
    {
        var xaml = SourceContract.Read(SettingsXaml);

        Assert.Contains("AutomationProperties.AutomationId=\"ImportRecordingsButton\"", xaml);
        Assert.Contains("Click=\"OnImportRecordingsClicked\"", xaml);
        Assert.Contains(
            "AutomationProperties.AutomationId=\"ImportRecordingsStatusText\"",
            xaml);

        // 處理常式必須真的存在，否則 XAML 編譯就會失敗——但這裡先給出可讀的訊息。
        Assert.False(string.IsNullOrEmpty(
            SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnImportRecordingsClicked")));
    }

    [Fact]
    public void 掃描包含資料根目錄下的舊錄影並在背景執行()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnImportRecordingsClicked");

        // 實機舊錄影在 <dataRoot>\recordings\<頻道>；少了這條就永遠掃不到。
        Assert.Contains("Path.Combine(_dataRoot, \"recordings\")", body);
        Assert.Contains("RecordingDiskImporter", body);
        Assert.Contains("Scan(", body);

        // 列舉整顆碟可能幾十秒，留在 UI 執行緒就是「沒有回應」。
        Assert.Contains("Task.Run(", body);

        // 掃描失敗不該讓整個設定中心掛掉。
        Assert.Contains("catch", body);
    }

    [Fact]
    public void 匯入後會重新整理用量()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnImportRecordingsClicked");
        var reload = body.IndexOf("ReloadUsage()", StringComparison.Ordinal);

        Assert.True(reload >= 0, "匯入後必須重新整理用量，否則畫面仍顯示舊的段數與容量");

        // 不能只驗字串存在：把呼叫註解掉（// ReloadUsage();）時 IndexOf 仍會命中，
        // 契約就變成裝飾品。這裡要求它是一行真正的敘述句。
        var callLine = body
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("ReloadUsage();", StringComparison.Ordinal));
        Assert.True(callLine is not null, "ReloadUsage() 必須真的被呼叫，不能只是註解");

        // 只有真的補回東西才重算：全部略過時重算一次沒有意義，也讓「有沒有匯入」難以分辨。
        var guard = body.IndexOf("AnyImported", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < reload, "ReloadUsage 必須在「有匯入」的分支裡");
    }

    [Fact]
    public void 匯入畫面只送操作員選定的頻道()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(ImportCode), "OnImportClicked");

        // 篩選與去重只有一份實作（Storage 的 SelectAssignments），畫面不得自己再寫一套。
        Assert.Contains("RecordingDiskImporter.SelectAssignments(", body);
        Assert.Contains(".Import(", body);

        // 預設值來自掃描結果（對不到頻道就是「略過」），不是畫面自己猜一個。
        var ctor = SourceContract.BodyOf(SourceContract.Read(ImportCode), "RecordingImportWindow");
        Assert.Contains("RecordingDiskImporter.DefaultChannelId(", ctor);
    }
}
