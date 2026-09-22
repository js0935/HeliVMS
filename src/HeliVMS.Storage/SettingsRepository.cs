namespace HeliVMS.Storage;

/// <summary>
/// 應用程式設定存取（M19；§9：app_settings 鍵值表為設定中心之權威來源）。
/// </summary>
public sealed class SettingsRepository
{
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
}