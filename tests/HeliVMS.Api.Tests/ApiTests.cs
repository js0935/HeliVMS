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

    private static string HttpUtility(DateTime utc) =>
        Uri.EscapeDataString(utc.ToString("o"));
}