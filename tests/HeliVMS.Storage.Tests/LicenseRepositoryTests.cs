namespace HeliVMS.Storage.Tests;

/// <summary>授權表存取（§19.7 v43 <c>license</c>）。</summary>
public class LicenseRepositoryTests : IDisposable
{
    private const string Code = "0123456789ABCDEF0123456789ABCDEF";
    private const string OtherCode = "FEDCBA9876543210FEDCBA9876543210";

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly LicenseRepository _repo;
    private readonly AuditLogRepository _audit;
    private readonly DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public LicenseRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-license-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new LicenseRepository(_store);
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
    public void Upsert_InsertsAllColumns()
    {
        var expires = _now.AddYears(1);

        var id = _repo.Upsert(
            "HELVMS-v2.a.b", Code, "lic-1", "進階版", 32, ["core", "ai"], "禾秝軟體開發團隊",
            expires, LicenseStatuses.Active, "admin", _now);

        var row = Assert.IsType<LicenseRecord>(_repo.Get(id));
        Assert.Equal("HELVMS-v2.a.b", row.KeyText);
        Assert.Equal(Code, row.DeviceCode);
        Assert.Equal("lic-1", row.LicenseId);
        Assert.Equal("進階版", row.Tier);
        Assert.Equal(32, row.MaxCameras);
        Assert.Equal(["core", "ai"], row.Features);
        Assert.Equal("禾秝軟體開發團隊", row.Issuer);
        Assert.Equal(expires, row.ExpiresUtc);
        Assert.Equal(_now, row.FirstSeenUtc);
        Assert.Equal(_now, row.LastVerifiedUtc);
        Assert.Equal(LicenseStatuses.Active, row.Status);
        Assert.Equal("admin", row.CreatedBy);
        Assert.True(row.IsActive);
    }

    [Fact]
    public void Upsert_NormalizesDeviceCodeToUpper()
    {
        _repo.Upsert("k", Code.ToLowerInvariant(), null, null, 8, ["core"], null, null,
            LicenseStatuses.Active, null, _now);

        Assert.NotNull(_repo.GetByDeviceCode(Code));
        Assert.NotNull(_repo.GetByDeviceCode(Code.ToLowerInvariant()));
    }

    [Fact]
    public void Upsert_NullOptionalFields()
    {
        _repo.Upsert("k", Code, null, null, 4, ["core"], null, null, LicenseStatuses.Active, null, _now);

        var row = Assert.Single(_repo.List());
        Assert.Null(row.LicenseId);
        Assert.Null(row.Tier);
        Assert.Null(row.Issuer);
        Assert.Null(row.ExpiresUtc);
        Assert.Null(row.CreatedBy);
    }

    [Fact]
    public void Upsert_SameDeviceCode_ReplacesLicenseAndKeepsFirstSeen()
    {
        var first = _repo.Upsert("k1", Code, "lic-1", "標準版", 8, ["core"], null,
            _now.AddDays(30), LicenseStatuses.Active, "admin", _now);
        var later = _now.AddDays(10);
        var second = _repo.Upsert("k2", Code, "lic-2", "企業版", 64, ["core", "ai"], null,
            _now.AddDays(300), LicenseStatuses.Active, "admin", later);

        Assert.Equal(first, second);
        var row = Assert.Single(_repo.List());
        Assert.Equal("k2", row.KeyText);
        Assert.Equal("lic-2", row.LicenseId);
        Assert.Equal(64, row.MaxCameras);
        Assert.Equal(_now, row.FirstSeenUtc);
        Assert.Equal(later, row.LastVerifiedUtc);
    }

