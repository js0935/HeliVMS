using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HeliVMS.WebApi;

/// <summary>一次帳號登入發出的工作階段（M306）。</summary>
/// <param name="Username">WPF 身分帳號的使用者名稱（來自 <c>AuthService.Authenticate</c>）。</param>
/// <param name="Role">帳號角色：<c>admin</c>／<c>viewer</c>。</param>
/// <param name="DisplayName">顯示名稱（可為 Null）。</param>
/// <param name="ExpiresUtc">過期時間；過期或登出後即失效。</param>
public sealed record AccountSession(
    string Username,
    string Role,
    string? DisplayName,
    DateTime ExpiresUtc);

/// <summary>
/// 網頁帳號登入的伺服器端工作階段（M306）。
///
/// 網頁登入（<c>POST /api/accounts/authenticate</c>）通過 <see cref="HeliVMS.Storage.AuthService"/>
/// 驗證後，由本暫存區簽發一個隨機 token，該 token 之後以 <c>Authorization: Bearer</c> 送出，
/// <see cref="ApiKeyAuthMiddleware"/> 把它當成與 API 金鑰同級的憑證。這樣「WEB 登入使用 WPF
/// 身分帳號」不需要先餵一顆 API 金鑰：登入成功本身就是授權。
///
/// <para>
/// 刻意用記憶體暫存而非簽章 token：登出要真的能撤銷，且工作階段只在主機存活期間有效——
/// 這是給同機瀏覽器用的管理介面，沒有跨行程分享的必要；主機重啟後瀏覽器重新登入即可。
/// 過期項目在讀取或新增時順手清除，避免暫存區無限成長（每台主機同時在線的使用者極少）。
/// </para>
/// </summary>
public sealed class AccountSessionStore
{
    /// <summary>工作階段有效期（12 小時）。過期後瀏覽器需重新登入。</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(12);

    /// <summary>每新增多少筆就掃一次過期項目；純粹避免字典只進不出。</summary>
    private const int PurgeEvery = 64;

    private readonly ConcurrentDictionary<string, AccountSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private int _sinceLastPurge;

    public AccountSessionStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>簽發新工作階段並回傳 token。token 為 32 位元組高強度亂數的 base64url。</summary>
    public string Create(string username, string role, string? displayName)
    {
        if (Interlocked.Increment(ref _sinceLastPurge) >= PurgeEvery)
        {
            Interlocked.Exchange(ref _sinceLastPurge, 0);
            PurgeExpired();
        }

        var token = GenerateToken();
        _sessions[token] = new AccountSession(username, role, displayName, _clock.GetUtcNow().UtcDateTime + SessionTtl);
        return token;
    }

    /// <summary>以 token 取回有效工作階段；不存在、已過期或已登出回 <c>false</c>。</summary>
    public bool TryGet(string token, out AccountSession session)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var found))
        {
            session = null!;
            return false;
        }

        if (found.ExpiresUtc <= _clock.GetUtcNow().UtcDateTime)
        {
            _sessions.TryRemove(token, out _);
            session = null!;
            return false;
        }

        session = found;
        return true;
    }

    /// <summary>登出：撤銷 token，之後任何請求都不能再用它。</summary>
    public void Revoke(string token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _sessions.TryRemove(token, out _);
        }
    }

    private void PurgeExpired()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var (token, session) in _sessions)
        {
            if (session.ExpiresUtc <= now)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    /// <summary>32 位元組 CSPRNG 亂數的 base64url 字串（無 <c>=</c> 填補）。</summary>
    internal static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}