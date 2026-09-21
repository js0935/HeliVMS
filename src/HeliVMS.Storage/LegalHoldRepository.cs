namespace HeliVMS.Storage;

/// <summary>一筆保存鎖定（M66，§14.1 #13）：指定通道時段豁免配額汰除；revoked_* 表示已沖銷。</summary>
public sealed record LegalHoldRecord(
    long Id,
    int ChannelId,
    DateTime FromUtc,
    DateTime ToUtc,
    string Reason,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc = null,
    string? RevokedBy = null,
    string? RevokedReason = null)
{
    /// <summary>是否仍在作用中（未沖銷）。</summary>
    public bool IsActive => RevokedAtUtc is null;
}

/// <summary>
/// 保存鎖定存取（M66，§14.1 #13）。時間戳一律 ISO8601 UTC；沖銷為軟刪（填 revoked_*）以留稽核；
/// 沖銷之高權限把關於 App 層（admin）。
/// </summary>
public sealed class LegalHoldRepository
{
    private readonly SqliteStore _store;

    public LegalHoldRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>建立一筆保存鎖定，回傳 ID。條件：from &lt; to。</summary>
    public long Add(int channelId, DateTime fromUtc, DateTime toUtc, string reason, string createdBy, DateTime createdAtUtc)
    {
        if (channelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId), "頻道須為正整數。");
        }

        if (fromUtc >= toUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(toUtc), "訖時間須晚於起時間。");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("原因不可為空。", nameof(reason));
        }

        return _store.Query(
            """
            INSERT INTO legal_holds (channel_id, from_utc, to_utc, reason, created_by, created_at)
            VALUES ($c, $f, $t, $r, $b, $a);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$f", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(toUtc));
                cmd.Parameters.AddWithValue("$r", reason);
                cmd.Parameters.AddWithValue("$b", createdBy);
                cmd.Parameters.AddWithValue("$a", SqliteStore.Iso(createdAtUtc));
            });
    }

    /// <summary>全部尚在作用中（未沖銷）之鎖定，按起時升序。</summary>
    public IReadOnlyList<LegalHoldRecord> ListActive()
    {
        return _store.Query(
            """
            SELECT id, channel_id, from_utc, to_utc, reason, created_by, created_at,
                   revoked_at, revoked_by, revoked_reason
            FROM legal_holds
            WHERE revoked_at IS NULL
            ORDER BY from_utc;
            """,
            ReadRecords);
    }

    /// <summary>全部鎖定（含已沖銷），按起時降序。</summary>
    public IReadOnlyList<LegalHoldRecord> ListAll()
    {
        return _store.Query(
            """
            SELECT id, channel_id, from_utc, to_utc, reason, created_by, created_at,
                   revoked_at, revoked_by, revoked_reason
            FROM legal_holds
            ORDER BY from_utc DESC;
            """,
            ReadRecords);
    }

    /// <summary>沖銷（軟刪）鎖定；回傳是否實際生效（不存在或已沖銷→false）。</summary>
    public bool Revoke(long id, string by, string reason, DateTime atUtc)
    {
        var active = _store.Query(
            "SELECT EXISTS(SELECT 1 FROM legal_holds WHERE id = $id AND revoked_at IS NULL);",
            static r =>
            {
                r.Read();
                return r.GetInt32(0) == 1;
            },
            cmd => cmd.Parameters.AddWithValue("$id", id));
        if (!active)
        {
            return false;
        }

        _store.Execute(
            """
            UPDATE legal_holds
            SET revoked_at = $t, revoked_by = $b, revoked_reason = $r
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(atUtc));
                cmd.Parameters.AddWithValue("$b", by);
                cmd.Parameters.AddWithValue("$r", reason);
                cmd.Parameters.AddWithValue("$id", id);
            });
        return true;
    }

    /// <summary>該頻道時間範圍是否與任一作用中鎖定重疊（含端點）。</summary>
    public bool IsLocked(int channelId, DateTime startUtc, DateTime endUtc)
    {
        return _store.Query(
            """
            SELECT EXISTS(
                SELECT 1 FROM legal_holds
                WHERE revoked_at IS NULL
                  AND channel_id = $c
                  AND to_utc >= $s
                  AND from_utc <= $e
            );
            """,
            static r =>
            {
                r.Read();
                return r.GetInt32(0) == 1;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", SqliteStore.Iso(startUtc));
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(endUtc));
            });
    }

    /// <summary>是否仍有任何作用中鎖定（供配額清理快速判斷）。</summary>
    public bool AnyActive()
    {
        return _store.Query(
            "SELECT EXISTS(SELECT 1 FROM legal_holds WHERE revoked_at IS NULL);",
            static r =>
            {
                r.Read();
                return r.GetInt32(0) == 1;
            });
    }

    private static IReadOnlyList<LegalHoldRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var list = new List<LegalHoldRecord>();
        while (reader.Read())
        {
            list.Add(new LegalHoldRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                SqliteStore.FromIso(reader.GetString(2)),
                SqliteStore.FromIso(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                SqliteStore.FromIso(reader.GetString(6)),
                reader.IsDBNull(7) ? null : SqliteStore.FromIso(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return list;
    }
}