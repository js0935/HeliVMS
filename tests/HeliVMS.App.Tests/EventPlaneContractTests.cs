using System.Text.RegularExpressions;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.App.Tests;

/// <summary>
/// 事件中心對齊參考機「事件紀錄」的接縫：分類篩選、單一事件平面（警報 ∪ 稽核）、稽核列唯讀。
/// </summary>
/// <remarks>
/// 桌面端沒有 WPF 執行期，這些是「看畫面不一定看得出來、但一改就錯」的接縫：
/// 分類欄與篩選各寫一份口徑（清單有這一列、選那個分類卻查不到）、
/// 稽核列被當成警報列（按了確認回報成功、資料庫什麼都沒變）、
/// 以及兩張表的 id 撞號（選到第 12 號稽核變成第 12 號警報）。
/// </remarks>
public sealed class EventPlaneContractTests
{
    private const string Xaml = "src/HeliVMS.App/EventCenterWindow.xaml";
    private const string Code = "src/HeliVMS.App/EventCenterWindow.xaml.cs";

    /// <summary>
    /// 事件紀錄是九欄（時間／分類／頻道／類型／持續／詳情／狀態／指派／操作者），
    /// 標題一律走 i18n 鍵。
    /// </summary>
    /// <remarks>
    /// 欄位數與標題索引綁死：少一欄而標題照舊往後推，畫面就是一整排錯位的標題
    /// （「狀態」下面是操作者），而且編譯不會有任何警告。
    /// </remarks>
    [Fact]
    public void 事件清單有九欄且每個標題走三語鍵()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.Equal(9, Regex.Matches(xaml, "<GridViewColumn ").Count);

        var i18n = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyI18n");
        Assert.Contains("grid.Columns.Count >= 9", i18n);

        var keys = new[]
        {
            "EventCenter.ColTime", "EventCenter.ColCategory", "EventCenter.ColChannel",
            "EventCenter.ColType", "EventCenter.ColDuration", "EventCenter.ColDetail",
            "EventCenter.ColStatus", "EventCenter.ColAssigned", "EventCenter.ColActor",
        };

