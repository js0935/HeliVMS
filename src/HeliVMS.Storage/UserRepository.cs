using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>本機使用者（M42，§18.6）。角色 admin／viewer。</summary>
public sealed record UserRecord(
    int Id,
    string Username,
    string PasswordHash,
    string Role,
    string? DisplayName,
    bool Enabled,
    int FailedLogins,
    string? LockedUntil,
    string? LastLogin);

/// <summary>
/// 本機使用者存取（M42，§18.6）。密碼一律經 <see cref="PasswordHasher"/> 雜湊後進庫。
/// </summary>
public sealed class UserRepository
{
    private readonly SqliteStore _store;

    public UserRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>建立使用者（username 大小寫不敏感唯一；重複拋 SqliteException）。回傳新 ID。</summary>
    public int CreateUser(string username, string passwordHash, string role, string? displayName = null)
    {
        if (!string.Equals(role, "admin", StringComparison.Ordinal) &&
            !string.Equals(role, "viewer", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "角色僅允許 admin/viewer");
        }

        return _store.Query(
            """
            INSERT INTO users (username, password_hash, role, display_name, enabled, failed_logins, created_at)
            VALUES ($u, $p, $r, $d, 1, 0, $t);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$u", username);
                cmd.Parameters.AddWithValue("$p", passwordHash);
                cmd.Parameters.AddWithValue("$r", role);
                cmd.Parameters.AddWithValue("$d", (object?)displayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(DateTime.UtcNow));
            });
    }

    /// <summary>依使用者名稱查詢（大小寫不敏感）。</summary>
    public UserRecord? GetByUsername(string username)
    {
        return _store.Query(
            """
            SELECT id, username, password_hash, role, display_name, enabled,
                   failed_logins, locked_until, last_login
            FROM users WHERE username = $u COLLATE NOCASE LIMIT 1;
            """,
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$u", username));
    }

    /// <summary>依 ID 查詢。</summary>
    public UserRecord? GetById(int id)
    {
        return _store.Query(
            """
            SELECT id, username, password_hash, role, display_name, enabled,
                   failed_logins, locked_until, last_login
            FROM users WHERE id = $id;
            """,
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>列出全部使用者（依 username 排序）。</summary>
    public IReadOnlyList<UserRecord> ListUsers()
    {
        return _store.Query(
            """
            SELECT id, username, password_hash, role, display_name, enabled,
                   failed_logins, locked_until, last_login
            FROM users ORDER BY username COLLATE NOCASE;
            """,
            static r =>
            {
                var list = new List<UserRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            });
    }

    /// <summary>啟用/停用使用者（停用＝無法登入）。</summary>
    public void SetEnabled(int id, bool enabled)
    {
        _store.Execute(
            "UPDATE users SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>設定角色（admin/viewer）。</summary>
    public void SetRole(int id, string role)
    {
        if (!string.Equals(role, "admin", StringComparison.Ordinal) &&
            !string.Equals(role, "viewer", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "角色僅允許 admin/viewer");
        }

        _store.Execute(
            "UPDATE users SET role = $r WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$r", role);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>設定顯示名稱（null 清除）。</summary>
    public void SetDisplayName(int id, string? displayName)
    {
        _store.Execute(
            "UPDATE users SET display_name = $d WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", (object?)displayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除使用者。</summary>
    public void DeleteUser(int id)
    {
        _store.Execute(
            "DELETE FROM users WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>
    /// 登入失敗計數：failed_logins+1；若累計 ≥ threshold 則設鎖定期間（逾時自動失效）。
    /// 回傳新的失敗次數。
    /// </summary>
    public int RecordFailedLogin(int id, int threshold, int lockoutMinutes, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var lockedUntil = SqliteStore.Iso(now.AddMinutes(lockoutMinutes));
        _store.Execute(
            """
            UPDATE users
            SET failed_logins = failed_logins + 1,
                locked_until  = CASE WHEN failed_logins + 1 >= $th THEN $lu ELSE locked_until END
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$th", threshold);
                cmd.Parameters.AddWithValue("$lu", lockedUntil);
            });

        return _store.Query(
            "SELECT failed_logins FROM users WHERE id = $id;",
            static r => r.Read() ? r.GetInt32(0) : -1,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>登入成功：清除失敗計數與鎖定、寫入 last_login。</summary>
    public void RecordLoginSuccess(int id, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        _store.Execute(
            """
            UPDATE users
            SET failed_logins = 0, locked_until = NULL, last_login = $t
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(now));
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>手動解除鎖定並清空失敗計數（供身份頁/測試）。</summary>
    public void ClearLock(int id)
    {
        _store.Execute(
            "UPDATE users SET failed_logins = 0, locked_until = NULL WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>是否仍處於鎖定期（locked_until &gt; utcNow 才算）。</summary>
    public bool IsLocked(int id, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var lockedUntil = GetById(id)?.LockedUntil;
        return lockedUntil is { Length: > 0 } lu &&
               SqliteStore.FromIso(lu) > now;
    }

    private static UserRecord Read(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.GetInt32(5) != 0,
            r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8));
}