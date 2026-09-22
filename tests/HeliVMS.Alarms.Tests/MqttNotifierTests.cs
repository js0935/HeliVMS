using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

public sealed class MqttNotifierTests
{
    private static NotificationSettings Cfg(
        string? host = "broker.local",
        string? topic = "helivms/alerts",
        string? user = null,
        string? password = null,
        int port = 1884)
        => new(
            Enabled: true,
            WebhookUrl: null,
            SmtpEnabled: false,
            SmtpHost: null,
            SmtpPort: 25,
            SmtpFrom: null,
            SmtpTo: [],
            SmtpUser: null,
            SmtpPassword: null,
            QuietStart: null,
            QuietEnd: null,
            MqttEnabled: true,
            MqttHost: host,
            MqttPort: port,
            MqttTopic: topic,
            MqttUser: user,
            MqttPassword: password);

    [Fact]
    public async Task Send_PublishesConfiguredTopicAndPayload()
    {
        var publisher = new FakePublisher();
        var notifier = new MqttNotifier(publisher);
        var cfg = Cfg(user: "alice", password: "s3cret");
        var e = NewEvent();

        var ok = await notifier.SendAsync(cfg, e);

        Assert.True(ok);
        Assert.Equal(1, publisher.ConnectCount);
        Assert.Equal("broker.local", publisher.LastHost);
        Assert.Equal(1884, publisher.LastPort);
        Assert.Equal("alice", publisher.LastUsername);
        Assert.Equal("s3cret", publisher.LastPassword);
        Assert.Equal("helivms/alerts", publisher.LastTopic);
        Assert.True(publisher.DisconnectCalled);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(publisher.LastPayload!));
        Assert.Equal(3, doc.RootElement.GetProperty("channel_id").GetInt32());
        Assert.Equal("motion", doc.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(e.StartUtc.ToString("o"), doc.RootElement.GetProperty("start_utc").GetString());
    }

    [Fact]
    public async Task Send_BlankHostOrTopic_ReturnsFalseWithoutConnect()
    {
        var publisher = new FakePublisher();
        var notifier = new MqttNotifier(publisher);

        var noHost = await notifier.SendAsync(Cfg(host: null), NewEvent());
        var noTopic = await notifier.SendAsync(Cfg(topic: null), NewEvent());

        Assert.False(noHost);
        Assert.False(noTopic);
        Assert.Equal(0, publisher.ConnectCount);
    }

    [Fact]
    public async Task Send_ConnectFailure_ReturnsFalse()
    {
        var publisher = new FakePublisher(connectOk: false);
        var notifier = new MqttNotifier(publisher);

        var ok = await notifier.SendAsync(Cfg(), NewEvent());

        Assert.False(ok);
        Assert.Equal(0, publisher.PublishCount);
    }

    [Fact]
    public async Task Send_PublishFailure_ReturnsFalseAndDisconnects()
    {
        var publisher = new FakePublisher(publishOk: false);
        var notifier = new MqttNotifier(publisher);

        var ok = await notifier.SendAsync(Cfg(), NewEvent());

        Assert.False(ok);
        Assert.Equal(1, publisher.ConnectCount);
        Assert.True(publisher.DisconnectCalled);
    }

    [Fact]
    public async Task Send_DefaultPort1883_WhenUnset()
    {
        var publisher = new FakePublisher();
        var notifier = new MqttNotifier(publisher);

        await notifier.SendAsync(Cfg(port: 0), NewEvent());

        Assert.Equal(1883, publisher.LastPort);
    }

    private static AlarmEventRecord NewEvent() => new()
    {
        Id = 101,
        ChannelId = 3,
        EventType = "motion",
        StartUtc = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
        Detail = "ratio=0.42",
        Status = "pending",
    };

    private sealed class FakePublisher : IMqttPublisher
    {
        private readonly bool _connectOk;
        private readonly bool _publishOk;

        public FakePublisher(bool connectOk = true, bool publishOk = true)
        {
            _connectOk = connectOk;
            _publishOk = publishOk;
        }

        public int ConnectCount { get; private set; }
        public int PublishCount { get; private set; }
        public bool DisconnectCalled { get; private set; }
        public string? LastHost { get; private set; }
        public int LastPort { get; private set; }
        public string? LastUsername { get; private set; }
        public string? LastPassword { get; private set; }
        public string? LastTopic { get; private set; }
        public byte[]? LastPayload { get; private set; }

        public MqttPublishResult Connect(string host, int port, string clientId, int keepAliveSeconds, string? username = null, string? password = null)
        {
            ConnectCount++;
            LastHost = host;
            LastPort = port;
            LastUsername = username;
            LastPassword = password;
            return _connectOk ? MqttPublishResult.Success() : MqttPublishResult.Fail("connect refused");
        }

        public MqttPublishResult Publish(string topic, byte[] payload)
        {
            PublishCount++;
            LastTopic = topic;
            LastPayload = payload;
            return _publishOk ? MqttPublishResult.Success() : MqttPublishResult.Fail("publish refused");
        }

        public MqttPublishResult Disconnect()
        {
            DisconnectCalled = true;
            return MqttPublishResult.Success();
        }
    }
}