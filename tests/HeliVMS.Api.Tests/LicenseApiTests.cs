using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;
using HeliVMS.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HeliVMS.Api.Tests;

/// <summary>用測試金鑰對簽發授權並寫入指定 DB，供各個 API factory 共用。</summary>
public static class TestLicenseSeeder
{
    private static readonly RSA TestKey = RSA.Create(2048);

    /// <summary>對應 <see cref="TestKey"/> 的公鑰 PEM，餵給 WebApi 的 <c>HELIVMS_LICENSE_PUBLIC_KEY</c>。</summary>
    public static string PublicPem { get; } = RsaPem.ToPublicPem(TestKey);

    public static void Apply(SqliteStore store, string[] features, DateTime nowUtc, DateTime? expiresUtc)
        => new LicenseService(store, new LicenseManager(PublicPem)).Apply(
            LicenseSerializer.Sign(
                new LicensePayload
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Machine = string.Empty,
                    IssuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpiresUtc = expiresUtc,
                    Cameras = 4,
                    Features = features,
                    Issuer = "測試",
                },
                TestKey),
            "test",
            nowUtc);
}

/// <summary>
/// 授權可控的 API 主機：以測試金鑰對簽發授權，透過 <c>HELIVMS_LICENSE_PUBLIC_KEY</c>
/// 讓 WebApi 端改用對應的 <c>LicenseManager</c>。不需動 <c>EmbeddedPublicKey</c>，故可在 CI 跑。
/// </summary>
public sealed class LicensedApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-api-lic-{Guid.NewGuid():N}.db");

    /// <summary>啟動前要匯入的授權功能旗標；null 表示不匯入任何授權。</summary>
    public string[]? Features { get; set; }

    /// <summary>目前 UTC；預設為固定時刻以便斷言到期。</summary>
    public DateTime NowUtc { get; set; } = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>授權到期時刻；null 表示永久授權。</summary>
    public DateTime? ExpiresUtc { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("HELIVMS_DB", DbPath);
        builder.UseSetting("HELIVMS_LICENSE_PUBLIC_KEY", TestLicenseSeeder.PublicPem);
        Seed();
    }

    /// <summary>
    /// 在主機啟動前把授權寫進同一個 DB 檔。主機稍後會用自己的連線開這個檔，
    /// 故順序是先建表＋寫列，再讓 API 主機讀——不需要在主機內部找服務。
    /// </summary>
    private void Seed()
    {
        if (Features is null)
        {
            return;
        }

        using var store = new SqliteStore(DbPath);
        store.Initialize();
        TestLicenseSeeder.Apply(store, Features, NowUtc, ExpiresUtc);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(DbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// 遠端 API 授權合併檢查（M210／§19.4「遠程 API（WebApi）：合併檢查，避免遠程繞過」）。
///
/// 桌面端把未授權功能藏起來（M209），但直接打 API 繞得過；這些測試釘住遠端沒有比較寬鬆的分支。
/// </summary>
public class LicenseApiTests
{
    private const string Key = "helivms-dev-key";

    private static HttpClient Client(LicensedApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Get(LicensedApiFactory factory, string url)
    {
        using var client = Client(factory);
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, Parse(body));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(LicensedApiFactory factory, string url, object payload)
    {
        using var client = Client(factory);
        using var response = await client.PostAsJsonAsync(url, payload);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, Parse(body));
    }

    private static JsonElement Parse(string body)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return doc.RootElement.Clone();
    }

    private static string DenialFeature(JsonElement body)
        => body.GetProperty("feature").GetString() ?? string.Empty;

    // ── 未匯入授權 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/shares", "remote")]
    [InlineData("/api/recording/schedules", "schedule")]
    [InlineData("/api/patrols", "schedule")]
    [InlineData("/api/detections", "ai")]
    [InlineData("/api/clip", "ai")]
    [InlineData("/api/audio", "ai")]
    [InlineData("/api/auth/providers", "ad")]
    public async Task NoLicense_BlocksFlagGatedEndpoint(string url, string feature)
    {
        using var factory = new LicensedApiFactory { Features = null };

        var (status, body) = await Get(factory, url);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(feature, DenialFeature(body));
        Assert.Equal("NotPresent", body.GetProperty("decision").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public void GateRules_AreAllExercisedByTheBlockedTheory()
    {
        // 中介軟體加一條規則、行為測試卻沒跟上，等於新端點沒人驗它真的被擋。
        // 直接從兩邊的來源拉出規則表對帳：Rules 裡的每個前綴都要在 NoLicense 測試有 InlineData。
        var middleware = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HeliVMS.WebApi", "LicenseGateMiddleware.cs"));
        var testSource = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "HeliVMS.Api.Tests", nameof(LicenseApiTests) + ".cs"));

        var prefixes = Regex.Matches(middleware, @"""(/api/[\w/]+)"",\s*LicenseFeatures\.")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(prefixes);

        var missing = prefixes
            .Where(p => !testSource.Contains($"[InlineData(\"{p}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "LicenseGateMiddleware 有規則但 NoLicense 行為測試沒涵蓋：" + string.Join("、", missing));
    }

    [Theory]
    [InlineData("/api/channels")]
    [InlineData("/api/health")]
    [InlineData("/api/legal-holds")]
    [InlineData("/api/alarm-board/summary")]
    [InlineData("/api/system-metrics")]
    public async Task NoLicense_LeavesCoreEndpointsOpen(string url)
    {
        // 基本監看不該因為沒買加購功能就整個不能用——那不是授權該做的事。
        using var factory = new LicensedApiFactory { Features = null };

        var (status, _) = await Get(factory, url);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task NoLicense_StillReportsStatus()
    {
        // 前端要先問自己被擋在哪，才畫得出畫面；狀態查詢不該被授權擋住。
        using var factory = new LicensedApiFactory { Features = null };

        var (status, body) = await Get(factory, "/api/license");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("NotPresent", body.GetProperty("decision").GetString());
        Assert.Equal(0, body.GetProperty("maxCameras").GetInt32());
        Assert.False(body.GetProperty("allowsNewRecording").GetBoolean());
        Assert.Equal(8, body.GetProperty("featureStatus").GetArrayLength());
    }

    [Fact]
    public async Task NoLicense_BlocksWriteEndpointsToo()
    {
        // 只擋讀取等於留一個寫入後門。
        using var factory = new LicensedApiFactory { Features = null };

        var (status, body) = await Post(factory, "/api/shares", new
        {
            kind = "clip",
            resourcePath = "C:/x.mp4",
            label = "t",
            expiresAt = (DateTime?)null,
            maxUses = 1,
        });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("remote", DenialFeature(body));
    }

    // ── 已匯入授權 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/shares", "remote")]
    [InlineData("/api/recording/schedules", "schedule")]
    [InlineData("/api/patrols", "schedule")]
    [InlineData("/api/detections", "ai")]
    [InlineData("/api/audio", "ai")]
    [InlineData("/api/auth/providers", "ad")]
    public async Task Licensed_AllowsGrantedFeature(string url, string feature)
    {
        using var factory = new LicensedApiFactory { Features = [feature] };

        var (status, _) = await Get(factory, url);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Licensed_StillBlocksNotPurchasedFeature()
    {
        using var factory = new LicensedApiFactory { Features = ["core", "ai"] };

        var (status, body) = await Get(factory, "/api/shares");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("remote", DenialFeature(body));
        Assert.Equal("Valid", body.GetProperty("decision").GetString());

        // 已購旗標要原樣回報，遠端才知道自己買了什麼。
        Assert.Equal(
            ["core", "ai"],
            body.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task LicenseStatus_ReportsPerFeatureDecision()
    {
        using var factory = new LicensedApiFactory { Features = ["core", "gis"] };

        var (status, body) = await Get(factory, "/api/license");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Valid", body.GetProperty("decision").GetString());
        Assert.True(body.GetProperty("allowsNewRecording").GetBoolean());

        var byFlag = body.GetProperty("featureStatus").EnumerateArray()
            .ToDictionary(e => e.GetProperty("feature").GetString()!, e => e.GetProperty("allowed").GetBoolean());

        Assert.True(byFlag["core"]);
        Assert.True(byFlag["gis"]);
        Assert.False(byFlag["ai"]);
        Assert.False(byFlag["schedule"]);
        Assert.False(byFlag["remote"]);
        Assert.False(byFlag["ad"]);
        Assert.False(byFlag["ai.l1"]);
        Assert.False(byFlag["ai.l2"]);
    }

    [Fact]
    public async Task LicenseStatus_ExpiredShowsDecisionsButClosesFeatures()
    {
        // 結論只有一種來源：API 與閘門都讀同一列，不會各講各的。
        // 到期仍可回放既有錄影（不可勒索客戶），但功能旗標全部關閉且不允許新增錄影。
        using var factory = new LicensedApiFactory
        {
            Features = ["core", "gis"],
            ExpiresUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var (status, body) = await Get(factory, "/api/license");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Expired", body.GetProperty("decision").GetString());
        Assert.False(body.GetProperty("allowsNewRecording").GetBoolean());
        Assert.All(
            body.GetProperty("featureStatus").EnumerateArray(),
            f => Assert.False(f.GetProperty("allowed").GetBoolean()));
    }

    [Fact]
    public async Task LicenseStatus_ExpiringSoonCarriesServerSideNotice()
    {
        // 續期提醒的文案由 Storage 端算（M211），前端不重寫一份。
        using var factory = new LicensedApiFactory
        {
            Features = ["core", "gis"],
            ExpiresUtc = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
        };

        var (status, body) = await Get(factory, "/api/license");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Valid", body.GetProperty("decision").GetString());
        Assert.Equal("2026-10-05T00:00:00Z", body.GetProperty("expiresUtc").GetString());
        Assert.Contains("到期", body.GetProperty("expiryMessage").GetString()!);
    }

    [Fact]
    public async Task LicenseStatus_PerpetualHasNoExpiryNotice()
    {
        using var factory = new LicensedApiFactory { Features = ["core"] };

        var (_, body) = await Get(factory, "/api/license");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("expiresUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("expiryMessage").ValueKind);
    }

    [Fact]
    public async Task ExpiredLicense_BlocksFlagGatedEndpoint()
    {
        using var factory = new LicensedApiFactory
        {
            Features = ["core", "gis"],
            ExpiresUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var (status, body) = await Get(factory, "/api/shares");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("Expired", body.GetProperty("decision").GetString());
        Assert.Contains("遠程存取", body.GetProperty("featureName").GetString()!);
    }

    [Fact]
    public async Task ExpiredLicense_LeavesCoreEndpointsOpen()
    {
        // 到期停的是加購功能與新增錄影，不是整台機器。
        using var factory = new LicensedApiFactory
        {
            Features = ["core", "gis"],
            ExpiresUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var (status, _) = await Get(factory, "/api/channels");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>往上找到含 <c>HeliVMS.slnx</c> 的目錄，讓契約測試能讀來源檔。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到 HeliVMS.slnx，無法定位儲存庫根目錄");
    }
}
