using System.Security.Cryptography;
using System.Text;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 產品端授權整合點（§19.4／§19.8）與回流時鐘防護。
/// </summary>
/// <remarks>
/// 測試需要以測試金鑰簽發的授權碼，但 <see cref="LicenseManager"/> 只認 <c>EmbeddedPublicKey</c>。
/// 因此本測試類別於測試金鑰對上「重簽」payload——簽章驗證以公鑰驗，與金鑰無關；
/// 機器綁定一律留空（不限機器）以免綁到測試機的實際設備碼。
/// </remarks>
public class LicenseServiceTests : IDisposable
{
    // 測試金鑰對：LicenseService 注入對應的 LicenseManager，故不必動 EmbeddedPublicKey。
    private static readonly RSA TestKey = RSA.Create(2048);
    private static readonly string TestPublicPem = RsaPem.ToPublicPem(TestKey);

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly LicenseService _service;
    private readonly AuditLogRepository _audit;
    private readonly DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public LicenseServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-licsvc-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new LicenseService(_store, new LicenseManager(TestPublicPem));
        _audit = new AuditLogRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Apply_ValidLicense_ActivatesAndAudits()
    {
        var token = Token(cameras: 32, features: ["core", "ai"], expires: _now.AddDays(90));

        var result = _service.Apply(token, "admin", _now);

        Assert.Equal(LicenseDecision.Valid, result.Decision);
        Assert.True(result.AllowsNewRecording);
        Assert.NotNull(result.Record);
        Assert.Equal(LicenseStatuses.Active, result.Record!.Status);
        Assert.Equal(32, result.Record.MaxCameras);
        Assert.Contains("license.activate", Actions());
    }

    [Fact]
    public void Apply_ValidLicense_ResolvesTierName()
    {
        // 進階版 = 32 路 ＋ core,ai,schedule,ai.l1,gis（旗標完整等於該等級才認定）。
        var token = Token(cameras: 32, features: ["core", "ai", "schedule", "ai.l1", "gis"]);

        var result = _service.Apply(token, "admin", _now);

        Assert.Equal("進階版", result.Record!.Tier);
    }

    [Fact]
    public void Apply_CamerasOverrideTierDefault_StoresPayloadCameras()
    {
        // 等級名稱只是簽發端的命名便利，通道上限一律以 payload.Cameras 為準（§19.3）。
        // 只有 core+ai 卻開 32 路時，等級名稱仍會標為基本版，但上限不得變成 4 路。
        var token = Token(cameras: 32, features: ["core", "ai"]);

        var result = _service.Apply(token, "admin", _now);

        Assert.Equal(32, result.Record!.MaxCameras);
        Assert.Equal(32, result.MaxCameras);
    }

    [Fact]
    public void Apply_NoToken_ReturnsNotPresentWithoutAudit()
    {
        var result = _service.Apply("   ", "system", _now);

        Assert.Equal(LicenseDecision.NotPresent, result.Decision);
        Assert.False(result.AllowsNewRecording);
        Assert.Empty(_audit.List(new AuditLogQuery { Category = AuditCategories.License }));
    }

    [Fact]
    public void Apply_ExpiredLicense_StopsNewRecordingAndAuditsOnce()
    {
        var token = Token(expires: _now.AddDays(-1));
        var result = _service.Apply(token, "admin", _now);

        Assert.Equal(LicenseDecision.Expired, result.Decision);
        Assert.False(result.AllowsNewRecording);
        Assert.Equal(LicenseStatuses.Expired, result.Record!.Status);

        // 再次驗證不重複寫入到期稽核（否則每次啟動都產生雜訊）。
        _service.Apply(token, "admin", _now.AddMinutes(5));
        Assert.Single(_audit.List(new AuditLogQuery { Action = "license.expire" }));
    }

