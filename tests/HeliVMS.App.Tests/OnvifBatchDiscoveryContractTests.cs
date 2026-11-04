namespace HeliVMS.App.Tests;

/// <summary>
/// ONVIF 搜尋與批次加入的桌面接線。
///
/// <para>
/// 現場痛點是「一台一台輸入 IP」：多播 WS-Discovery 在 VLAN／VPN 常被擋掉，搜尋不到就只能
/// 手打位址，而且加完一台還要再開一次精靈。這裡釘住三件事：搜尋欄位要能吃整段位址並走
/// 掃描路徑、名單要有勾選與批次加入、單台與批次都從同一份候選清單出去
/// （呼叫端只有一條「逐台命名＋寫入」的路）。
/// </para>
/// <para>
/// 掃描與解析本身是行為測試（<c>HeliVMS.Devices.Tests</c> 的 DiscoveryAddressRange／
/// DiscoverySweep／OnvifEnrollmentPlanner）；桌面端沒有 WPF 執行期測試，所以這裡掃原始碼。
/// </para>
/// </summary>
public sealed class OnvifBatchDiscoveryContractTests
{
    private const string WizardCode = "src/HeliVMS.App/OnvifWizardWindow.xaml.cs";
    private const string WizardXaml = "src/HeliVMS.App/OnvifWizardWindow.xaml";
    private const string ChannelsCode = "src/HeliVMS.App/ChannelManagerWindow.xaml.cs";
    private const string CamerasCode = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 搜尋欄位接受整段位址並走掃描路徑()
    {
        var discover = SourceContract.BodyOf(SourceContract.Read(WizardCode), "OnDiscoverClicked");

        // 位址欄的字串要先經過展開：單一 IP 仍走原本的逐候選探測，多個位址才走掃描。
        Assert.Contains("DiscoveryAddressRange.Expand", discover);
        Assert.Contains("RunSweepAsync", discover);
        Assert.Contains("expansion.Addresses.Count == 1", discover);

        var sweep = SourceContract.BodyOf(SourceContract.Read(WizardCode), "RunSweepAsync");
        Assert.Contains("DiscoverySweep.RunAsync", sweep);
        Assert.Contains("Onvif.Sweeping", sweep);
        // 超過上限要說出來，否則操作員以為整段都掃過了。條件式本身也要在（只看字串的話，
        // 把 if (expansion.Truncated) 改成 if (false) 也照樣通過）。
        Assert.Contains("expansion.Truncated", sweep);
        Assert.Contains("Onvif.SweepTruncated", sweep);
        // 掃描用的短逾時探測（不是單台那條 42 個候選、每個 10 秒的路徑）。
        Assert.Contains("DiscoveryClient.ProbeAddressAsync", sweep);
    }

    [Fact]
    public void 名單每列可勾選且批次與全選鈕有接線()
    {
        var xaml = SourceContract.Read(WizardXaml);

        Assert.Contains("x:Name=\"BatchAddButton\"", xaml);
        Assert.Contains("Click=\"OnBatchAddClicked\"", xaml);
        Assert.Contains("x:Name=\"SelectAllButton\"", xaml);
        Assert.Contains("Click=\"OnSelectAllClicked\"", xaml);
        // 勾選框在列樣板裡（不是靠 Ctrl+點選多選：現場是十幾台要一次全加）。
        Assert.Contains("IsChecked=\"{Binding IsChecked, Mode=TwoWay}\"", xaml);
        // 位址欄下方要有看得見的說明（只放 tooltip 等於沒人知道可以打網段）。
        Assert.Contains("x:Name=\"OnvifAddressHint\"", xaml);

        var buttons = SourceContract.BodyOf(SourceContract.Read(WizardCode), "UpdateBatchButtons");
        Assert.Contains("_rows.Any(r => r.IsChecked)", buttons);
    }

    [Fact]
    public void 批次加入走共用解析器且全部失敗時不關窗()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(WizardCode), "OnBatchAddClicked");

        // 解析邏輯在 HeliVMS.Devices（有行為測試），桌面端只負責顯示與回報。
        // 比對整句呼叫而不是型別名稱：註解裡就有型別名稱，光比對它會被註解滿足。
        Assert.Contains("new OnvifEnrollmentPlanner().RunAsync(", body);
        Assert.Contains("r.IsChecked", body);
        // 一台都沒成功就留在視窗上、把原因寫出來；全部成功或部分成功才交出去。
        Assert.Contains("_candidates.Count == 0", body);
        Assert.Contains("Onvif.BatchAllFailed", body);
        Assert.Contains("Onvif.BatchPartial", body);
        // 進度要說「輪到哪一台」。
        Assert.Contains("Onvif.BatchProgress", body);
    }

    [Fact]
    public void 單台與批次都從同一份候選清單出去()
    {
        var wizard = SourceContract.Read(WizardCode);

        // 精靈只有一個出口：呼叫端不必分辨「這是單台還是批次」。
        Assert.Contains("IReadOnlyList<OnvifEnrollmentCandidate> Candidates", wizard);

        var channels = SourceContract.BodyOf(SourceContract.Read(ChannelsCode), "OnOnvifWizardClicked");
        Assert.Contains("foreach (var candidate in wizard.Candidates)", channels);
        Assert.Contains("candidate.MainStreamUrl", channels);
        Assert.Contains("ChannelEnrollment.Enroll(", channels);
        // 逐台命名：後一台要看得到前一台佔用的名稱（否則十台同型號會同名）。
        Assert.Contains("NextChannelName(candidate.SuggestedName)", channels);

        var cameras = SourceContract.BodyOf(SourceContract.Read(CamerasCode), "OnOnvifWizardClicked");
        Assert.Contains("foreach (var candidate in wizard.Candidates)", cameras);
        Assert.Contains("ChannelEnrollment.EnrollDevice(", cameras);
        Assert.Contains("NextDeviceName(candidate.SuggestedName)", cameras);
    }

    [Fact]
    public void 掃描與批次加入的文案三語都在()
    {
        var i18n = SourceContract.Read(I18n);
        string[] keys =
        [
            "Onvif.AddressHint", "Onvif.Sweeping", "Onvif.SweepTruncated",
            "Onvif.BatchAdd", "Onvif.BatchAddTip", "Onvif.SelectAll", "Onvif.SelectNone",
            "Onvif.SelectAllTip", "Onvif.BatchNoneSelected", "Onvif.BatchProgress",
            "Onvif.BatchAllFailed", "Onvif.BatchPartial",
            "Channels.EnrolledBatch", "Channels.EnrolledBatchPartial",
            "Camera.EnrolledBatch", "Camera.EnrolledBatchPartial", "Camera.DefaultName",
        ];

        foreach (var key in keys)
        {
            var lines = i18n.Split('\n')
                .Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal))
                .ToList();

            // 三語各一行；少一語，該語系的操作員會看到空白的按鈕或提示。
            Assert.True(lines.Count == 3, $"{key} 應該有三語，實際 {lines.Count}");
        }

        // 帶參數的文案少了 {0} 等於一句沒有內容的話。
        foreach (var key in new[] { "Onvif.Sweeping", "Onvif.BatchProgress", "Onvif.BatchPartial" })
        {
            var lines = i18n.Split('\n').Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal));
            Assert.All(lines, line => Assert.Contains("{0}", line));
        }
    }
}
