// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using HeliVMS.Helpers;
using HeliVMS.Models;

namespace HeliVMS.Services;

public class AuthenticationService : IAuthenticationService {
    private readonly IUserService _userService;
    private readonly IEventService _eventLog;
    private static readonly string _sessionPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "session.json");

    private User? _pending2FAUser;

    // ── 帳號鎖定 ──
    private static readonly ConcurrentDictionary<string, (int Attempts, DateTime LockoutEnd)> _failedAttempts = new();
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    // ── Session 逾時 ──
    private Timer? _sessionTimer;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

    public User? CurrentUser { get; private set; }
    public bool IsLoggedIn => CurrentUser is not null;

    public event Action<User>? LoginSucceeded;
    public event Action? LoggedOut;
    public event Action? SessionExpired;

    public AuthenticationService(IUserService userService, IEventService eventLog) {
        _userService = userService;
        _eventLog = eventLog;
        TryRestoreSession();
    }

    public bool IsAccountLocked(string username) {
        if (_failedAttempts.TryGetValue(username, out var entry)) {
            if (entry.Attempts < MaxFailedAttempts) return false;
            if (DateTime.UtcNow < entry.LockoutEnd) return true;
            _failedAttempts.TryRemove(username, out _);
        }
        return false;
    }

    public int GetRemainingLockoutMinutes(string username) {
        if (_failedAttempts.TryGetValue(username, out var entry)) {
            if (entry.Attempts < MaxFailedAttempts) return 0;
            if (DateTime.UtcNow < entry.LockoutEnd) {
                return (int)(entry.LockoutEnd - DateTime.UtcNow).TotalMinutes + 1;
            }
            _failedAttempts.TryRemove(username, out _);
        }
        return 0;
    }

    private void RecordFailedAttempt(string username) {
        var (Attempts, LockoutEnd) = _failedAttempts.AddOrUpdate(username,
            _ => (1, default),
            (_, existing) => {
                var attempts = existing.Attempts + 1;
                var lockoutEnd = attempts >= MaxFailedAttempts
                    ? DateTime.UtcNow + LockoutDuration
                    : existing.LockoutEnd;
                return (attempts, lockoutEnd);
            });

        if (Attempts >= MaxFailedAttempts) {
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService",
                "帳號鎖定：{Username} 已達 {MaxFailedAttempts} 次失敗，鎖定 {LockoutMinutes} 分鐘");
        }
    }

    private static void ResetFailedAttempts(string username) {
        _failedAttempts.TryRemove(username, out _);
    }

    public void ResetSessionTimer() {
        _sessionTimer?.Change(SessionTimeout, Timeout.InfiniteTimeSpan);
    }

    public void StopSessionTimer() {
        _sessionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StartSessionTimer() {
        _sessionTimer?.Dispose();
        _sessionTimer = new Timer(_ => {
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService",
                "Session 逾時，自動登出", "閒置超過 30 分鐘");
            CurrentUser = null;
            ClearSession();
            SessionExpired?.Invoke();
        }, null, SessionTimeout, Timeout.InfiniteTimeSpan);
    }

    public bool Login(string username, string password) {
        var result = LoginWithTwoFactorSupport(username, password);
        if (!result.Success) { return false; }
        if (result.RequiresTwoFactor) { return false; }
        return true;
    }

    public (bool Success, bool RequiresTwoFactor) LoginWithTwoFactorSupport(string username, string password) {
        if (IsAccountLocked(username)) {
            _ = GetRemainingLockoutMinutes(username);
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService",
                "登入失敗：{Username} 帳號已鎖定", "剩餘 {RemainMinutes} 分鐘");
            return (false, false);
        }

        var user = _userService.GetUserByUsername(username);
        if (user is null || !user.IsEnabled) {
            RecordFailedAttempt(username);
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService", "登入失敗：使用者 {Username} 不存在或已停用");
            return (false, false);
        }

        var stored = user.PasswordHash;
        bool valid;

        if (stored.Contains(':')) {
            valid = VerifyPasswordPBKDF2(password, stored);
        } else {
            valid = stored == HashPasswordLegacySHA256(password);
        }

        if (!valid) {
            RecordFailedAttempt(username);
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService", "登入失敗：{Username} 密碼錯誤");
            return (false, false);
        }

        ResetFailedAttempts(username);

        if (!stored.Contains(':')) {
            user.PasswordHash = HashPasswordPBKDF2(password);
        }

        if (user.IsTwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret)) {
            _pending2FAUser = user;
            _eventLog.LogInfo(EventCategories.Security, "AuthenticationService", "使用者 {Username} 密碼驗證通過，等待雙因子驗證");
            return (true, true);
        }

        CompleteLogin(user);
        return (true, false);
    }

    public bool CompleteTwoFactorLogin(string code) {
        if (_pending2FAUser is null) {
            return false;
        }

        if (string.IsNullOrEmpty(_pending2FAUser.TwoFactorSecret)) {
            _pending2FAUser = null;
            return false;
        }

        if (!TotpHelper.VerifyCode(_pending2FAUser.TwoFactorSecret, code)) {
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService", "雙因子驗證失敗：{Username} 驗證碼錯誤");
            _pending2FAUser = null;
            return false;
        }

        CompleteLogin(_pending2FAUser);
        _pending2FAUser = null;
        return true;
    }

    public bool RequiresTwoFactor(string username) {
        var user = _userService.GetUserByUsername(username);
        return user is not null && user.IsTwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret);
    }

    public bool VerifyTwoFactor(string username, string code) {
        var user = _userService.GetUserByUsername(username);
        if (user is null || string.IsNullOrEmpty(user.TwoFactorSecret)) {
            return false;
        }
        return TotpHelper.VerifyCode(user.TwoFactorSecret, code);
    }

    public void SetupTwoFactor(string userId) {
        var user = _userService.GetUserById(userId);
        if (user is null) { return; }

        user.TwoFactorSecret = TotpHelper.GenerateSecret();
        user.IsTwoFactorEnabled = true;
        _userService.UpdateUser(user);
        _eventLog.LogInfo(EventCategories.Security, "AuthenticationService", "使用者 {Username} 已啟用雙因子驗證");
    }

    public bool DisableTwoFactor(string userId, string password) {
        var user = _userService.GetUserById(userId);
        if (user is null) { return false; }

        var stored = user.PasswordHash;
        bool valid;
        if (stored.Contains(':')) {
            valid = VerifyPasswordPBKDF2(password, stored);
        } else {
            valid = stored == HashPasswordLegacySHA256(password);
        }

        if (!valid) { return false; }

        user.TwoFactorSecret = null;
        user.IsTwoFactorEnabled = false;
        _userService.UpdateUser(user);
        _eventLog.LogInfo(EventCategories.Security, "AuthenticationService", "使用者 {Username} 已停用雙因子驗證");
        return true;
    }

    public string GetTwoFactorQrCodeUri(string userId) {
        var user = _userService.GetUserById(userId);
        if (user is null || string.IsNullOrEmpty(user.TwoFactorSecret)) {
            return string.Empty;
        }
        return TotpHelper.GenerateQrCodeUri(user.Username, user.TwoFactorSecret);
    }

    private void CompleteLogin(User user) {
        user.LastLoginAt = DateTime.Now;
        _userService.UpdateUser(user);
        CurrentUser = user;
        SaveSession(user.Id);
        StartSessionTimer();
        LoginSucceeded?.Invoke(user);
        _eventLog.LogInfo(EventCategories.Security, "AuthenticationService", "使用者 {Username} 登入成功", "角色: {UserRole}");
    }

    public void Logout() {
        StopSessionTimer();
        var user = CurrentUser;
        CurrentUser = null;
        ClearSession();
        _pending2FAUser = null;
        LoggedOut?.Invoke();
        if (user is not null) {
            _eventLog.LogInfo(EventCategories.Security, "AuthenticationService", "使用者 {Username} 登出");
        }
    }

    public bool ChangePassword(string oldPassword, string newPassword) {
        if (CurrentUser is null) { return false; }

        var storedHash = CurrentUser.PasswordHash;
        bool valid;

        if (storedHash.Contains(':')) {
            valid = VerifyPasswordPBKDF2(oldPassword, storedHash);
        } else {
            valid = storedHash == HashPasswordLegacySHA256(oldPassword);
        }

        if (!valid) {
            _eventLog.LogWarning(EventCategories.Security, "AuthenticationService", "變更密碼失敗：{Username} 舊密碼錯誤");
            return false;
        }

        CurrentUser.PasswordHash = HashPasswordPBKDF2(newPassword);
        _userService.UpdateUser(CurrentUser);
        _eventLog.LogInfo(EventCategories.Security, "AuthenticationService", "使用者 {Username} 變更密碼");
        return true;
    }

    /// <summary>PBKDF2 password hash (compatible with legacy project)</summary>
    public static string HashPasswordPBKDF2(string password) {
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 100000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    /// <summary>Verify PBKDF2 hash</summary>
    public static bool VerifyPasswordPBKDF2(string password, string storedHash) {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) { return false; }
        try {
            var salt = Convert.FromBase64String(parts[0]);
            var stored = Convert.FromBase64String(parts[1]);
            var computed = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, 100000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(stored, computed);
        } catch { return false; }
    }

    /// <summary>Legacy SHA256 hash (backward compatible)</summary>
    public static string HashPasswordLegacySHA256(string password) {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // 保留舊方法名稱供 UserService 編譯使用
    public static string HashPassword(string password) => HashPasswordPBKDF2(password);

    private static void SaveSession(string userId) {
        try {
            var dir = Path.GetDirectoryName(_sessionPath);
            if (dir is not null && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            File.WriteAllText(_sessionPath, JsonSerializer.Serialize(new { UserId = userId }));
        } catch { }
    }

    private static void ClearSession() {
        try { if (File.Exists(_sessionPath)) File.Delete(_sessionPath); } catch { }
    }

    private void TryRestoreSession() {
        try {
            if (!File.Exists(_sessionPath)) { return; }
            var json = File.ReadAllText(_sessionPath);
            var data = JsonSerializer.Deserialize<SessionData>(json);
            if (data is not null) {
                var user = _userService.GetUserById(data.UserId);
                if (user is not null) {
                    CurrentUser = user;
                }
            }
        } catch { ClearSession(); }
    }

    private record SessionData(string UserId);
}