    [Fact]
    public void Apply_Reissue_RecordsUpgradeAudit()
    {
        // 16 路 ＋ 4 旗標 = 專業版；64 路 ＋ 8 旗標 = 企業版。
        var first = Token(cameras: 16, features: ["core", "ai", "schedule", "ai.l1"]);
        _service.Apply(first, "admin", _now);
        var second = Token(cameras: 64, features: ["core", "ai", "schedule", "ai.l1", "ai.l2", "gis", "remote", "ad"]);

        var result = _service.Apply(second, "admin", _now.AddDays(3));

        Assert.Equal(LicenseStatuses.Active, result.Record!.Status);
        var upgrade = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.upgrade" }));
        Assert.Contains("金鑰已更換", upgrade.Detail, StringComparison.Ordinal);
        Assert.Contains("通道 16→64", upgrade.Detail, StringComparison.Ordinal);
        Assert.Contains("等級 專業版→企業版", upgrade.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_TamperedSignature_RejectedAndAudited()
    {
        var token = Token(cameras: 32, features: ["core", "ai"]);
        var parts = token.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";

        var result = _service.Apply(forged, "admin", _now);

        Assert.Equal(LicenseDecision.Invalid, result.Decision);
        Assert.False(result.AllowsNewRecording);
        Assert.Null(result.Record);
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.reject" }));
        Assert.Equal(AuditCategories.License, audit.Category);
    }

    [Fact]
    public void Apply_ForeignMachineCode_RejectedAndAudited()
    {
        var token = Token(cameras: 32, features: ["core", "ai"], machine: "0123456789ABCDEF0123456789ABCDEF");

        var result = _service.Apply(token, "admin", _now);

        Assert.Equal(LicenseDecision.MachineMismatch, result.Decision);
        Assert.False(result.AllowsNewRecording);
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.mismatch" }));
        Assert.Contains("0123456789ABCDEF0123456789ABCDEF", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RevokedLicense_Refused()
    {
        var token = Token(cameras: 32, features: ["core", "ai"]);
        var result = _service.Apply(token, "admin", _now);
        Assert.True(_service.Current()!.IsActive);

        new LicenseRepository(_store).Revoke(result.LicenseRowId!.Value, "admin", "退貨", _now.AddDays(1));

        var after = _service.Apply(token, "admin", _now.AddDays(2));
        Assert.Equal(LicenseDecision.Revoked, after.Decision);
        Assert.False(after.AllowsNewRecording);
    }

    [Fact]
    public void Apply_BlankActor_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => _service.Apply(Token(), "  ", _now));
    }

    [Fact]
    public void MaxSeen_AdvancesMonotonically()
    {
        var token = Token(cameras: 32, features: ["core", "ai"]);
        _service.Apply(token, "admin", _now);
        Assert.Equal(_now, _service.MaxSeenUtc());

        var later = _now.AddDays(2);
        _service.Apply(token, "admin", later);
        Assert.Equal(later, _service.MaxSeenUtc());
    }

    [Fact]
    public void Rollback_ClockMovedBackBeyondTolerance_DisablesAndAudits()
    {
        var token = Token(cameras: 32, features: ["core", "ai"], expires: _now.AddDays(365));
        _service.Apply(token, "admin", _now);
        var rolledBack = _now.AddDays(-8);

        var result = _service.Apply(token, "admin", rolledBack);

        Assert.Equal(LicenseDecision.TimeRollback, result.Decision);
        Assert.False(result.AllowsNewRecording);
        Assert.Equal(LicenseStatuses.TimeRollback, result.Record!.Status);
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.status" }));
        Assert.Contains("校正系統時鐘", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_WithinTolerance_StaysValid()
    {
        var token = Token(cameras: 32, features: ["core", "ai"], expires: _now.AddDays(365));
        _service.Apply(token, "admin", _now);

        var result = _service.Apply(token, "admin", _now.AddDays(-3));

        Assert.Equal(LicenseDecision.Valid, result.Decision);
    }

    [Fact]
    public void Rollback_ExpiredLicense_LongExpiredIsNotMistakenForRollback()
    {
        // 高水位只在驗證成功時推進，所以一張早已到期半年的授權不會被誤判成時鐘被改回。
        var token = Token(cameras: 32, features: ["core", "ai"], expires: _now.AddDays(1));
        _service.Apply(token, "admin", _now);
        Assert.Equal(_now, _service.MaxSeenUtc());

        var halfYearLater = _now.AddDays(180);
        var result = _service.Apply(token, "admin", halfYearLater);

        Assert.Equal(LicenseDecision.Expired, result.Decision);
        Assert.Equal(LicenseStatuses.Expired, result.Record!.Status);
        Assert.Equal(_now, _service.MaxSeenUtc());
    }

    [Fact]
    public void Rollback_ForwardJumpThenBack_Disables()
    {
        // 永久授權在未來時間驗證成功 → 高水位被推高；之後把時鐘調回即被攔下。
        var token = Token(cameras: 32, features: ["core", "ai"], expires: null);
        var future = _now.AddYears(5);
        _service.Apply(token, "admin", future);
        Assert.Equal(future, _service.MaxSeenUtc());

        var result = _service.Apply(token, "admin", _now);

        Assert.Equal(LicenseDecision.TimeRollback, result.Decision);
    }

    [Fact]
    public void Rollback_MaxSeenNotLoweredByRollback()
    {
        var token = Token(cameras: 32, features: ["core", "ai"], expires: _now.AddDays(365));
        _service.Apply(token, "admin", _now);

        _service.Apply(token, "admin", _now.AddDays(-30));

        Assert.Equal(_now, _service.MaxSeenUtc());
    }

    [Fact]
    public void Rollback_PermanentLicense_NotFlagged()
    {
        // 永久授權沒有到期時間可與 max_seen 比較，只有時鐘規則適用。
        var token = Token(cameras: 32, features: ["core", "ai"], expires: null);
        _service.Apply(token, "admin", _now);

        var result = _service.Apply(token, "admin", _now.AddDays(-1));

        Assert.Equal(LicenseDecision.Valid, result.Decision);
    }

    [Fact]
    public void Current_ReflectsStoredRow()
    {
        Assert.Null(_service.Current());
        _service.Apply(Token(cameras: 32, features: ["core", "ai"]), "admin", _now);

        var current = _service.Current();

        Assert.NotNull(current);
        Assert.Equal(32, current!.MaxCameras);
        Assert.Equal(_service.DeviceCode, current.DeviceCode);
    }

    [Fact]
    public void MaxSeen_SettingKeyIsDocumented()
    {
        var token = Token(cameras: 32, features: ["core", "ai"]);
        _service.Apply(token, "admin", _now);

        var raw = new SettingsRepository(_store).Get(LicenseService.MaxSeenSettingKey);

        Assert.Equal(SqliteStore.Iso(_now), raw);
    }

    private List<string> Actions()
        => _audit.List(new AuditLogQuery { Category = AuditCategories.License })
            .Select(e => e.Action)
            .ToList();

    /// <summary>以測試金鑰簽發一張不限機器的測試授權（見類別說明）。</summary>
    private static string Token(
        int cameras = 32,
        string[]? features = null,
        DateTime? expires = null,
        string? machine = null)
        => LicenseSerializer.Sign(
            new LicensePayload
            {
                Id = Guid.NewGuid().ToString("N"),
                Machine = machine ?? string.Empty,
                IssuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresUtc = expires,
                Cameras = cameras,
                Features = features ?? ["core", "ai"],
                Issuer = "測試",
            },
            TestKey);
}
