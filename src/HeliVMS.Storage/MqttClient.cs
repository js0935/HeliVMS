using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Storage;

/// <summary>MQTT 發送結果（M103，§14.7 #15）。</summary>
public sealed record MqttPublishResult(bool Ok, string? Error)
{
    public static MqttPublishResult Success() => new(true, null);
    public static MqttPublishResult Fail(string error) => new(false, error);
}

/// <summary>MQTT 發送介面（M103）：測試注入 fake；正式為 <see cref="MqttClient"/>。</summary>
public interface IMqttPublisher
{
    MqttPublishResult Connect(string host, int port, string clientId, int keepAliveSeconds);
    MqttPublishResult Publish(string topic, byte[] payload);
    MqttPublishResult Disconnect();
}

/// <summary>
/// 最小 MQTT 3.1.1 客戶端（M103，§14.7 #15，純 BCL）：QoS0 發送（CONNECT→CONNACK 檢查→
/// PUBLISH→DISCONNECT）。與 M85 LDAP 同風格，以 TcpClient/NetworkStream 同步走 wire；
/// 不訂閱、不正規 MQTT broker 也相容（HA/Frigate）。剩餘長度多字節編碼符合 §2.2.3。
/// </summary>
public sealed class MqttClient : IMqttPublisher, IDisposable
{
    private readonly TimeSpan _timeout;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public MqttClient(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public MqttPublishResult Connect(string host, int port, string clientId, int keepAliveSeconds)
    {
        try
        {
            var tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = (int)_timeout.TotalMilliseconds;
            tcp.SendTimeout = (int)_timeout.TotalMilliseconds;
            tcp.Connect(host, port);
            var stream = tcp.GetStream();

            var connectPacket = BuildConnect(clientId, keepAliveSeconds);
            Write(stream, connectPacket);
            var connack = ReadFully(stream, 4);
            if (connack.Length < 4 || connack[0] != 0x20)
            {
                tcp.Dispose();
                return MqttPublishResult.Fail("broker 未回 CONNACK");
            }

            var rc = connack[3];
            if (rc != 0)
            {
                tcp.Dispose();
                return MqttPublishResult.Fail($"CONNACK 拒絕（return code={rc}）");
            }

            _tcp = tcp;
            _stream = stream;
            return MqttPublishResult.Success();
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or ArgumentException)
        {
            return MqttPublishResult.Fail($"連線失敗（{ex.Message}）");
        }
    }

    public MqttPublishResult Publish(string topic, byte[] payload)
    {
        if (_stream is null)
        {
            return MqttPublishResult.Fail("尚未連線");
        }

        if (string.IsNullOrEmpty(topic))
        {
            return MqttPublishResult.Fail("topic 不得為空白");
        }

        try
        {
            var packet = BuildPublish(topic, payload);
            Write(_stream, packet);
            return MqttPublishResult.Success();
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return MqttPublishResult.Fail($"發送失敗（{ex.Message}）");
        }
    }

    public MqttPublishResult Disconnect()
    {
        if (_stream is null)
        {
            return MqttPublishResult.Success();
        }

        try
        {
            Write(_stream, [0xE0, 0x00]);
        }
        catch (Exception)
        {
            // broker 已斷亦可
        }

        _tcp?.Dispose();
        _tcp = null;
        _stream = null;
        return MqttPublishResult.Success();
    }

    public void Dispose() => Disconnect();

    private static byte[] BuildConnect(string clientId, int keepAliveSeconds)
    {
        var clientBytes = Encoding.UTF8.GetBytes(clientId ?? string.Empty);
        var remaining = 10 + 2 + clientBytes.Length;   // 變動表頭 10 位元組＋client id
        var header = new List<byte> { 0x10 };
        header.AddRange(EncodeRemainingLength(remaining));
        header.AddRange([0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04, 0x02]);
        header.Add((byte)(keepAliveSeconds >> 8));
        header.Add((byte)(keepAliveSeconds & 0xFF));
        header.AddRange([(byte)(clientBytes.Length >> 8), (byte)(clientBytes.Length & 0xFF)]);
        header.AddRange(clientBytes);
        return header.ToArray();
    }

    private static byte[] BuildPublish(string topic, byte[] payload)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var remaining = 2 + topicBytes.Length + payload.Length;
        var packet = new List<byte> { 0x30 };
        packet.AddRange(EncodeRemainingLength(remaining));
        packet.AddRange([(byte)(topicBytes.Length >> 8), (byte)(topicBytes.Length & 0xFF)]);
        packet.AddRange(topicBytes);
        packet.AddRange(payload);
        return packet.ToArray();
    }

    private static byte[] EncodeRemainingLength(int length)
    {
        var encoded = new List<byte>();
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0)
            {
                digit |= 0x80;
            }

            encoded.Add(digit);
        }
        while (length > 0);
        return encoded.ToArray();
    }

    private static void Write(NetworkStream stream, byte[] data)
    {
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    private static byte[] ReadFully(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return buffer[..offset];
    }
}