using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 功能旗標閘門（M209／§19.4「未授權之等級功能於 UI 隱藏」）。
///
/// 桌面端 <c>LicenseUiGate</c> 只是把旗標結論翻成 <c>Visibility.Collapsed</c>；
/// 真正的規則全部落在 <see cref="LicenseApplyResult.AllowsFeature"/> 與
/// <see cref="LicenseApplyResult.FeatureDenialMessage"/>，所以規則在 Storage 層測。
/// </summary>
public class FeatureGateTests : IDisposable
{
    private static readonly RSA TestKey = RSA.Create(2048);
    private static readonly string TestPublicPem = RsaPem.ToPublicPem(TestKey);

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly LicenseService _service;
    private readonly LicenseRepository _licenses;
    private readonly DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public FeatureGateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-feat-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new LicenseService(_store, new LicenseManager(TestPublicPem));
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

    // ── 旗標清單 ───────────────────────────────────────────────────────

    [Fact]
    public void All_ContainsEverySpecFeature()
    {
        Assert.Equal(
            ["core", "schedule", "ai", "ai.l1", "ai.l2", "gis", "remote", "ad"],
            LicenseFeatures.All);
    }

    [Fact]
    public void All_HasNoDuplicate()
        => Assert.Equal(LicenseFeatures.All.Count, LicenseFeatures.All.Distinct().Count());

    [Theory]
    [InlineData("core", "核心監看與錄影")]
    [InlineData("schedule", "排程錄影")]
    [InlineData("ai", "AI 事件偵測")]
    [InlineData("ai.l1", "L1 人員／車輛辨識")]
    [InlineData("ai.l2", "L2 車牌／人臉辨識")]
    [InlineData("gis", "地圖與 IO")]
    [InlineData("remote", "遠程存取")]
    [InlineData("ad", "企業 AD／SSO 登入")]
    public void DisplayName_MapsEveryKnownFlag(string feature, string expected)
    {
        Assert.Equal(expected, LicenseFeatures.DisplayName(feature));
        Assert.True(LicenseFeatures.IsKnown(feature));
    }

