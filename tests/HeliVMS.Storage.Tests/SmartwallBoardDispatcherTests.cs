using System.Linq;
using System.Text.Json;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>智慧牆看板 MQTT 派送掛載（M156，§14.7 #14）。</summary>
public sealed class SmartwallBoardDispatcherTests
{
    private sealed class FakePublisher : IMqttPublisher
    {
        public string? Topic { get; private set; }
        public byte[]? Payload { get; private set; }
        public int PublishCalls { get; private set; }
        public MqttPublishResult ConnectResult { get; set; } = MqttPublishResult.Success();

        public MqttPublishResult Connect(string host, int port, string clientId, int keepAliveSeconds, string? user, string? password)
            => ConnectResult;

        public MqttPublishResult Publish(string topic, byte[] payload)
        {
            PublishCalls++;
            Topic = topic;
            Payload = payload;
            return MqttPublishResult.Success();
        }

        public MqttPublishResult PublishRetained(string topic, byte[] payload) => MqttPublishResult.Success();

        public MqttPublishResult Subscribe(string topic) => MqttPublishResult.Success();

        public MqttPublishResult Disconnect() => MqttPublishResult.Success();

        public void Dispose()
        {
        }
    }

    [Fact]
    public void TopicFor_PerChannel()
    {
        Assert.Equal("helivms/smartwall/alerts/7", SmartwallBoardDispatcher.TopicFor("helivms/smartwall", 7));
        Assert.Equal("helivms/smartwall/alerts/7", SmartwallBoardDispatcher.TopicFor("helivms/smartwall/", 7));
        Assert.Equal("helivms/smartwall/alerts/7", SmartwallBoardDispatcher.TopicFor("", 7));
    }

    [Fact]
    public void Payload_ContainsContractFields()
    {
        var bytes = SmartwallBoardDispatcher.Payload(new SmartwallBoardEvent(9, "tamper", "high", new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc), 3));
        using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        var root = doc.RootElement;

        Assert.Equal(9, root.GetProperty("channel_id").GetInt64());
        Assert.Equal("tamper", root.GetProperty("event_type").GetString());
        Assert.Equal("high", root.GetProperty("priority").GetString());
        Assert.Equal(3, root.GetProperty("rule_order").GetInt32());
        Assert.StartsWith("2026-01-01T00:00:00", root.GetProperty("occurred_at_utc").GetString());
    }

    [Fact]
    public void Dispatch_PublishesToFakePublisher()
    {
        var fake = new FakePublisher();
        var e = new SmartwallBoardEvent(11, "motion", "critical", new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc), 1);

        var result = SmartwallBoardDispatcher.Dispatch(fake, e, "localhost", 1883, "helivms/smartwall", "u", "p", e.OccurredAtUtc);

        Assert.True(result.Ok);
        Assert.Equal("helivms/smartwall/alerts/11", fake.Topic);
        Assert.NotNull(fake.Payload);
        Assert.Equal(1, fake.PublishCalls);
    }

    [Fact]
    public void Dispatch_MissingHost_Fails()
    {
        var fake = new FakePublisher();
        var e = new SmartwallBoardEvent(1, "motion", "low", new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc), 1);

        var result = SmartwallBoardDispatcher.Dispatch(fake, e, "", 1883, "helivms/smartwall", null, null, e.OccurredAtUtc);

        Assert.False(result.Ok);
        Assert.Equal(0, fake.PublishCalls);
    }

    [Fact]
    public void Dispatch_ConnectFailure_NotPublished()
    {
        var fake = new FakePublisher { ConnectResult = MqttPublishResult.Fail("無法連線") };
        var e = new SmartwallBoardEvent(2, "motion", "low", new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc), 1);

        var result = SmartwallBoardDispatcher.Dispatch(fake, e, "localhost", 1883, "helivms/smartwall", null, null, e.OccurredAtUtc);

        Assert.False(result.Ok);
        Assert.Equal(0, fake.PublishCalls);
    }
}