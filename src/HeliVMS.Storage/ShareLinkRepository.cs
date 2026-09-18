using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 外部安全共享連結（M51，§14.7 #4）：<c>token</c> 對應一項資源，可設到期／次數／密碼／撤銷。
/// </summary>
public sealed record ShareLinkRecord(
    int Id,
    string Token,
    string Kind,
    string ResourcePath,
    string? Label,
    string? PasswordHash,
    string CreatedAt,
    string? CreatedBy,
    string? ExpiresAt,
    int MaxUses,
    int UseCount,
    bool Revoked,
    string? LastUsedAt)
{
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
}

/// <summary>分享連結存取（M51，§14.7 #4）。</summary>
public sealed class ShareLinkRepository
{
    private const string Columns =
        "id, token, kind, resource_path, label, password_hash, created_at, created_by, " +
        "expires_at, max_uses, use_count, revoked, last_used_at";

    private readonly SqliteStore _store;

    public ShareLinkRepository(SqliteStore store)
    {
        _store = store;
    }

    public int Add(
        string token,
        string kind,
        string resourcePath,
        string? label,
        string? passwordHash,
        DateTime createdUtc,
        string? createdBy,
        DateTime? expiresUtc,
        int maxUses)
    {
        return _store.Query(
            $"""
            INSERT INTO share_links
                (token, kind, resource_path, label, password_hash, created_at, created_by, expires_at, max_uses, use_count, revoked)
            VALUES
                ($t, $k, $p, $l, $h, $c, $by, $e, $m, 0, 0);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", token);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$p", resourcePath);
                cmd.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$h", (object?)passwordHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", SqliteStore.Iso(createdUtc));
                cmd.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$e", expiresUtc is { } ex ? SqliteStore.Iso(ex) : DBNull.Value);
                cmd.Parameters.AddWithValue("$m", maxUses);
            });
    }

    public ShareLinkRecord? Get(int id)
        => _store.Query(
            $"SELECT {Columns} FROM share_links WHERE id = $id;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));

    public ShareLinkRecord? GetByToken(string token)
        => _store.Query(
            $"SELECT {Columns} FROM share_links WHERE token = $t LIMIT 1;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$t", token));

    public IReadOnlyList<ShareLinkRecord> List()
        => _store.Query(
            $"SELECT {Columns} FROM share_links ORDER BY id DESC;",
            static r =>
            {
                var list = new List<ShareLinkRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            });

    /// <summary>未撤銷且未到期（或無到期）之連結。</summary>
    public IReadOnlyList<ShareLinkRecord> ListActive(DateTime nowUtc)
        => _store.Query(
            $"""
            SELECT {Columns} FROM share_links
            WHERE revoked = 0 AND (expires_at IS NULL OR expires_at > $now)
            ORDER BY id DESC;
            """,
            static r =>
            {
                var list = new List<ShareLinkRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$now", SqliteStore.Iso(nowUtc)));

    public void IncrementUse(int id, DateTime usedUtc)
        => _store.Execute(
            "UPDATE share_links SET use_count = use_count + 1, last_used_at = $t WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(usedUtc));
                cmd.Parameters.AddWithValue("$id", id);
            });

    public void SetRevoked(int id, bool revoked)
        => _store.Execute(
            "UPDATE share_links SET revoked = $r WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$r", revoked ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });

    public void Delete(int id)
        => _store.Execute(
            "DELETE FROM share_links WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));

    /// <summary>刪除所有已到期連結，回傳刪除數。</summary>
    public int PurgeExpired(DateTime nowUtc)
    {
        var now = SqliteStore.Iso(nowUtc);
        var count = _store.Query(
            "SELECT COUNT(*) FROM share_links WHERE expires_at IS NOT NULL AND expires_at <= $now;",
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$now", now));

        if (count > 0)
        {
            _store.Execute(
                "DELETE FROM share_links WHERE expires_at IS NOT NULL AND expires_at <= $now;",
                cmd => cmd.Parameters.AddWithValue("$now", now));
        }

        return count;
    }

    private static ShareLinkRecord Read(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.GetInt32(9),
            r.GetInt32(10),
            r.GetInt32(11) != 0,
            r.IsDBNull(12) ? null : r.GetString(12));
}
