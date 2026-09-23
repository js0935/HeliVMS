using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using HeliVMS.WebApi;

namespace HeliVMS.Api.Tests;

/// <summary>
/// M117 (section 14.3 P0): boots the real Web host over a throwaway database and
/// exercises the JSON surface end to end.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("HELIVMS_DB", DbPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                File.Delete(DbPath);
            }
            catch (IOException)
            {
            }
        }
    }
}

public class ApiTests : IClassFixture<ApiFactory>, IDisposable
{
    private const string Key = "helivms-dev-key";
    private readonly ApiFactory _factory;

    public ApiTests(ApiFactory factory) => _factory = factory;

    public void Dispose() => GC.SuppressFinalize(this);

    private HttpClient Client(bool withKey = true)
    {
        var client = _factory.CreateClient();
        if (withKey)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private T Service<T>() where T : notnull =>
        _factory.Services.GetRequiredService<T>();

    private SqliteStore Store => Service<SqliteStore>();

    private long InsertMotion(string detail, DateTime? atUtc = null)
    {
        var repo = Service<AlarmEventRepository>();
        var id = repo.Insert(1, "motion", atUtc ?? DateTime.UtcNow.AddSeconds(-30), detail: detail);
        Service<EventSearchRepository>().RebuildIndex();
        return id;
    }

    private async Task<long> InsertPos(string registerId, string txnNo, long cents, DateTime? atUtc = null)
    {
        var repo = Service<POSEventRepository>();
        return repo.Insert(1, registerId, txnNo, cents, atUtc ?? DateTime.UtcNow.AddSeconds(-30));
    }

    [Fact]
    public async Task Health_IsOpenAndReportsChannels()
    {
        using var client = Client(withKey: false);
        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<ApiEndpoints.HealthResponse>(response);
        Assert.Equal("ok", body.Status);
        Assert.Equal("ok", body.Database);
        Assert.True(body.Channels >= 2, "seeded channels expected");
    }

    [Fact]
    public async Task Channels_RequiresApiKey()
    {
        using var noKey = Client(withKey: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await noKey.GetAsync("/api/channels")).StatusCode);

        using var wrongKey = Client();
        wrongKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.GetAsync("/api/channels")).StatusCode);
    }

    [Fact]
    public async Task Channels_ReturnsSeeded()
    {
        using var client = Client();

        var items = await ReadAsync<List<ChannelInfo>>(await client.GetAsync("/api/channels"));

        Assert.True(items.Count >= 2);
        Assert.Contains(items, c => c.Id == 1 && !string.IsNullOrWhiteSpace(c.MainStreamUrl));
    }

    [Fact]
    public async Task Events_ListReturnsInsertedMotionEvent()
    {
        var id = InsertMotion("robot-detected-117");
        using var client = Client();
        var now = DateTime.UtcNow;

        var response = await client.GetAsync(
            $"/api/events?from={HttpUtility(now.AddMinutes(-5))}&to={HttpUtility(now.AddMinutes(1))}&keyword=robot-detected-117");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<ApiEndpoints.Paged<AlarmEventRecord>>(response);

        Assert.Contains(body.Items, e => e.Id == id && e.EventType == "motion");
        Assert.Equal(1, body.Count);
    }

    [Fact]
    public async Task Events_ForensicSearchReturnsAlarmHit()
    {
        InsertMotion("shelf-crash-77");
        using var client = Client();

        var response = await client.GetAsync("/api/events/search?q=shelf-crash-77&limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<ApiEndpoints.Paged<ForensicSearchHit>>(response);

        Assert.Contains(body.Items, h => h.SourceId > 0 && h.Text.Contains("shelf-crash-77", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Events_AcknowledgeFlipsFlag()
    {
        var id = InsertMotion("ack-me");
        using var client = Client();

        var post = await client.PostAsJsonAsync($"/api/events/{id}/ack", new ApiEndpoints.AckRequest(true));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var repo = Service<AlarmEventRepository>();
        var record = repo.ListByRange(1, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
            .First(e => e.Id == id);
        Assert.True(record.Acknowledged);
    }

    [Fact]
    public async Task Events_Triage_SetsPriorityAndReflectsOnBoard()
    {
        var id = InsertMotion("board-me");
        using var client = Client();

        var triage = await client.PostAsJsonAsync(
            $"/api/events/{id}/triage", new ApiEndpoints.TriageRequest("critical", null, "ops"));
        Assert.Equal(HttpStatusCode.OK, triage.StatusCode);

        var summary = await ReadAsync<AlarmBoardSummary>(await client.GetAsync("/api/alarms/summary"));
        Assert.True(summary.Pending >= 1);

        var board = await ReadAsync<List<AlarmBoardRow>>(await client.GetAsync("/api/alarms/board?take=50"));
        var row = board.First(r => r.EventId == id);
        Assert.Equal("critical", row.Priority);
    }

    [Fact]
    public async Task Events_Disposition_InvalidStatusReturns400()
    {
        var id = InsertMotion("bad-status");
        using var client = Client();

        var response = await client.PostAsJsonAsync(
            $"/api/events/{id}/disposition", new ApiEndpoints.DispositionRequest("exploded", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Events_Triage_InvalidPriorityReturns400()
    {
        var id = InsertMotion("bad-priority");
        using var client = Client();

        var response = await client.PostAsJsonAsync(
            $"/api/events/{id}/triage", new ApiEndpoints.TriageRequest("epic", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pos_QueryReturnsInsertedTransaction()
    {
        var id = await InsertPos("REG-1", "TXN-1", 12345);
        using var client = Client();
        var now = DateTime.UtcNow;

        var response = await client.GetAsync(
            $"/api/pos?deviceId=1&from={HttpUtility(now.AddMinutes(-5))}&to={HttpUtility(now.AddMinutes(1))}&limit=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<ApiEndpoints.Paged<POSEvent>>(response);

        Assert.Contains(body.Items, p => p.Id == id && p.AmountCents == 12345);
    }

    [Fact]
    public async Task Pos_ReconWithoutEvents_ReportsUnmatchedTransaction()
    {
        var past = DateTime.UtcNow.AddMinutes(-38);
        await InsertPos("REG-2", "TXN-2", 500, past);
        using var client = Client();

        var response = await client.GetAsync(
            $"/api/pos/recon?deviceId=1&registerId=REG-2&from={HttpUtility(past.AddMinutes(-1))}&to={HttpUtility(past.AddMinutes(1))}");
        var result = await ReadAsync<ApiEndpoints.PosReconResult>(response);

        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public async Task Smartwall_Board_ReturnsCriticalHighlightCell()
    {
        InsertMotion("wall-event", DateTime.UtcNow.AddSeconds(-2));
        var repo = Service<AlarmTriageRepository>();
        var recent = Service<AlarmEventRepository>().ListByRange(1, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow)
            .First(e => e.Detail == "wall-event");
        repo.SetTriage(recent.Id, "critical", null, "ops", DateTime.UtcNow);

        using var client = Client();
        var body = await ReadAsync<List<BoardCell>>(await client.GetAsync("/api/smartwall/board"));

        Assert.Contains(body, c => c.Priority == "critical" && c.Highlight);
    }

    [Fact]
    public async Task RecordingTimeline_ReturnsSegmentsAndMarkers()
    {
        var dayStart = DateTime.UtcNow.Date;
        var repo = Service<SegmentRepository>();
        var segId = repo.BeginSegment(1, "main", Path.Combine(Path.GetTempPath(), "timeline.mp4"), dayStart.AddHours(9));
        repo.CompleteSegment(segId, dayStart.AddHours(10), 1024, 3600, "abc");
        var motionId = InsertMotion("timeline-event", dayStart.AddHours(9).AddMinutes(30));
        using var client = Client();

        var body = await ReadAsync<PlaybackTimeline>(await client.GetAsync(
            $"/api/recording/timeline?channelId=1&stream=main&day={HttpUtility(dayStart)}"));

        Assert.Single(body.Bars);
        Assert.Contains(body.Markers, m => m.EventId == motionId && m.Kind == "motion");
        Assert.True(body.GapFraction > 0 && body.GapFraction < 1, "day with one bar must not be fully gapped");
    }

    [Fact]
    public async Task RecordingTimeline_EmptyDay_HasFullGap()
    {
        var dayStart = DateTime.UtcNow.Date.AddDays(30);
        using var client = Client();

        var body = await ReadAsync<PlaybackTimeline>(await client.GetAsync(
            $"/api/recording/timeline?channelId=1&stream=main&day={HttpUtility(dayStart)}"));

        Assert.Empty(body.Bars);
        Assert.Equal(1.0, body.GapFraction);
        Assert.Equal(1, body.GapCount);
    }

    [Fact]
    public async Task RecordingSegments_ReturnsWindowLedger()
    {
        var from = DateTime.UtcNow.Date.AddHours(7);
        var repo = Service<SegmentRepository>();
        var segId = repo.BeginSegment(1, "main", Path.Combine(Path.GetTempPath(), "seg.mp4"), from.AddMinutes(20));
        repo.CompleteSegment(segId, from.AddMinutes(50), 2048, 1800, "deadbeef");
        using var client = Client();

        var body = await ReadAsync<ApiEndpoints.Paged<SegmentRecord>>(await client.GetAsync(
            $"/api/recording/segments?channelId=1&stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(3))}"));

        var seg = body.Items.FirstOrDefault(s => s.Id == segId);
        Assert.NotNull(seg);
        Assert.Equal("deadbeef", seg.Sha256);
        Assert.Equal(2048, seg.SizeBytes);
    }

    [Fact]
    public async Task RecordingSegments_OutOfWindow_Excluded()
    {
        var from = DateTime.UtcNow.Date.AddHours(20);
        using var client = Client();

        var body = await ReadAsync<ApiEndpoints.Paged<SegmentRecord>>(await client.GetAsync(
            $"/api/recording/segments?channelId=1&stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(2))}"));
        var far = await ReadAsync<ApiEndpoints.Paged<SegmentRecord>>(await client.GetAsync(
            $"/api/recording/segments?channelId=1&stream=main&from={HttpUtility(from.AddDays(1))}&to={HttpUtility(from.AddDays(1).AddHours(2))}"));

        Assert.Empty(body.Items);
        Assert.Empty(far.Items);
    }

    [Fact]
    public async Task Events_ForensicSearchWithoutQueryReturns400()
    {
        using var client = Client();

        var response = await client.GetAsync("/api/events/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AlertsWebSocket_StreamsTriageUpdate()
    {
        var id = InsertMotion("ws-trigger");
        using var client = Client();

        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {Key}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/api/alerts/ws"), cts.Token);

        var response = await client.PostAsJsonAsync(
            $"/api/events/{id}/triage", new ApiEndpoints.TriageRequest("high", null, "ops"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), cts.Token);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

        Assert.Contains("alarm.triage", text);
        Assert.Contains("high", text);
    }

    [Fact]
    public async Task AlertsWebSocket_WithoutKey_IsRefused()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<Exception>(() => wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/api/alerts/ws"), cts.Token));
    }

    [Fact]
    public async Task SpaIndex_IsServedWithoutApiKey()
    {
        using var client = Client(withKey: false);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"app\"", html);
        Assert.Contains("HeliVMS", html);
    }

    [Fact]
    public async Task SpaModule_IsServedAsStaticAsset()
    {
        using var client = Client(withKey: false);

        var response = await client.GetAsync("/lib.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var js = await response.Content.ReadAsStringAsync();
        Assert.Contains("export function", js);
    }

    [Fact]
    public async Task AlertsWebSocket_QueryKey_IsAccepted()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Normal GET to the WS route must reach the handler (400), proving the key branch authorizes.
        using var probe = Client();
        var probeResponse = await probe.GetAsync($"/api/alerts/ws?key={Uri.EscapeDataString(Key)}");
        Assert.Equal(HttpStatusCode.BadRequest, probeResponse.StatusCode);

        using var socket = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/api/alerts/ws?key={Uri.EscapeDataString(Key)}"), cts.Token);

        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task Accounts_Authenticate_ReturnsRoleAndRejectsBadPassword()
    {
        var users = new UserRepository(Store);
        users.CreateUser("op1", PasswordHasher.Hash("s3cret"), "admin", "操作員一");
        users.CreateUser("view1", PasswordHasher.Hash("view"), "viewer", "觀看員");

        using var client = Client();
        var ok = await client.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "op1", password = "s3cret" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ReadAsync<LoginBody>(ok);
        Assert.Equal("admin", body.Role);
        Assert.Equal("操作員一", body.DisplayName);

        using var noKey = Client(withKey: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await noKey.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "view1", password = "view" })).StatusCode);

        using var bad = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "view1", password = "wrong" })).StatusCode);

        using var none = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await none.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "", password = "" })).StatusCode);
    }

    [Fact]
    public async Task Accounts_Crud_List_Create_Patch_Delete()
    {
        using var client = Client();

        var created = await client.PostAsJsonAsync(
            "/api/accounts", new { username = "cashier1", password = "pw1", role = "viewer", displayName = "收銀一" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var dup = await client.PostAsJsonAsync(
            "/api/accounts", new { username = "CASHIER1", password = "pw2", role = "viewer" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var badRole = await client.PostAsJsonAsync(
            "/api/accounts", new { username = "nope", password = "pw", role = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var list = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var json = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        var accounts = await ReadAsync<List<AccountItem>>(list);
        Assert.Contains(accounts, a => a.Username == "cashier1" && a.Role == "viewer");

        var target = accounts.First(a => a.Username == "cashier1");
        var patch = await client.PatchAsync(
            $"/api/accounts/{target.Id}", JsonContent.Create(new { role = "admin", enabled = false }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var after = await ReadAsync<List<AccountItem>>(await client.GetAsync("/api/accounts"));
        var updated = after.First(a => a.Id == target.Id);
        Assert.Equal("admin", updated.Role);
        Assert.False(updated.Enabled);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/accounts/{target.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/accounts/{target.Id}")).StatusCode);
        var gone = await ReadAsync<List<AccountItem>>(await client.GetAsync("/api/accounts"));
        Assert.DoesNotContain(gone, a => a.Id == target.Id);
    }

    [Fact]
    public async Task Config_ReadsDefaultsAndAppliesAuthSettings()
    {
        using var client = Client();

        var get = await client.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var defaults = await ReadAsync<ConfigBody>(get);
        Assert.False(defaults.AuthEnabled);
        Assert.Equal(5, defaults.LockoutThreshold);

        var put = await client.PutAsJsonAsync(
            "/api/config", new { authEnabled = true, lockoutThreshold = 3, lockoutMinutes = 30 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var applied = await ReadAsync<ConfigBody>(await client.GetAsync("/api/config"));
        Assert.True(applied.AuthEnabled);
        Assert.Equal(3, applied.LockoutThreshold);
        Assert.Equal(30, applied.LockoutMinutes);

        var bad = await client.PutAsJsonAsync("/api/config", new { lockoutThreshold = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Config_RoundTripsRetentionSettings()
    {
        using var client = Client();
        var put = await client.PutAsJsonAsync(
            "/api/config",
            new { recordingRetentionDays = 90, recordingWatermarkGb = 200.5 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = await ReadAsync<ConfigBody2>(await client.GetAsync("/api/config"));
        Assert.Equal(90, got.RecordingRetentionDays);
        Assert.Equal(200.5, got.RecordingWatermarkGb);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/config", new { recordingRetentionDays = 0 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/config", new { recordingWatermarkGb = -1 })).StatusCode);

        var usage = await ReadAsync<UsageBody>(await client.GetAsync("/api/config/usage"));
        Assert.True(usage.Bytes >= 0);
    }

    [Fact]
    public async Task Retention_RunPurgesByAgeAndWatermark()
    {
        var segments = Service<SegmentRepository>();
        var old = segments.BeginSegment(1, "main", "/tmp/old.mp4", new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        segments.CompleteSegment(old, new DateTime(2020, 1, 2, 1, 0, 0, DateTimeKind.Utc), 64 * 1024 * 1024, 3600, "sha-old");
        var fresh = segments.BeginSegment(1, "main", "/tmp/new.mp4", DateTime.UtcNow.AddMinutes(-5));
        segments.CompleteSegment(fresh, DateTime.UtcNow, 32 * 1024 * 1024, 600, "sha-new");
        Service<AlarmEventRepository>().Insert(1, "motion", new DateTime(2020, 1, 2, 2, 0, 0, DateTimeKind.Utc), detail: "x");
        Service<AlarmEventRepository>().Insert(1, "motion", DateTime.UtcNow.AddMinutes(-2), detail: "y");

        using var client = Client();
        await client.PutAsJsonAsync("/api/config", new { recordingRetentionDays = 3 });

        var run = await ReadAsync<RetentionRunBody>(await client.PostAsync("/api/retention/run", null));
        Assert.True(run.AgePurged >= 1, "old segments purged");
        Assert.True(run.AlarmPurged >= 1, "old alarms purged");

        await client.PutAsJsonAsync("/api/config", new { recordingRetentionDays = 3650, recordingWatermarkGb = 0.01 });
        var wm = await ReadAsync<RetentionRunBody>(await client.PostAsync("/api/retention/run", null));
        Assert.True(wm.WatermarkPurged >= 1);

        var usage = await ReadAsync<UsageBody>(await client.GetAsync("/api/config/usage"));
        Assert.True(usage.Bytes <= 0.011 * 1073741824, "usage under watermark after purge");
        await client.PutAsJsonAsync("/api/config", new { recordingRetentionDays = 30, recordingWatermarkGb = 0 });
    }

    private sealed record RetentionRunBody(int AgePurged, int WatermarkPurged, long BytesFreed, long AlarmPurged);

    private sealed record UsageBody(long Bytes, double Gb, int RetentionDays, double WatermarkGb, int AlarmRetentionDays);

    private sealed record ConfigBody2(bool AuthEnabled, int LockoutThreshold, int LockoutMinutes, int RecordingRetentionDays, double RecordingWatermarkGb, int AlarmRetentionDays);

    [Fact]
    public async Task Evidence_PackagesVerifiesAndLists()
    {
        var work = Path.Combine(Path.GetTempPath(), $"helivms-ev-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var src = Path.Combine(work, "frame.jpg");
        await File.WriteAllBytesAsync(src, new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            using var client = Client();
            var bad = await client.PostAsJsonAsync(
                "/api/evidence/package",
                new { bundleName = "", files = new[] { src } });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var packaged = await client.PostAsJsonAsync(
                "/api/evidence/package",
                new { bundleName = "test-bundle", files = new[] { src } });
            Assert.Equal(HttpStatusCode.OK, packaged.StatusCode);
            var pkg = await ReadAsync<EvidencePackageResult>(packaged);
            Assert.True(File.Exists(pkg.BundlePath));
            Assert.Equal(1, pkg.Items);
            Assert.False(string.IsNullOrWhiteSpace(pkg.BundleSha256));

            var missing = await client.PostAsJsonAsync(
                "/api/evidence/package",
                new { bundleName = "x", files = new[] { Path.Combine(work, "nope.jpg") } });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

            var verified = await client.PostAsJsonAsync(
                "/api/evidence/verify",
                new { bundlePath = pkg.BundlePath });
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            var vr = await ReadAsync<EvidenceVerifyResult>(verified);
            Assert.True(vr.Valid);
            Assert.Empty(vr.Failures);
            Assert.Single(vr.Items);

            var listed = await client.GetAsync("/api/evidence");
            var rows = await ReadAsync<List<EvidenceListItem>>(listed);
            Assert.Contains(rows, r => r.Status == "packaged" && r.Items == 1);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private sealed record EvidencePackageResult(string BundlePath, string BundleSha256, int Items, string CreatedUtc);

    private sealed record EvidenceVerifyResult(bool Valid, bool Expired, IReadOnlyList<string> Failures, IReadOnlyList<EvidenceItemBody> Items);

    private sealed record EvidenceItemBody(string RelativePath, string Sha256, long SizeBytes, string Kind);

    private sealed record EvidenceListItem(int Id, string Status, string CreatedAt, string? LastVerifiedAt, int Items);

    [Fact]
    public async Task Backup_RunsCopySegmentsAndAdvanceCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"helivms-backup-{Guid.NewGuid():N}");
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        var dst = Path.Combine(root, "dst");
        _ = dst;
        var content = new byte[4096];
        new Random(7).NextBytes(content);
        var filePath = Path.Combine(src, "ch1", "2021-01-02_000000.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, content);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

        var segments = Service<SegmentRepository>();
        var id = segments.BeginSegment(1, "main", filePath, new DateTime(2021, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        segments.CompleteSegment(id, new DateTime(2021, 1, 2, 0, 10, 0, DateTimeKind.Utc), content.Length, 600, sha);

        try
        {
            using var client = Client();
            var bad = await client.PostAsJsonAsync(
                "/api/backup/run",
                new { sourceRoot = Path.Combine(root, "missing"), targetRoot = dst });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var once = await client.PostAsJsonAsync(
                "/api/backup/run",
                new { sourceRoot = src, targetRoot = dst });
            Assert.Equal(HttpStatusCode.OK, once.StatusCode);
            var result = await ReadAsync<BackupRunResultBody>(once);
            Assert.Equal(1, result.Scanned);
            Assert.Equal(1, result.Copied);
            Assert.Equal(content.Length, result.CopiedBytes);
            Assert.True(result.Advanced);

            var targetFile = Path.Combine(dst, "ch1", Path.GetFileName(filePath));
            Assert.True(File.Exists(targetFile), "backup file on target with mirror rel path");
            Assert.Equal(sha, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(targetFile))).ToLowerInvariant());

            var two = await client.PostAsJsonAsync(
                "/api/backup/run",
                new { sourceRoot = src, targetRoot = dst });
            var again = await ReadAsync<BackupRunResultBody>(two);
            Assert.Equal(0, again.Copied);

            var runsResp = await client.GetAsync("/api/backup/runs");
            var runs = await ReadAsync<List<BackupRunRecordBody>>(runsResp);
            Assert.True(runs.Count >= 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record BackupRunResultBody(int Scanned, int Copied, long CopiedBytes, int Failed, bool Advanced);

    [Fact]
    public async Task Door_EventsQueryFilters()
    {
        var doors = Service<DoorEventRepository>();
        doors.Insert(3, 1, "CARD-1", "In", true, "ok", new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc));
        doors.Insert(3, 2, "CARD-2", "Out", false, "denied", new DateTime(2026, 1, 5, 8, 5, 0, DateTimeKind.Utc));

        using var client = Client();
        var allResp = await client.GetAsync("/api/door/events?from=2026-01-05T00:00:00Z&to=2026-01-06T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, allResp.StatusCode);
        var all = await ReadAsync<List<DoorEventItem>>(allResp);
        Assert.Equal(2, all.Count);
        Assert.Equal("CARD-2", all[0].CardId);

        var denied = await ReadAsync<List<DoorEventItem>>(
            await client.GetAsync("/api/door/events?from=2026-01-05T00:00:00Z&to=2026-01-06T00:00:00Z&granted=false"));
        Assert.Single(denied);
        Assert.False(denied[0].Granted);

        var empty = await ReadAsync<List<DoorEventItem>>(
            await client.GetAsync("/api/door/events?from=2026-01-05T00:00:00Z&to=2026-01-06T00:00:00Z&card=CARD-9"));
        Assert.Empty(empty);
    }

    private sealed record DoorEventItem(long Id, int DeviceId, int DoorId, string CardId, string Direction, bool Granted, string Reason, string OccurredAtUtc);

    [Fact]
    public async Task Detections_ListAndSummary()
    {
        var detects = Service<DetectionRepository>();
        try
        {
            detects.AddBatch(
            [
                new HeliVMS.Shared.Models.DetectionRecord { ChannelId = 1, Class = "person", Confidence = 0.95f, X = 0.1f, Y = 0.2f, W = 0.3f, H = 0.4f, DetectedUtc = new DateTime(2026, 3, 4, 8, 0, 0, DateTimeKind.Utc) },
                new HeliVMS.Shared.Models.DetectionRecord { ChannelId = 1, Class = "vehicle", Confidence = 0.4f, X = 0.5f, Y = 0.5f, W = 0.2f, H = 0.1f, DetectedUtc = new DateTime(2026, 3, 4, 8, 1, 0, DateTimeKind.Utc) },
            ]);

            using var client = Client();
            var listResp = await client.GetAsync("/api/detections?from=2026-03-04T07:00:00Z&to=2026-03-04T09:00:00Z");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var list = await ReadAsync<List<DetectionItem>>(listResp);
            Assert.Equal(2, list.Count);

            var high = await ReadAsync<List<DetectionItem>>(
                await client.GetAsync("/api/detections?from=2026-03-04T07:00:00Z&to=2026-03-04T09:00:00Z&minConfidence=0.9"));
            Assert.Single(high);
            Assert.Equal("person", high[0].Class);

            var summary = await ReadAsync<List<DetectionSummaryItem>>(
                await client.GetAsync("/api/detections/summary?from=2026-03-04T07:00:00Z&to=2026-03-04T09:00:00Z"));
            Assert.Contains(summary, s => s.Class == "person" && s.Count == 1);
            Assert.Contains(summary, s => s.Class == "vehicle" && s.Count == 1);
        }
        finally
        {
            detects.DeleteBefore(DateTime.MaxValue);
        }
    }

    private sealed record DetectionItem(long Id, int ChannelId, string Class, float Confidence, float X, float Y, float W, float H, string DetectedUtc);
    private sealed record DetectionSummaryItem(string Class, int Count);

    [Fact]
    public async Task Notifications_ListRecent()
    {
        var logs = Service<NotificationLogRepository>();
        logs.Add(1, "motion", "webhook", true, 1, "delivered");
        logs.Add(1, "siren", "smtp", false, 3, "retry exceeded");

        using var client = Client();
        var resp = await client.GetAsync("/api/notifications?limit=500");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var items = await ReadAsync<List<NotificationLogItem>>(resp);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, n => n.EventType == "motion" && n.Route == "webhook" && n.Ok);
        Assert.Contains(items, n => n.EventType == "siren" && n.Route == "smtp" && !n.Ok && n.Attempts == 3);
    }

    private sealed record NotificationLogItem(long Id, string TsUtc, int ChannelId, string EventType, string Route, bool Ok, int Attempts, string? Detail);

    [Fact]
    public async Task Exports_EnqueueAndList()
    {
        using var client = Client();
        var bad = await client.PostAsJsonAsync(
            "/api/exports",
            new { channelId = 1, stream = "main", fromUtc = "2026-05-01T00:00:00Z", toUtc = "2026-04-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/exports",
            new { channelId = 1, stream = "main", fromUtc = "2026-05-01T00:00:00Z", toUtc = "2026-05-01T01:00:00Z" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var job = await ReadAsync<ExportJobItem>(created);
        Assert.Equal("queued", job.Status);

        var list = await ReadAsync<List<ExportJobItem>>(await client.GetAsync("/api/exports"));
        Assert.Contains(list, j => j.Id == job.Id && j.Status == "queued" && j.ChannelId == 1);
    }

    private sealed record ExportJobItem(long Id, int ChannelId, string Stream, string StartUtc, string EndUtc, string Status, string? OutputPath, long? FileSizeBytes, string? Sha256, string? Error, string CreatedUtc);

    [Fact]
    public async Task AuthProviders_Crud()
    {
        using var client = Client();
        var badKind = await client.PostAsJsonAsync(
            "/api/auth/providers",
            new { name = "saml", kind = "saml", configJson = "{}", enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

        var badJson = await client.PostAsJsonAsync(
            "/api/auth/providers",
            new { name = "ad", kind = "ldap", configJson = "not json", enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/auth/providers",
            new { name = "corp-ad", kind = "ldap", configJson = "{\"host\":\"ldap.corp\",\"base\":\"dc=corp\"}", enabled = true });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var item = await ReadAsync<AuthProviderItem>(created);
        Assert.Equal("corp-ad", item.Name);
        Assert.Equal("ldap", item.Kind);

        var list = await ReadAsync<List<AuthProviderItem>>(await client.GetAsync("/api/auth/providers"));
        Assert.Contains(list, p => p.Name == "corp-ad" && p.Enabled);

        var toggle = await client.PutAsJsonAsync($"/api/auth/providers/{item.Id}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);
        var after = await ReadAsync<List<AuthProviderItem>>(await client.GetAsync("/api/auth/providers"));
        Assert.Contains(after, p => p.Id == item.Id && !p.Enabled);

        var nf = await client.DeleteAsync("/api/auth/providers/999999");
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);

        var del = await client.DeleteAsync($"/api/auth/providers/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var gone = await ReadAsync<List<AuthProviderItem>>(await client.GetAsync("/api/auth/providers"));
        Assert.DoesNotContain(gone, p => p.Id == item.Id);
    }

    private sealed record AuthProviderItem(int Id, string Name, string Kind, bool Enabled, string ConfigJson, string CreatedAt);

    [Fact]
    public async Task LegalHolds_AddRevoke()
    {
        using var client = Client();
        var bad = await client.PostAsJsonAsync(
            "/api/legal-holds",
            new { channelId = 1, fromUtc = "2026-06-01T00:00:00Z", toUtc = "2026-05-01T00:00:00Z", reason = "x", createdBy = "test" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/legal-holds",
            new { channelId = 1, fromUtc = "2026-06-01T00:00:00Z", toUtc = "2026-06-02T00:00:00Z", reason = "案件 A", createdBy = "auditor" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var hold = await ReadAsync<LegalHoldItem>(created);
        Assert.True(hold.Active);

        var active = await ReadAsync<List<LegalHoldItem>>(await client.GetAsync("/api/legal-holds/active"));
        Assert.Contains(active, h => h.Id == hold.Id);

        var revoke = await client.PutAsJsonAsync($"/api/legal-holds/{hold.Id}/revoke", new { by = "auditor", reason = "結案" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var gone = await ReadAsync<List<LegalHoldItem>>(await client.GetAsync("/api/legal-holds/active"));
        Assert.DoesNotContain(gone, h => h.Id == hold.Id);

        var all = await ReadAsync<List<LegalHoldItem>>(await client.GetAsync("/api/legal-holds"));
        var after = Assert.Single(all, h => h.Id == hold.Id);
        Assert.False(after.Active);
        Assert.Equal("auditor", after.RevokedBy);
    }

    private sealed record LegalHoldItem(long Id, int ChannelId, string FromUtc, string ToUtc, string Reason, string CreatedBy, string CreatedAtUtc, string? RevokedAtUtc, string? RevokedBy, string? RevokedReason, bool Active);

    [Fact]
    public async Task AlertRules_Crud()
    {
        using var client = Client();
        var bad = await client.PostAsJsonAsync("/api/alert-rules", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/alert-rules",
            new { name = "夜間動態", eventType = "motion", channelId = 2, frames = 0, minEventsInWindow = 2 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var rule = await ReadAsync<AlertRuleItem>(created);
        Assert.True(rule.Enabled);

        var list = await ReadAsync<List<AlertRuleItem>>(await client.GetAsync("/api/alert-rules"));
        Assert.Contains(list, r => r.Id == rule.Id && r.EventType == "motion");

        var toggle = await client.PutAsJsonAsync($"/api/alert-rules/{rule.Id}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);
        var after = await ReadAsync<List<AlertRuleItem>>(await client.GetAsync("/api/alert-rules"));
        Assert.Contains(after, r => r.Id == rule.Id && !r.Enabled);

        var nf = await client.DeleteAsync("/api/alert-rules/999999");
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);

        var del = await client.DeleteAsync($"/api/alert-rules/{rule.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var gone = await ReadAsync<List<AlertRuleItem>>(await client.GetAsync("/api/alert-rules"));
        Assert.DoesNotContain(gone, r => r.Id == rule.Id);
    }

    private sealed record AlertRuleItem(long Id, string Name, string? EventType, int? ChannelId, string? Keyword, string? Channels, bool Enabled, string? MatchEventTypes, int FrameMinutes, int MinEventsInWindow);

    [Fact]
    public async Task Shares_CreateRevoke()
    {
        using var client = Client();
        var bad = await client.PostAsJsonAsync(
            "/api/shares",
            new { kind = "bogus", resourcePath = "/tmp/x.mp4" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/shares",
            new { kind = "segment", resourcePath = "/tmp/x.mp4", label = "深夜動態", maxUses = 3 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var share = await ReadAsync<ShareItem>(created);
        Assert.True(share.Active);
        Assert.False(string.IsNullOrWhiteSpace(share.Token));

        var list = await ReadAsync<List<ShareItem>>(await client.GetAsync("/api/shares"));
        Assert.Contains(list, s => s.Id == share.Id && s.Kind == "segment");

        var revoke = await client.PutAsJsonAsync($"/api/shares/{share.Id}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var after = await ReadAsync<List<ShareItem>>(await client.GetAsync("/api/shares"));
        Assert.Contains(after, s => s.Id == share.Id && !s.Active && s.Revoked);

        var del = await client.DeleteAsync($"/api/shares/{share.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var gone = await ReadAsync<List<ShareItem>>(await client.GetAsync("/api/shares"));
        Assert.DoesNotContain(gone, s => s.Id == share.Id);
    }

    private sealed record ShareItem(int Id, string Token, string Kind, string ResourcePath, string? Label, string? CreatedBy, string? ExpiresAt, int MaxUses, int UseCount, bool Revoked, bool Active);

    [Fact]
    public async Task Redactions_AddListRemove()
    {
        using var client = Client();
        var bad = await client.PostAsJsonAsync(
            "/api/redactions",
            new { sourceType = "video", refId = 1, channelId = 1, occurredAtUtc = "2026-07-01T00:00:00Z", x = 0, y = 0, width = -5, height = 5, filled = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/redactions",
            new { sourceType = "clip", refId = 9, channelId = 1, occurredAtUtc = "2026-07-01T00:00:00Z", x = 10, y = 20, width = 50, height = 60, filled = true });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var item = await ReadAsync<RedactionItem>(created);
        Assert.Equal("clip", item.SourceType);

        var bySource = await ReadAsync<List<RedactionItem>>(
            await client.GetAsync("/api/redactions?sourceType=clip&refId=9"));
        Assert.Contains(bySource, r => r.Id == item.Id && r.Width == 50);

        var byTime = await ReadAsync<List<RedactionItem>>(
            await client.GetAsync("/api/redactions?from=2026-07-01T00:00:00Z&to=2026-07-02T00:00:00Z"));
        Assert.Contains(byTime, r => r.Id == item.Id);

        var nf = await client.DeleteAsync("/api/redactions/999999");
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);

        var del = await client.DeleteAsync($"/api/redactions/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var gone = await ReadAsync<List<RedactionItem>>(
            await client.GetAsync("/api/redactions?sourceType=clip&refId=9"));
        Assert.DoesNotContain(gone, r => r.Id == item.Id);
    }

    private sealed record RedactionItem(long Id, string SourceType, long RefId, int ChannelId, string OccurredAtUtc, int X, int Y, int Width, int Height, bool Filled, string CreatedAtUtc);

    [Fact]
    public async Task Replication_StatusListsJobs()
    {
        using var client = Client();
        var repo = Service<OffsiteReplicationRepository>();
        var src = Path.Combine(Path.GetTempPath(), "helivms-src-" + Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(), "helivms-dst-" + Guid.NewGuid().ToString("N"));
        var id = repo.Upsert(src, dst, 60, true);
        repo.SetRunResult(id, false, "複製失敗 1 檔");

        var rows = await ReadAsync<List<ReplicationItem>>(await client.GetAsync("/api/replication"));
        var job = Assert.Single(rows, j => j.Id == id);
        Assert.Equal(60, job.IntervalMinutes);
        Assert.True(job.Enabled);
        Assert.Equal("FAILED", job.LastResult);
        Assert.Equal("複製失敗 1 檔", job.LastError);
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.NotNull(job.LastRunUtc);
    }

    private sealed record ReplicationItem(long Id, string SourcePath, string DestinationPath, int IntervalMinutes, bool Enabled, string? LastRunUtc, string? LastResult, string? LastError, int ConsecutiveFailures, bool Due);

    [Fact]
    public async Task AlarmBoard_TriageAndDisposition()
    {
        using var client = Client();
        var events = Service<AlarmEventRepository>();
        var triage = Service<AlarmTriageRepository>();
        var evId = events.Insert(1, "motion", DateTime.UtcNow.AddHours(-1), null, "M146 面板測試");
        triage.SetTriage(evId, AlarmPriority.High, DateTime.UtcNow.AddMinutes(-5), "ops", DateTime.UtcNow);

        var summary0 = await ReadAsync<AlarmBoardSummaryItem>(await client.GetAsync("/api/alarm-board/summary"));
        Assert.True(summary0.Pending >= 1);

        var board = await ReadAsync<List<AlarmBoardItem>>(await client.GetAsync("/api/alarm-board?take=500"));
        var row = Assert.Single(board, b => b.EventId == evId);
        Assert.Equal("high", row.Priority);
        Assert.True(row.Overdue);
        Assert.Equal("ops", row.Owner);

        var bad = await client.PutAsJsonAsync(
            $"/api/alarm-board/{evId}/triage",
            new { priority = "omg" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var disp = await client.PutAsJsonAsync(
            $"/api/alarm-board/{evId}/disposition",
            new { status = "false_alarm", assignedTo = (string?)null, note = "誤報" });
        Assert.Equal(HttpStatusCode.OK, disp.StatusCode);

        var after = await ReadAsync<List<AlarmBoardItem>>(await client.GetAsync("/api/alarm-board?take=500"));
        Assert.DoesNotContain(after, b => b.EventId == evId);
        var summary1 = await ReadAsync<AlarmBoardSummaryItem>(await client.GetAsync("/api/alarm-board/summary"));
        Assert.True(summary1.FalseAlarm >= 1);
    }

    [Fact]
    public async Task Get_SystemMetrics_ReturnsProcessSnapshot()
    {
        var client = Client();
        var resp = await client.GetAsync("/api/system-metrics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode     ); var snap = await ReadAsync<SystemMetricsItem>(resp);
        Assert.False(string.IsNullOrWhiteSpace(snap.CapturedAtUtc));
        Assert.True(snap.UptimeMinutes >= 0);
        Assert.True(snap.WorkingSetMb > 0);
        Assert.True(snap.ManagedHeapMb >= 0);
        Assert.InRange(snap.CpuPercent, 0, 100);


        var disks = snap.Disks ?? [];
        Assert.NotEmpty(disks);
        Assert.All(disks, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.True(d.TotalMb >= 0);
            Assert.True(d.FreeMb >= 0);
            Assert.False(string.IsNullOrWhiteSpace(d.Format));
            Assert.True(d.FreeMb <= d.TotalMb, $"{d.Name} 可用不得超過總量");
        });
    }

    [Fact]
    public async Task Get_SystemMetricsHistory_ReturnsTrendPoints()
    {
        var client = Client();
        var resp = await client.GetAsync("/api/system-metrics/history");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var history = await ReadAsync<List<SystemMetricsTrendItem>>(resp);
        Assert.NotNull(history);
        Assert.All(history, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.CapturedAtUtc));
            Assert.True(p.WorkingSetGb > 0);
        });
    }

    [Fact]
    public async Task Get_SystemMetricsDiskHistory_ReturnsDiskTrendPoints()
    {
        var client = Client();
        var resp = await client.GetAsync("/api/system-metrics/disks/history");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var history = await ReadAsync<List<SystemDiskCapacityItem>>(resp);
        Assert.NotNull(history);
        Assert.All(history, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(p.FreeMb >= 0);
            Assert.True(p.TotalMb >= 0);
            Assert.True(p.FreeMb <= p.TotalMb, $"{p.Name} 可用不得超過總量");
        });
    }

    [Fact]
    public async Task Post_ClipSearch_ReturnsTopKHits()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync(
            "/api/clip/search",
            new { vector = new[] { 0.8f, 0.2f }, topK = 10 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var hits = await ReadAsync<List<ClipHitItem>>(resp);
        Assert.NotNull(hits);
    }

    [Fact]
    public async Task Post_ClipSearch_EmptyVectorRejected()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/api/clip/search", new { vector = Array.Empty<float>() });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Device_Crud_ListUpdateDelete()
    {
        var client = Client();

        var add = await client.PostAsJsonAsync("/api/devices", new
        {
            name = "ApiCam",
            ip = "192.168.9.9",
            port = 8000,
            username = "admin",
            password = "pw",
            vendor = "V",
            enabled = true,
            actor = "apitest",
        });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = await ReadAsync<IdBody>(add);

        var list = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var rows = await ReadAsync<List<DeviceItemBody>>(list);
        Assert.Contains(rows, r => r.Id == added.Id && r.Name == "ApiCam");

        var put = await client.PutAsJsonAsync($"/api/devices/{added.Id}", new
        {
            name = "ApiCam2",
            ip = "192.168.9.10",
            port = 554,
            vendor = "V2",
            enabled = false,
            actor = "apitest",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var del = await client.DeleteAsync($"/api/devices/{added.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var list2 = await client.GetAsync("/api/devices");
        var rows2 = await ReadAsync<List<DeviceItemBody>>(list2);
        Assert.DoesNotContain(rows2, r => r.Id == added.Id);
    }

    [Fact]
    public async Task Clip_IndexRoundTrip_Delete()
    {
        var client = Client();

        var index = await client.PostAsJsonAsync("/api/clip/index", new
        {
            sourceType = "event",
            refId = 901,
            label = "motion",
            vector = new[] { 0.5f, 0.5f },
        });
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        var get = await client.GetAsync("/api/clip/event/901");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await ReadAsync<ClipVectorBody>(get);
        Assert.NotNull(body.Vector);
        Assert.Equal(2, body.Vector.Length);

        var del = await client.DeleteAsync("/api/clip/event/901");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var miss = await client.GetAsync("/api/clip/event/901");
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
    }

    [Fact]
    public async Task Post_SearchFuse_ReturnsMergedRanking()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/api/search/fuse", new
        {
            items = new[]
            {
                new { key = "alarm:1", textScore = 0.9, vectorScore = (double?)null },
                new { key = "alarm:2", textScore = 0.0, vectorScore = (double?)0.9 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var fused = await ReadAsync<List<FuseResultBody>>(resp);
        Assert.Equal(2, fused.Count);
        Assert.Equal("alarm:2", fused[0].Key);
    }

    private sealed record FuseResultBody(string Key, double Score);

    private sealed record ClipVectorBody(float[] Vector);

    private sealed record IdBody(int Id);
    private sealed record DeviceItemBody(int Id, string Name, string Ip, int Port, string Vendor, bool Enabled);

    private sealed record ClipHitItem(long RefId, string SourceType, string? Label, double Score);

    private sealed record SystemMetricsTrendItem(string CapturedAtUtc, double WorkingSetGb);
    private sealed record SystemDiskCapacityItem(string CapturedAtUtc, string Name, long FreeMb, long TotalMb);

    private sealed record AlarmBoardItem(long EventId, int ChannelId, string EventType, string StartUtc, string Status, string Priority, string? DueUtc, string? Owner, string? AssignedTo, bool Overdue);
    private sealed record AlarmBoardSummaryItem(int Pending, int Acknowledged, int Actioned, int FalseAlarm, int Overdue);

    private sealed record BackupRunRecordBody(
        long Id,
        System.DateTime RunAt,
        string SourceRoot,
        string TargetRoot,
        System.DateTime? CheckpointUtc,
        int CopiedCount,
        long CopiedBytes,
        int FailedCount,
        string? Detail);

    [Fact]
    public async Task Audit_QueriesAndExportsCsv()
    {
        var ops = Service<AuditLogRepository>();
        ops.Record("tester", "login.ok", "auth", "user", 1, "role=admin", DateTime.UtcNow.AddHours(-2));
        ops.Record("admin", "settings.set", "config", "settings", null, "auth.enabled=1,包含,逗號", DateTime.UtcNow.AddMinutes(-5));

        using var client = Client();
        var list = await client.GetAsync("/api/audit?category=config");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await ReadAsync<PagedBody<AuditEntry>>(list);
        Assert.Contains(page.Items, e => e.Category == "config" && e.Detail!.Contains(",逗號"));

        var noKey = await client.GetAsync("/api/audit?category=config&from=9999-01-01T00:00:00Z&to=2000-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var csv = await client.GetAsync("/api/audit/export.csv?category=config");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM leading");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("settings.set", text);
        Assert.Contains("\"auth.enabled=1,包含,逗號\"", text);
    }

    [Fact]
    public async Task DailyReport_AggregatesRecordingTrendAndEvents()
    {
        var dayFrom = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var dayTo = dayFrom.AddDays(1);
        var segments = Service<SegmentRepository>();
        var id = segments.BeginSegment(1, "main", "/tmp/a.mp4", dayFrom.AddHours(2));
        segments.CompleteSegment(id, dayFrom.AddHours(3), 64 * 1024 * 1024, 3600, "sha");
        Service<AlarmEventRepository>().Insert(1, "motion", dayFrom.AddHours(2).AddMinutes(10), detail: "x");
        Service<AlarmEventRepository>().Insert(1, "offline", dayFrom.AddHours(2).AddMinutes(20), detail: "y");

        using var client = Client();
        var response = await client.GetAsync(
            $"/api/reports/daily?from={HttpUtility(dayFrom)}&to={HttpUtility(dayTo)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await ReadAsync<DailyReportBody>(response);
        Assert.Equal(2, report.Recording.Count);
        var seeded = report.Recording.First(r => r.ChannelId == 1);
        Assert.True(seeded.Hours > 0, "seeded one-hour segment");
        Assert.Contains(report.Events, e => e.EventType == "motion");
        Assert.Equal(1, report.Disconnects);

        var bad = await client.GetAsync(
            $"/api/reports/daily?from={HttpUtility(dayTo)}&to={HttpUtility(dayFrom)}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task RecordingSchedules_CrudAndValidation()
    {
        using var client = Client();
        var created = await client.PostAsJsonAsync(
            "/api/recording/schedules",
            new ApiEndpoints.ScheduleUpsertRequest(1, 0b0111110, 9 * 60, 17 * 60, true));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var row = await ReadAsync<ScheduleBody>(created);
        Assert.True(row.Id > 0);
        Assert.Equal(0b0111110, row.DaysMask);

        var listResp = await client.GetAsync("/api/recording/schedules");
        var list = await ReadAsync<List<ScheduleBody>>(listResp);
        Assert.Single(list);

        var badMask = await client.PostAsJsonAsync(
            "/api/recording/schedules",
            new ApiEndpoints.ScheduleUpsertRequest(1, 128, 0, 60, true));
        Assert.Equal(HttpStatusCode.BadRequest, badMask.StatusCode);

        var badRange = await client.PostAsJsonAsync(
            "/api/recording/schedules",
            new ApiEndpoints.ScheduleUpsertRequest(1, 1, 120, 60, true));
        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);

        var del = await client.DeleteAsync($"/api/recording/schedules/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var afterResp = await client.GetAsync("/api/recording/schedules");
        var after = await ReadAsync<List<ScheduleBody>>(afterResp);
        Assert.Empty(after);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recording/schedules/{row.Id}")).StatusCode);
    }

    [Fact]
    public async Task Patrols_CrudAndValidation()
    {
        using var client = Client();
        var bad = await client.PostAsJsonAsync(
            "/api/patrols",
            new ApiEndpoints.PatrolUpsertRequest("", 1, true, "09:00", "17:00",
                [new ApiEndpoints.PatrolStepBody("P1", 5)]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/patrols",
            new ApiEndpoints.PatrolUpsertRequest("日巡", 1, true, null, null,
                [
                    new ApiEndpoints.PatrolStepBody("P1", 5),
                    new ApiEndpoints.PatrolStepBody("P2", 8),
                ]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync<CreatedBody>(created)).Id;

        var listResp = await client.GetAsync("/api/patrols");
        var list = await ReadAsync<List<PatrolBody>>(listResp);
        var patrol = Assert.Single(list);
        Assert.Equal("日巡", patrol.Name);
        Assert.Equal(2, patrol.Steps.Count);
        Assert.Equal("00:00", patrol.WindowStart);

        var updated = await client.PutAsJsonAsync(
            $"/api/patrols/{id}",
            new ApiEndpoints.PatrolUpsertRequest("日巡", 1, false, "08:00", "18:00",
                [new ApiEndpoints.PatrolStepBody("P1", 5)]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var afterResp = await client.GetAsync("/api/patrols");
        var after = await ReadAsync<List<PatrolBody>>(afterResp);
        Assert.False(after[0].Enabled);
        Assert.Equal("08:00-18:00", $"{after[0].WindowStart}-{after[0].WindowEnd}");

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/patrols/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/patrols/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync(
                $"/api/patrols/{id}",
                new ApiEndpoints.PatrolUpsertRequest("x", 2, true, null, null, null))).StatusCode);
    }

    private sealed record CreatedBody(long Id);

    private sealed record PatrolBody(
        long? Id,
        string Name,
        int ChannelId,
        bool Enabled,
        string WindowStart,
        string WindowEnd,
        IReadOnlyList<PrtStepBody> Steps);

    private sealed record PrtStepBody(string PresetName, int DwellSeconds);

    private sealed record DailyReportBody(
        IReadOnlyList<RecordingRow> Recording,
        IReadOnlyList<CapacityRow> Capacity,
        long Disconnects,
        IReadOnlyList<EventCountRow> Events);

    private sealed record RecordingRow(int ChannelId, string ChannelName, double Hours, long Bytes);

    private sealed record CapacityRow(string Day, long Bytes, double Hours);

    private sealed record EventCountRow(string EventType, long Count);

    private sealed record PagedBody<T>(IReadOnlyList<T> Items, int Count);

    private sealed record ScheduleBody(long Id, int ChannelId, int DaysMask, int StartMinute, int EndMinute, bool Enabled);

    private sealed record AuditEntry(long Id, string Actor, string Action, string Category, string? TargetType, long? TargetId, string? Detail);

    private sealed record ConfigBody(bool AuthEnabled, int LockoutThreshold, int LockoutMinutes);

    private sealed record AccountItem(int Id, string Username, string Role, string? DisplayName, bool Enabled, bool Locked);

    private sealed record LoginBody(string Role, string? DisplayName);

    private sealed record SystemMetricsItem(
        string? CapturedAtUtc,
        int UptimeMinutes,
        double WorkingSetMb,
        double ManagedHeapMb,
        double CpuPercent,
        IReadOnlyList<SystemDiskItem> Disks);

    private sealed record SystemDiskItem(string Name, string Format, long TotalMb, long FreeMb, long? UsableMb);

    private static string HttpUtility(DateTime utc) =>
        Uri.EscapeDataString(utc.ToString("o"));
}