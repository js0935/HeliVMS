using System.Globalization;
using System.Text;
using HeliVMS.Licensing;

namespace HeliVMS.LicenseProducer;

/// <summary>批次 CSV 一列解析後的簽發指示（§19.6.3 批次簽發）。</summary>
/// <param name="Line">來源檔案行號（1 起算，含表頭），錯誤訊息要指得出是哪一列。</param>
/// <param name="Customer">客戶／案場名稱，僅寫入結果索引，不進授權碼。</param>
/// <param name="Machine">綁定設備碼（32 碼大寫十六進位）；<see cref="AnyMachine"/> 表示不綁定。</param>
/// <param name="AnyMachine">true 表示明確要求不綁定機器（CSV 內寫 <c>*</c>）。</param>
/// <param name="Tier"><see cref="LicenseTier"/> 等級；<see cref="TierName"/> 保留 CSV 原文供錯誤訊息與稽核使用。</param>
/// <param name="TierName">等級名稱原文；自訂組合（僅給 cameras/features）為 null。</param>
/// <param name="Cameras">通道數上限；<see cref="CamerasExplicit"/> 為 true 表示 CSV 明列。</param>
/// <param name="CamerasExplicit">CSV 是否明列 cameras（false 表示沿用等級建議值）。</param>
/// <param name="Features">功能旗標；null 表示沿用等級。</param>
/// <param name="LicenseId">授權 ID；null 表示由簽發端產生 GUID。</param>
/// <param name="ExpiresUtc">到期時刻（UTC）；null 為永久授權。</param>
/// <param name="Issuer">發行者；null 表示採預設。</param>
/// <param name="Note">備註，僅寫入結果索引。</param>
public sealed record BatchLicenseRow(
    int Line,
    string Customer,
    string Machine,
    bool AnyMachine,
    LicenseTier? Tier,
    string? TierName,
    int Cameras,
    bool CamerasExplicit,
    string[]? Features,
    string? LicenseId,
    DateTime? ExpiresUtc,
    string? Issuer,
    string? Note)
{
    /// <summary>結果索引用的安全檔名：客戶名與 ID 皆可能含路徑分隔符或非法字元。</summary>
    public string FileStem
    {
        get
        {
            var source = Customer.Length > 0 ? Customer : LicenseId;
            var stem = source is null ? string.Empty : SanitizeFileName(source);
            return stem.Length > 0 ? stem : $"license-{Line:D4}";
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' || char.IsControl(c) ? '_' : c);
        }

        return sb.ToString();
    }
}

/// <summary>批次 CSV 的一項問題（列號＋可操作訊息）。</summary>
/// <param name="Line">行號；檔層級問題為 0。</param>
/// <param name="Column">欄名；檔層級問題為 null。</param>
/// <param name="Message">問題說明。</param>
public sealed record BatchCsvIssue(int Line, string? Column, string Message)
{
    /// <summary>人可讀的單行訊息。</summary>
    public override string ToString()
        => Column is null
            ? $"第 {Line} 行：{Message}"
            : $"第 {Line} 行 [{Column}]：{Message}";
}

/// <summary>批次 CSV 剖析結果。</summary>
/// <param name="Rows">可簽發的列（<see cref="Errors"/> 非空時可能為空——寧可全部不簽，也不要簽一半）。</param>
/// <param name="Errors">所有問題，已依行號排序。</param>
public sealed record BatchCsvParseResult(IReadOnlyList<BatchLicenseRow> Rows, IReadOnlyList<BatchCsvIssue> Errors)
{
    /// <summary>無任何問題才為 true。</summary>
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// 批次簽發的 CSV 剖析與檢核（§19.6「批次簽發：匯入 CSV（設備碼/等級/max/到期）一次簽發多張」）。
/// </summary>
/// <remarks>
/// <para>整份檔案「全有或全無」：任一列有問題即不簽任何一張，且一次回報全部問題。
/// 批次常是 50～500 列，若逐列簽到壞的那列才停，廠商手上會有半套已發出去的授權、
/// 稽核軌跡也會殘缺難以對帳。</para>
/// <para>與簽發（RSA 簽章、稽核）刻意分離：本型別不碰金鑰，故可在測試裡窮舉各種畸形輸入。</para>
/// </remarks>
public static class BatchLicenseCsv
{
    /// <summary>接受的欄名（小寫比較）。</summary>
    public static readonly IReadOnlyList<string> Columns =
        ["machine", "tier", "cameras", "features", "expires", "id", "issuer", "customer", "note"];

    /// <summary>剖析並檢核 CSV 內容。</summary>
    /// <param name="content">整份 CSV（可含 BOM、CRLF、<c>#</c> 註解與空行）。</param>
    /// <param name="nowUtc">判斷「到期日已過」的基準時間；測試可注入。</param>
    public static BatchCsvParseResult Parse(string content, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);

        var errors = new List<BatchCsvIssue>();
        var records = ReadRecords(content, errors);

