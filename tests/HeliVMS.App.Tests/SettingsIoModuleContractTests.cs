using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 「新增一顆 ADAM-6250」在桌面端應該是一個動作：選機型、填位址、按下新增，
/// 然後 8 個 DI／8 個 DO 就出現在 IO 頁面上、可以掛警報規則、可以放上地圖。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要釘住這幾行：舊表單只呼叫 <c>IoRepository.AddDevice</c>，只寫 <c>io_devices</c>
/// 而沒有 <c>io_channels</c>，所以「模組加好了，接點一條都沒有」——兩張表都寫入成功，
/// 沒有任何錯誤訊息，操作員只能懷疑是不是自己少填了什麼。而且舊版還要求 DI 必須先綁攝影機，
/// 等於「想用接點掛規則，得先有一台攝影機」，與現場「一顆 ADAM 放在電箱裡」的用法相反。
/// </para>
/// <para>
/// 桌面專案沒有 WPF 執行期測試環境，這裡掃原始碼；行為本身（Enroll 建通道、Mirror 建接點、
/// 端點到規則）由 <c>IoModuleEnrollerTests</c> 與 <c>IoModuleEndToEndTests</c> 真的跑過。
/// 這一條守的是「桌面端真的接上那兩個實作」，接錯或漏接都不會有例外，只會少幾個接點。
/// </para>
/// </remarks>
public sealed class SettingsIoModuleContractTests
{
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";

