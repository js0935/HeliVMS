using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>登入 session（M99，§14.7 #1）：簽發後以 session_id 驗證與撤銷。</summary>
public sealed record LoginSessionRecord(
    long Id,
    string SessionId,
    int UserId,
    string Username,
    string Role,
    string Provider,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt);

/// <summary>
/// 登入 session 存取（M99，§14.7 #1）：企業登入（LDAP/OIDC）成功後簽發 session；
/// <see cref="IsActive"/> 以「未撤銷且未過期」判定（供 UI/後端逐一驗證）；時間 UTC ISO8601。
/// </summary>
public sealed class LoginSessionRepository
{
    private readonly SqliteStore _store;

    public LoginSessionRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>簽發新 session（session_id 為 32 位元組加密安全亂數），回傳 session_id。</summary>
    public string Create(int userId, string username, string role, string provider, TimeSpan ttl, DateTime utcNow)
    {
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _store.Execute(
            """
            INSERT INTO login_sessions (session_id, user_id, username, role, provider, issued_at, expires_at)
            VALUES ($s, $u, $n, $r, $p, $i, $e);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", sessionId);
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.Parameters.AddWithValue("$n", username);
                cmd.Parameters.AddWithValue("$r", role);
                cmd.Parameters.AddWithValue("$p", provider);
                cmd.Parameters.AddWithValue("$i", SqliteStore.Iso(utcNow));
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(utcNow + ttl));
            });
        return sessionId;
    }

    /// <summary>以 session_id 讀取；不存在→null。</summary>
    public LoginSessionRecord? Get(string sessionId)
    {
        return _store.Query(
            """
            SELECT id, session_id, user_id, username, role, provider, issued_at, expires_at, revoked_at
            FROM login_sessions WHERE session_id = $s;
            """,
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$s", sessionId));
    }

    /// <summary>session 是否仍有效（未撤銷且未過期）。</summary>
    public bool IsActive(string sessionId, DateTime utcNow)
    {
        var session = Get(sessionId);
        if (session is null)
        {
            return false;
        }

        return session.RevokedAt is null && session.ExpiresAt > utcNow;
    }

    /// <summary>撤銷 session（已撤銷則無動作）。</summary>
    public void Revoke(string sessionId, DateTime utcNow)
    {
        _store.Execute(
            """
            UPDATE login_sessions
            SET revoked_at = $t
            WHERE session_id = $s AND revoked_at IS NULL;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", sessionId);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(utcNow));
            });
    }

    /// <summary>清除已過期 session（置換/稽核清理），回傳刪除筆數。</summary>
    public int PurgeExpired(DateTime utcNow)
    {
        return _store.Query(
            """
            DELETE FROM login_sessions WHERE expires_at <= $now;
            SELECT changes();
            """,
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$now", SqliteStore.Iso(utcNow)));
    }

    private static LoginSessionRecord Read(Microsoft.Data.Sqlite.SqliteDataReader r)
        => new(
            r.GetInt64(0),
            r.GetString(1),
            r.GetInt32(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            SqliteStore.FromIso(r.GetString(6)),
            SqliteStore.FromIso(r.GetString(7)),
            r.IsDBNull(8) ? null : SqliteStore.FromIso(r.GetString(8)));
}