    [Fact]
    public void Upsert_MarkVerifiedFalse_LeavesLastVerifiedUntouched()
    {
        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, null,
            LicenseStatuses.Active, "admin", _now);
        var later = _now.AddDays(5);
        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, null,
            LicenseStatuses.TimeRollback, "admin", later, markVerified: false);

        var row = Assert.Single(_repo.List());
        Assert.Equal(_now, row.LastVerifiedUtc);
        Assert.Equal(LicenseStatuses.TimeRollback, row.Status);
    }

    [Theory]
    [InlineData("", "code", LicenseStatuses.Active)]
    [InlineData("key", "  ", LicenseStatuses.Active)]
    [InlineData("key", "code", "")]
    public void Upsert_BlankRequired_Throws(string key, string code, string status)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => _repo.Upsert(key, code, null, null, 8, ["core"], null, null, status, null, _now));
    }

    [Fact]
    public void Upsert_NonPositiveCameras_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _repo.Upsert("k", Code, null, null, 0, ["core"], null, null,
                LicenseStatuses.Active, null, _now));
    }

    [Fact]
    public void Get_UnknownIdOrDevice_ReturnsNull()
    {
        Assert.Null(_repo.Get(999));
        Assert.Null(_repo.GetByDeviceCode(OtherCode));
    }

    [Fact]
    public void GetByDeviceCode_Blank_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => _repo.GetByDeviceCode("  "));
    }

    [Fact]
    public void RecordVerified_UpdatesTimestamp()
    {
        var id = _repo.Upsert("k", Code, "l", null, 8, ["core"], null, null,
            LicenseStatuses.Active, "admin", _now);
        var later = _now.AddDays(3);

        _repo.RecordVerified(id, later);

        Assert.Equal(later, _repo.Get(id)!.LastVerifiedUtc);
    }

    [Fact]
    public void RecordVerified_NonPositiveId_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.RecordVerified(0, _now));
    }

    [Fact]
    public void SetStatus_UpdatesAndAudits()
    {
        var id = _repo.Upsert("k", Code, "l", "進階版", 32, ["core"], null, null,
            LicenseStatuses.Active, "admin", _now);
        var at = _now.AddDays(400);

        _repo.SetStatus(id, LicenseStatuses.Expired, "system", at, "到期");

        Assert.Equal(LicenseStatuses.Expired, _repo.Get(id)!.Status);
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.status" }));
        Assert.Equal(AuditCategories.License, audit.Category);
        Assert.Equal(id, audit.TargetId);
        Assert.Contains("expired", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("到期", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Revoke_MarksRevokedAndAudits()
    {
        var id = _repo.Upsert("k", Code, "l", null, 8, ["core"], null, null,
            LicenseStatuses.Active, "admin", _now);

        Assert.True(_repo.Revoke(id, "admin", "退貨", _now.AddDays(1)));

        var row = _repo.Get(id)!;
        Assert.Equal(LicenseStatuses.Revoked, row.Status);
        Assert.False(row.IsActive);
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.revoke" }));
        Assert.Equal("退貨", audit.Detail);
    }

    [Fact]
    public void Revoke_UnknownId_ReturnsFalse()
    {
        Assert.False(_repo.Revoke(4242, "admin", "無此授權", _now));
        Assert.Empty(_audit.List(new AuditLogQuery { Action = "license.revoke" }));
    }

    [Fact]
    public void Revoke_BlankReason_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => _repo.Revoke(1, "admin", "  ", _now));
    }

    [Fact]
    public void ListActive_OnlyActive()
    {
        _repo.Upsert("k1", Code, "l1", null, 8, ["core"], null, null,
            LicenseStatuses.Active, null, _now);
        _repo.Upsert("k2", OtherCode, "l2", null, 8, ["core"], null, null,
            LicenseStatuses.Expired, null, _now);

        var active = Assert.Single(_repo.ListActive());

        Assert.Equal(Code, active.DeviceCode);
    }

    [Fact]
    public void List_OrdersByMostRecentlyVerified()
    {
        _repo.Upsert("k1", Code, "l1", null, 8, ["core"], null, null,
            LicenseStatuses.Active, null, _now);
        _repo.Upsert("k2", OtherCode, "l2", null, 8, ["core"], null, null,
            LicenseStatuses.Active, null, _now.AddDays(2));

        var list = _repo.List();

        Assert.Equal(2, list.Count);
        Assert.Equal(OtherCode, list[0].DeviceCode);
    }

    [Fact]
    public void DeleteAll_RemovesEverything()
    {
        _repo.Upsert("k1", Code, null, null, 8, ["core"], null, null, LicenseStatuses.Active, null, _now);
        _repo.Upsert("k2", OtherCode, null, null, 8, ["core"], null, null, LicenseStatuses.Active, null, _now);

        Assert.Equal(2, _repo.DeleteAll());
        Assert.Empty(_repo.List());
    }

    [Fact]
    public void FirstActivation_RecordsLicenseActivateAudit()
    {
        _repo.Upsert("k", Code, "lic-1", "進階版", 32, ["core"], null, null,
            LicenseStatuses.Active, "admin", _now);

        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.activate" }));
        Assert.Equal(AuditCategories.License, audit.Category);
        Assert.Equal("admin", audit.Actor);
        Assert.Equal("license", audit.TargetType);
        Assert.Contains("lic-1", audit.Detail, StringComparison.Ordinal);
        Assert.Contains(Code, audit.Detail, StringComparison.Ordinal);
        Assert.Contains("進階版", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Upgrade_RecordsLicenseUpgradeAuditWithDiff()
    {
        _repo.Upsert("k1", Code, "lic-1", "標準版", 8, ["core"], null, _now.AddDays(30),
            LicenseStatuses.Active, "admin", _now);
        var later = _now.AddDays(5);

        _repo.Upsert("k2", Code, "lic-2", "企業版", 64, ["core", "ai"], null, _now.AddDays(300),
            LicenseStatuses.Active, "admin", later);

        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.upgrade" }));
        Assert.Equal(later, audit.OccurredAtUtc);
        Assert.Contains("金鑰已更換", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("通道 8→64", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("等級 標準版→企業版", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("lic-2", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineRevalidation_DoesNotWriteAudit()
    {
        // 重新驗證每次啟動都會發生；稽核只留真正的變更（啟用／到期／改版／續期）。
        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, _now.AddDays(30),
            LicenseStatuses.Active, "admin", _now);

        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, _now.AddDays(30),
            LicenseStatuses.Active, "admin", _now.AddMinutes(1));

        var audit = Assert.Single(_audit.List(new AuditLogQuery { Category = AuditCategories.License }));
        Assert.Equal("license.activate", audit.Action);
        Assert.Equal(_now.AddMinutes(1), _repo.List()[0].LastVerifiedUtc);
    }

    [Fact]
    public void Renewal_RecordsExpiryChange()
    {
        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, _now.AddDays(30),
            LicenseStatuses.Active, "admin", _now);

        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, _now.AddDays(365),
            LicenseStatuses.Active, "admin", _now.AddDays(1));

        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.update" }));
        Assert.Contains("到期", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedResync_RecordsNothingBeyondTheActivation()
    {
        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, _now.AddDays(30),
            LicenseStatuses.Active, "admin", _now);

        _repo.Upsert("k1", Code, "lic-1", "進階版", 32, ["core"], null, _now.AddDays(30),
            LicenseStatuses.Active, "admin", _now);

        // 只有首次啟用那一筆；第二次同步沒有任何差異，不該再寫一筆。
        var audit = Assert.Single(_audit.List(new AuditLogQuery { Category = AuditCategories.License }));
        Assert.Equal("license.activate", audit.Action);
    }


    [Fact]
    public void BlankCreatedBy_FallsBackToSystemActor()
    {
        _repo.Upsert("k", Code, "lic-1", null, 8, ["core"], null, null,
            LicenseStatuses.Active, "   ", _now);

        var audit = Assert.Single(_audit.List(new AuditLogQuery { Action = "license.activate" }));
        Assert.Equal("system", audit.Actor);
    }
}
