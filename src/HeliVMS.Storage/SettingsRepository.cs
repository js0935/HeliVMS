using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// 應用程式設定存取（M19；§9：app_settings 鍵值表為設定中心之權威來源）。
///
/// <para>
/// 另有版本快照（§9.5，v52）：<see cref="Snapshot"/> 保存整份 <c>app_settings</c>，
/// <see cref="Restore"/> 可一鍵回滾至任一先前版本，<see cref="ExportJson"/>／
/// <see cref="ImportJson"/> 供跨機複製部署。
/// </para>
/// </summary>
public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public SettingsRepository(SqliteStore store)
    {
        _store = store;
        _audit = new AuditLogRepository(store);
    }

    /// <summary>依鍵讀取設定值；不存在時回傳 null。</summary>
    public string? Get(string key)
    {
        return _store.Query(
            "SELECT value FROM app_settings WHERE key = $k;",
            static r => r.Read() ? r.GetString(0) : null,
            cmd => cmd.Parameters.AddWithValue("$k", key));
    }

    /// <summary>依鍵讀取設定值；不存在時回傳預設值。</summary>
    public string GetOrDefault(string key, string defaultValue)
        => Get(key) ?? defaultValue;

    /// <summary>新增或更新設定（upsert，更新 updated_at）。</summary>
    public void Set(string key, string value)
    {
        _store.Execute(
            """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(DateTime.UtcNow));
            });
        _audit.Record("system", "settings.set", AuditCategories.Config,
            targetType: "settings", detail: key);
    }

    /// <summary>依鍵讀取非負數值（GB 等）；無法解析或不存在時回傳預設值。</summary>
    public double GetDoubleOrDefault(string key, double defaultValue)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw) ||
            !double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            return defaultValue;
        }

        return v;
    }

    /// <summary>
    /// 建立整份設定的快照並回傳版本 id（§9.5：重大變更前自動快照）。空設定也可快照——
    /// 「把設定清空」同樣是一個合法的可回滾狀態。
    /// </summary>
    public long Snapshot(string? reason = null, string? actor = null)
    {
        var map = ReadAll();
        var at = DateTime.UtcNow;
        var id = _store.Query(
            """
            INSERT INTO settings_versions (snapshot, key_count, reason, actor, created_at)
            VALUES ($s, $c, $r, $a, $t);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", JsonSerializer.Serialize(map));
                cmd.Parameters.AddWithValue("$c", map.Count);
                cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$a", actor ?? AuditActor.Current);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(at));
            });
        _audit.Record(actor, "settings.snapshot", AuditCategories.Config,
            targetType: "settings", targetId: id,
            detail: string.IsNullOrWhiteSpace(reason) ? $"v{id}（{map.Count} 鍵）" : $"v{id}（{map.Count} 鍵）：{reason}");
        return id;
    }

    /// <summary>列出最近的設定版本（新到舊）。</summary>
    public IReadOnlyList<SettingsVersion> ListVersions(int limit = 50)
    {
        if (limit <= 0)
        {
            return [];
        }

        return _store.Query(
            """
            SELECT id, key_count, reason, actor, created_at
            FROM settings_versions
            ORDER BY created_at DESC, id DESC
            LIMIT $n;
            """,
            static r =>
            {
                var list = new List<SettingsVersion>();
                while (r.Read())
                {
                    list.Add(new SettingsVersion(
                        r.GetInt64(0),
                        r.GetInt32(1),
                        r.IsDBNull(2) ? null : r.GetString(2),
                        r.GetString(3),
                        SqliteStore.FromIso(r.GetString(4))));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$n", limit));
    }

    /// <summary>
    /// 回滾至指定版本（§9.5）：以快照**整份取代**目前設定，包含移除「快照之後才新增」的鍵——
    /// 只補差異會留下不屬於那個版本的鍵，還原出來的狀態並不一致。回傳還原的鍵數。
    /// </summary>
    public int Restore(long versionId, string? actor = null)
    {
        var json = _store.Query(
            "SELECT snapshot FROM settings_versions WHERE id = $id;",
            static r => r.Read() ? r.GetString(0) : null,
            cmd => cmd.Parameters.AddWithValue("$id", versionId))
            ?? throw new InvalidOperationException($"找不到設定版本 {versionId}。");

        var map = Deserialize(json);
        ReplaceAll(map);
        _audit.Record(actor, "settings.rollback", AuditCategories.Config,
            targetType: "settings", targetId: versionId, detail: $"還原自 v{versionId}（{map.Count} 鍵）");
        return map.Count;
    }

    /// <summary>匯出目前全部設定為 JSON（§9.5：跨機複製部署）。</summary>
    public string ExportJson(string? actor = null)
    {
        var map = ReadAll();
        _audit.Record(actor, "settings.export", AuditCategories.Config,
            targetType: "settings", detail: $"{map.Count} 鍵");
        return JsonSerializer.Serialize(map, IndentedOptions);
    }

    /// <summary>
    /// 以 JSON 整份取代目前設定（§9.5）。格式不正確時在任何寫入前就擲回，現有設定不受影響。
    /// </summary>
    public int ImportJson(string json, string? actor = null)
    {
        var map = Deserialize(json);
        ReplaceAll(map);
        _audit.Record(actor, "settings.import", AuditCategories.Config,
            targetType: "settings", detail: $"{map.Count} 鍵");
        return map.Count;
    }

    /// <summary>整份取代 app_settings（單一交易，刪除＋寫入全有或全無）。</summary>
    private void ReplaceAll(IReadOnlyDictionary<string, string> map)
    {
        _store.InTransaction(() =>
        {
            _store.Execute("DELETE FROM app_settings;");
            foreach (var (key, value) in map)
            {
                _store.Execute(
                    "INSERT INTO app_settings (key, value, updated_at) VALUES ($k, $v, $t);",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("$k", key);
                        cmd.Parameters.AddWithValue("$v", value);
                        cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(DateTime.UtcNow));
                    });
            }
        });
    }

    private SortedDictionary<string, string> ReadAll()
    {
        return _store.Query(
            "SELECT key, value FROM app_settings ORDER BY key;",
            static r =>
            {
                var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
                while (r.Read())
                {
                    map[r.GetString(0)] = r.GetString(1);
                }

                return map;
            });
    }

    private static SortedDictionary<string, string> Deserialize(string json)
    {
        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("設定 JSON 格式不正確。", nameof(json), ex);
        }

        if (parsed is null)
        {
            throw new ArgumentException("設定 JSON 必須是鍵值物件。", nameof(json));
        }

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parsed)
        {
            if (value is null)
            {
                throw new ArgumentException($"設定鍵 '{key}' 的值不可為 null。", nameof(json));
            }

            map[key] = value;
        }

        return map;
    }
}

/// <summary>設定版本快照摘要（§9.5）。</summary>
public sealed record SettingsVersion(
    long Id, int KeyCount, string? Reason, string Actor, DateTime CreatedAtUtc);
