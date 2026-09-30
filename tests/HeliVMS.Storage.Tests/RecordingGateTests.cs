using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Storage.Tests;

/// <summary>錄影閘門（§19.4「超限行為」「到期行為」「回流時鐘防護」）。</summary>
public class RecordingGateTests : IDisposable
{
    // 測試金鑰對：注入對應的 LicenseManager，故不需動 EmbeddedPublicKey。
    private static readonly RSA TestKey = RSA.Create(2048);
    private static readonly string TestPublicPem = RsaPem.ToPublicPem(TestKey);

    private const int ExpiredChannel = 900000001;
    private const int OtherChannel = 900000002;

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly LicenseService _service;
    private readonly AuditLogRepository _audit;
    private readonly LicenseRepository _licenses;
    private readonly DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public RecordingGateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-gate-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new LicenseService(_store, new LicenseManager(TestPublicPem));
        _audit = new AuditLogRepository(_store);
        _licenses = new LicenseRepository(_store);
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
    public void NoLicense_BlocksRecording()
    {
        var gate = _service.CheckRecording(ExpiredChannel, _now, RecordingGateSources.Manual);

        Assert.False(gate.Allowed);
        Assert.Equal(LicenseDecision.NotPresent, gate.Decision);
        Assert.Equal(0, gate.MaxCameras);
        Assert.Equal(LicenseService.NotPresentMessage, gate.Reason);
    }

    [Fact]
    public void WithinCameraLimit_Allows()
    {
        Apply(cameras: 32, features: ["core", "ai"]);

        var gate = _service.CheckRecording(32, _now, RecordingGateSources.Manual);

        Assert.True(gate.Allowed);
        Assert.Equal(32, gate.MaxCameras);
        Assert.Null(gate.Reason);
    }

    [Fact]
    public void BeyondCameraLimit_BlocksWithMessageAndEventAudit()
    {
        Apply(cameras: 4, features: ["core", "ai"]);

        var gate = _service.CheckRecording(5, _now, RecordingGateSources.Manual);

        Assert.False(gate.Allowed);
        Assert.Equal(LicenseDecision.Valid, gate.Decision);
        Assert.Equal(4, gate.MaxCameras);
        Assert.Contains("已達授權上限（4 路）", gate.Reason!, StringComparison.Ordinal);
        Assert.Contains("頻道 5", gate.Reason!, StringComparison.Ordinal);

        // 超限屬「有效授權但額度用完」，Decision 保持 Valid，UI 以此區分要不要寫事件中心。
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.recording_blocked" }));
        Assert.Equal(AuditCategories.License, audit.Category);
        Assert.Equal("channel", audit.TargetType);
        Assert.Equal(5, audit.TargetId);
        Assert.Equal(RecordingGateSources.Manual, audit.Actor);
    }

    [Fact]
    public void CameraLimit_UsesChannelIdNotRecordingCount()
    {
        // 刪掉中間的頻道不應讓後面的頻道獲得資格：判定只看 channelId 與上限。
        Apply(cameras: 4, features: ["core", "ai"]);

        Assert.True(_service.CheckRecording(4, _now, RecordingGateSources.Schedule).Allowed);
        Assert.False(_service.CheckRecording(6, _now, RecordingGateSources.Schedule).Allowed);
    }

