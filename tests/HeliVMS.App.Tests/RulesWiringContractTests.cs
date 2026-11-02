namespace HeliVMS.App.Tests;

/// <summary>
/// 規則變更「立即生效」與三區「編輯」的接線契約。
/// 兩條現場教訓：改完規則要重啟才生效（回呼沒接／引擎只重建一半），以及只能刪掉重加
/// （Id 換掉、稽核斷線）。來源掃描是既有慣例（WPF 無執行期測試）。
/// </summary>
public sealed class RulesWiringContractTests
{
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Rules = "src/HeliVMS.App/RulesWindow.xaml.cs";

    [Fact]
    public void 開啟規則視窗要接上事件規則與IO規則兩個回呼()
    {
        // 教訓：RulesWindow 三個變更點都會呼叫 _eventRulesChanged，
        // 但主視窗只傳了 ioRulesChanged——複合事件規則改完要重啟程式才生效。
        var body = SourceContract.BodyOf(SourceContract.Read(Main), "OpenRulesWindow");
        Assert.Contains("ioRulesChanged", body);
        Assert.Contains("eventRulesChanged", body);
        Assert.Contains("RebuildEventRules", body);
    }

    [Fact]
    public void IO規則變更要重建兩個宿主的引擎()
    {
        // 教訓：攝影機乾接點（CameraDiHost）與 Modbus 接點（IoMonitorHost）各自持有一份
        // IoRuleHost 引擎、只共用路由寫入器——只重載攝影機側，Modbus 連動改完要重啟。
        var body = SourceContract.BodyOf(SourceContract.Read(Main), "ReloadIoRuleEngines");
        Assert.Contains("_cameraDiHost?.ReloadRules()", body);
        Assert.Contains("_ioHost?.ReloadRules()", body);
    }

    [Fact]
    public void 複合事件規則提供編輯並走Update而非刪加重加()
    {
        var source = SourceContract.Read(Rules);
        Assert.Contains("OnEditClicked", source);
        Assert.Contains("_editingRuleId", source);
        Assert.Contains("_rules.Update(", source);

        // 編輯把條件 JSON 解回簡易表單；解不回的形狀必須退回進階模式（硬解會改壞規則）。
        var body = SourceContract.BodyOf(source, "OnEditClicked");
        Assert.Contains("TryParseExpression", body);
        Assert.Contains("RulesModeAdvanced", body);
    }

    [Fact]
    public void IO連動與排程任務也提供編輯()
    {
        var source = SourceContract.Read(Rules);
        Assert.Contains("OnIoRuleEditClicked", source);
        Assert.Contains("_ioRules.Update(", source);
        Assert.Contains("OnTaskEditClicked", source);
        Assert.Contains("_taskRepo.Update(", source);
    }

    [Fact]
    public void 開新增面板會清掉編輯中的Id()
    {
        // 沒有這一行，操作員先點「編輯」再按「新增」，看起來是新增、存下去卻覆寫了
        // 上一條被編輯的規則——兩種模式共用同一個確認按鈕時的經典混態。
        var source = SourceContract.Read(Rules);
        var toggle = SourceContract.BodyOf(source, "OnAddToggleClicked");
        Assert.Contains("_editingRuleId = null", toggle);

        var ioToggle = SourceContract.BodyOf(source, "OnIoRuleAddToggleClicked");
        Assert.Contains("_editingIoRuleId = null", ioToggle);

        var taskToggle = SourceContract.BodyOf(source, "OnTaskAddToggleClicked");
        Assert.Contains("_editingTaskId = null", taskToggle);
    }
}