    [Theory]
    [InlineData("telemetry")]
    [InlineData("CORE")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void UnknownFlag_IsNotKnown(string? feature)
    {
        Assert.False(LicenseFeatures.IsKnown(feature));
        // 未知旗標原樣回傳，不丟例外：上游新增旗標時桌面端只會顯示原文，不會整個視窗炸掉。
        Assert.Equal(feature ?? string.Empty, LicenseFeatures.DisplayName(feature));
    }

    // ── 旗標結論 ───────────────────────────────────────────────────────

    [Fact]
    public void NoLicense_DeniesEveryFeature()
    {
        var license = _service.Evaluate(_now);

        Assert.Equal(LicenseDecision.NotPresent, license.Decision);

        // 沒有授權時連 core 都不開：§19.4「未匯入一律 false」是 fail-closed，
        // 不是「至少讓你看得到畫面」。整個 UI 這時只留匯入授權的路。
        foreach (var feature in LicenseFeatures.All)
        {
            Assert.False(license.AllowsFeature(feature));
        }
    }

    [Fact]
    public void NoLicense_DenialMessage_NamesTheFeature()
    {
        var message = _service.Evaluate(_now).FeatureDenialMessage(LicenseFeatures.Remote);

        Assert.NotNull(message);
        Assert.Contains(LicenseFeatures.DisplayName(LicenseFeatures.Remote), message);
        Assert.Contains(LicenseService.NotPresentMessage, message);
    }

    [Fact]
    public void Licensed_AllowsOnlyGrantedFeatures()
    {
        Apply([LicenseFeatures.Core, LicenseFeatures.Schedule, LicenseFeatures.Ai, LicenseFeatures.AiL1]);

        var license = _service.Evaluate(_now);

        Assert.True(license.AllowsFeature(LicenseFeatures.Core));
        Assert.True(license.AllowsFeature(LicenseFeatures.Schedule));
        Assert.True(license.AllowsFeature(LicenseFeatures.Ai));
        Assert.True(license.AllowsFeature(LicenseFeatures.AiL1));

        // 沒買就沒有：L2、地圖、遠程、AD 都不能因為「上面買了 AI」而自動開通。
        Assert.False(license.AllowsFeature(LicenseFeatures.AiL2));
        Assert.False(license.AllowsFeature(LicenseFeatures.Gis));
        Assert.False(license.AllowsFeature(LicenseFeatures.Remote));
        Assert.False(license.AllowsFeature(LicenseFeatures.AdSso));
    }

    [Fact]
    public void GrantedFeature_HasNoDenialMessage()
    {
        Apply([LicenseFeatures.Core, LicenseFeatures.Gis]);

        Assert.Null(_service.Evaluate(_now).FeatureDenialMessage(LicenseFeatures.Gis));
    }

    [Fact]
    public void MissingFeature_DenialMessage_NamesTheFeature()
    {
        Apply([LicenseFeatures.Core]);

        var message = _service.Evaluate(_now).FeatureDenialMessage(LicenseFeatures.AiL2);

        Assert.NotNull(message);
        Assert.Contains(LicenseFeatures.DisplayName(LicenseFeatures.AiL2), message);
        Assert.DoesNotContain(LicenseService.NotPresentMessage, message);
    }

    [Fact]
    public void ExpiredLicense_DeniesEveryNonCoreFeature()
    {
        Apply([LicenseFeatures.Core, LicenseFeatures.Gis], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var license = _service.Evaluate(_now);

        Assert.Equal(LicenseDecision.Expired, license.Decision);
        Assert.False(license.AllowsFeature(LicenseFeatures.Gis));
    }

    [Fact]
    public void RevokedLicense_DeniesEveryNonCoreFeature()
    {
        Apply([LicenseFeatures.Core, LicenseFeatures.Gis]);
        var record = Assert.Single(_licenses.List());
        _licenses.Revoke(record.Id, "測試", "測試作廢", _now);

        var license = _service.Evaluate(_now);

        Assert.Equal(LicenseDecision.Revoked, license.Decision);
        Assert.False(license.AllowsFeature(LicenseFeatures.Gis));
        Assert.Contains(LicenseService.RevokedMessage, license.FeatureDenialMessage(LicenseFeatures.Gis));
    }

    [Fact]
    public void TimeRollback_DeniesEveryNonCoreFeature()
    {
        Apply([LicenseFeatures.Core, LicenseFeatures.Gis]);

        // 先把時鐘推到遠期推進高水位，再回來看——必須判定為回流而不是重新授權。
        _service.Evaluate(_now.AddDays(30));
        _service.Apply(
            Token([LicenseFeatures.Core, LicenseFeatures.Gis], null),
            "測試",
            _now.AddDays(-40));

        var license = _service.Evaluate(_now);

        Assert.Equal(LicenseDecision.TimeRollback, license.Decision);
        Assert.False(license.AllowsFeature(LicenseFeatures.Gis));
    }

    [Fact]
    public void CoreFlagMissing_StillDeniesCore()
    {
        // 旗標判定採嚴格比對：授權清單沒寫就是沒有。
        // 所有等級（基本～客製）都由簽發端帶 core，所以缺 core 只會出現在
        // 手改或舊格式金鑰；那種金鑰不該拿到比它標示的更多權限。
        Apply([LicenseFeatures.Schedule]);

        Assert.False(_service.Evaluate(_now).AllowsFeature(LicenseFeatures.Core));
        Assert.True(_service.Evaluate(_now).AllowsFeature(LicenseFeatures.Schedule));
    }

    private void Apply(string[] features, DateTime? expires = null)
        => _service.Apply(Token(features, expires), "測試", _now);

    private static string Token(string[] features, DateTime? expires)
        => LicenseSerializer.Sign(
            new LicensePayload
            {
                Id = Guid.NewGuid().ToString("N"),
                Machine = string.Empty,
                IssuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresUtc = expires,
                Cameras = 4,
                Features = features,
                Issuer = "測試",
            },
            TestKey);
}
