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

    public void Dispose()
    {
    }
}

internal sealed class FakeMqttBroker : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _connackReturnCode;
    private Task? _task;

    public FakeMqttBroker(int connackReturnCode = 0)
    {
        _connackReturnCode = connackReturnCode;
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
    public List<byte> LastPublishedRemainingLength { get; } = new();
    public bool DisconnectSeen { get; private set; }
    public string? LastError { get; private set; }

    public void Start() => _task = Task.Run(async () =>
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var stream = client.GetStream();
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

                    case 0x30:   // PUBLISH QoS0
                        var topicLen = (body[0] << 8) | body[1];
                        LastTopic = Encoding.UTF8.GetString(body, 2, topicLen);
                        LastPayload = body[(2 + topicLen)..];
                        LastPublishedRemainingLength.AddRange(lengthBytes);
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