        var rows = new List<BatchLicenseRow>();
        var header = records.Count > 0 ? records[0] : null;
        if (header is null || header.Fields.All(string.IsNullOrWhiteSpace))
        {
            errors.Add(new BatchCsvIssue(1, null, "CSV 為空，沒有表頭可解析。"));
            return new BatchCsvParseResult([], errors);
        }

        var indexes = MapColumns(header.Fields, header.Line, errors);

        var dataCount = 0;
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            dataCount++;
            if (record.Fields.Length > indexes.Count)
            {
                errors.Add(new BatchCsvIssue(
                    record.Line,
                    null,
                    $"欄數 {record.Fields.Length} 多於表頭 {header.Fields.Length} 欄（多出的內容會被忽略，故視為錯誤）。"));
            }

            var row = ReadRow(record, indexes, nowUtc, errors);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        if (dataCount == 0)
        {
            errors.Add(new BatchCsvIssue(header.Line + 1, null, "沒有資料列可簽發。"));
        }

        ReportDuplicates(rows, errors);

        if (errors.Count > 0)
        {
            return new BatchCsvParseResult([], errors.OrderBy(e => e.Line).ToList());
        }

        return new BatchCsvParseResult(rows, []);
    }

    /// <summary>必要欄位；id 可留空由簽發端產生 GUID，故不列入。</summary>
    private static readonly string[] RequiredColumns = ["machine"];

    private static Dictionary<string, int> MapColumns(string[] fields, int line, List<BatchCsvIssue> errors)
    {
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Length; i++)
        {
            var name = fields[i].Trim().TrimStart('\uFEFF');
            if (name.Length == 0)
            {
                continue;
            }

            // 未知欄名一律拒絕：把 cameras 打成 camaers 而被默默忽略，會照等級預設值簽出去，
            // 客戶拿到 4 路而不是 16 路，而且沒有任何錯誤訊息。
            if (!Columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new BatchCsvIssue(
                    line,
                    name,
                    $"未知欄名（可用欄位：{string.Join('、', Columns)}）。"));
                continue;
            }

