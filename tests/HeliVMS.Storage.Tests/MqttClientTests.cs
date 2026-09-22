using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HeliVMS.Storage.Tests;

public class MqttClientTests : IDisposable
{
    private const string BaseTopic = "helivms/events";
    private static DateTime T0() => new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Connect_Publishes_And_Disconnects_RoundTrip()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        string topic = "helivms/events/ch5/motion";
        var payload = Encoding.UTF8.GetBytes("{\"seen\":1}");

        Assert.True(client.Connect("127.0.0.1", broker.Port, "helivms-1", 30).Ok);
        Assert.True(client.Publish(topic, payload).Ok);
        Assert.True(client.Disconnect().Ok);
        WaitUntil(() => broker.DisconnectSeen);

        Assert.Equal("helivms-1", broker.ClientId);
        Assert.True(broker.CleanSession);
        Assert.Equal(topic, broker.LastTopic);
        Assert.Equal(payload, broker.LastPayload);
        Assert.True(broker.DisconnectSeen);
    }

    [Fact]
    public void Connect_RejectedByBroker_ReturnsFail()
    {
        using var broker = new FakeMqttBroker(connackReturnCode: 5);
        broker.Start();
        using var client = new MqttClient();

        var result = client.Connect("127.0.0.1", broker.Port, "x", 30);

        Assert.False(result.Ok);
        Assert.Contains("CONNACK", result.Error);
    }

    [Fact]
    public void Publish_WithoutConnect_ReturnsFail()
    {
        using var client = new MqttClient();
        var result = client.Publish("a/b", new byte[] { 1 });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Connect_PortClosed_ReturnsFail()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var client = new MqttClient(TimeSpan.FromSeconds(2));
        Assert.False(client.Connect("127.0.0.1", port, "x", 30).Ok);
    }

    [Fact]
    public void Publish_LongTopic_MultiByteLength_RoundTrips()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var topic = "helivms/events/" + new string('c', 180) + "/motion";

        Assert.True(client.Connect("127.0.0.1", broker.Port, "t", 30).Ok);
        Assert.True(client.Publish(topic, Encoding.UTF8.GetBytes("{}")).Ok);
        WaitUntil(() => broker.LastTopic is not null);

        Assert.Equal(topic, broker.LastTopic);
    }

    [Fact]
    public void Publish_RemainingLength_128Boundary_EncodesCorrectly()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var topic = new string('t', 119);   // remaining = 2+119+7 = 128 → 0x80 0x01
        var payload = Encoding.UTF8.GetBytes("payload");

        Assert.True(client.Connect("127.0.0.1", broker.Port, "t", 30).Ok);
        Assert.True(client.Publish(topic, payload).Ok);
        WaitUntil(() => broker.LastTopic is not null);

        Assert.Equal(topic, broker.LastTopic);
        Assert.Equal((byte)0x80, broker.LastPublishedRemainingLength.First());
    }

    [Fact]
    public void Publish_EmptyTopic_ReturnsFail()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        Assert.True(client.Connect("127.0.0.1", broker.Port, "t", 30).Ok);

        var result = client.Publish("", Array.Empty<byte>());
        Assert.False(result.Ok);
        Assert.Null(broker.LastTopic);
    }

    [Fact]
    public void Connect_WithCredentials_SendsUserAndPassword()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();

        Assert.True(client.Connect("127.0.0.1", broker.Port, "helivms-x", 30, "alice", "s3cret").Ok);
        Assert.True(client.Disconnect().Ok);
        WaitUntil(() => broker.DisconnectSeen);

        Assert.Equal("alice", broker.Username);
        Assert.Equal("s3cret", broker.Password);
        Assert.True(broker.CleanSession);
    }

    [Fact]
    public void Connect_UsernameOnly_NoPasswordFlag()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();

        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30, "bob").Ok);

        Assert.Equal("bob", broker.Username);
        Assert.Null(broker.Password);
    }

    [Fact]
    public void Connect_PasswordWithoutUsername_SetsBothFlags()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();

        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30, password: "pw").Ok);

        Assert.Equal(string.Empty, broker.Username);
        Assert.Equal("pw", broker.Password);
    }

    [Fact]
    public void Connect_EmptyClientId_ReturnsFail()
    {
        using var client = new MqttClient();
        var result = client.Connect("127.0.0.1", 1, "", 30);

        Assert.False(result.Ok);
        Assert.Contains("clientId", result.Error);
    }

    [Fact]
    public void Router_TopicFormatNormalizesEventType()
    {
        Assert.Equal("helivms/events/ch3/motion_detected",
            MqttEventRouter.Topic("helivms/events", 3, "Motion Detected"));
        Assert.Equal("a/b/c/ch9/x", MqttEventRouter.Topic("a/b/c/", 9, "X"));
        Assert.Equal($"{MqttEventRouter.DefaultPrefix}/ch1/tamper",
            MqttEventRouter.Topic(null, 1, "tamper"));
    }

    [Fact]
    public void Router_PayloadJson_ContainsFields()
    {
        var json = MqttEventRouter.PayloadJson(2, "motion", T0());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("channel_id").GetInt32());
        Assert.Equal("motion", doc.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(SqliteStore.Iso(T0()), doc.RootElement.GetProperty("ts").GetString());
    }

    [Fact]
    public void Subscribe_RoundTrips_ValidatesWithBroker()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();

        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30).Ok);
        var result = client.Subscribe("helivms/events/#");

        Assert.True(result.Ok);
        WaitUntil(() => broker.LastSubscribeTopic is not null);
        Assert.Equal("helivms/events/#", broker.LastSubscribeTopic);
    }

    [Fact]
    public void Subscribe_WithoutConnect_ReturnsFail()
    {
        using var client = new MqttClient();
        var result = client.Subscribe("helivms/events/#");
        Assert.False(result.Ok);
    }

    [Fact]
    public void Subscribe_RejectedByBroker_ReturnsFail()
    {
        using var broker = new FakeMqttBroker(subackReturnCode: 0x80);
        broker.Start();
        using var client = new MqttClient();
        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30).Ok);

        var result = client.Subscribe("helivms/events/#");

        Assert.False(result.Ok);
    }

    [Fact]
    public void Subscribe_EmptyTopic_ReturnsFail()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30).Ok);

        var result = client.Subscribe("");

        Assert.False(result.Ok);
        Assert.Null(broker.LastSubscribeTopic);
    }

    [Fact]
    public void Presence_Online_RetainedStatusTopicAndPayload()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var reporter = new MqttPresenceReporter(client, "helivms/svc", "helivms-rec-1");
        var t0 = new DateTime(2026, 9, 22, 13, 0, 0, DateTimeKind.Utc);

        Assert.True(client.Connect("127.0.0.1", broker.Port, "helivms-rec-1", 30).Ok);
        var result = reporter.Online(t0);

        Assert.True(result.Ok);
        WaitUntil(() => broker.LastTopic is not null);
        Assert.Equal("helivms/svc/status", broker.LastTopic);
        Assert.True(broker.LastRetained);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(broker.LastPayload!));
        Assert.Equal("online", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("helivms-rec-1", doc.RootElement.GetProperty("client").GetString());
        Assert.Equal(SqliteStore.Iso(t0), doc.RootElement.GetProperty("at").GetString());
    }

    [Fact]
    public void Presence_Offline_FlipsStatus()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var reporter = new MqttPresenceReporter(client, null!, "rec");
        var t0 = new DateTime(2026, 9, 22, 13, 5, 0, DateTimeKind.Utc);

        Assert.True(client.Connect("127.0.0.1", broker.Port, "rec", 30).Ok);
        Assert.True(reporter.Offline(t0).Ok);
        WaitUntil(() => broker.LastTopic is not null);

        Assert.Equal($"{MqttEventRouter.DefaultPrefix}/status", broker.LastTopic);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(broker.LastPayload!));
        Assert.Equal("offline", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Router_StatusTopic_NormalizesPrefix()
    {
        Assert.Equal("helivms/events/status", MqttEventRouter.StatusTopic(null));
        Assert.Equal("a/b/status", MqttEventRouter.StatusTopic("a/b/"));
        Assert.Equal("x/y/status", MqttEventRouter.StatusTopic("x/y"));
    }

    [Fact]
    public void ReceiveMessage_GetsServerPushedPublish()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30).Ok);

        broker.Push("helivms/events/status", Encoding.UTF8.GetBytes("{\"status\":\"online\"}"));
        var result = client.ReceiveMessage(TimeSpan.FromSeconds(5));

        Assert.True(result.Ok);
        Assert.False(result.TimedOut);
        Assert.Equal("helivms/events/status", result.Message!.Topic);
        Assert.Equal("{\"status\":\"online\"}", Encoding.UTF8.GetString(result.Message.Payload));
    }

    [Fact]
    public void ReceiveMessage_Timeout_ReturnsTimedOut()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        Assert.True(client.Connect("127.0.0.1", broker.Port, "c", 30).Ok);

        var result = client.ReceiveMessage(TimeSpan.FromMilliseconds(300));

        Assert.False(result.Ok);
        Assert.True(result.TimedOut);
        Assert.Null(result.Message);
    }

    [Fact]
    public void ReceiveMessage_WithoutConnect_ReturnsFail()
    {
        using var client = new MqttClient();
        var result = client.ReceiveMessage(TimeSpan.FromSeconds(1));
        Assert.False(result.Ok);
        Assert.Contains("尚未連接", result.Error);
    }

    [Theory]
    [InlineData("helivms/events/#", "helivms/events/ch3/motion", true)]
    [InlineData("helivms/events/#", "helivms/events", true)]
    [InlineData("#", "anything/at/all", true)]
    [InlineData("helivms/+/status", "helivms/svc/status", true)]
    [InlineData("helivms/+/status", "helivms/svc/cam/status", false)]
    [InlineData("helivms/+", "helivms/a/b", false)]
    [InlineData("helivms/events", "helivms/events/motion", false)]
    [InlineData("other/topic", "helivms/events", false)]
    [InlineData("a/+/c", "a/b/c", true)]
    [InlineData("a/+/c", "a/b/d", false)]
    public void TopicFilter_Matches(string filter, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicFilter.Matches(filter, topic));
    }

    [Fact]
    public void Hub_Pump_RoutesPushedMessageToMatchingHandler()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var hub = new MqttMessageHub(client);
        var topic = default(string);
        var payload = default(string);
        hub.Register("helivms/events/#", m => { topic = m.Topic; payload = Encoding.UTF8.GetString(m.Payload); });
        Assert.True(client.Connect("127.0.0.1", broker.Port, "hub", 30).Ok);
        Assert.Equal(1, hub.SubscribeAll());
        WaitUntil(() => broker.LastSubscribeTopic is not null);

        broker.Push("helivms/events/ch3/motion", Encoding.UTF8.GetBytes("{\"x\":1}"));
        var result = hub.Pump(TimeSpan.FromSeconds(5));

        Assert.False(result.TimedOut);
        Assert.False(result.ConnectionLost);
        Assert.Equal(1, result.Handled);
        Assert.Equal("helivms/events/ch3/motion", topic);
        Assert.Equal("{\"x\":1}", payload);
    }

    [Fact]
    public void Hub_Pump_NonMatchingTopicSkipsHandler()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var hub = new MqttMessageHub(client);
        var called = false;
        hub.Register("helivms/+/status", _ => called = true);
        Assert.True(client.Connect("127.0.0.1", broker.Port, "hub", 30).Ok);
        Assert.Equal(1, hub.SubscribeAll());
        WaitUntil(() => broker.LastSubscribeTopic is not null);

        broker.Push("helivms/svc/events", Encoding.UTF8.GetBytes("{}"));
        var result = hub.Pump(TimeSpan.FromSeconds(5));

        Assert.False(result.ConnectionLost);
        Assert.Equal(0, result.Handled);
        Assert.False(called);
    }

    [Fact]
    public void Hub_SubscribeAll_SendsEveryFilter()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var hub = new MqttMessageHub(client);
        hub.Register("helivms/events/#", _ => { });
        hub.Register("a/+/c", _ => { });
        Assert.True(client.Connect("127.0.0.1", broker.Port, "hub", 30).Ok);

        Assert.Equal(2, hub.SubscribeAll());
        WaitUntil(() => broker.SubscribeTopics.Count == 2);
        Assert.Contains("helivms/events/#", broker.SubscribeTopics);
        Assert.Contains("a/+/c", broker.SubscribeTopics);
    }

    [Fact]
    public void Hub_Pump_MultipleHandlersForSameFilter_AllInvoked()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var hub = new MqttMessageHub(client);
        var count = 0;
        hub.Register("t/#", _ => count++);
        hub.Register("t/#", _ => count++);
        Assert.True(client.Connect("127.0.0.1", broker.Port, "hub", 30).Ok);
        Assert.Equal(1, hub.SubscribeAll());
        WaitUntil(() => broker.LastSubscribeTopic is not null);

        broker.Push("t/x", new byte[] { 1 });
        var result = hub.Pump(TimeSpan.FromSeconds(5));

        Assert.Equal(2, result.Handled);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Hub_Pump_WithoutConnection_ReportsConnectionLost()
    {
        using var client = new MqttClient();
        var hub = new MqttMessageHub(client);
        hub.Register("t/#", _ => { });

        var result = hub.Pump(TimeSpan.FromSeconds(1));

        Assert.True(result.ConnectionLost);
        Assert.Equal(0, result.Handled);
    }

    [Fact]
    public async Task Hub_Run_LoopsUntilCancelled_DeliveringMessages()
    {
        using var broker = new FakeMqttBroker();
        broker.Start();
        using var client = new MqttClient();
        var hub = new MqttMessageHub(client);
        var received = 0;
        hub.Register("events/#", _ => received++);
        Assert.True(client.Connect("127.0.0.1", broker.Port, "hub", 30).Ok);
        hub.SubscribeAll();
        WaitUntil(() => broker.LastSubscribeTopic is not null);

        using var cts = new CancellationTokenSource();
        var run = hub.Run(TimeSpan.FromMilliseconds(50), cts.Token);
        broker.Push("events/a", new byte[] { 1 });
        broker.Push("events/b", new byte[] { 2 });
        WaitUntil(() => received == 2);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeMqttBroker : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _connackReturnCode;
    private readonly int _subackReturnCode;
    private Task? _task;
    private NetworkStream? _clientStream;

    public FakeMqttBroker(int connackReturnCode = 0, int subackReturnCode = 0)
    {
        _connackReturnCode = connackReturnCode;
        _subackReturnCode = subackReturnCode;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }
    public string? ClientId { get; private set; }
    public string? Username { get; private set; }
    public string? Password { get; private set; }
    public bool CleanSession { get; private set; }
    public string? LastTopic { get; private set; }
    public byte[]? LastPayload { get; private set; }
    public bool LastRetained { get; private set; }
    public string? LastSubscribeTopic { get; private set; }
    public List<string> SubscribeTopics { get; } = new();
    public List<byte> LastPublishedRemainingLength { get; } = new();
    public bool DisconnectSeen { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>模擬 broker 主動推送 PUBLISH（QoS0）給已連線 client。</summary>
    public void Push(string topic, byte[] payload)
    {
        var tb = Encoding.UTF8.GetBytes(topic);
        var remaining = 2 + tb.Length + payload.Length;
        var encoded = new List<byte>();
        var length = remaining;
        while (true)
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0)
            {
                digit |= 0x80;
            }

            encoded.Add(digit);
            if (length == 0)
            {
                break;
            }
        }

        var packet = new List<byte> { 0x30 };
        packet.AddRange(encoded);
        packet.AddRange([(byte)(tb.Length >> 8), (byte)(tb.Length & 0xFF)]);
        packet.AddRange(tb);
        packet.AddRange(payload);
        _clientStream!.Write(packet.ToArray(), 0, packet.Count);
        _clientStream.Flush();
    }

    public void Start() => _task = Task.Run(async () =>
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var stream = client.GetStream();
            _clientStream = stream;
            while (true)
            {
                var packet = await ReadPacketAsync(stream).ConfigureAwait(false);
                if (packet is null)
                {
                    return;
                }

                var header = packet.Value.Header;
                var body = packet.Value.Body;
                var lengthBytes = packet.Value.RemainingLengthBytes;
                switch (header)
                {
                    case 0x10:   // CONNECT
                    case 0x12:
                    {
                        var flags = body[7];
                        var idLen = (body[10] << 8) | body[11];
                        ClientId = Encoding.UTF8.GetString(body, 12, idLen);
                        var offset = 12 + idLen;
                        if ((flags & 0x80) != 0)
                        {
                            var userLen = (body[offset] << 8) | body[offset + 1];
                            Username = Encoding.UTF8.GetString(body, offset + 2, userLen);
                            offset += 2 + userLen;
                        }

                        if ((flags & 0x40) != 0)
                        {
                            var passLen = (body[offset] << 8) | body[offset + 1];
                            Password = Encoding.UTF8.GetString(body, offset + 2, passLen);
                        }

                        CleanSession = (flags & 0x02) != 0;
                        stream.Write(new byte[] { 0x20, 0x02, 0x00, (byte)_connackReturnCode });
                        break;
                    }

                    case 0x30:      // PUBLISH QoS0，即時（非保留）
                    case 0x31:      // PUBLISH 保留
                        var topicLen = (body[0] << 8) | body[1];
                        LastTopic = Encoding.UTF8.GetString(body, 2, topicLen);
                        LastPayload = body[(2 + topicLen)..];
                        LastRetained = (header & 0x01) != 0;
                        LastPublishedRemainingLength.AddRange(lengthBytes);
                        break;

                    case 0x82:      // SUBSCRIBE
                        var subTopicLen = (body[2] << 8) | body[3];
                        LastSubscribeTopic = Encoding.UTF8.GetString(body, 4, subTopicLen);
                        SubscribeTopics.Add(LastSubscribeTopic);
                        stream.Write(new byte[] { 0x90, 0x03, body[0], body[1], (byte)_subackReturnCode });
                        break;

                    case 0xE0:   // DISCONNECT
                        DisconnectSeen = true;
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
        }
    });

    public void Dispose()
    {
        _listener.Stop();
        try { _task?.GetAwaiter().GetResult(); } catch { }
    }

    private static async Task<(byte Header, byte[] Body, List<byte> RemainingLengthBytes)?> ReadPacketAsync(Stream stream)
    {
        var first = await ReadByteAsync(stream).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        var lengthBytes = new List<byte>();
        var multiplier = 1;
        var length = 0;
        byte digit;
        do
        {
            var d = await ReadByteAsync(stream).ConfigureAwait(false);
            if (d is null)
            {
                return null;
            }

            digit = d.Value;
            lengthBytes.Add(digit);
            length += (digit & 0x7F) * multiplier;
            multiplier *= 128;
        }
        while ((digit & 0x80) != 0);

        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset)).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            offset += read;
        }

        return (first.Value, body, lengthBytes);
    }

    private static async Task<byte?> ReadByteAsync(Stream stream)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
        return read == 0 ? null : buffer[0];
    }
}