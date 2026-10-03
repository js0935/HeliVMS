using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 報表 CSV 產生器（§14.1 #9）：匯出、手動寄送與排程寄送共用同一份，
/// 這支測試釘住欄位順序與逗號跳脫，避免三個入口日後各自漂移。
/// </summary>
public sealed class ReportCsvTests
{
    [Fact]
    public void 報表CSV含四類並跳脫含逗號的欄位()
    {
        var csv = ReportCsv.Build(
            new[] { new RecordingSummaryRow(1, "前門,東", 2.5, 1024) },
            3,
            new[] { new CapacityTrendRow("2026-03-01", 2048, 1.5) },
            new[] { new AiEventCountRow("motion", 7) });

        Assert.Contains("類別,項目,值1,值2", csv);
        Assert.Contains("\"前門,東\"", csv);
        Assert.Contains("斷線次數,offline,3,", csv);
        Assert.Contains("容量趨勢,2026-03-01,1.50小時,2048位元組", csv);
        Assert.Contains("AI事件統計,motion,7,", csv);
    }

    [Fact]
    public void 無資料時仍輸出表頭與斷線列()
    {
        var csv = ReportCsv.Build([], 0, [], []);

        Assert.Contains("類別,項目,值1,值2", csv);
        Assert.Contains("斷線次數,offline,0,", csv);
    }
}
