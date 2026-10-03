using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// 統圖報表 CSV 產生器（§14.1 #9）。
///
/// <para>
/// 匯出（桌面）與排程郵寄（背景）必須是同一份內容，否則兩邊的欄位順序會各自漂移，
/// 使用者收到的附件和畫面看到的就不一致。這裡把產生邏輯集中成純函式，
/// UI 與 <c>ReportMailer</c> 都呼叫它。
/// </para>
/// </summary>
public static class ReportCsv
{
    public static string Build(
        IReadOnlyList<RecordingSummaryRow> recording,
        long disconnect,
        IReadOnlyList<CapacityTrendRow> trend,
        IReadOnlyList<AiEventCountRow> ai)
    {
        var sb = new StringBuilder();
        sb.AppendLine("類別,項目,值1,值2");
        foreach (var r in recording)
        {
            sb.AppendLine($"錄影時數,{Escape(r.ChannelName)},{r.Hours:F2}小時,{r.Bytes}位元組");
        }

        sb.AppendLine($"斷線次數,offline,{disconnect},");

        foreach (var r in trend)
        {
            sb.AppendLine($"容量趨勢,{r.Day},{r.Hours:F2}小時,{r.Bytes}位元組");
        }

        foreach (var r in ai)
        {
            sb.AppendLine($"AI事件統計,{Escape(r.EventType)},{r.Count},");
        }

        return sb.ToString();
    }

    /// <summary>RFC 4180 風格跳脫：含逗號／引號／換行的欄位加引號並將引號加倍。</summary>
    public static string Escape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
