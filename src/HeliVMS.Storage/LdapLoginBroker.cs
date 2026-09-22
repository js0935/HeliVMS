namespace HeliVMS.Storage;

/// <summary>LDAP 登入結果（M99，§14.7 #1）：含簽發之 session_id。</summary>
public sealed record LdapLoginResult(
    bool Succeeded,
    string? SessionId,
    string Role,
    string? DisplayName,
    string Error)
{
    public static LdapLoginResult Success(string sessionId, string role, string? displayName)
        => new(true, sessionId, role, displayName, string.Empty);

    public static LdapLoginResult Fail(string error)
        => new(false, null, "viewer", null, error);
}

/// <summary>
/// 企業登入落地（M99，§14.7 #1）：把 LDAP 綁定驗證閉合成「登入」——
/// 成功＝對映本地 RBAC 角色、同步 users 鏡像列（password_hash 為不可驗證記號，本機密碼登入對
/// 企業鏡像無效）、清除失敗計數並簽發 <see cref="LoginSessionRepository"/> session；
/// 失敗＝既有鏡像列計入失敗鎖定（沿用 auth.lockout.threshold/minutes，與 M42 本機登入同鍵）。
/// 停用/鎖定鏡像列先於綁定檢查（停用即管理端 kill-switch，連綁定都不做）。
/// </summary>
public sealed class LdapLoginBroker
{
    /// <summary>企業鏡像列的不可驗證密碼記號（PasswordHasher.Verify 必 false：格式不符）。</summary>
    public const string NoLocalPasswordHash = "v1$0$!";

    private const string ThresholdKey = "auth.lockout.threshold";
    private const string MinutesKey = "auth.lockout.minutes";

    private readonly UserRepository _users;
    private readonly LoginSessionRepository _sessions;
    private readonly SettingsRepository _settings;

    public LdapLoginBroker(SqliteStore store)
    {
        _users = new UserRepository(store);
        _sessions = new LoginSessionRepository(store);
        _settings = new SettingsRepository(store);
    }

    /// <summary>
    /// 以 LDAP（prov）/simple bind 驗證並簽發登入 session。utcNow 供測試注入。
    /// </summary>
    public LdapLoginResult Login(
        AuthProviderRecord provider,
        string username,
        string password,
        ILdapBinder binder,
        TimeSpan sessionTtl,
        DateTime? utcNow = null)
    {
        if (!string.Equals(provider.Kind, AuthProviderRepository.KindLdap, StringComparison.Ordinal))
        {
            return LdapLoginResult.Fail("提供者種類不是 LDAP");
        }

        var now = utcNow ?? DateTime.UtcNow;
        var mirror = _users.GetByUsername(username);

        if (mirror is not null)
        {
            if (!mirror.Enabled)
            {
                return LdapLoginResult.Fail("帳號已停用");
            }

            if (_users.IsLocked(mirror.Id, now))
            {
                return LdapLoginResult.Fail("帳號已鎖定，請稍後再試");
            }
        }

        var settings = LdapSettings.FromJson(provider.ConfigJson);
        var groups = binder.Bind(settings, username, password);
        if (groups is null)
        {
            if (mirror is not null)
            {
                var threshold = (int)_settings.GetDoubleOrDefault(ThresholdKey, 5);
                var minutes = (int)_settings.GetDoubleOrDefault(MinutesKey, 5);
                var attempts = _users.RecordFailedLogin(mirror.Id, threshold, minutes, now);
                return attempts >= threshold
                    ? LdapLoginResult.Fail($"LDAP 驗證失敗，帳號已鎖定 {minutes} 分鐘")
                    : LdapLoginResult.Fail($"LDAP 驗證失敗（剩餘 {threshold - attempts} 次機會）");
            }

            return LdapLoginResult.Fail("LDAP 驗證失敗");
        }

        var role = RoleMapper.Map(groups, settings.AdminGroups, settings.DefaultRole);
        var userId = mirror?.Id ?? _users.CreateUser(username, NoLocalPasswordHash, role, username);
        if (mirror is not null)
        {
            _users.SetRole(userId, role);
            _users.SetDisplayName(userId, username);
        }

        _users.RecordLoginSuccess(userId, now);
        var sessionId = _sessions.Create(userId, username, role, AuthProviderRepository.KindLdap, sessionTtl, now);
        return LdapLoginResult.Success(sessionId, role, username);
    }
}