    [Fact]
    public void IO頁可以直接選機型並覆寫DI與DO點數()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"IoModelCombo\"", xaml);
        Assert.Contains("SelectionChanged=\"OnIoModelChanged\"", xaml);
        Assert.Contains("x:Name=\"IoDiCountBox\"", xaml);
        Assert.Contains("x:Name=\"IoDoCountBox\"", xaml);

        // 這一行是「DI 綁攝影機是選用的」唯一的說明處；沒有它，操作員會以為留白就建不出接點。
        Assert.Contains("x:Name=\"IoChannelHint\"", xaml);

        // XAML 不得出現中文（三語字典才是字串來源）。
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", xaml);
    }

    [Fact]
    public void 新增模組會一次建好通道並立刻登記成接點()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnIoAddDeviceClicked");

        // 一次呼叫＝模組 ＋ 它的 DI/DO 通道；只建模組就是「接點一條都沒有」的舊缺陷。
        Assert.Contains("IoModuleEnroller.Enroll(", body);

        // 通道要立刻變成接點：警報規則與地圖只認 io_ports。
        Assert.Contains("IoPortMirror.Sync(", body);
        Assert.Contains("RefreshAndStart(", body);

        // 剛建立的模組要選取起來，否則清單列出「全部模組」的接點，剛加的 16 點混在裡面，
        // 看起來像沒有全部建立。
        Assert.Contains("SelectIoDevice(result.DeviceId)", body);
    }

    [Fact]
    public void 模組DI不必先綁攝影機()
    {
        var code = SourceContract.Read(Code);

        // 綁攝影機只決定「觸發要不要寫進事件中心」，不是新增通道的前提。
        Assert.DoesNotContain("Settings.DiNeedsCamera", code);

        // 頁面上要明講這件事，否則操作員會以為不選攝影機就建不出接點。
        var apply = SourceContract.BodyOf(code, "ApplyI18n");
        Assert.Contains("IoChannelHint.Text = Localizer.T(\"Settings.IoChannelHint\")", apply);
    }

    [Fact]
    public void 換機型會帶入該機型的連線參數與點數()
    {
        var code = SourceContract.Read(Code);
        var body = SourceContract.BodyOf(code, "ApplyIoProfileDefaults");

        Assert.Contains("profile.DefaultPort", body);
        Assert.Contains("profile.DefaultUnitId", body);
        Assert.Contains("profile.DefaultDiCount", body);
        Assert.Contains("profile.DefaultDoCount", body);

        // 程式自己重建機型下拉時不能把點數洗回預設值：那會讓「填好 16DI／0DO 再按新增」
        // 建出 8／8 的模組，而畫面一切正常。
        var reload = SourceContract.BodyOf(code, "ReloadIoModelCombo");
        Assert.Contains("_suppressIoModelChanged = true", reload);
    }

    [Fact]
    public void 點數留白用機型預設而壞字會被擋下()
    {
        var code = SourceContract.Read(Code);
        var body = SourceContract.BodyOf(code, "TryParsePointCount");

        // 留白＝null（用機型預設）；填了壞字要回 false 讓呼叫端擋下來，
        // 而不是默默建出 8 點的模組。
        Assert.Contains("count = null", body);
        Assert.Contains("return false", body);

        var add = SourceContract.BodyOf(code, "OnIoAddDeviceClicked");
        Assert.Contains("Settings.PointCountInvalid", add);
    }

    [Fact]
    public void 規則與地圖上的接點都帶上來源名稱()
    {
        // 拼字串只有一份實作（Shared.IoContactLabel）；三個畫面各自拼一份遲早漂移，
        // 症狀是同一個接點在規則裡叫 A、在地圖上叫 B，兩邊都沒有錯誤訊息。
        var rules = SourceContract.Read("src/HeliVMS.App/RulesWindow.xaml.cs");
        Assert.Contains("IoContactLabel.ModuleChannel(", rules);
        Assert.Contains("IoContactLabel.Port(", rules);

        // 要知道接點屬於哪一顆模組／哪一台攝影機，就得查得到那兩張表。
        Assert.Contains("new IoRepository(", rules);
        Assert.Contains("_ioChannelById", rules);

        Assert.Contains(
            "IoContactLabel.ModuleChannel(",
            SourceContract.Read("src/HeliVMS.App/MapWindow.xaml.cs"));
        Assert.Contains(
            "IoContactLabel.ModuleChannel(",
            SourceContract.Read(Code));
    }

    [Fact]
    public void IO頁有即時狀態與連線欄而且只在頁面可見時更新()
    {
        var xaml = SourceContract.Read(Xaml);
        var code = SourceContract.Read(Code);

        // 兩個新欄位：模組的「連線」（最近一次 Modbus 輪詢結果）與接點的「狀態」（目前的開關）。
        // 加了模組卻填錯 IP，症狀是「接點建好了但永遠沒有事件」——沒有這兩欄就只能在日誌裡找答案。
        Assert.Contains("x:Name=\"IoColLink\"", xaml);
        Assert.Contains("x:Name=\"IoColState\"", xaml);

        // 狀態來源是監視器（不是再開一條 Modbus 連線）：接錯來源會出現第二個輪詢者搶同一台模組。
        Assert.Contains("_ioHost?.PollStatus(", code);
        Assert.Contains("_ioHost?.States(", code);

        // 節拍只在 IO 頁可見時做事；關窗要停掉（設定中心可能開很久，計時器不留著空轉）。
        var tick = SourceContract.BodyOf(code, "OnIoStateTimerTick");
        Assert.Contains("PageIo.Visibility != Visibility.Visible", tick);
        Assert.Contains("RefreshIoLiveState()", tick);

        var closed = SourceContract.BodyOf(code, "OnClosed");
        Assert.Contains("_ioStateTimer.Stop()", closed);
    }

    [Fact]
    public void IO頁即時狀態欄沒有狀態時不會重建清單()
    {
        // 每秒重建 ItemsSource 會讓清單的選取與捲動位置每秒跳一次；
        // 狀態沒變就什麼都不做（用 record 的值相等比較當「有沒有變」的判準）。
        var refresh = SourceContract.BodyOf(SourceContract.Read(Code), "RefreshIoLiveState");

        Assert.Contains("signature == _ioStateSignature", refresh);
        Assert.Contains("with { LinkLabel =", refresh);
        Assert.Contains("with { StateLabel =", refresh);
    }

    [Fact]
    public void 修改接點是更新而不是刪除重加()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnIoApplyChannelClicked");

        // id 不變的更新：刪除重加會讓鏡射的接點（io_ports）換 id，指向它的 io_rules 從此
        // 默默失效——症狀是「規則還在但永遠不觸發」，兩張表都寫入成功、沒有任何錯誤。
        Assert.Contains("_io.UpdateChannel(", body);
        Assert.DoesNotContain("_io.DeleteChannel(", body);
        Assert.DoesNotContain("_io.AddChannel(", body);

        // io_ports 的名稱／極性與監視器的去抖要跟上，否則規則顯示舊名、新的去抖要等重啟。
        Assert.Contains("RefreshAndStart(", body);
    }

    [Fact]
    public void IO頁新增的字串三語齊備()
    {
        string[] keys =
        [
            "Settings.IoModelLabel",
            "Settings.IoModelTip",
            "Settings.IoNameLabel",
            "Settings.IoHostLabel",
            "Settings.IoPortLabel",
            "Settings.IoUnitLabel",
            "Settings.IoUnitTip",
            "Settings.IoPollLabel",
            "Settings.IoDiCountLabel",
            "Settings.IoDiCountTip",
            "Settings.IoDoCountLabel",
            "Settings.IoDoCountTip",
            "Settings.IoChannelDirLabel",
            "Settings.IoChannelIndexLabel",
            "Settings.IoChannelDebounceLabel",
            "Settings.IoChannelCameraLabel",
            "Settings.IoChannelHint",
            "Settings.ModuleEnrolled",
            "Settings.EnrollCreated",
            "Settings.EnrollToppedUp",
            "Settings.PointCountInvalid",
            "Settings.IoLinkHeader",
            "Settings.IoStateHeader",
            "Settings.IoOnline",
            "Settings.IoOffline",
            "Settings.IoPolling",
            "Settings.IoStateOn",
            "Settings.IoStateOff",
            "Settings.IoApplyChannel",
            "Settings.ChannelUpdated",
        ];

        // 不靠 I18n.Get 的繁中 fallback：那會讓「英文缺鍵」看起來有翻譯。
        foreach (var lang in I18n.Languages)
        {
            var table = I18n.ForLang(lang);
            foreach (var key in keys)
            {
                Assert.True(table.ContainsKey(key), $"{lang} 缺 {key}");
                Assert.False(string.IsNullOrWhiteSpace(table[key]), $"{lang} 的 {key} 是空白");
            }
        }
    }
}