            if (!indexes.TryAdd(name, i))
            {
                errors.Add(new BatchCsvIssue(line, name, "欄名重複。"));
            }
        }

        foreach (var required in RequiredColumns)
        {
            if (!indexes.ContainsKey(required))
            {
                errors.Add(new BatchCsvIssue(line, required, $"缺少必要欄位「{required}」。"));
            }
        }

        return indexes;
    }

    private static BatchLicenseRow? ReadRow(
        CsvRecord record,
        Dictionary<string, int> indexes,
        DateTime nowUtc,
        List<BatchCsvIssue> errors)
    {
        var before = errors.Count;

        var machine = Get(record, indexes, "machine");
        var anyMachine = machine is "*" or "-" or "any";
        if (!anyMachine)
        {
            machine = NormalizeMachine(machine);
            if (machine is null)
            {
                errors.Add(new BatchCsvIssue(
                    record.Line,
                    "machine",
                    "設備碼必須是 32 碼十六進位（§19.1），或明確填 * 表示不綁定機器；" +
                    "留空會發出一張任何機器都能用的授權，故視為錯誤。"));
            }
        }

        var tierName = Get(record, indexes, "tier");
        var tier = tierName.Length > 0 ? LicenseTiers.Find(tierName) : null;
        if (tierName.Length > 0 && tier is null)
        {
            errors.Add(new BatchCsvIssue(
                record.Line,
                "tier",
                $"未知等級「{tierName}」。可用等級：{string.Join('、', LicenseTiers.All.Select(t => t.Name))}"));
        }

        var camerasText = Get(record, indexes, "cameras");
        var camerasExplicit = camerasText.Length > 0;
        var cameras = tier?.DefaultCameras ?? 0;
        if (camerasExplicit)
        {
            if (!int.TryParse(camerasText, NumberStyles.None, CultureInfo.InvariantCulture, out cameras))
            {
                errors.Add(new BatchCsvIssue(record.Line, "cameras", "通道數必須是不含符號的整數。"));
                cameras = 0;
            }
            else if (cameras is < 1 or > LicenseTiers.MaxCameras)
            {
                errors.Add(new BatchCsvIssue(
                    record.Line,
                    "cameras",
                    $"通道數必須在 1～{LicenseTiers.MaxCameras} 之間（§19.3 客製版上限）。"));
            }
        }

        // 只在「完全沒給 tier」時提示；給了但無法辨識已由上面的未知等級訊息涵蓋，
        // 否則同一列會同時出現兩則錯誤，廠商會以為有兩個問題要修。
        if (tierName.Length == 0 && !camerasExplicit)
        {
            errors.Add(new BatchCsvIssue(
                record.Line,
                "tier",
                "須填 tier 或 cameras 其一；兩者皆空無從決定通道數與功能旗標。"));
        }

        string[]? features = null;
        var featuresText = Get(record, indexes, "features");
        if (featuresText.Length > 0)
        {
            features = featuresText
                .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (features.Length == 0)
            {
                errors.Add(new BatchCsvIssue(record.Line, "features", "功能旗標解析後為空。"));
            }
        }

        var expiresText = Get(record, indexes, "expires");
        DateTime? expires = null;
        if (expiresText.Length > 0)
        {
            // 只接受純日期或標準 ISO；DateTime.TryParse 的寬鬆剖析會把「2027-13-45」之類的
            // 垃圾也當成合法（擲回當月最後一天），簽出去就是一張立刻到期的卡。
            if (!TryParseExpiry(expiresText, out var parsed))
            {
                errors.Add(new BatchCsvIssue(
                    record.Line,
                    "expires",
                    $"到期日「{expiresText}」不是合法日期，請用 yyyy-MM-dd 或 ISO 8601（UTC）。"));
            }
            else if (parsed <= nowUtc)
            {
                errors.Add(new BatchCsvIssue(
                    record.Line,
                    "expires",
                    $"到期日 {parsed:u} 已過期；簽出去的卡一裝就失效，多半是年份打錯。永久授權請留空。"));
            }
            else
            {
                expires = parsed;
            }
        }

        var id = Get(record, indexes, "id");
        if (id.Length > 0 && id.Any(char.IsWhiteSpace))
        {
            errors.Add(new BatchCsvIssue(record.Line, "id", "授權 ID 不可含空白字元。"));
        }

        if (errors.Count != before)
        {
            return null;
        }

        return new BatchLicenseRow(
            record.Line,
            Get(record, indexes, "customer"),
            anyMachine ? string.Empty : machine!.ToUpperInvariant(),
            anyMachine,
            tier,
            tierName.Length > 0 ? tierName : null,
            cameras,
            camerasExplicit,
            features,
            id.Length > 0 ? id : null,
            expires,
            NullIfEmpty(Get(record, indexes, "issuer")),
            NullIfEmpty(Get(record, indexes, "note")));
    }

    private static void ReportDuplicates(IReadOnlyList<BatchLicenseRow> rows, List<BatchCsvIssue> errors)
    {
        foreach (var group in rows.GroupBy(r => r.Machine, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            errors.Add(new BatchCsvIssue(
                group.First().Line,
                "machine",
                $"設備碼重複（第 {string.Join('、', group.Select(r => r.Line))} 行）。" +
                "同一台機器同時掛兩張授權只會讓使用者搞不清楚哪張有效；換機請分批簽。"));
        }

        foreach (var group in rows.Where(r => r.LicenseId is { Length: > 0 })
                     .GroupBy(r => r.LicenseId!, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            errors.Add(new BatchCsvIssue(
                group.First().Line,
                "id",
                $"授權 ID 重複（第 {string.Join('、', group.Select(r => r.Line))} 行）：{group.Key}。"));
        }
    }

    private static bool TryParseExpiry(string text, out DateTime expires)
    {
        var formats = new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss" };
        if (DateTime.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out expires))
        {
            return true;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out expires);
    }

    private static string? NormalizeMachine(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 32 && trimmed.All(Uri.IsHexDigit) ? trimmed.ToUpperInvariant() : null;
    }

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    private static string Get(CsvRecord record, Dictionary<string, int> indexes, string column)
        => indexes.TryGetValue(column, out var index) && index < record.Fields.Length
            ? record.Fields[index].Trim()
            : string.Empty;

    private sealed record CsvRecord(int Line, string[] Fields);

    /// <summary>
    /// 讀出所有非註解、非空白的 CSV 列（含原始行號）。
    /// 引號內可含逗號與換行；<c>""</c> 表示字面引號。
    /// </summary>
    private static List<CsvRecord> ReadRecords(string content, List<BatchCsvIssue> errors)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var started = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            records.Add(new CsvRecord(recordLine, [.. fields]));
            fields.Clear();
            started = false;
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    started = true;
                    break;

                case ',':
                    EndField();
                    started = true;
                    break;

                case '\r':
                    break;

                case '\n':
                    line++;
                    if (!started && fields.Count == 0)
                    {
                        // 純空行。
                        break;
                    }

                    EndRecord();
                    recordLine = line;
                    break;

                default:
                    started = true;
                    field.Append(c);
                    break;
            }
        }

        if (inQuotes)
        {
            errors.Add(new BatchCsvIssue(recordLine, null, "CSV 結尾仍在引號內（引號未閉合）。"));
        }

        if (started || fields.Count > 0)
        {
            EndRecord();
        }

        // BOM 要在判斷註解之前剝掉：\uFEFF 不是空白字元，TrimStart 不會處理它，
        // 結果 Excel 匯出的第一行註解會被當成表頭，整份檔案解析錯位。
        var stripped = records
            .Select(r => new CsvRecord(r.Line, [.. r.Fields.Select(f => f.TrimStart('\uFEFF'))]))
            .Where(r => r.Fields.Any(f => f.Trim().Length > 0))
            .Where(r => IsComment(r) is false)
            .ToList();

        return stripped;
    }

    /// <summary>單一欄位且以 # 開頭者視為註解行（匯出工具常在表頭前塞說明文字）。</summary>
    private static bool IsComment(CsvRecord record)
        => record.Fields.Length == 1 && record.Fields[0].TrimStart().StartsWith('#');
}