    [Fact]
    public void ExpiredLicense_BlocksNewRecordingButKeepsGateMaxCameras()
    {
        Apply(cameras: 32, features: ["core", "ai"], expires: _now.AddDays(30));
        Assert.True(_service.CheckRecording(1, _now, RecordingGateSources.Manual).Allowed);

        var gate = _service.CheckRecording(1, _now.AddDays(31), RecordingGateSources.Manual);

        Assert.False(gate.Allowed);
        Assert.Equal(LicenseDecision.Expired, gate.Decision);
        Assert.Equal(32, gate.MaxCameras);
        Assert.Contains("既有錄影仍可回放", gate.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RevokedLicense_BlocksRecording()
    {
        Apply(cameras: 32, features: ["core", "ai"]);
        _licenses.Revoke(_service.Current()!.Id, "admin", "退貨", _now.AddDays(1));

        var gate = _service.CheckRecording(1, _now.AddDays(2), RecordingGateSources.Manual);

        Assert.False(gate.Allowed);
        Assert.Equal(LicenseDecision.Revoked, gate.Decision);
        Assert.Equal(LicenseService.RevokedMessage, gate.Reason);
    }

    [Fact]
    public void TimeRollback_StatusIsStickyUntilRevalidated()
    {
        // 閘門只讀不寫，故 time_rollback 由「重新驗證」寫入授權列；一旦寫入就必須靠再次
        // 驗證解除，Evaluate 不得因高水位已過就自行放行（否則重開機等於解封）。
        var token = Token(cameras: 32, features: ["core", "ai"], _now.AddDays(365));
        _service.Apply(token, "admin", _now);

        var rolled = _service.Apply(token, "admin", _now.AddDays(-30));
        Assert.Equal(LicenseDecision.TimeRollback, rolled.Decision);
        Assert.Equal(LicenseStatuses.TimeRollback, _service.Current()!.Status);

        var corrected = _service.CheckRecording(1, _now.AddDays(1), RecordingGateSources.Manual);
        Assert.False(corrected.Allowed);
        Assert.Equal(LicenseDecision.TimeRollback, corrected.Decision);

        // 重新驗證（匯入授權）後才恢復。
        _service.Apply(token, "admin", _now.AddDays(1));
        Assert.Equal(LicenseStatuses.Active, _service.Current()!.Status);
        Assert.True(_service.CheckRecording(1, _now.AddDays(1), RecordingGateSources.Manual).Allowed);
    }

    [Fact]
    public void TimeRollback_ClockFixedInSameSession_AllowsWithoutReimport()
    {
        // 閘門不寫授權列，故同一程序內把時鐘校回來即可恢復（使用者已自行修正）。
        // 跨程序則走上面的 sticky 路徑：下次啟動 Apply 看到 time_rollback 需重新驗證才放行。
        var token = Token(cameras: 32, features: ["core", "ai"], _now.AddDays(365));
        _service.Apply(token, "admin", _now);

        Assert.False(_service.CheckRecording(1, _now.AddDays(-30), RecordingGateSources.Manual).Allowed);

        var fixedClock = _service.CheckRecording(1, _now.AddMinutes(1), RecordingGateSources.Manual);
        Assert.True(fixedClock.Allowed);
        Assert.Equal(LicenseStatuses.Active, _service.Current()!.Status);
    }

    [Fact]
    public void Evaluate_DoesNotWriteTableOrAudit()
    {
        Apply(cameras: 32, features: ["core", "ai"]);
        var auditsBefore = _audit.List(new AuditLogQuery { Category = AuditCategories.License }).Count;
        var verified = _service.Current()!.LastVerifiedUtc;

        _service.Evaluate(_now.AddMinutes(30));
        _service.CheckRecording(1, _now.AddMinutes(30), RecordingGateSources.Schedule);

        Assert.Equal(auditsBefore, _audit.List(new AuditLogQuery { Category = AuditCategories.License }).Count);
        Assert.Equal(verified, _service.Current()!.LastVerifiedUtc);
    }

    [Fact]
    public void Rejection_IsAuditedOncePerChannelReasonSource()
    {
        // 排程器每 30 秒調和一次；若每次都寫，使用者只看得到幾萬筆同樣的拒絕紀錄。
        for (var i = 0; i < 50; i++)
        {
            _service.CheckRecording(ExpiredChannel, _now.AddSeconds(i * 30), RecordingGateSources.Schedule);
        }

        Assert.Single(_audit.List(new AuditLogQuery { Action = "license.recording_blocked" }));

        // 不同來源是不同事實，須各自留痕。
        _service.CheckRecording(ExpiredChannel, _now, RecordingGateSources.Manual);
        Assert.Equal(2, _audit.List(new AuditLogQuery { Action = "license.recording_blocked" }).Count);
    }

    [Fact]
    public void Rejection_DifferentChannelsAuditedSeparately()
    {
        Apply(cameras: 4, features: ["core", "ai"]);

        _service.CheckRecording(5, _now, RecordingGateSources.Schedule);
        _service.CheckRecording(ExpiredChannel, _now, RecordingGateSources.Schedule);

        Assert.Equal(2, _audit.List(new AuditLogQuery { Action = "license.recording_blocked" }).Count);
    }

    [Fact]
    public void Evaluate_NoLicenseRow_NotPresent()
    {
        var result = _service.Evaluate(_now);

        Assert.Equal(LicenseDecision.NotPresent, result.Decision);
        Assert.Null(result.Record);
        Assert.Null(result.State);
    }

    [Fact]
    public void Evaluate_ActiveLicense_IsValid()
    {
        Apply(cameras: 32, features: ["core", "ai"]);

        var result = _service.Evaluate(_now);

        Assert.Equal(LicenseDecision.Valid, result.Decision);
        Assert.Equal(32, result.MaxCameras);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Evaluate_ExpiredAtExactlyNow_IsExpired()
    {
        Apply(cameras: 32, features: ["core", "ai"], expires: _now);

        Assert.Equal(LicenseDecision.Expired, _service.Evaluate(_now).Decision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CheckRecording_NonPositiveChannel_Throws(int channelId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _service.CheckRecording(channelId, _now, RecordingGateSources.Manual));
    }

    private void Apply(int cameras, string[] features, DateTime? expires = null)
        => _service.Apply(Token(cameras, features, expires), "admin", _now);

    /// <summary>以測試金鑰簽發一張不限機器的測試授權（見類別說明）。</summary>
    private static string Token(int cameras, string[] features, DateTime? expires)
        => LicenseSerializer.Sign(
            new LicensePayload
            {
                Id = Guid.NewGuid().ToString("N"),
                Machine = string.Empty,
                IssuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresUtc = expires,
                Cameras = cameras,
                Features = features,
                Issuer = "測試",
            },
            TestKey);
}
