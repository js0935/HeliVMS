using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// 稽核日誌 CSV 產生器（M109／§14.1）。WebApi 的 <c>/api/audit/export.csv</c> 與桌面端稽核視窗共用同一份，
/// 避免兩邊的欄位順序或跳脫規則各改各的——匯出檔是稽核證據，兩份格式不一致等於無法互相對帳。
/// </summary>
public static class AuditLogCsv
{
    /// <summary>欄位順序（＝ <c>audit_log</c> 的欄位），外部對帳依賴此順序。</summary>
    public const string Header = "id,occurred_at,actor,action,category,target_type,target_id,detail";

    /// <summary>產生不含 BOM 的 CSV 內容；呼叫端負責以 UTF-8 BOM 寫檔（Excel 相容）。</summary>
    public static string Render(IEnumerable<AuditLogEntry> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(r.Id).Append(',')
              .Append(Escape(SqliteStore.Iso(r.OccurredAtUtc))).Append(',')
              .Append(Escape(r.Actor)).Append(',')
              .Append(Escape(r.Action)).Append(',')
              .Append(Escape(r.Category)).Append(',')
              .Append(Escape(r.TargetType)).Append(',')
              .Append(Escape(r.TargetId?.ToString())).Append(',')
              .Append(Escape(r.Detail)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180 跳脫：含逗號、引號或換行的值以雙引號包住並將內部引號加倍；
    /// 空值輸出空字串（非 <c>""</c>），讓匯出檔的欄位數固定。
    /// </summary>
    public static string Escape(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
