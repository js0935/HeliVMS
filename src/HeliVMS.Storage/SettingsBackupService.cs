using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>匯入前給 UI 確認用的摘要：檔案版本、產生時間與各設定表的筆數。</summary>
public sealed record SettingsBackupSummary(
    int Version,
    string CreatedUtc,
    string AppVersion,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// 設定檔匯出／匯入（§2.1 設定遷移）：把「定義系統行為」的設定表整份寫成一個 JSON 檔，
/// 並可原樣倒回另一台機器（或同一台的還原點）。
/// </summary>
/// <remarks>
/// <para>
/// 匯出集合刻意<b>只含設定</b>（攝影機、頻道、群組、排程、IO、警報規則、巡邏、地圖、
/// 遮蔽、智慧牆、排程任務與 app_settings）。事件、稽核、錄影索引、書籤是歷史資料不是設定；
/// <c>users</c>／<c>auth_providers</c> 含憑證、<c>license</c> 是機器綁定——跟著設定檔走等於
/// 把身分與授權搬去別台機器，是安全決策不是遺漏。
/// </para>
/// <para>
/// 匯入是<b>原子</b>的：先關 FK、一個交易內清空再重灌、提交後才把 FK 打開。任何一列失敗
/// （檔案被改壞、欄位對不上、惡意欄名）整份回滾，現場設定不會變成半套。列的欄名會先對照
/// 實際表結構（pragma_table_info）——欄名來自匯入檔，不能拿去拼 SQL。
/// </para>
/// <para>
/// 各表的 Id 原樣保留（AUTOINCREMENT 表插入明確 Id 會自動推進計數），因此頻道 Id 與
/// 規則裡引用的 channel_id 在匯入後仍然對得上。
/// </para>
/// </remarks>
public sealed class SettingsBackupService
{
    /// <summary>匯出檔格式版本。讀到比這更大的數字＝檔案來自更新的程式，拒絕匯入。</summary>
    public const int BackupVersion = 1;

    /// <summary>
    /// 匯出（也是匯入的清空與重灌順序）清單：父表在前、子表在後。
    /// </summary>
    internal static readonly string[] ExportTables =
    [
        "app_settings",
        "devices",
        "device_groups",
        "channels",
        "channel_groups",
        "channel_group_members",
        "recording_schedule",
        "alert_rules",
        "ai_rules",
        "io_devices",
        "io_channels",
        "io_ports",
        "io_rules",
        "patrols",
        "patrol_steps",
        "maps",
        "map_devices",
        "analytics_zones",
        "redaction_regions",
        "scheduled_tasks",
        "smartwall_layouts",
        "smartwall_tiles",
    ];

    private static readonly string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly SqliteStore _store;
    private readonly string _appVersion;

    public SettingsBackupService(SqliteStore store, string appVersion)
    {
        _store = store;
        _appVersion = appVersion;
    }

    /// <summary>把設定表整份寫成 JSON。</summary>
    public void ExportTo(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var tables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var table in ExportTables)
        {
            tables[table] = ReadTable(table);
        }

        var file = new Dictionary<string, object?>
        {
            ["version"] = BackupVersion,
            ["createdUtc"] = DateTime.UtcNow.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture),
            ["appVersion"] = _appVersion,
            ["tables"] = tables,
        };

        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    /// <summary>讀摘要（不動資料庫）：給 UI 在「真的要覆蓋嗎」確認框顯示。</summary>
    public SettingsBackupSummary ReadSummary(string filePath)
    {
        using var doc = ParseFile(filePath);
        var root = doc.RootElement;

        if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("設定檔缺少 version 欄位。");
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Object)
        {
            foreach (var table in tables.EnumerateObject())
            {
                counts[table.Name] = table.Value.ValueKind == JsonValueKind.Array ? table.Value.GetArrayLength() : 0;
            }
        }

        return new SettingsBackupSummary(
            versionElement.GetInt32(),
            root.TryGetProperty("createdUtc", out var created) ? created.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("appVersion", out var app) ? app.GetString() ?? string.Empty : string.Empty,
            counts);
    }

    /// <summary>把設定檔倒回資料庫（原子；失敗整份回滾）。</summary>
    public SettingsBackupSummary ImportFrom(string filePath)
    {
        var summary = ReadSummary(filePath);
        if (summary.Version > BackupVersion)
        {
            throw new InvalidOperationException(
                $"設定檔版本 {summary.Version} 比本程式支援的 {BackupVersion} 新；請先升級程式再匯入。");
        }

        // 先把整個檔解析成列，再碰資料庫：壞檔在交易開始前就失敗，不會留下半套。
        using var doc = ParseFile(filePath);
        var parsed = ParseTables(doc.RootElement);

        _store.Execute("PRAGMA foreign_keys=OFF;");
        try
        {
            _store.Execute("BEGIN IMMEDIATE;");
            foreach (var table in ExportTables)
            {
                _store.Execute("DELETE FROM \"" + table + "\";");
            }

            foreach (var (table, rows) in parsed)
            {
                foreach (var row in rows)
                {
                    InsertRow(table, row);
                }
            }

            _store.Execute("COMMIT;");
        }
        catch
        {
            try { _store.Execute("ROLLBACK;"); } catch { }
            throw;
        }
        finally
        {
            _store.Execute("PRAGMA foreign_keys=ON;");
        }

        return summary;
    }

    private JsonDocument ParseFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("設定檔不是有效的 JSON：" + ex.Message, ex);
        }
    }

    private Dictionary<string, List<Dictionary<string, object?>>> ParseTables(JsonElement root)
    {
        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("設定檔缺少 tables 區塊。");
        }

        var parsed = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var table in tables.EnumerateObject())
        {
            if (!ExportTables.Contains(table.Name, StringComparer.Ordinal))
            {
                continue;   // 不在白名單的表（舊版多出或惡意附加）一律略過，不進資料庫。
            }

            if (table.Value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"設定檔的 {table.Name} 不是列陣列。");
            }

            var rows = new List<Dictionary<string, object?>>();
            foreach (var element in table.Value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"設定檔的 {table.Name} 含非物件列。");
                }

                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    row[property.Name] = FromJson(property.Value);
                }

                rows.Add(row);
            }

            parsed[table.Name] = rows;
        }

        return parsed;
    }

    private void InsertRow(string table, Dictionary<string, object?> row)
    {
        // 欄名來自匯入檔，先對照實際表結構：不存在的欄直接拒絕（整份回滾），
        // 絕不把檔案內的字串拿去拼 SQL。
        var valid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _store.Query(
            $"SELECT name FROM pragma_table_info('{table}');",
            r =>
            {
                var names = new List<string>();
                while (r.Read())
                {
                    names.Add(r.GetString(0));
                }

                return names;
            }))
        {
            valid.Add(name);
        }

        var columns = row.Keys.Where(valid.Contains).ToList();
        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"設定檔的 {table} 列沒有任何有效欄位。");
        }

        var columnList = string.Join(", ", columns.Select(c => "\"" + c + "\""));
        var parameterList = string.Join(", ", columns.Select((_, i) => "$c" + i));
        var sql = $"INSERT INTO \"{table}\" ({columnList}) VALUES ({parameterList});";

        _store.Execute(sql, cmd =>
        {
            for (var i = 0; i < columns.Count; i++)
            {
                cmd.Parameters.AddWithValue("$c" + i, row[columns[i]] ?? DBNull.Value);
            }
        });
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var i) ? i : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => null,
    };

    private List<Dictionary<string, object?>> ReadTable(string table)
    {
        return _store.Query(
            "SELECT * FROM \"" + table + "\";",
            r =>
            {
                var rows = new List<Dictionary<string, object?>>();
                while (r.Read())
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < r.FieldCount; i++)
                    {
                        row[r.GetName(i)] = r.IsDBNull(i) ? null : Normalize(r.GetValue(i));
                    }

                    rows.Add(row);
                }

                return rows;
            });
    }

    private static object? Normalize(object? value) => value switch
    {
        long l => l,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        bool flag => flag ? 1L : 0L,
        decimal m => (double)m,
        _ => value,
    };
}