        for (var i = 0; i < keys.Length; i++)
        {
            Assert.Contains($"grid.Columns[{i}].Header = Localizer.T(\"{keys[i]}\")", i18n);
            foreach (var lang in I18n.Languages)
            {
                Assert.NotEqual(keys[i], I18n.Get(lang, keys[i]));
            }
        }
    }

    /// <summary>
    /// 分類欄、分類下拉與 CMS 型別碼共用 <see cref="EventTaxonomy.Buckets"/> 這一份口徑。
    /// </summary>
    /// <remarks>
    /// 分類衍生自既有資料（<c>alarm_events.event_type</c> 與 <c>audit_log.category</c>），
    /// 所以「清單上的分類」與「篩選條件」必須由同一個函式決定；各寫一份的症狀是
    /// 清單看得到某一列、選那個分類卻查不到（或反過來，選了分類卻冒出別的列）。
    /// 五個分類鍵也要三語齊備，否則非中文語系會顯示 <c>EventTaxonomy.Bucket.event</c>。
    /// </remarks>
    [Fact]
    public void 分類篩選與分類欄共用同一份口徑()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.Contains("x:Name=\"CategoryCombo\"", xaml);
        Assert.Contains("SelectionChanged=\"OnChannelSelectionChanged\"", xaml);

        var code = SourceContract.Read(Code);
        var categories = SourceContract.BodyOf(code, "RefreshCategories");
        Assert.Contains("EventTaxonomy.Buckets", categories);
        Assert.Contains("EventTaxonomy.BucketLabelKey(bucket)", categories);
        Assert.Contains("EventCenter.AllCategories", categories);

        // 分類欄與篩選下拉走同一個鍵。
        Assert.Contains("Localizer.T(EventTaxonomy.BucketLabelKey(Bucket))", code);

        // 清單查詢與 CSV 匯出都要帶分類條件：只做一邊，匯出檔會是「全部」，
        // 操作員拿它去比對畫面時會以為匯出多撈了別的類別。
        Assert.Contains("Bucket = SelectedCategory()", SourceContract.BodyOf(code, "DoRefresh"));
        Assert.Contains("Bucket = SelectedCategory()", SourceContract.BodyOf(code, "BuildQuery"));

        foreach (var lang in I18n.Languages)
        {
            Assert.NotEqual("EventCenter.AllCategories", I18n.Get(lang, "EventCenter.AllCategories"));
            foreach (var bucket in EventTaxonomy.Buckets)
            {
                var key = EventTaxonomy.BucketLabelKey(bucket);
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }

    /// <summary>
    /// 稽核列是唯讀的：不能確認、處置、回放、定位或遮蔽。
    /// </summary>
    /// <remarks>
    /// 稽核沒有 <c>event_dispositions</c> 的列，確認／處置寫進去只是一個沒有列的 update：
    /// 畫面會說「已套用」而資料庫什麼都沒變。回放與地圖也一樣——稽核列沒有錄影、沒有目標頻道，
    /// 開出來的視窗必然是空的，操作員只會覺得「按了沒反應」。
    /// </remarks>
    [Fact]
    public void 稽核列是唯讀的()
    {
        var code = SourceContract.Read(Code);

        var selection = SourceContract.BodyOf(code, "ApplySelection");
        Assert.Contains("rows.All(static r => r.IsAudit)", selection);
        Assert.Contains("AckButton.IsEnabled = count > 0 && !auditOnly", selection);
        Assert.Contains("DispositionCombo.IsEnabled = count > 0 && !auditOnly", selection);
        Assert.Contains("AssignBox.IsEnabled = count > 0 && !auditOnly", selection);
        Assert.Contains("NoteBox.IsEnabled = count > 0 && !auditOnly", selection);
        Assert.Contains("EventCenter.AuditReadOnly", selection);

        // 批次只取警報鍵（"a:"）：兩張表的 id 各自從 1 起算，混進去就會改到別的列。
        var batch = SourceContract.BodyOf(code, "SelectedIdsForBatch");
        Assert.Contains("k.StartsWith(\"a:\"", batch);

        foreach (var entry in new[] { "OnPlaybackClicked", "OnMapLocateClicked", "OnRedactSnapshotClicked" })
        {
            Assert.Contains("IsAudit: false", SourceContract.BodyOf(code, entry));
        }

        foreach (var lang in I18n.Languages)
        {
            Assert.NotEqual(
                "EventCenter.AuditReadOnly", I18n.Get(lang, "EventCenter.AuditReadOnly"));
        }
    }

    /// <summary>
    /// 事件清單、分頁總數與 CSV 都走事件平面，而且兩張表的 id 不會被當成同一個鍵。
    /// </summary>
    /// <remarks>
    /// 清單若退回只查 <c>alarm_events</c>，稽核紀錄（登入、設定變更、錄影停滯）就不會出現，
    /// 而畫面看起來完全正常——只是「事件紀錄」少了半個系統。選取鍵必須帶來源前綴，
    /// 否則同一頁的第 12 號警報與第 12 號稽核會被當成同一列：勾一列選兩列、
    /// 重整後勾錯列。
    /// </remarks>
    [Fact]
    public void 清單分頁與匯出都走事件平面且選取鍵帶來源前綴()
    {
        var code = SourceContract.Read(Code);

        var refresh = SourceContract.BodyOf(code, "DoRefresh");
        Assert.Contains("_eventLog.List(q)", refresh);
        Assert.Contains("_eventLog.Count(q)", refresh);
        Assert.Contains("EventLogQuery", refresh);
        Assert.DoesNotContain("_events.List(", refresh);

        Assert.Contains("_eventLog.List(q)", SourceContract.BodyOf(code, "OnExportCsvClicked"));

        // 參數型別在簽章上（BodyOf 從第一個 { 起算），所以整份原始碼比對。
        Assert.Contains("IReadOnlyList<EventLogEntry> list", code);

        var row = SourceContract.BodyOf(code, "ToRow");
        Assert.Contains("(ev.IsAudit ? \"u:\" : \"a:\") + ev.Id", row);
    }
}
