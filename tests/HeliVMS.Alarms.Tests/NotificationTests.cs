using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public NotificationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-notify-{Guid.NewGuid():N}.db");
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

    private static byte[] DecodeB64Url(string s)
        => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/')
            + new string('=', (4 - s.Length % 4) % 4));

    private static AlarmEventRecord Event(
        string type = "motion",
        int channelId = 7,
        string? detail = "duration=1200ms peak=42%",
        string? snapshotPath = null)
        => new()
        {
            Id = 1,
            ChannelId = channelId,
            EventType = type,
            StartUtc = new DateTime(2026, 9, 14, 1, 2, 3, DateTimeKind.Utc),
            Detail = detail,
            SnapshotPath = snapshotPath,
        };

    private SettingsRepository Settings => new(_store);

    [Fact]
    public void Load_NoKeys_EnabledByDefault_NoChannels()
    {
        var cfg = NotificationSettings.Load(Settings);
        Assert.True(cfg.Enabled);
        Assert.False(cfg.AnyChannelConfigured);
        Assert.Equal(587, cfg.SmtpPort);
    }

    [Fact]
    public void Load_HonorsDbValues()
    {
        var s = Settings;
        s.Set("notify.enabled", "false");
        s.Set("notify.webhook.url", "http://x/hook");
        s.Set("notify.smtp.enabled", "true");
        s.Set("notify.smtp.port", "2525");

        var cfg = NotificationSettings.Load(s);
        Assert.False(cfg.Enabled);
        Assert.Equal("http://x/hook", cfg.WebhookUrl);
        Assert.True(cfg.SmtpEnabled);
        Assert.Equal(2525, cfg.SmtpPort);
        Assert.True(cfg.AnyChannelConfigured);
    }

    [Fact]
    public void Load_PasswordProtectedStored_Unprotects()
    {
        var s = Settings;
        s.Set("notify.smtp.password", SecretProtector.Protect("s3cret"));

        var cfg = NotificationSettings.Load(s);
        Assert.Equal("s3cret", cfg.SmtpPassword);
    }

    [Fact]
    public async Task WebhookNotifier_PostsJsonPayload()
    {
        using var server = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        var notifier = new WebhookNotifier();

        var ok = await notifier.SendAsync(server.Url, Event());
        Assert.True(ok);
        Assert.Equal(1, server.Hits);

        var req = server.Requests.Single();
        Assert.Equal("POST", req.Method);
        using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
        Assert.Equal("motion", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("channel").GetInt32());
        Assert.Equal("2026-09-14T01:02:03.0000000Z", doc.RootElement.GetProperty("ts").GetString());
        Assert.True(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task WebhookNotifier_ServerError_ReturnsFalse()
    {
        using var server = new FakeHttpServer(_ => "HTTP/1.1 500 Boom");
        var notifier = new WebhookNotifier();
        Assert.False(await notifier.SendAsync(server.Url, Event()));
    }

    [Fact]
    public async Task WebhookNotifier_Unreachable_ReturnsFalse()
    {
        var notifier = new WebhookNotifier();
        // 127.0.0.1 上未監聽的埠 → 連線失敗
        Assert.False(await notifier.SendAsync("http://127.0.0.1:1/", Event()));
    }

    [Fact]
    public async Task Service_Unconfigured_DoesNotSend()
    {
        var svc = new NotificationService(_store, interval: TimeSpan.FromSeconds(1));
        try
        {
            svc.Enqueue(Event());
            svc.FlushNow();
            await Task.Delay(300);
            Assert.Equal(0, svc.DeliveredCount);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Service_RetriesThenDelivers()
    {
        Settings.Set("notify.webhook.url", "http://127.0.0.1:1/hook"); // 先給不可達埠，稍後導向假伺服器
        using var server = new FakeHttpServer(hit => hit < 3 ? "HTTP/1.1 500 Fail" : "HTTP/1.1 200 OK");
        Settings.Set("notify.webhook.url", server.Url);

        var svc = new NotificationService(_store, interval: TimeSpan.FromMilliseconds(40), backoffBase: TimeSpan.FromMilliseconds(40), maxAttempts: 3);
        try
        {
            svc.Enqueue(Event());
            await WaitUntilAsync(() => svc.DeliveredCount > 0, TimeSpan.FromSeconds(5));
            Assert.Equal(1, svc.DeliveredCount);
            Assert.Equal(3, server.Hits);   // 500 ×2 → 200
            Assert.Equal(0, svc.FailedCount);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Service_GivesUpAfterMaxAttempts()
    {
        Settings.Set("notify.webhook.url", "http://127.0.0.1:1/hook");
        using var server = new FakeHttpServer(_ => "HTTP/1.1 500 Boom");
        Settings.Set("notify.webhook.url", server.Url);

        var svc = new NotificationService(_store, interval: TimeSpan.FromMilliseconds(40), backoffBase: TimeSpan.FromMilliseconds(40), maxAttempts: 3);
        try
        {
            svc.Enqueue(Event());
            await WaitUntilAsync(() => svc.FailedCount > 0, TimeSpan.FromSeconds(5));
            Assert.Equal(1, svc.FailedCount);
            Assert.Equal(0, svc.DeliveredCount);
            Assert.Equal(3, server.Hits);
            Assert.Equal(0, svc.PendingCount);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task SmtpNotifier_DeliversToFakeServer()
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");

        using var smtp = new FakeSmtpServer();
        var cfg = NotificationSettings.Load(Settings).WithHostPort(smtp.Host, smtp.Port);
        var notifier = new SmtpNotifier();

        var ok = await notifier.SendAsync(cfg, Event());
        Assert.True(ok);

        var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.Contains("helivms.local", msg.MailFrom);
        Assert.Contains(msg.RcptTo, r => r.Contains("ops@helivms.local"));

        var decoded = DecodeMail(msg);
        Assert.Contains("motion", decoded.Subject);
        Assert.Contains("7", decoded.Subject);
        Assert.Contains("duration=1200ms", decoded.Body);
    }

    [Fact]
    public async Task SmtpNotifier_MultiRecords_OneMergedMail()
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");

        using var smtp = new FakeSmtpServer();
        var cfg = NotificationSettings.Load(Settings).WithHostPort(smtp.Host, smtp.Port);
        var notifier = new SmtpNotifier();

        var second = Event("offline", channelId: 8, detail: "signal lost");
        var ok = await notifier.SendAsync(cfg, new[] { Event(), second });
        Assert.True(ok);

        var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.False(smtp.TryDequeueMessage(out _));

        var decoded = DecodeMail(msg);
        Assert.Contains("（2 則）", decoded.Subject);
        Assert.Contains("motion", decoded.Body);
        Assert.Contains("offline", decoded.Body);
        Assert.Contains("signal lost", decoded.Body);
        Assert.Contains("頻道：8", decoded.Body);
    }

    [Fact]
    public async Task SmtpNotifier_AttachesExistingSnapshot()
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");

        var snapPath = Path.Combine(Path.GetTempPath(), $"helivms-snap-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(snapPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
            using var smtp = new FakeSmtpServer();
            var cfg = NotificationSettings.Load(Settings).WithHostPort(smtp.Host, smtp.Port);
            var notifier = new SmtpNotifier();

            var ok = await notifier.SendAsync(cfg, Event(snapshotPath: snapPath));
            Assert.True(ok);

            var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
            Assert.NotNull(msg);
            Assert.Contains("image/png", msg.Payload);
            Assert.Contains(Path.GetFileName(snapPath), msg.Payload);
        }
        finally
        {
            File.Delete(snapPath);
        }
    }

    [Fact]
    public async Task SmtpNotifier_MissingSnapshot_NoAttachment()
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");

        var snapPath = Path.Combine(Path.GetTempPath(), $"helivms-nosnap-{Guid.NewGuid():N}.png");
        using var smtp = new FakeSmtpServer();
        var cfg = NotificationSettings.Load(Settings).WithHostPort(smtp.Host, smtp.Port);
        var notifier = new SmtpNotifier();

        var ok = await notifier.SendAsync(cfg, Event(snapshotPath: snapPath));
        Assert.True(ok);

        var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.DoesNotContain("image/png", msg.Payload);
        var decoded = DecodeMail(msg);
        Assert.Contains(snapPath, decoded.Body);
    }

    [Fact]
    public async Task Service_PureSmtp_MergesIntoSingleMail()
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");

        using var smtp = new FakeSmtpServer();
        Settings.Set("notify.smtp.host", smtp.Host);
        Settings.Set("notify.smtp.port", smtp.Port.ToString());

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(50),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        svc.Enqueue(Event("offline", channelId: 8, detail: "signal lost"));
        await WaitUntilAsync(() => svc.DeliveredCount == 2, TimeSpan.FromSeconds(10));

        var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.False(smtp.TryDequeueMessage(out _));
        Assert.Equal(0, svc.FailedCount);

        var decoded = DecodeMail(msg);
        Assert.Contains("（2 則）", decoded.Subject);

        var log = new NotificationLogRepository(_store);
        Assert.Equal(2, log.Count());
        var rows = log.ListRecent(10);
        Assert.All(rows, r => Assert.True(r.Ok));
        Assert.All(rows, r => Assert.Equal("smtp", r.Route));
    }

    [Fact]
    public async Task Service_PureSmtp_QuietSkipsWithoutMail()
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");
        Settings.Set("notify.smtp.host", "127.0.0.1");
        Settings.Set("notify.smtp.port", "2525");
        var nowLocal = DateTime.Now;
        Settings.Set("notify.quiet.start", nowLocal.AddMinutes(-30).ToString("HH:mm"));
        Settings.Set("notify.quiet.end", nowLocal.AddMinutes(30).ToString("HH:mm"));

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(50),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.SkippedDuringQuietCount == 2, TimeSpan.FromSeconds(10));

        Assert.Equal(0, svc.DeliveredCount);
        Assert.Equal(0, new NotificationLogRepository(_store).Count());
    }

    [Fact]
    public async Task Service_QuietRetransmit_DefersThenDeliversWebhook()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        Settings.Set("notify.enabled", "true");
        Settings.Set("notify.webhook.url", $"http://127.0.0.1:{http.Port}/hook");
        var nowLocal = DateTime.Now;
        if (nowLocal.Hour < 1 || nowLocal.Hour >= 23)
        {
            return;   // 鄰近午夜避免 HH:mm 跨日誤判（守衛跳過）
        }

        var qStartText = nowLocal.AddMinutes(-30).ToString("HH:mm");
        var qEndText = nowLocal.AddMinutes(30).ToString("HH:mm");
        Settings.Set("notify.quiet.start", qStartText);
        Settings.Set("notify.quiet.end", qEndText);
        Settings.Set("notify.quiet.retransmit", "true");
        var probe = NotificationSettings.Load(Settings);
        Assert.True(probe.QuietRetransmit, "retransmit 未載入");
        Assert.True(probe.IsInQuietHours(DateTime.Now), "未處於靜默時段");

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(100),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.SkippedDuringQuietCount == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(0, svc.DeliveredCount);
        Assert.Equal(0, http.Hits);
        Assert.Equal(0, new NotificationLogRepository(_store).Count());

        Settings.Set("notify.quiet.start", nowLocal.AddHours(-2).ToString("HH:mm"));
        Settings.Set("notify.quiet.end", nowLocal.AddHours(-1).ToString("HH:mm"));
        await WaitUntilAsync(() => svc.DeliveredCount == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(1, http.Hits);
        var log = new NotificationLogRepository(_store);
        var row = Assert.Single(log.ListRecent(10));
        Assert.True(row.Ok);
        Assert.Contains("延後補送", row.Detail);
    }

    [Fact]
    public async Task Service_QuietRetransmit_SmtpBatchDefersThenDelivers()
    {
        using var smtp = new FakeSmtpServer();
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");
        Settings.Set("notify.smtp.host", "127.0.0.1");
        Settings.Set("notify.smtp.port", smtp.Port.ToString());
        var nowLocal = DateTime.Now;
        if (nowLocal.Hour < 1 || nowLocal.Hour >= 23)
        {
            return;   // 守衛同上
        }

        Settings.Set("notify.quiet.start", nowLocal.AddMinutes(-30).ToString("HH:mm"));
        Settings.Set("notify.quiet.end", nowLocal.AddMinutes(30).ToString("HH:mm"));
        Settings.Set("notify.quiet.retransmit", "true");

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(50),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.SkippedDuringQuietCount == 2, TimeSpan.FromSeconds(10));

        Assert.Equal(0, svc.DeliveredCount);
        Assert.False(smtp.TryDequeueMessage(out _));

        Settings.Set("notify.quiet.start", nowLocal.AddHours(-2).ToString("HH:mm"));
        Settings.Set("notify.quiet.end", nowLocal.AddHours(-1).ToString("HH:mm"));
        await WaitUntilAsync(() => svc.DeliveredCount == 2, TimeSpan.FromSeconds(10));

        var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.False(smtp.TryDequeueMessage(out _));

        var log = new NotificationLogRepository(_store);
        var rows = log.ListRecent(10);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Ok));
        Assert.All(rows, r => Assert.Contains("延後補送", r.Detail));
    }

    /// <summary>將 SMTP DATA payload 解出主旨與內文（處理 MIME base64 內文與折疊 encoded-word 主旨）。</summary>
    private static (string Subject, string Body) DecodeMail(FakeSmtpMessage msg)
    {
        var raw = msg.Payload;
        const string Sep = "\r\n\r\n";
        var idx = raw.IndexOf(Sep, StringComparison.Ordinal);
        var headersRaw = idx >= 0 ? raw[..idx] : raw;
        var bodyRaw = idx >= 0 ? raw[(idx + Sep.Length)..] : string.Empty;

        // 併回 folded header（\r\n + 空白 → 空白）
        var headers = headersRaw.Replace("\r\n ", " ");

        var subject = string.Empty;
        foreach (var h in headers.Split('\n'))
        {
            if (h.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                subject = h.Substring("Subject:".Length).Trim();
                break;
            }
        }

        // 相鄰 encoded-word 之間的空格費棄（併回同一 token 串）
        subject = System.Text.RegularExpressions.Regex.Replace(subject, "\\?=\\s+(?==\\?)", "?=");
        subject = System.Text.RegularExpressions.Regex.Replace(subject,
            @"=\?utf-8\?B\?(?<b64>[A-Za-z0-9+/=]+)\?=",
            m => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups["b64"].Value)));

        var body = bodyRaw;
        if (headers.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var compact = string.Concat(bodyRaw.Where(c => !char.IsWhiteSpace(c)));
                body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(compact));
            }
            catch (FormatException)
            {
                // 非 base64 內文
            }
        }

        return (subject, body);
    }

    private static async Task<FakeSmtpMessage?> WaitForSmtpAsync(FakeSmtpServer smtp, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (smtp.TryDequeueMessage(out var m))
            {
                return m;
            }

            await Task.Delay(30);
        }

        return null;
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

    [Fact]
    public void IsInQuietHours_NormalInterval()
    {
        var at2h = new DateTime(2026, 9, 15, 2, 30, 0);
        var at5h = new DateTime(2026, 9, 15, 5, 0, 0);
        var cfg = QuietBase() with { QuietStart = "02:00", QuietEnd = "04:00" };
        Assert.True(cfg.IsInQuietHours(at2h));
        Assert.False(cfg.IsInQuietHours(at5h));
    }

    [Fact]
    public void IsInQuietHours_OvernightInterval()
    {
        var at2h = new DateTime(2026, 9, 15, 2, 30, 0);
        var atNoon = new DateTime(2026, 9, 15, 12, 0, 0);
        var at23h = new DateTime(2026, 9, 15, 23, 30, 0);
        var cfg = QuietBase() with { QuietStart = "22:00", QuietEnd = "06:00" };
        Assert.True(cfg.IsInQuietHours(at2h));
        Assert.True(cfg.IsInQuietHours(at23h));
        Assert.False(cfg.IsInQuietHours(atNoon));
    }

    [Fact]
    public void IsInQuietHours_EmptyOrInvalid_Disabled()
    {
        var at = new DateTime(2026, 9, 15, 2, 30, 0);
        var b = QuietBase();
        Assert.False((b with { QuietStart = null, QuietEnd = null }).IsInQuietHours(at));
        Assert.False((b with { QuietStart = "02:00", QuietEnd = "02:00" }).IsInQuietHours(at));
        Assert.False((b with { QuietStart = "25:00", QuietEnd = "04:00" }).IsInQuietHours(at));
        Assert.False((b with { QuietStart = "22:00", QuietEnd = "xx" }).IsInQuietHours(at));
    }

    [Fact]
    public async Task Service_AfterDelivery_WritesLogOk()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        Settings.Set("notify.enabled", "true");
        Settings.Set("notify.webhook.url", $"http://127.0.0.1:{http.Port}/hook");

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(100),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.DeliveredCount == 1, TimeSpan.FromSeconds(10));

        var log = new NotificationLogRepository(_store);
        Assert.Equal(1, log.Count());
        var row = Assert.Single(log.ListRecent(10));
        Assert.True(row.Ok);
        Assert.Equal("webhook", row.Route);
        Assert.Equal(7, row.ChannelId);
        Assert.Equal("motion", row.EventType);
    }

    [Fact]
    public async Task Service_AfterMaxAttempts_WritesLogFailure()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 500 Internal Server Error");
        Settings.Set("notify.enabled", "true");
        Settings.Set("notify.webhook.url", $"http://127.0.0.1:{http.Port}/hook");

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(100),
            backoffBase: TimeSpan.FromMilliseconds(50),
            maxAttempts: 3);
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.FailedCount == 1, TimeSpan.FromSeconds(10));

        var log = new NotificationLogRepository(_store);
        Assert.Equal(1, log.Count());
        var row = Assert.Single(log.ListRecent(10));
        Assert.False(row.Ok);
        Assert.Equal(3, row.Attempts);
        Assert.Contains("webhook", row.Detail);
    }

    [Fact]
    public async Task Service_QuietHours_SkipsWithoutLog()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        Settings.Set("notify.enabled", "true");
        Settings.Set("notify.webhook.url", $"http://127.0.0.1:{http.Port}/hook");
        var nowLocal = DateTime.Now;
        var qStartText = nowLocal.AddMinutes(-30).ToString("HH:mm");
        var qEndText = nowLocal.AddMinutes(30).ToString("HH:mm");
        var cfgIn = QuietBase() with { QuietStart = qStartText, QuietEnd = qEndText };
        Assert.True(cfgIn.IsInQuietHours(nowLocal));
        Settings.Set("notify.quiet.start", qStartText);
        Settings.Set("notify.quiet.end", qEndText);

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(100),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.SkippedDuringQuietCount == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(0, svc.DeliveredCount);
        Assert.Equal(0, svc.FailedCount);
        Assert.Equal(0, http.Hits);
        Assert.Equal(0, new NotificationLogRepository(_store).Count());
    }

    private NotificationSettings QuietBase() =>
        NotificationSettings.Load(Settings) with { QuietStart = null, QuietEnd = null };

    [Fact]
    public void Load_ReadsMqttValues()
    {
        var s = Settings;
        s.Set(NotificationSettings.MqttEnabledKey, "true");
        s.Set(NotificationSettings.MqttHostKey, "broker.local");
        s.Set(NotificationSettings.MqttPortKey, "2883");
        s.Set(NotificationSettings.MqttTopicKey, "helivms/alerts");
        s.Set(NotificationSettings.MqttUserKey, "ops");
        s.Set(NotificationSettings.MqttPasswordKey, SecretProtector.Protect("sekrit"));

        var cfg = NotificationSettings.Load(s);

        Assert.True(cfg.MqttEnabled);
        Assert.Equal("broker.local", cfg.MqttHost);
        Assert.Equal(2883, cfg.MqttPort);
        Assert.Equal("helivms/alerts", cfg.MqttTopic);
        Assert.Equal("ops", cfg.MqttUser);
        Assert.Equal("sekrit", cfg.MqttPassword);
        Assert.True(cfg.AnyChannelConfigured);
    }

    [Fact]
    public async Task MqttNotifier_PublishesJsonToBroker()
    {
        using var broker = new FakeMqttBroker();
        var cfg = NotificationSettings.Load(Settings) with
        {
            MqttEnabled = true,
            MqttHost = "127.0.0.1",
            MqttPort = broker.Port,
            MqttTopic = "helivms/alerts",
        };

        var ok = await new MqttNotifier().SendAsync(cfg, Event());

        Assert.True(ok);
        await broker.WaitForPublishAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("helivms/alerts", broker.ReceivedTopic);
        Assert.NotNull(broker.ReceivedPayload);
        using var doc = JsonDocument.Parse(broker.ReceivedPayload!);
        Assert.Equal(7, doc.RootElement.GetProperty("channel_id").GetInt32());
        Assert.Equal("motion", doc.RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task MqttNotifier_ConnackRejected_ReturnsFalse()
    {
        using var broker = new FakeMqttBroker { ConnackReturnCode = 5 };
        var cfg = NotificationSettings.Load(Settings) with
        {
            MqttEnabled = true,
            MqttHost = "127.0.0.1",
            MqttPort = broker.Port,
            MqttTopic = "helivms/alerts",
        };

        Assert.False(await new MqttNotifier().SendAsync(cfg, Event()));
    }

    [Fact]
    public async Task MqttNotifier_CannotConnect_ReturnsFalse()
    {
        var cfg = NotificationSettings.Load(Settings) with
        {
            MqttEnabled = true,
            MqttHost = "127.0.0.1",
            MqttPort = 1,
            MqttTopic = "helivms/alerts",
        };

        Assert.False(await new MqttNotifier().SendAsync(cfg, Event()));
    }

    [Fact]
    public void Load_PushFromStore_UnprotectsPrivateKey()
    {
        var s = Settings;
        s.Set(NotificationSettings.PushEnabledKey, "true");
        s.Set(NotificationSettings.PushEndpointKey, "https://push.example/v2/sub");
        s.Set(NotificationSettings.PushPublicKeyKey, "BPubKey");
        s.Set(NotificationSettings.PushPrivateKeyKey, SecretProtector.Protect("priv-key-raw"));

        var cfg = NotificationSettings.Load(s);
        Assert.True(cfg.PushEnabled);
        Assert.Equal("https://push.example/v2/sub", cfg.PushEndpoint);
        Assert.Equal("BPubKey", cfg.PushPublicKey);
        Assert.Equal("priv-key-raw", cfg.PushPrivateKey);
        Assert.True(cfg.HasPushRoute);
        Assert.True(cfg.AnyChannelConfigured);
    }

    [Fact]
    public void PushRoute_RequiresEnabledEndpointAndKey()
    {
        var cfg = NotificationSettings.Load(Settings) with
        {
            PushEnabled = true,
            PushEndpoint = "http://x:8080/sub",
        };
        Assert.False(cfg.HasPushRoute);

        var cfg2 = NotificationSettings.Load(Settings) with
        {
            PushEnabled = false,
            PushEndpoint = "http://x:8080/sub",
            PushPrivateKey = "priv",
        };
        Assert.False(cfg2.HasPushRoute);

        var cfg3 = NotificationSettings.Load(Settings) with
        {
            PushEnabled = true,
            PushEndpoint = "http://x:8080/sub",
            PushPrivateKey = "priv",
        };
        Assert.True(cfg3.HasPushRoute);
    }

    [Fact]
    public void VapidJwt_Es256SignatureVerifies()
    {
        var (pub, priv) = PushNotifier.GenerateKeyPair();
        var jwt = PushNotifier.CreateVapidJwt(priv, pub, "http://127.0.0.1:9999");

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var point = DecodeB64Url(pub);
        using var verifier = ECDsa.Create();
        verifier.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        Assert.True(verifier.VerifyData(signingInput, DecodeB64Url(parts[2]), HashAlgorithmName.SHA256));

        using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(DecodeB64Url(parts[1])));
        Assert.Equal("http://127.0.0.1:9999", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:helivms@localhost", claims.RootElement.GetProperty("sub").GetString());
    }

    [Fact]
    public async Task PushNotifier_PostsToEndpointWithVapidAuth()
    {
        using var server = new FakeHttpServer(_ => "HTTP/1.1 201 Created");
        var (pub, priv) = PushNotifier.GenerateKeyPair();
        var cfg = NotificationSettings.Load(Settings) with
        {
            PushEnabled = true,
            PushEndpoint = server.Url,
            PushPrivateKey = priv,
            PushPublicKey = pub,
        };

        var ok = await new PushNotifier().SendAsync(cfg, Event());
        Assert.True(ok);
        Assert.Equal(1, server.Hits);

        var req = server.Requests.Single();
        Assert.Equal("POST", req.Method);
        var auth = req.Headers
            .Single(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .Value;
        Assert.StartsWith("vapid t=", auth);
        Assert.Contains(", k=", auth);
        Assert.Equal("60", req.Headers
            .Single(h => h.Name.Equals("TTL", StringComparison.OrdinalIgnoreCase))
            .Value);

        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(7, doc.RootElement.GetProperty("channel_id").GetInt32());
        Assert.Equal("motion", doc.RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task PushNotifier_ServerError_ReturnsFalse()
    {
        using var server = new FakeHttpServer(_ => "HTTP/1.1 410 Gone");
        var (pub, priv) = PushNotifier.GenerateKeyPair();
        var cfg = NotificationSettings.Load(Settings) with
        {
            PushEnabled = true,
            PushEndpoint = server.Url,
            PushPrivateKey = priv,
            PushPublicKey = pub,
        };

        Assert.False(await new PushNotifier().SendAsync(cfg, Event()));
    }

    [Fact]
    public async Task PushNotifier_MissingPrivateKey_ReturnsFalse()
    {
        var cfg = NotificationSettings.Load(Settings) with
        {
            PushEnabled = true,
            PushEndpoint = "http://127.0.0.1:1/sub",
        };

        Assert.False(await new PushNotifier().SendAsync(cfg, Event()));
    }

    [Fact]
    public async Task Service_WebhookPlusPush_LogsBothRoutes()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 201 Created");
        var (pub, priv) = PushNotifier.GenerateKeyPair();
        Settings.Set(NotificationSettings.WebhookUrlKey, http.Url);
        Settings.Set(NotificationSettings.PushEnabledKey, "true");
        Settings.Set(NotificationSettings.PushEndpointKey, http.Url);
        Settings.Set(NotificationSettings.PushPublicKeyKey, pub);
        Settings.Set(NotificationSettings.PushPrivateKeyKey, SecretProtector.Protect(priv));

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(50),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.DeliveredCount == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(2, http.Hits);
        var row = Assert.Single(new NotificationLogRepository(_store).ListRecent(10));
        Assert.True(row.Ok);
        Assert.Equal("webhook+push", row.Route);
    }

    [Fact]
    public async Task Service_WebhookPlusMqtt_LogsBothRoutes()
    {
        using var http = new FakeHttpServer(_ => "HTTP/1.1 200 OK");
        using var broker = new FakeMqttBroker();
        Settings.Set(NotificationSettings.WebhookUrlKey, http.Url);
        Settings.Set(NotificationSettings.MqttEnabledKey, "true");
        Settings.Set(NotificationSettings.MqttHostKey, "127.0.0.1");
        Settings.Set(NotificationSettings.MqttPortKey, broker.Port.ToString());
        Settings.Set(NotificationSettings.MqttTopicKey, "helivms/alerts");

        using var svc = new NotificationService(_store,
            interval: TimeSpan.FromMilliseconds(50),
            backoffBase: TimeSpan.FromMilliseconds(50));
        svc.Enqueue(Event());
        await WaitUntilAsync(() => svc.DeliveredCount == 1, TimeSpan.FromSeconds(10));
        await broker.WaitForPublishAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, http.Hits);
        var row = Assert.Single(new NotificationLogRepository(_store).ListRecent(10));
        Assert.True(row.Ok);
        Assert.Equal("webhook+mqtt", row.Route);
    }
}

internal static class NotificationSettingsTestExtensions
{
    public static NotificationSettings WithHostPort(this NotificationSettings cfg, string host, int port)
        => cfg with { SmtpHost = host, SmtpPort = port, SmtpEnabled = true };
}

/// <summary>迷你 HTTP 伺服器（以 TcpListener 實作，避免 http.sys URLACL 依賴）。</summary>
internal sealed class FakeHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<int, string> _responder;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _hits;

    public FakeHttpServer(Func<int, string> responder)
    {
        _responder = responder;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/hook";

    public int Hits => Volatile.Read(ref _hits);

    public ConcurrentQueue<(string Method, string Path, string Body, List<(string Name, string Value)> Headers)> Requests { get; } = new();

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var firstLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? string.Empty;
            var parts = firstLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : string.Empty;
            var path = parts.Length > 1 ? parts[1] : string.Empty;

            var contentLength = 0;
            var headers = new List<(string Name, string Value)>();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                headers.Add((line[..idx], line[(idx + 1)..].Trim()));
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Split(':')[1], out contentLength);
                }
            }

            var body = string.Empty;
            if (contentLength > 0)
            {
                var buf = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    read += await reader.ReadAsync(buf, read, contentLength - read).WaitAsync(TimeSpan.FromSeconds(5));
                }

                body = new string(buf);
            }

            Requests.Enqueue((method, path, body, headers));
            var hit = Interlocked.Increment(ref _hits);
            var response = _responder(hit);
            var payload = Encoding.ASCII.GetBytes(
                $"{response}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(payload);
        }
        catch (Exception)
        {
            // 測試端點：忽略單一連線錯誤
        }
        finally
        {
            client?.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>迷你 SMTP 伺服器：可接受 EHLO/MAIL/RCPT/DATA，捕捉郵件內容。Socket-level（無 SSL）。</summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<FakeSmtpMessage> _messages = new();

    public FakeSmtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Host = "127.0.0.1";
        _acceptLoop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string Host { get; }

    public bool TryDequeueMessage([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FakeSmtpMessage? message)
        => _messages.TryDequeue(out message);

    public int CountMessages => _messages.Count;

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.ASCII, 512, leaveOpen: true) { NewLine = "\r\n" };
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            await writer.WriteLineAsync("220 fake-smtp HeliVMS test ready");
            await writer.FlushAsync();

            var msg = new FakeSmtpMessage();
            var inData = false;
            var dataLines = new List<string>();

            string? line;
            while ((line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))) is not null)
            {
                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        msg.Payload = string.Join("\r\n", dataLines);
                        _messages.Enqueue(msg);
                        await writer.WriteLineAsync("250 OK: queued");
                        await writer.FlushAsync();
                    }
                    else
                    {
                        dataLines.Add(line);
                    }

                    continue;
                }

                var cmd = line.Split(' ')[0].ToUpperInvariant();
                switch (cmd)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake-smtp");
                        await writer.WriteLineAsync("250 OK");
                        await writer.FlushAsync();
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 OK");
                        await writer.FlushAsync();
                        break;
                    case "MAIL":
                        msg.MailFrom = line.Substring(line.IndexOf(':', StringComparison.Ordinal) + 1);
                        await writer.WriteLineAsync("250 OK");
                        await writer.FlushAsync();
                        break;
                    case "RCPT":
                        msg.RcptTo.Add(line.Substring(line.IndexOf(':', StringComparison.Ordinal) + 1));
                        await writer.WriteLineAsync("250 OK");
                        await writer.FlushAsync();
                        break;
                    case "DATA":
                        inData = true;
                        dataLines.Clear();
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        await writer.FlushAsync();
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        await writer.FlushAsync();
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        await writer.FlushAsync();
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 測試端點：忽略
        }
        finally
        {
            client?.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>SMTP 交談結果。</summary>
internal sealed class FakeSmtpMessage
{
    public string MailFrom { get; set; } = string.Empty;

    public List<string> RcptTo { get; } = new();

    public string Payload { get; set; } = string.Empty;

    public string Subject =>
        Payload.Split("\r\n").FirstOrDefault(l => l.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            ?.Substring("Subject:".Length).Trim() ?? string.Empty;

    public string Body =>
        string.Join("\r\n", Payload.Split("\r\n")
            .SkipWhile(l => !string.IsNullOrWhiteSpace(l))
            .Skip(1));
}

/// <summary>迷你 MQTT broker：接受 CONNECT→回 CONNACK→收 PUBLISH（QoS0）並記錄 topic/payload。</summary>
internal sealed class FakeMqttBroker : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serveTask;
    private readonly TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeMqttBroker()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serveTask = Task.Run(ServeLoop);
    }

    public int Port { get; }
    public int ConnackReturnCode { get; set; }
    public string? ReceivedTopic { get; private set; }
    public string? ReceivedPayload { get; private set; }

    public Task WaitForPublishAsync(TimeSpan timeout) => _published.Task.WaitAsync(timeout);

    private async Task ServeLoop()
    {
        using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
        var stream = client.GetStream();

        // CONNECT（跳過 body）
        await ReadExactAsync(stream, 1, _cts.Token);
        var connectLen = await ReadVarintAsync(stream, _cts.Token);
        await ReadExactAsync(stream, connectLen, _cts.Token);

        // CONNACK：0x20 0x02 0x00 <rc>
        stream.Write(new byte[] { 0x20, 0x02, 0x00, (byte)ConnackReturnCode });

        // PUBLISH：0x30 <len> <topicLen:2> <topic> <payload>
        await ReadExactAsync(stream, 1, _cts.Token);
        var publishLen = await ReadVarintAsync(stream, _cts.Token);
        var body = new byte[publishLen];
        var done = 0;
        while (done < body.Length)
        {
            var n = await stream.ReadAsync(body.AsMemory(done), _cts.Token);
            if (n == 0)
            {
                throw new IOException("broker: peer closed during publish body");
            }
            done += n;
        }
        var topicLen = (body[0] << 8) | body[1];
        ReceivedTopic = Encoding.UTF8.GetString(body, 2, topicLen);
        ReceivedPayload = Encoding.UTF8.GetString(body, 2 + topicLen, publishLen - 2 - topicLen);
        _published.TrySetResult();
    }

    private async Task ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0)
            {
                throw new IOException("broker: peer closed");
            }
            offset += n;
        }
    }

    private static async Task<int> ReadVarintAsync(Stream s, CancellationToken ct)
    {
        var value = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            var one = new byte[1];
            var n = await s.ReadAsync(one, ct);
            if (n == 0)
            {
                throw new IOException("broker: peer closed");
            }

            value += (one[0] & 0x7F) * multiplier;
            if ((one[0] & 0x80) == 0)
            {
                return value;
            }

            multiplier *= 128;
        }

        throw new FormatException("bad remaining length");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}