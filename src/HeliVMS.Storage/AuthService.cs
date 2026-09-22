namespace HeliVMS.Storage;

/// <summary>登入驗證結果（M42，§18.6）。</summary>
public sealed record AuthResult(
    bool Succeeded,
    string Role,
    string? DisplayName,
    string Error)
{
    public static AuthResult Success(string role, string? displayName)
        => new(true, role, displayName, string.Empty);

    public static AuthResult Fail(string error)
        => new(false, "viewer", null, error);
}

/// <summary>
/// 本機登入驗證（M42，§18.6）：雜湊驗證＋失敗鎖定＋停用檢查。
/// 設定鍵：auth.enabled（0/1）、auth.lockout.threshold（預設 5）、auth.lockout.minutes（預設 5）。
/// </summary>
public sealed class AuthService
{
    private const string EnabledKey = "auth.enabled";
    private const string ThresholdKey = "auth.lockout.threshold";
    private const string MinutesKey = "auth.lockout.minutes";

    private readonly UserRepository _users;
    private readonly SettingsRepository _settings;
    private readonly AuditLogRepository _audit;

    public AuthService(SqliteStore store)
    {
        _users = new UserRepository(store);
        _settings = new SettingsRepository(store);
        _audit = new AuditLogRepository(store);
    }

    /// <summary>是否啟用登入（app_settings auth.enabled == "1"）。</summary>
    public bool IsAuthEnabled => _settings.GetOrDefault(EnabledKey, "0") == "1";

    /// <summary>連續失敗幾次鎖定（預設 5）。</summary>
    public int LockoutThreshold => (int)_settings.GetDoubleOrDefault(ThresholdKey, 5);

    /// <summary>鎖定分鐘數（預設 5）。</summary>
    public int LockoutMinutes => (int)_settings.GetDoubleOrDefault(MinutesKey, 5);

    /// <summary>
    /// 驗證登入（utcNow 供測試注入）。成功時清除失敗計數並寫 last_login。
    /// 失敗超過 threshold 自動鎖定。
    /// </summary>
    public AuthResult Authenticate(string username, string password, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var user = _users.GetByUsername(username);
        if (user is null)
        {
            _audit.Record(username, "login.fail", AuditCategories.Auth,
                targetType: "user", detail: "使用者不存在", occurredAtUtc: now);
            return AuthResult.Fail("帳號或密碼錯誤");
        }

        if (!user.Enabled)
        {
            _audit.Record(username, "login.fail", AuditCategories.Auth,
                targetType: "user", targetId: user.Id, detail: "帳號停用", occurredAtUtc: now);
            return AuthResult.Fail("帳號已停用");
        }

        if (user.LockedUntil is { Length: > 0 } lu && SqliteStore.FromIso(lu) > now)
        {
            _audit.Record(username, "login.fail", AuditCategories.Auth,
                targetType: "user", targetId: user.Id, detail: "帳號鎖定", occurredAtUtc: now);
            return AuthResult.Fail("帳號已鎖定，請稍後再試");
        }

        if (PasswordHasher.Verify(password, user.PasswordHash))
        {
            _users.RecordLoginSuccess(user.Id, now);
            _audit.Record(username, "login.ok", AuditCategories.Auth,
                targetType: "user", targetId: user.Id, detail: $"role={user.Role}", occurredAtUtc: now);
            return AuthResult.Success(user.Role, user.DisplayName);
        }

        var attempts = _users.RecordFailedLogin(user.Id, LockoutThreshold, LockoutMinutes, now);
        _audit.Record(username, "login.fail", AuditCategories.Auth,
            targetType: "user", targetId: user.Id,
            detail: $"密碼錯誤（剩餘 {LockoutThreshold - attempts} 次機會）", occurredAtUtc: now);
        return attempts >= LockoutThreshold
            ? AuthResult.Fail($"密碼錯誤，帳號已鎖定 {LockoutMinutes} 分鐘")
            : AuthResult.Fail($"密碼錯誤（剩餘 {LockoutThreshold - attempts} 次機會）");
    }
}