namespace HeliVMS.App.Tests;

/// <summary>
/// IO 與警報規則的分工契約（對照 Home Assistant「裝置／實體」與「自動化」分離）。
/// </summary>
/// <remarks>
/// 現場指示：設定中心 → IO 只負責登記接點（Modbus 模組／通道、攝影機乾接點都算）；
/// DI 觸發後做什麼（產生警報事件、驅動繼電器、帶畫面、截圖、播音、地圖、賦歸）
/// 在警報規則設定。把兩件事放同一頁的後果是雙頭馬車——接點頁改了行為，警報頁
/// 看起來卻像沒生效。
/// </remarks>
public sealed class AlarmIoRuleContractTests
{
    private const string RulesCode = "src/HeliVMS.App/RulesWindow.xaml.cs";
    private const string RulesXaml = "src/HeliVMS.App/RulesWindow.xaml";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Host = "src/HeliVMS.App/Services/CameraDiHost.cs";
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void IO連動規則在警報中心設定()
    {
        // 警報規則視窗有「IO 連動」：清單＋新增／啟停／刪除，輸入接點只列登記過的 DI。
        var xaml = SourceContract.Read(RulesXaml);
        Assert.Contains("x:Name=\"IoRuleList\"", xaml);
        Assert.Contains("x:Name=\"IoRulesEditPanel\"", xaml);

        var source = SourceContract.Read(RulesCode);
        Assert.Contains("new IoRuleRepository(store)", source);
        Assert.Contains("new IoPortRepository(store)", source);
        Assert.Contains("_ioRules.SetEnabled(", source);
        Assert.Contains("_ioRules.Delete(", source);

        // 只認登記過的接點：沒登記的端子選不到，規則才不會指向不存在的硬體。
        Assert.Contains("p.Kind == IoPortKind.Di", source);
        Assert.Contains("p.Kind == IoPortKind.Do", source);
    }

    [Fact]
    public void 設定中心的IO頁只登記接點不設定規則()
    {
        // 接點頁不得出現 io_rules 的存取：規則寫在這裡，警報頁就是空的權威來源。
        Assert.DoesNotContain("IoRuleRepository", SourceContract.Read(SettingsCode));
        Assert.DoesNotContain("IoActionKind", SourceContract.Read(SettingsCode));
    }

    [Fact]
    public void 規則變更會讓執行中的監視器立即生效()
    {
        // 警報視窗帶回呼；主視窗把它接到攝影機 DI 監視器的引擎重建。
        Assert.Contains("ioRulesChanged", SourceContract.BodyOf(SourceContract.Read(Main), "OpenRulesWindow"));
        Assert.Contains("ReloadRules", SourceContract.Read(Host));

        var host = SourceContract.BodyOf(SourceContract.Read(Host), "ReloadRules");
        Assert.Contains("_ruleHost.Reload()", host);
    }

    /// <summary>
    /// 警報動作完整化：DI 觸發後除了事件與繼電器，還能帶畫面、截圖、播音、開地圖、賦歸。
    /// </summary>
    /// <remarks>
    /// 繼電器的「輸出秒數」由宿主計時（閉合 N 秒後自動放開——現場「警報響了停不下來」
    /// 就是沒有這一段）；畫面側的動作由主視窗執行（牆、截圖、地圖都是主視窗的狀態）。
    /// </remarks>
    [Fact]
    public void 警報動作涵蓋畫面截圖播音地圖與繼電器輸出秒數()
    {
        var storage = SourceContract.Read("src/HeliVMS.Storage/IoRuleRepository.cs");
        foreach (var column in new[] { "link_channel_id", "link_fullscreen", "link_snapshot", "link_sound_path", "link_map", "reset_seconds", "output_seconds" })
        {
            Assert.Contains(column, storage);
        }

        var host = SourceContract.Read("src/HeliVMS.Alarms/IoRuleHost.cs");
        Assert.Contains("ScheduleOutputReset", host);

        var main = SourceContract.Read(Main);
        var executor = SourceContract.BodyOf(main, "ExecuteIoLinkAction");
        Assert.Contains("LinkCameraOnWall", executor);
        Assert.Contains("CaptureChannelSnapshot", executor);
        Assert.Contains("SoundPlayer", executor);
        Assert.Contains("OpenMapWindow", executor);
        Assert.Contains("ScheduleLinkReset", executor);

        // 事件怎麼到畫面層：監視器 → 宿主 → 主視窗。
        Assert.Contains("LinkTriggered", SourceContract.Read("src/HeliVMS.Alarms/CameraDiMonitor.cs"));
        Assert.Contains("LinkTriggered", SourceContract.Read(Host));
        Assert.Contains("OnCameraDiLinkTriggered", main);
    }
}
