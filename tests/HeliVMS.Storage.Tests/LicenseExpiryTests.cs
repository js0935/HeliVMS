using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 到期提醒（M211／§19.4「到期前 14 天 UI 浮條提醒」）。
///
/// 「14 天」是規格數字，所以規則寫在 <see cref="LicenseExpiry"/> 並在此鎖住；
/// 若哪天有人把它改成 7 天，這些測試必須先紅。
/// </summary>
public class LicenseExpiryTests : IDisposable
{
    private static readonly RSA TestKey = RSA.Create(2048);
    private static readonly string TestPublicPem = RsaPem.ToPublicPem(TestKey);

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly LicenseService _service;
    private readonly DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public LicenseExpiryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-exp-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new LicenseService(_store, new LicenseManager(TestPublicPem));
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
    public void WarnWindow_IsFourteenDaysAsSpec()
        => Assert.Equal(14, LicenseExpiry.WarnDays);

    [Fact]
    public void NoLicense_DoesNotWarn()
    {
        // 未匯入不是「即將到期」，混在一起會讓浮條講錯話。
        var notice = _service.Evaluate(_now).Expiry(_now);

        Assert.Equal(LicenseExpiryStage.None, notice.Stage);
        Assert.False(notice.ShouldWarn);
        Assert.Null(notice.Message);
    }

    [Fact]
    public void PerpetualLicense_NeverWarns()
    {
        Apply(null);

        var notice = _service.Evaluate(_now).Expiry(_now);

        Assert.Equal(LicenseExpiryStage.None, notice.Stage);
        Assert.Null(notice.ExpiresUtc);
        Assert.Null(notice.DaysRemaining);
        Assert.False(notice.ShouldWarn);
    }

    [Fact]
    public void FarFromExpiry_DoesNotWarn()
    {
        Apply(_now.AddDays(400));

        var notice = _service.Evaluate(_now).Expiry(_now);

        Assert.Equal(LicenseExpiryStage.None, notice.Stage);
        Assert.False(notice.ShouldWarn);
    }

    [Fact]
    public void JustInsideWindow_Warns()
    {
        // 邊界要含：13.9 天在窗內，且剩餘天數向下取整為 13。
        Apply(_now.AddDays(13.9));

        var notice = _service.Evaluate(_now).Expiry(_now);

        Assert.Equal(LicenseExpiryStage.Upcoming, notice.Stage);
        Assert.True(notice.ShouldWarn);
        Assert.Equal(13, notice.DaysRemaining);
        Assert.Contains("剩 13 天", notice.Message);
    }

    [Fact]
    public void ExactlyOnBoundary_StillCounts()
    {
        Apply(_now.AddDays(14));

        Assert.Equal(LicenseExpiryStage.Upcoming, _service.Evaluate(_now).Expiry(_now).Stage);
    }

    [Fact]
    public void JustOutsideWindow_DoesNotWarn()
    {
        Apply(_now.AddDays(14.1));

        Assert.Equal(LicenseExpiryStage.None, _service.Evaluate(_now).Expiry(_now).Stage);
    }

    [Fact]
    public void WithinThreeDays_EscalatesToUrgent()
    {
        Apply(_now.AddDays(2));

        var notice = _service.Evaluate(_now).Expiry(_now);

        Assert.Equal(LicenseExpiryStage.Urgent, notice.Stage);
        Assert.Contains("立即匯入新授權", notice.Message);
    }

    [Fact]
    public void PartialDayLeft_IsUrgentNotUpcoming()
    {
        // 用總時數判斷邊界：只剩 5 小時若被算成「13 天」會完全失去緊迫感。
        Apply(_now.AddHours(5));

        Assert.Equal(LicenseExpiryStage.Urgent, _service.Evaluate(_now).Expiry(_now).Stage);
    }

    [Fact]
    public void Expired_ReportsExpiredAndExplainsPlaybackStays()
    {
        // 已到期停的是新增錄影，既有仍可回放——提醒文案必須說清楚，不然會被當成資料遺失。
        Apply(_now.AddDays(-1));

        var notice = _service.Evaluate(_now).Expiry(_now);

        Assert.Equal(LicenseExpiryStage.Expired, notice.Stage);
        Assert.True(notice.ShouldWarn);
        Assert.Contains("新增錄影已停止", notice.Message);
        Assert.Contains("既有錄影仍可回放", notice.Message);
    }

    [Fact]
    public void Expired_FeaturesAreClosedButBannerStillWarns()
    {
        Apply(_now.AddDays(-1));

        var license = _service.Evaluate(_now);
        var notice = license.Expiry(_now);

        Assert.Equal(LicenseDecision.Expired, license.Decision);
        Assert.False(license.AllowsFeature(LicenseFeatures.Core));
        Assert.True(notice.ShouldWarn);
    }

    [Fact]
    public void Revoked_DoesNotWarn()
    {
        // 作廢是「授權無效」，不是「快到期」；浮條只講到期，別混。
        Apply(_now.AddDays(5));
        var record = Assert.Single(new LicenseRepository(_store).List());
        new LicenseRepository(_store).Revoke(record.Id, "測試", "測試作廢", _now);

        Assert.Equal(LicenseExpiryStage.None, _service.Evaluate(_now).Expiry(_now).Stage);
    }

    [Theory]
    [InlineData(LicenseExpiryStage.None, null)]
    [InlineData(LicenseExpiryStage.Upcoming, "剩 2 天")]
    [InlineData(LicenseExpiryStage.Urgent, "剩 2 天")]
    [InlineData(LicenseExpiryStage.Expired, "已於")]
    public void Message_IsOnlyForWarningStages(LicenseExpiryStage stage, string? fragment)
    {
        var notice = new LicenseExpiryNotice(stage, stage == LicenseExpiryStage.Expired ? -1 : 2, _now);

        if (stage == LicenseExpiryStage.None)
        {
            Assert.Null(notice.Message);
        }
        else
        {
            Assert.NotNull(notice.Message);
            if (fragment is not null)
            {
                Assert.Contains(fragment, notice.Message);
            }
        }
    }

    private void Apply(DateTime? expires)
        => _service.Apply(Token(expires), "測試", _now);

    private static string Token(DateTime? expires)
        => LicenseSerializer.Sign(
            new LicensePayload
            {
                Id = Guid.NewGuid().ToString("N"),
                Machine = string.Empty,
                IssuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresUtc = expires,
                Cameras = 4,
                Features = [LicenseTiers.FeatureCore, LicenseTiers.FeatureAi],
                Issuer = "測試",
            },
            TestKey);
}
