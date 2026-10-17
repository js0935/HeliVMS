using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

public sealed class TeamPlusNotifierTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public TeamPlusNotifierTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-teamplus-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }

    private static AlarmEventRecord Event(string type = "motion", int channelId = 7, string? detail = "peak=42%")
        => new()
        {
            Id = 1,
            ChannelId = channelId,
            EventType = type,
            StartUtc = new DateTime(2026, 9, 14, 1, 2, 3, DateTimeKind.Utc),
            Detail = detail,
        };

    private static NotificationSettings Cfg(string apiUrl) =>
        new NotificationSettings(true, null, false, null, 587, null, Array.Empty<string>(), null, null, null, null) with
        {
            TeamPlusEnabled = true,
            TeamPlusApiUrl = apiUrl,
            TeamPlusAccount = "acct",
            TeamPlusApiKey = "key123",
            TeamPlusChatSn = "chat-9",
        };

    private static Dictionary<string, string> ParseForm(string body)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
            map[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return map;
    }

    [Fact]
    public async Task 設定齊全時以表單欄位推播至teamPlus()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        var cfg = Cfg($"http://127.0.0.1:{http.Port}/api");

        var ok = await new TeamPlusNotifier().SendAsync(cfg, Event());

        Assert.True(ok);
        Assert.Equal(1, http.Hits);
        var req = Assert.Single(http.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/api", req.Path);
        Assert.Contains(
            req.Headers,
            h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                 && h.Value.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));

        var form = ParseForm(req.Body);
        Assert.Equal("acct", form["account"]);
        Assert.Equal("key123", form["api_key"]);
        Assert.Equal("chat-9", form["chat_sn"]);
        Assert.Equal("1", form["Content_type"]);
        Assert.Contains("motion", form["msg_content"]);
        Assert.Contains("7", form["msg_content"]);
        Assert.Contains("peak=42%", form["msg_content"]);
    }

    [Fact]
    public async Task 缺少聊天室編號時不送出()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        var cfg = Cfg($"http://127.0.0.1:{http.Port}/api") with { TeamPlusChatSn = null };
        Assert.False(cfg.HasTeamPlusRoute);

        var ok = await new TeamPlusNotifier().SendAsync(cfg, Event());

        Assert.False(ok);
        Assert.Equal(0, http.Hits);
    }

    [Fact]
    public async Task teamPlus回非成功狀態時回報失敗()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 500 Internal Server Error");

        var ok = await new TeamPlusNotifier().SendAsync(Cfg($"http://127.0.0.1:{http.Port}/api"), Event());

        Assert.False(ok);
    }

    [Fact]
    public async Task 通知服務會將事件路由至teamPlus通道()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        var settings = new SettingsRepository(_store);
        settings.Set(NotificationSettings.EnabledKey, "true");
        settings.Set(NotificationSettings.TeamPlusEnabledKey, "true");
        settings.Set(NotificationSettings.TeamPlusApiUrlKey, $"http://127.0.0.1:{http.Port}/api");
        settings.Set(NotificationSettings.TeamPlusAccountKey, "acct");
        settings.Set(NotificationSettings.TeamPlusApiKeyKey, SecretProtector.Protect("key123"));
        settings.Set(NotificationSettings.TeamPlusChatSnKey, "chat-9");

        using var svc = new NotificationService(
            _store,
            interval: TimeSpan.FromMilliseconds(100),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.DeliveredCount == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(1, http.Hits);
        var row = Assert.Single(new NotificationLogRepository(_store).ListRecent(10));
        Assert.True(row.Ok);
        Assert.Equal("teamplus", row.Route);
    }

    private static async Task WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond())
            {
                return;
            }

            await Task.Delay(30);
        }

        Assert.Fail("條件在逾時內未成立。");
    }
}
