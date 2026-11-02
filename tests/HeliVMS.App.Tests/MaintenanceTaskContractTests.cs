namespace HeliVMS.App.Tests;

/// <summary>
/// 排程任務的接線契約（§14）：到點判定在 Shared 規劃器（行為測試在 Shared.Tests），
/// 執行在 App 的宿主，編輯在警報規則視窗。這裡釘住「接線」與兩條現場安全規則——
/// 先記後跑（斷電不會陷入 30 秒開機循環）、稽核先於動作（重開機後行程消失，事後寫的稽核永遠寫不到）。
/// WPF 無執行期測試，來源掃描是既有慣例（見 SingleInstanceContractTests 的說明）。
/// </summary>
public sealed class MaintenanceTaskContractTests
{
    private const string Host = "src/HeliVMS.App/Services/MaintenanceTaskHost.cs";
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Rules = "src/HeliVMS.App/RulesWindow.xaml.cs";
    private const string Repo = "src/HeliVMS.Storage/ScheduledTaskRepository.cs";
    private const string Planner = "src/HeliVMS.Shared/MaintenanceTaskPlanner.cs";

    [Fact]
    public void 主視窗會建立並啟動排程任務宿主且關窗時收掉()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Main), "OnLoaded");
        Assert.Contains("new MaintenanceTaskHost(", body);
        Assert.Contains("_taskHost.Start()", body);

        // 重開機的收尾與操作員按 ✕ 走同一條路：Close() 觸發 OnWindowClosed 的收尾序列。
        Assert.Contains("RebootRequested", body);
        Assert.Contains("Close", body);
        Assert.Contains("ReconnectHandler", body);

        // 宿主綁著計時器與閘門，關窗不收就是洩漏（對照巡邏宿主的收法）。
        var closed = SourceContract.BodyOf(SourceContract.Read(Main), "OnWindowClosed");
        Assert.Contains("_taskHost?.Dispose()", closed);
        Assert.Contains("_taskHost = null", closed);
    }

    [Fact]
    public void 執行任務前必須先記下last_run()
    {
        // 事件：宿主在執行重開機途中死掉（斷電、當機），若「跑完才記」，
        // 下一輪 30 秒的輪詢又判定到點，機器就陷入 30 秒一次的開機循環。
        var body = SourceContract.BodyOf(SourceContract.Read(Host), "RunTaskAsync");
        var markAt = body.IndexOf("MarkRunStarted", StringComparison.Ordinal);
        var dispatchAt = body.IndexOf("switch", StringComparison.Ordinal);
        Assert.True(markAt >= 0, "RunTaskAsync 必須呼叫 MarkRunStarted");
        Assert.True(dispatchAt > markAt, "MarkRunStarted 必須在動手執行（switch 分派）之前");
    }

    [Fact]
    public void 重開機先寫稽核再排定系統重開機()
    {
        // 重開機之後這個行程就不在了：稽核若排在動作之後，「機器為什麼自己重開機」永遠查不到。
        var body = SourceContract.BodyOf(SourceContract.Read(Host), "RunReboot");
        var auditAt = body.IndexOf("_audit.Record", StringComparison.Ordinal);
        var startAt = body.IndexOf("Process.Start", StringComparison.Ordinal);
        Assert.True(auditAt >= 0, "重開機必須寫稽核");
        Assert.True(startAt > auditAt, "稽核必須寫在 Process.Start 之前");

        // shutdown /r /t N：排定的是「重開機」而不是關機，且留寬限讓本程式優雅收尾。
        Assert.Contains("\"/r\"", body);
        Assert.Contains("\"/t\"", body);
        Assert.Contains("RebootGraceSeconds", body);
        Assert.Contains("RebootRequested", body);
    }

    [Fact]
    public void 到點判定一律經過共享規劃器()
    {
        // 宿主不得自帶第二份「什麼時候該跑」的邏輯：兩份判定一定漂移，
        // 漂移的方向通常是「多跑一次重開機」。
        var body = SourceContract.BodyOf(SourceContract.Read(Host), "EvaluateAsync");
        Assert.Contains("MaintenanceTaskPlanner.IsDue", body);
    }

    [Fact]
    public void 重開機不能用每隔週期且倉儲會擋下()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Repo), "Validate");
        Assert.Contains("MaintenanceTaskKind.Reboot", body);
        Assert.Contains("MaintenanceScheduleKind.Interval", body);
        Assert.Contains("throw", body);
    }

    [Fact]
    public void 警報規則視窗提供排程任務的編輯與清單()
    {
        var source = SourceContract.Read(Rules);
        Assert.Contains("new ScheduledTaskRepository(", source);
        Assert.Contains("TaskList.ItemsSource", source);
        Assert.Contains("OnTaskConfirmed", source);
        Assert.Contains("OnTaskToggleClicked", source);
        Assert.Contains("OnTaskDeleteClicked", source);

        // 建立即寫 last_run 的決策落在倉儲（Add 內），視窗不自訂建立時間。
        var add = SourceContract.BodyOf(SourceContract.Read(Repo), "Add");
        Assert.Contains("last_run_utc", add);
    }
}
