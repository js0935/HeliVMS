using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HeliVMS.Rtc;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// M244 WebRTC/WHEP 端點的 HTTP 契約。
///
/// <para>
/// 這裡刻意<b>不</b>真的啟動 ffmpeg：那一層由 <c>HeliVMS.Rtc.Tests</c> 搭配合成 RTP 封包
/// 驗證（見 <c>LiveStreamServiceTests</c>）。這裡要守的是「HTTP 邊界的樣子」——
/// 狀態碼、Content-Type、認證與授權——這些一旦錯了，使用者看到的是空白畫面或
/// 403，卻完全查不出是 ffmpeg 還是路由的問題。
/// </para>
/// </summary>
public sealed class WhepApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "helivms-whep-key";

public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-whep-{Guid.NewGuid():N}.db");

    public static string PlaceholderRtsp => "rtsp://192.0.2.10:554/stream";

    public static string PlaceholderSubRtsp => "rtsp://192.0.2.10:554/sub-stream";

    /// <summary>
    /// 端點實際交給 publisher 的 RTSP 位址（依序）。
    ///
    /// <para>
    /// 「選了次碼流但其實還在拉主流」不會有任何錯誤訊息，只有首包慢二十幾秒——所以碼流的選擇
    /// 必須在這一層看得見。取代 <see cref="LiveStreamService"/> 的 starter 是最省事的觀測點：
    /// ffmpeg 在測試裡本來就不存在，啟動必定失敗，但失敗前的位址已經被記下來了。
    /// </para>
    /// </summary>
    public List<string> PublishedSources { get; } = [];

    /// <summary>
    /// 覆寫 <c>HELIVMS_WHEP_TURN</c>；必須在第一次 <c>CreateClient()</c> 之前設定，
    /// 因為主機在那一刻才建置。
    /// </summary>
    public string? TurnSetting { get; set; }

    /// <summary>覆寫 <c>HELIVMS_WHEP_PUBLIC_HOST</c>；同樣須在第一次 <c>CreateClient()</c> 之前設定。</summary>
    public string? PublicHostSetting { get; set; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("HELIVMS_DB", DbPath);
        builder.UseSetting("HELIVMS_API_KEY", Key);
        builder.UseSetting("HELIVMS_LICENSE_PUBLIC_KEY", TestLicenseSeeder.PublicPem);

        // ffmpeg 不存在也要讓伺服器啟得起來：端點層的測試不該被外部相依綁住。
        builder.UseSetting("HELIVMS_WHEP_FFMPEG", "ffmpeg-does-not-exist-for-tests");

        if (TurnSetting is not null) builder.UseSetting("HELIVMS_WHEP_TURN", TurnSetting);
        if (PublicHostSetting is not null) builder.UseSetting("HELIVMS_WHEP_PUBLIC_HOST", PublicHostSetting);

        // 換掉 publisher starter，只為了看得見端點把它送到哪一條 RTSP 位址（碼流選擇）。
        // ConfigureTestServices 在應用程式自己的註冊之後才跑，同型別的後一筆註冊
        // 就是解析出來的那一筆。ffmpeg 不存在，所以啟動必定失敗——但位址已經記下來了。
        builder.ConfigureTestServices(services => services.AddSingleton(sp => new LiveStreamService(
            sp.GetRequiredService<WhepOptions>(),
            sp.GetRequiredService<WhepSessionStore>(),
            (LivePublisher publisher, System.Net.IPEndPoint target, ChannelSource source, CancellationToken token) =>
            {
                lock (PublishedSources) PublishedSources.Add(source.RtspUrl);
                return PublisherStarters.StartFfmpeg(publisher, target, source, token);
            })));

        using var store = new SqliteStore(DbPath);
        store.Initialize();
        TestLicenseSeeder.Apply(
            store,
            LicenseFeatures.All.ToArray(),
            new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
            expiresUtc: null);
    }

    /// <summary>建立一路攝影機；回傳實際拿到的通道編號。</summary>
    public int SeedChannel(string? mainStreamUrl, string? subStreamUrl = null)
    {
        using var store = new SqliteStore(DbPath);
        store.Initialize();
        var channels = new ChannelRepository(store);
        return channels.Add($"cam-{Guid.NewGuid():N}"[..12], mainStreamUrl ?? string.Empty, subStreamUrl);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try
        {
            File.Delete(DbPath);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class WhepApiTests : IDisposable
{
    private const string Sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";

    /// <summary>
    /// 真的 Chrome 送出的 offer（實機樣本，同 <c>WhepNegotiatedPayloadTypeTests</c>）。
    ///
    /// <para>
    /// 碼流選擇的測試必須讓協商真的成功：協商失敗時 <c>OpenAsync</c> 不會走到 publisher，
    /// 於是「拉的是哪條位址」根本沒有發生——那種測試不管端點挑了什麼都會綠。
    /// 假的 ffmpeg 會在下一步立刻失敗（502），但位址已經被記下來了。
    /// </para>
    /// </summary>
    private const string ChromeOffer = """
        v=0
        o=- 2642440267667155890 2 IN IP4 127.0.0.1
        s=-
        t=0 0
        a=group:BUNDLE 0
        a=msid-semantic: WMS
        m=video 9 UDP/TLS/RTP/SAVPF 96 97 102 108
        c=IN IP4 0.0.0.0
        a=rtcp:9 IN IP4 0.0.0.0
        a=ice-ufrag:KWu6
        a=ice-pwd:HxEbw5Y6H2h7RvLb1ggdxf+O
        a=ice-options:trickle
        a=fingerprint:sha-256 51:9B:B9:AE:FD:3C:DA:C6:B0:B3:C1:98:AF:E6:73:E2:76:34:7A:E4:9E:15:E2:9E:ED:A8:98:01:35:10:08:20
        a=setup:actpass
        a=mid:0
        a=recvonly
        a=rtcp-mux
        a=rtpmap:96 VP8/90000
        a=rtpmap:97 rtx/90000
        a=fmtp:97 apt=96
        a=rtpmap:102 H264/90000
        a=rtcp-fb:102 transport-cc
        a=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f
        a=rtpmap:108 H264/90000
        a=rtcp-fb:108 transport-cc
        a=fmtp:108 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f
        """;

    private readonly WhepApiFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private HttpClient Client(bool withKey = true)
    {
        var client = _factory.CreateClient();
        if (withKey)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WhepApiFactory.Key);
        }

        return client;
    }

    private int SeedRtspChannel()
        => _factory.SeedChannel(WhepApiFactory.PlaceholderRtsp);

    private static HttpContent SdpBody(string sdp = Sdp, string mediaType = "application/sdp")
        => new StringContent(sdp, Encoding.UTF8, mediaType);

    // ── 認證 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 建立會話需要ApiKey()
    {
        using var client = Client(withKey: false);

        var response = await client.PostAsync($"/api/stream/{SeedRtspChannel()}/whep", SdpBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 查詢狀態需要ApiKey()
    {
        using var client = Client(withKey: false);

        var response = await client.GetAsync($"/api/stream/{SeedRtspChannel()}/whep");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 關閉會話需要ApiKey()
    {
        using var client = Client(withKey: false);

        var response = await client.DeleteAsync("/api/stream/whep/any-session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 金鑰不接受放在query()
    {
        // WHEP 是普通 POST，不是 WebSocket 升級。秘密若允許走 ?key=，就會進
        // browser history、反向代理的 access log 與瀏覽器書籤。既有 AuthService
        // 只認標頭；這裡確認 WHEP 沒有偷偷開後門。
        using var client = Client(withKey: false);
        var channel = SeedRtspChannel();

        var response = await client.PostAsync(
            $"/api/stream/{channel}/whep?key={Uri.EscapeDataString(WhepApiFactory.Key)}",
            SdpBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Content-Type 與請求驗證 ──────────────────────────────────────────

    [Fact]
    public async Task 非SDPContentType被拒()
    {
        using var client = Client();

        var response = await client.PostAsync(
            $"/api/stream/{SeedRtspChannel()}/whep",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task 通道不存在回404()
    {
        using var client = Client();

        var response = await client.PostAsync($"/api/stream/987654/whep", SdpBody());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 通道沒設定RTS位址回409()
    {
        // 沒有 RTSP 位址就沒有「即時」可言，但這不是呼叫方的錯誤，所以是 409 而不是 400。
        var channel = _factory.SeedChannel(string.Empty);
        using var client = Client();

        var response = await client.PostAsync($"/api/stream/{channel}/whep", SdpBody());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── 碼流選擇：多分割監看要次碼流 ────────────────────────────────────

    [Fact]
    public async Task 要求次碼流時publisher拉的是次流位址()
    {
        // 現場回報「多格監看有些格等 24 秒以上仍無影格」：publisher 拉的是主流（1080p），
        // 實測同一組 10 路併發首包 2.6–24.3 秒，次碼流全部 2.2–3.0 秒。
        // 「選了次碼流但其實還在拉主流」不會有任何錯誤訊息，所以要在這裡釘住位址本身。
        var channel = _factory.SeedChannel(WhepApiFactory.PlaceholderRtsp, WhepApiFactory.PlaceholderSubRtsp);
        using var client = Client();

        await client.PostAsync($"/api/stream/{channel}/whep?stream=sub", SdpBody(ChromeOffer));

        Assert.Equal([WhepApiFactory.PlaceholderSubRtsp], _factory.PublishedSources);
    }

    [Fact]
    public async Task 沒有指定碼流時仍然拉主流()
    {
        // 沒有這個參數的既有用戶端（行動版、QCC 相容層、維運手上的 curl）不該看到畫質默默變差。
        var channel = _factory.SeedChannel(WhepApiFactory.PlaceholderRtsp, WhepApiFactory.PlaceholderSubRtsp);
        using var client = Client();

        await client.PostAsync($"/api/stream/{channel}/whep", SdpBody(ChromeOffer));

        Assert.Equal([WhepApiFactory.PlaceholderRtsp], _factory.PublishedSources);
    }

    [Fact]
    public async Task 頻道沒有次流時要求次碼流會退回主流()
    {
        // 手動輸入的頻道常常只有一條位址；退回主流是「畫質差一點」而不是「整格打不開」。
        var channel = _factory.SeedChannel(WhepApiFactory.PlaceholderRtsp);
        using var client = Client();

        await client.PostAsync($"/api/stream/{channel}/whep?stream=sub", SdpBody(ChromeOffer));

        Assert.Equal([WhepApiFactory.PlaceholderRtsp], _factory.PublishedSources);
    }

    [Fact]
    public async Task 壞掉的SDP回400()
    {
        using var client = Client();

        // ffmpeg 不存在，所以 publisher 一定起不來 —— 這裡驗證的是「錯誤有被翻譯成
        // 可理解的狀態碼」，不是 500。
        var response = await client.PostAsync(
            $"/api/stream/{SeedRtspChannel()}/whep",
            SdpBody("這不是 SDP"));

        Assert.InRange((int)response.StatusCode, 400, 502);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task 錯誤回應是JSON且帶有訊息()
    {
        using var client = Client();

        var response = await client.PostAsync(
            $"/api/stream/{SeedRtspChannel()}/whep",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("application/sdp", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 錯誤訊息不含RTS帳密()
    {
        // 守衛：WHEP 的錯誤會回給瀏覽器並寫進稽核。若 RTSP 帳密從這裡漏出去，
        // 就等於把攝影機憑證散播到每個看得到錯誤訊息的人手上。
        using var client = Client();

        var response = await client.PostAsync(
            $"/api/stream/{SeedRtspChannel()}/whep",
            SdpBody("這不是 SDP"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("rtsp://", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pass", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── 關閉 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 關閉不存在的會話是冪等204()
    {
        using var client = Client();

        var response = await client.DeleteAsync("/api/stream/whep/does-not-exist");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task 重複關閉仍是204()
    {
        using var client = Client();

        var first = await client.DeleteAsync("/api/stream/whep/same-id");
        var second = await client.DeleteAsync("/api/stream/whep/same-id");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    // ── 狀態查詢 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 狀態查詢回應結構穩定()
    {
        using var client = Client();
        var channel = SeedRtspChannel();

        var response = await client.GetAsync($"/api/stream/{channel}/whep");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("active", body, StringComparison.Ordinal);
        Assert.Contains("viewers", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 狀態查詢會回報收包與漏包數讓掉包可以被歸因()
    {
        using var client = Client();
        var channel = SeedRtspChannel();

        var response = await client.GetAsync($"/api/stream/{channel}/whep");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // 「ICE／DTLS 都通了、Chrome 每秒送 PLI、畫面十幾秒才出來」只有兩種成因：
        // 封包掉在 ingest 之前，或掉在轉送出去之後。分界線就是這兩欄——少了它們，
        // 現場只能靠猜測去改編碼／解碼參數，而兩邊的症狀一模一樣。
        Assert.True(json.TryGetProperty("packetsReceived", out var received));
        Assert.True(json.TryGetProperty("packetsMissed", out var missed));
        Assert.Equal(JsonValueKind.Number, received.ValueKind);
        Assert.Equal(JsonValueKind.Number, missed.ValueKind);

        // 沒有 publisher 時必須說得出來，而不是讓前端以為「正在串流但沒影格」。
        Assert.True(json.TryGetProperty("publisher", out var publisher));
        Assert.Equal("Stopped", publisher.GetString());
    }

    // ── 契約與結構 ──────────────────────────────────────────────────────

    [Fact]
    public void 三個WHEP端點都有掛上()
    {
        // 路由少掛一個，症狀是前端按了沒反應而伺服器沒有任何錯誤。
        using var _ = _factory.CreateClient();
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .OfType<string>()
            .ToList();

        Assert.Contains(routes, r => r.Contains("/stream/{channelId:int}/whep", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Contains("/stream/whep/{sessionId}", StringComparison.Ordinal));
    }

    [Fact]
    public void 建立會話宣告applicationSDP的產生與接受()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "HeliVMS.WebApi", "WhepEndpoints.cs"));

        Assert.Contains("application/sdp", source, StringComparison.Ordinal);
        Assert.Contains("Status201Created", source, StringComparison.Ordinal);
        Assert.Contains("Headers.Location", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根目錄。");
    }
}
