using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心「儲存」頁的剩餘可錄時間（QCV8001 對照的落地，見 <c>docs/STORAGE-ALIGNMENT.md</c> 缺口 1）。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端沒有 WPF 執行期測試，但這一塊的錯誤都是<b>不會報錯</b>的那種：
/// 顯示成 0（其實是「沒有寫入樣本」）、永遠不更新（掛錯地方）、或者兩個畫面各自算一份
/// （同一顆碟兩個數字）。所以這裡釘的是接線與文案口徑，算術本身在
/// <c>RecordingCapacityTests</c> 與 <c>RecordingCapacityServiceTests</c>。
/// </para>
/// </remarks>
public sealed class RecordingCapacityContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string RetentionCode = "src/HeliVMS.Recording/RetentionService.cs";

    private static readonly string[] Keys =
    [
        "Settings.RemainingRecordText",
        "Settings.RemainingRecordTextMulti",
        "Settings.RemainingRecordDays",
        "Settings.RemainingRecordHours",
        "Settings.RemainingRecordCompactDays",
        "Settings.RemainingRecordCompactHours",
        "Settings.RemainingRecordNone",
        "Settings.RemainingRecordUnknown",
        "Settings.RemainingRecordAtReserve",
        "Settings.RemainingRecordUnavailable",
        "Settings.PlanHeading",
        "Settings.PlanChannelsLabel",
        "Settings.PlanMbpsLabel",
        "Settings.PlanDaysLabel",
        "Settings.PlanDiskSizeLabel",
        "Settings.PlanExistingDisksLabel",
        "Settings.PlanCompute",
        "Settings.PlanComputeTip",
        "Settings.PlanRaidNone",
        "Settings.PlanRaidMirror",
        "Settings.PlanRaid5",
        "Settings.PlanRaid6",
        "Settings.PlanInvalid",
        "Settings.PlanResult",
        "Settings.PlanDisks",
        "Settings.PlanDisksHave",
        "Settings.PlanDisksShortfall",
        "Settings.PlanDisksOk",
    ];

    [Fact]
    public void 容量規劃用純函式且輸入不合理時不編數字()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        Assert.Matches(@"<Button[^>]*x:Name=""PlanComputeButton""[^>]*Click=""OnPlanComputeClicked""", xaml);

        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnPlanComputeClicked");

        // 規劃是「還沒有機器可以量」的場合：吃輸入碼率，且與 §11.2 的算式共用同一個純函式。
        Assert.Contains("RecordingCapacityPlanner.Plan", body);
        Assert.Contains("RecordingCapacityPlanner.DisksRequired", body);
        Assert.Contains("RecordingCapacityPlanner.Evaluate", body);
        Assert.Contains("Settings.PlanInvalid", body);
        Assert.Contains("Settings.PlanDisksShortfall", body);

        // RAID 容損耗是採購時最常被忘掉的一項：有填「手上已有幾顆」就要走 Evaluate 並回報不足。
        Assert.Contains("PlanExistingDisksBox", body);
    }

    [Fact]
    public void 儲存頁有剩餘可錄時間且跟著用量重新載入()
    {
        Assert.Contains("x:Name=\"RemainingRecordText\"", SourceContract.Read(SettingsXaml));

        // 掛在 ReloadUsage 的尾巴：用量重新整理時它才會跟著更新（設定頁開頁、按下保留空間、
        // 清理之後都會走到那裡）。少那一行，畫面就永遠停在開頁那一刻的數字。
        Assert.Contains("ReloadRemainingRecordTime()", SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadUsage"));
    }

    [Fact]
    public void 用服務層的同一份實作與同一個量測窗()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadRemainingRecordTime");

        Assert.Contains("RecordingCapacityService.Measure", body);
        Assert.Contains("RecordingCapacityService.DefaultWindow", body);
        Assert.Contains("_recordingsRoot", body);
    }

    [Fact]
    public void 未知顯示破折號而低於保留空間才顯示零()
    {
        var source = SourceContract.Read(SettingsCode);
        var body = SourceContract.BodyOf(source, "ReloadRemainingRecordTime");
        var describe = SourceContract.BodyOf(source, "DescribeRemaining");

        // 「沒有樣本」與「已低於保留空間」的處置完全相反，畫面必須分得出來。
        Assert.Contains("RecordingCapacityState.AtReserve => Localizer.T(\"Settings.RemainingRecordAtReserve\")", body);
        Assert.Contains("RecordingCapacityState.Unknown => string.Format(", body);
        Assert.Contains("Localizer.T(\"Settings.RemainingRecordUnknown\")", body);

        // 逐顆清單也一樣：未知是「—」，只有真的低於保留空間才是 0。
        Assert.Contains("Settings.RemainingRecordNone", describe);
        Assert.Contains("RecordingCapacityState.AtReserve => \"0\"", describe);
    }

    [Fact]
    public void 剩餘時間的三語文案齊備()
    {
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in Keys)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }

    [Fact]
    public void 寫入量只有一份實作()
    {
        // 壓力汰除與剩餘可錄時間問的是同一件事（這顆碟最近寫了多少）。
        // 兩份實作會讓同一顆碟在兩個畫面上出現兩個數字，而操作員只能兩個都不信。
        var retention = SourceContract.Read(RetentionCode);
        Assert.Contains("RecordingVolume.WrittenBytes", SourceContract.BodyOf(retention, "HourlyWriteEstimate"));
        Assert.DoesNotContain("ListFinalBetween", retention);
    }
}
