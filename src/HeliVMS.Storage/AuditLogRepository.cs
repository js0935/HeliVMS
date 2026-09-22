namespace HeliVMS.Storage;

/// <summary>稽核日誌事件類別（M109，§14.1 資安治理）。</summary>
public static class AuditCategories
{
    public const string Auth = "auth";
    public const string Config = "config";
    public const string Export = "export";
    public const string Evidence = "evidence";
    public const string Share = "share";
    public const string Retention = "retention";
    public const string LegalHold = "legal_hold";
}

/// <summary>稽核日誌項目（M109，v40 `audit_log`）。</summary>
public sealed record AuditLogEntry(
    long Id,
    DateTime OccurredAtUtc,
    string Actor,
    string Action,
    string Category,
    string? TargetType,
    long? TargetId,
    string? Detail);

/// <summary>稽核查詢條件（全欄位選濾；發生時間左閉右開）。</summary>
public sealed class AuditLogQuery
{
    public string? Category { get; init; }
    public string? Actor { get; init; }
    public string? Action { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public int Limit { get; init; }
    public int Offset { get; init; }
}

/// <summary>
/// 稽核日誌倉儲（M109，§14.1「誰/何時/做了什麼」）：Record 追加（actor/action/category 空白→
/// <see cref="ArgumentException"/>；occurredAt 注入可測）、List 多條件篩選（時序 DESC）、
/// Count、PruneOlderThan（稽核保管期限清理）。自動記錄掛載點（登入/參數變更/匯出/共享）由
/// 呼叫端以 const 類別合作。
/// </summary>
public sealed class AuditLogRepository
{
    private readonly SqliteStore _store;

    public AuditLogRepository(SqliteStore store) => _store = store;

    /// <summary>追加稽核項目；回 Id。</summary>
    public long Record(
        string actor,
        string action,
        string category,
        string? targetType = null,
        long? targetId = null,
        string? detail = null,
        DateTime? occurredAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (targetType is not null && targetType.Length == 0)
        {
            throw new ArgumentException("目標類型不可為空字串。", nameof(targetType));
        }

        _store.Execute(
            """
            INSERT INTO audit_log (occurred_at, actor, action, category, target_type, target_id, detail)
            VALUES ($t, $a, $x, $c, $tt, $ti, $d);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(occurredAtUtc ?? DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$a", actor);
                cmd.Parameters.AddWithValue("$x", action);
                cmd.Parameters.AddWithValue("$c", category);
                cmd.Parameters.AddWithValue("$tt", (object?)targetType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ti", (object?)targetId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            });
        return _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public IReadOnlyList<AuditLogEntry> List(AuditLogQuery q)
    {
        var where = new List<string>();
        if (q.Category is { Length: > 0 }) where.Add("category = $category");
        if (q.Actor is { Length: > 0 }) where.Add("actor = $actor");
        if (q.Action is { Length: > 0 }) where.Add("action = $action");
        if (q.FromUtc is { } f) where.Add("occurred_at >= $from");
        if (q.ToUtc is { } t) where.Add("occurred_at < $to");

        var sql = "SELECT id, occurred_at, actor, action, category, target_type, target_id, detail FROM audit_log";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        sql += " ORDER BY occurred_at DESC, id DESC";
        if (q.Limit > 0)
        {
            sql += " LIMIT $lim";
            if (q.Offset > 0)
            {
                sql += " OFFSET $off";
            }
        }

        return _store.Query(sql, reader =>
        {
            var rows = new List<AuditLogEntry>();
            while (reader.Read())
            {
                rows.Add(new AuditLogEntry(
                    reader.GetInt64(0),
                    SqliteStore.FromIso(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return rows;
        }, cmd =>
        {
            if (q.Category is { Length: > 0 }) cmd.Parameters.AddWithValue("$category", q.Category);
            if (q.Actor is { Length: > 0 }) cmd.Parameters.AddWithValue("$actor", q.Actor);
            if (q.Action is { Length: > 0 }) cmd.Parameters.AddWithValue("$action", q.Action);
            if (q.FromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (q.ToUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
            if (q.Limit > 0)
            {
                cmd.Parameters.AddWithValue("$lim", q.Limit);
                if (q.Offset > 0)
                {
                    cmd.Parameters.AddWithValue("$off", q.Offset);
                }
            }
        });
    }

    public int Count(AuditLogQuery q)
    {
        var where = new List<string>();
        if (q.Category is { Length: > 0 }) where.Add("category = $category");
        if (q.Actor is { Length: > 0 }) where.Add("actor = $actor");
        if (q.Action is { Length: > 0 }) where.Add("action = $action");
        if (q.FromUtc is { } f) where.Add("occurred_at >= $from");
        if (q.ToUtc is { } t) where.Add("occurred_at < $to");

        var sql = "SELECT COUNT(*) FROM audit_log";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        return _store.Query<int>(sql, r => r.Read() ? r.GetInt32(0) : 0, cmd =>
        {
            if (q.Category is { Length: > 0 }) cmd.Parameters.AddWithValue("$category", q.Category);
            if (q.Actor is { Length: > 0 }) cmd.Parameters.AddWithValue("$actor", q.Actor);
            if (q.Action is { Length: > 0 }) cmd.Parameters.AddWithValue("$action", q.Action);
            if (q.FromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (q.ToUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
        });
    }

    /// <summary>清理比 <paramref name="beforeUtc"/> 更舊的稽核項目；回傳刪除筆數。</summary>
    public int PruneOlderThan(DateTime beforeUtc)
    {
        _store.Execute(
            "DELETE FROM audit_log WHERE occurred_at < $before;",
            cmd => cmd.Parameters.AddWithValue("$before", SqliteStore.Iso(beforeUtc)));
        return _store.Query<int>("SELECT changes();", r => r.Read() ? r.GetInt32(0) : 0);
    }
}