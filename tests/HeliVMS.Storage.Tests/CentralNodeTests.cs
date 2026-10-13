using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HeliVMS.Shared.Models;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// M295（§14.1 #10 中央/多機集群管理）：節點清單存取、出站用戶端與聚合探測。
///
/// 這些測試刻意不碰真實網路：出站改成注入假 <see cref="HttpMessageHandler"/>／假
/// <see cref="ICentralNodeClient"/>，因此「有沒有帶 Bearer、打哪個路徑、單台失敗會不會拖垮整輪」
/// 都能在 CI 上穩定驗證。
/// </summary>
public class CentralNodeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly CentralNodeRepository _repo;

    public CentralNodeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-central-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new CentralNodeRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        GC.SuppressFinalize(this);
    }

    // ---------- repository ----------

    [Fact]
    public void 新增節點後可完整列出設定值()
    {
        var id = _repo.Add("機房 A", "http://10.0.0.5:5080", "key-a");

        var node = Assert.Single(_repo.List());
        Assert.Equal(id, node.Id);
        Assert.Equal("機房 A", node.Name);
        Assert.Equal("http://10.0.0.5:5080", node.BaseUrl);
        Assert.Equal("key-a", node.ApiKey);
        Assert.True(node.Enabled);
        Assert.Null(node.Online);
        Assert.Null(node.LastSeenUtc);
    }

    [Fact]
    public void 更新只改指定欄位不影響其他欄位()
    {
        var id = _repo.Add("舊名", "http://10.0.0.5:5080", "key-a");

        _repo.Update(id, name: "新名", enabled: false);

        var node = Assert.Single(_repo.List());
        Assert.Equal("新名", node.Name);
        Assert.Equal("http://10.0.0.5:5080", node.BaseUrl);
        Assert.Equal("key-a", node.ApiKey);
        Assert.False(node.Enabled);
    }

    [Fact]
    public void 啟用清單只含啟用中的節點()
    {
        _repo.Add("開", "http://a:1", "k");
        _repo.Add("關", "http://b:1", "k", enabled: false);

        var enabled = Assert.Single(_repo.ListEnabled());
        Assert.Equal("開", enabled.Name);
        Assert.Equal(2, _repo.List().Count);
    }

    [Fact]
    public void 刪除節點後不再列出()
    {
        var id = _repo.Add("x", "http://a:1", "k");

        _repo.Delete(id);

        Assert.Empty(_repo.List());
    }

    [Fact]
    public void 探測成功會更新上線狀態與機型資訊()
    {
        var id = _repo.Add("x", "http://a:1", "k");
        var at = new DateTime(2026, 3, 1, 2, 3, 4, DateTimeKind.Utc);

        _repo.RecordProbe(id, online: true, at, new NodeStatus("N1", "child", "0.1.0", 8), error: null);

        var node = Assert.Single(_repo.List());
        Assert.True(node.Online);
        Assert.Equal("N1", node.NodeId);
        Assert.Equal("0.1.0", node.Version);
        Assert.Equal(8, node.Channels);
        Assert.Equal(at, node.LastSeenUtc);
        Assert.Null(node.LastError);
    }

    [Fact]
    public void 探測失敗保留上次機型並記錄錯誤()
    {
        var id = _repo.Add("x", "http://a:1", "k");
        _repo.RecordProbe(id, online: true, DateTime.UtcNow, new NodeStatus("N1", "child", "0.1.0", 4), error: null);

        _repo.RecordProbe(id, online: false, DateTime.UtcNow, result: null, error: "連線逾時");

        var node = Assert.Single(_repo.List());
        Assert.False(node.Online);
        Assert.Equal("N1", node.NodeId);
        Assert.Equal(4, node.Channels);
        Assert.Equal("連線逾時", node.LastError);
    }

    // ---------- HttpCentralNodeClient ----------

    [Fact]
    public async Task 用戶端以_Bearer_金鑰呼叫_api_node()
    {
        var handler = new FakeHandler(_ => Json("{\"nodeId\":\"N1\",\"name\":\"child\",\"version\":\"0.1.0\",\"channels\":8}"));
        using var client = new HttpCentralNodeClient(handler);

        var status = await client.FetchAsync("http://10.0.0.5:5080/", "key-a");

        Assert.Equal("N1", status.NodeId);
        Assert.Equal("child", status.Name);
        Assert.Equal(8, status.Channels);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(new Uri("http://10.0.0.5:5080/api/node"), handler.LastUri);
        Assert.Equal("Bearer", handler.LastAuth?.Scheme);
        Assert.Equal("key-a", handler.LastAuth?.Parameter);
    }

    [Fact]
    public async Task 用戶端相容含路徑與尾端斜線的_base_url()
    {
        var handler = new FakeHandler(_ => Json("{\"nodeId\":\"N1\",\"name\":\"c\",\"version\":\"1\",\"channels\":1}"));
        using var client = new HttpCentralNodeClient(handler);

        // 子節點可能掛在反向代理的子路徑下，且尾端斜線可有可無；兩者都必須指向同一個 api/node。
        await client.FetchAsync("http://host:5080/vms", "k");
        Assert.Equal(new Uri("http://host:5080/vms/api/node"), handler.LastUri);

        await client.FetchAsync("http://host:5080/vms/", "k");
        Assert.Equal(new Uri("http://host:5080/vms/api/node"), handler.LastUri);
    }

    [Fact]
    public async Task 空金鑰時不帶授權標頭()
    {
        var handler = new FakeHandler(_ => Json("{\"nodeId\":\"N1\",\"name\":\"c\",\"version\":\"1\",\"channels\":1}"));
        using var client = new HttpCentralNodeClient(handler);

        await client.FetchAsync("http://host:1", string.Empty);

        Assert.Null(handler.LastAuth);
    }

    [Fact]
    public async Task 代理以_Bearer_金鑰取回指定路徑且非_2xx_不擲例外()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new ByteArrayContent(new byte[] { 9, 8, 7 }),
        });
        using var client = new HttpCentralNodeClient(handler);

        var resource = await client.FetchResourceAsync(
            "http://10.0.0.5:5080/", "key-a",
            "api/stream/1/playlist.m3u8?stream=main&from=2026-01-01T00:00:00.0000000Z");

        // 非 2xx 不得擲例外：代理要靠狀態碼分辨「此時段沒錄影」與真的連線失敗。
        Assert.Equal(404, resource.StatusCode);
        Assert.Equal(new byte[] { 9, 8, 7 }, resource.Body);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(
            new Uri("http://10.0.0.5:5080/api/stream/1/playlist.m3u8?stream=main&from=2026-01-01T00:00:00.0000000Z"),
            handler.LastUri);
        Assert.Equal("Bearer", handler.LastAuth?.Scheme);
        Assert.Equal("key-a", handler.LastAuth?.Parameter);
    }

    [Fact]
    public async Task 代理成功時回傳內容型別與主體()
    {
        var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        content.Headers.ContentType = new MediaTypeHeaderValue("video/iso.segment");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpCentralNodeClient(handler);

        var resource = await client.FetchResourceAsync("http://host:1", "k", "api/stream/segment/7/media.m4s");

        Assert.Equal(200, resource.StatusCode);
        Assert.Equal("video/iso.segment", resource.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, resource.Body);
    }

    [Fact]
    public async Task 代理相容含路徑與尾端斜線的_base_url()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        });
        using var client = new HttpCentralNodeClient(handler);

        await client.FetchResourceAsync("http://host:5080/vms", "k", "api/stream/1/playlist.m3u8");

        Assert.Equal(new Uri("http://host:5080/vms/api/stream/1/playlist.m3u8"), handler.LastUri);
    }

    [Fact]
    public async Task 非成功狀態碼會擲例外()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpCentralNodeClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync("http://host:1", "bad"));
    }

    // ---------- CentralNodeService ----------

    [Fact]
    public async Task 探測成功會把節點標為上線並記錄時間()
    {
        var id = _repo.Add("child", "http://child:5080", "k");
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new CentralNodeService(
            _repo, new FakeClient(_ => new NodeStatus("N1", "child", "0.1.0", 8)), clock);

        var node = await service.ProbeAsync(_repo.List()[0]);

        Assert.Equal(id, node.Id);
        Assert.True(node.Online);
        Assert.Equal("N1", node.NodeId);
        Assert.Equal(8, node.Channels);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, node.LastSeenUtc);
        Assert.Null(node.LastError);
    }

    [Fact]
    public async Task 聚合只探測啟用中的節點()
    {
        _repo.Add("on", "http://on:1", "k");
        _repo.Add("off", "http://off:1", "k", enabled: false);
        var client = new FakeClient(_ => new NodeStatus("N", "n", "1", 1));
        var service = new CentralNodeService(_repo, client);

        var nodes = await service.ProbeAllAsync();

        Assert.Equal(1, client.Calls);
        Assert.Equal("http://on:1", Assert.Single(client.Urls));
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public async Task 單一節點失敗不影響其他節點()
    {
        var goodId = _repo.Add("good", "http://good:1", "k");
        var badId = _repo.Add("bad", "http://bad:1", "k");
        var client = new FakeClient(url => url.Contains("bad", StringComparison.Ordinal)
            ? throw new HttpRequestException("連線逾時")
            : new NodeStatus("G", "good", "1", 4));
        var service = new CentralNodeService(_repo, client);

        var nodes = await service.ProbeAllAsync();

        var good = nodes.Single(n => n.Id == goodId);
        var bad = nodes.Single(n => n.Id == badId);
        Assert.True(good.Online);
        Assert.Equal(4, good.Channels);
        Assert.False(bad.Online);
        Assert.Equal("連線逾時", bad.LastError);
    }

    [Fact]
    public async Task 逾時視為離線而非外部取消()
    {
        _repo.Add("x", "http://x:1", "k");
        // HttpClient 逾時會擲 TaskCanceledException，但呼叫端的權杖並未取消。
        var client = new FakeClient(_ => throw new TaskCanceledException("timeout"));
        var service = new CentralNodeService(_repo, client);

        var node = Assert.Single(await service.ProbeAllAsync());

        Assert.False(node.Online);
        Assert.NotNull(node.LastError);
    }

    [Fact]
    public async Task 外部取消會往外拋且不記為離線()
    {
        _repo.Add("x", "http://x:1", "k");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeClient(_ => throw new OperationCanceledException(cts.Token));
        var service = new CentralNodeService(_repo, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProbeAllAsync(cts.Token));

        Assert.Null(_repo.List()[0].Online);
    }

    // ---------- ListChannelsAsync（M302 頻道聚合） ----------

    [Fact]
    public async Task 頻道聚合會帶來源節點並解析_json()
    {
        var a = _repo.Add("機房 A", "http://a:5080", "k");
        var b = _repo.Add("機房 B", "http://b:5080", "k");
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (url, _) => url.Contains("a:5080", StringComparison.Ordinal)
                ? Resource("[{\"id\":1,\"name\":\"大門\",\"enabled\":true},{\"id\":2,\"name\":\"後門\",\"enabled\":false}]")
                : Resource("[{\"id\":1,\"name\":\"車道\"}]"));
        var service = new CentralNodeService(_repo, client);

        var channels = await service.ListChannelsAsync();

        Assert.Equal(3, channels.Count);
        var gate = channels.Single(c => c.NodeId == a && c.ChannelId == 1);
        Assert.Equal("機房 A", gate.NodeName);
        Assert.Equal("大門", gate.ChannelName);
        Assert.True(gate.Enabled);
        Assert.False(channels.Single(c => c.NodeId == a && c.ChannelId == 2).Enabled);
        // 缺 enabled 一律視為啟用（舊子節點沒這個欄位不該被畫成停用）。
        Assert.True(channels.Single(c => c.NodeId == b).Enabled);
        // 兩台都只查 api/channels。
        Assert.All(client.ResourcePaths, p => Assert.Equal("api/channels", p));
    }

    [Fact]
    public async Task 頻道聚合遇到失敗節點只跳過該節點()
    {
        var ok = _repo.Add("ok", "http://ok:1", "k");
        _repo.Add("boom", "http://boom:1", "k");
        _repo.Add("bad", "http://bad:1", "k");
        _repo.Add("weird", "http://weird:1", "k");
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (url, _) => url switch
            {
                var u when u.Contains("boom", StringComparison.Ordinal) => throw new HttpRequestException("連線逾時"),
                var u when u.Contains("bad", StringComparison.Ordinal) => Resource("", status: 500),
                var u when u.Contains("weird", StringComparison.Ordinal) => Resource("{ not json"),
                _ => Resource("[{\"id\":7,\"name\":\"cam\"}]"),
            });
        var service = new CentralNodeService(_repo, client);

        var channels = await service.ListChannelsAsync();

        var only = Assert.Single(channels);
        Assert.Equal(ok, only.NodeId);
        Assert.Equal(7, only.ChannelId);
    }

    [Fact]
    public async Task 頻道聚合不查停用或未設位址的節點()
    {
        _repo.Add("on", "http://on:1", "k");
        _repo.Add("off", "http://off:1", "k", enabled: false);
        _repo.Add("empty", "", "k");
        var client = new FakeClient(_ => new NodeStatus("N", "n", "1", 1), (_, _) => Resource("[]"));
        var service = new CentralNodeService(_repo, client);

        await service.ListChannelsAsync();

        Assert.Equal("http://on:1", Assert.Single(client.ResourceUrls));
    }

    [Fact]
    public async Task 頻道聚合缺名稱以編號補名且忽略無效條目()
    {
        _repo.Add("a", "http://a:1", "k");
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (_, _) => Resource("[{\"id\":5},{\"name\":\"no-id\"},{\"id\":0,\"name\":\"zero\"},{\"id\":\"x\",\"name\":\"str\"},\"junk\"]"));
        var service = new CentralNodeService(_repo, client);

        var channels = await service.ListChannelsAsync();

        var only = Assert.Single(channels);
        Assert.Equal(5, only.ChannelId);
        Assert.Equal("ch5", only.ChannelName);
    }

    [Fact]
    public async Task 頻道聚合的外部取消會往外拋()
    {
        _repo.Add("x", "http://x:1", "k");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (_, _) => throw new OperationCanceledException(cts.Token));
        var service = new CentralNodeService(_repo, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListChannelsAsync(cts.Token));
    }

    // ---------- ListAlarmsAsync（M303 警報聚合） ----------

    [Fact]
    public async Task 警報聚合會帶來源節點並依逾期與時間排序()
    {
        _repo.Add("機房 A", "http://a:5080", "k");
        _repo.Add("機房 B", "http://b:5080", "k");
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (url, _) => url.Contains("a:", StringComparison.Ordinal)
                ? Resource("[{\"eventId\":1,\"channelId\":1,\"eventType\":\"motion\",\"startUtc\":\"2026-03-01T00:00:00Z\",\"overdue\":false},{\"eventId\":2,\"channelId\":3,\"eventType\":\"line_cross\",\"startUtc\":\"2026-03-02T00:00:00Z\",\"overdue\":false}]")
                : Resource("[{\"eventId\":9,\"channelId\":2,\"eventType\":\"intrusion\",\"startUtc\":\"2026-03-01T12:00:00Z\",\"overdue\":true}]"));
        var service = new CentralNodeService(_repo, client);

        var alarms = await service.ListAlarmsAsync();

        // 逾期一律排最前（跨機看板最在意處理時限），其餘依事件時間新到舊。
        Assert.Equal(new long[] { 9, 2, 1 }, alarms.Select(a => a.EventId).ToArray());
        var first = alarms[0];
        Assert.Equal("機房 B", first.NodeName);
        Assert.Equal(2, first.ChannelId);
        Assert.Equal("intrusion", first.EventType);
        Assert.Equal(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), first.StartUtc);
        Assert.True(first.Overdue);
        // 兩台都只查子節點看板，且帶入上限。
        Assert.All(client.ResourcePaths, p => Assert.Equal("api/alarm-board?take=100", p));
    }

    [Fact]
    public async Task 警報聚合遇到失敗節點只跳過該節點()
    {
        var ok = _repo.Add("ok", "http://ok:1", "k");
        _repo.Add("boom", "http://boom:1", "k");
        _repo.Add("bad", "http://bad:1", "k");
        _repo.Add("weird", "http://weird:1", "k");
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (url, _) => url switch
            {
                var u when u.Contains("boom", StringComparison.Ordinal) => throw new HttpRequestException("連線逾時"),
                var u when u.Contains("bad", StringComparison.Ordinal) => Resource("", status: 500),
                var u when u.Contains("weird", StringComparison.Ordinal) => Resource("{ not json"),
                _ => Resource("[{\"eventId\":7,\"channelId\":1,\"eventType\":\"motion\"}]"),
            });
        var service = new CentralNodeService(_repo, client);

        var alarms = await service.ListAlarmsAsync();

        var only = Assert.Single(alarms);
        Assert.Equal(ok, only.NodeId);
        Assert.Equal(7, only.EventId);
    }

    [Fact]
    public async Task 警報聚合不查停用或未設位址的節點且套用取用上限()
    {
        _repo.Add("on", "http://on:1", "k");
        _repo.Add("off", "http://off:1", "k", enabled: false);
        _repo.Add("empty", "", "k");
        var client = new FakeClient(_ => new NodeStatus("N", "n", "1", 1), (_, _) => Resource("[]"));
        var service = new CentralNodeService(_repo, client);

        await service.ListAlarmsAsync(take: 25);

        Assert.Equal("http://on:1", Assert.Single(client.ResourceUrls));
        Assert.Equal("api/alarm-board?take=25", Assert.Single(client.ResourcePaths));
    }

    [Fact]
    public async Task 警報聚合忽略無效條目並補上預設值()
    {
        _repo.Add("a", "http://a:1", "k");
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (_, _) => Resource("[{\"eventId\":5},{\"eventId\":0},{\"eventId\":\"x\"},{\"channelId\":1},\"junk\"]"));
        var service = new CentralNodeService(_repo, client);

        var alarms = await service.ListAlarmsAsync();

        var only = Assert.Single(alarms);
        Assert.Equal(5, only.EventId);
        Assert.Equal(0, only.ChannelId);
        Assert.Equal(string.Empty, only.EventType);
        // 缺 status/priority 一律補預設，避免跨機看板出現空白格。
        Assert.Equal("pending", only.Status);
        Assert.Equal(AlarmPriority.Normal, only.Priority);
        Assert.False(only.Overdue);
        Assert.Null(only.DueUtc);
    }

    [Fact]
    public async Task 警報聚合的外部取消會往外拋()
    {
        _repo.Add("x", "http://x:1", "k");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeClient(
            _ => new NodeStatus("N", "n", "1", 1),
            (_, _) => throw new OperationCanceledException(cts.Token));
        var service = new CentralNodeService(_repo, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAlarmsAsync(cancellationToken: cts.Token));
    }

    // ---------- 測試替身 ----------

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static CentralNodeResource Resource(string json, int status = 200)
        => new(status, "application/json", Encoding.UTF8.GetBytes(json));


    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public Uri? LastUri { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public AuthenticationHeaderValue? LastAuth { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 請求在用戶端 method 結束時會被釋放，先把要斷言的欄位抄下來。
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            LastAuth = request.Headers.Authorization;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class FakeClient : ICentralNodeClient
    {
        private readonly Func<string, NodeStatus> _respond;
        private readonly Func<string, string, CentralNodeResource>? _resource;

        public FakeClient(
            Func<string, NodeStatus> respond,
            Func<string, string, CentralNodeResource>? resource = null)
        {
            _respond = respond;
            _resource = resource;
        }

        public int Calls { get; private set; }

        public List<string> Urls { get; } = new();

        public List<string> ResourcePaths { get; } = new();

        public List<string> ResourceUrls { get; } = new();

        public Task<NodeStatus> FetchAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            Urls.Add(baseUrl);
            return Task.FromResult(_respond(baseUrl));
        }

        public Task<CentralNodeResource> FetchResourceAsync(
            string baseUrl, string apiKey, string relativePath, CancellationToken cancellationToken = default)
        {
            ResourcePaths.Add(relativePath);
            ResourceUrls.Add(baseUrl);
            if (_resource is null)
            {
                throw new NotSupportedException("聚合探測測試替身不處理跨機回放代理。");
            }
            return Task.FromResult(_resource(baseUrl, relativePath));
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
