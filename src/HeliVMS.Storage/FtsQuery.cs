namespace HeliVMS.Storage;

/// <summary>FTS5 MATCH 查詢正規化（M97）：逐 token 引號包覆防 FTS5 語法誤解析
/// （如 `TXN-999` 的 `-999` 會被當負項→視為欄位名而報「no such column」）。</summary>
internal static class FtsQuery
{
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("查詢不可為空", nameof(raw));
        }

        var terms = raw
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", string.Empty))
            .Where(t => t.Length > 0)
            .Select(t => IsOperator(t) ? t.ToUpperInvariant() : "\"" + t + "\"")
            .ToList();

        if (terms.Count == 0)
        {
            throw new ArgumentException("查詢不可為空", nameof(raw));
        }

        return string.Join(" ", terms);
    }

    private static bool IsOperator(string token) =>
        token.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("NOT", StringComparison.OrdinalIgnoreCase);
}