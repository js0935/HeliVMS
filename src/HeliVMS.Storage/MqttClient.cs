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
    MqttPublishResult Connect(string host, int port, string clientId, int keepAliveSeconds, string? username = null, string? password = null);
    MqttPublishResult Publish(string topic, byte[] payload);
    MqttPublishResult PublishRetained(string topic, byte[] payload);
    MqttPublishResult Disconnect();
}

/// <summary>
/// 最小 MQTT 3.1.1 客戶端（M103，§14.7 #15，純 BCL）：QoS0 發送（CONNECT→CONNACK 檢查→
/// PUBLISH→DISCONNECT）。與 M85 LDAP 同風格，以 TcpClient/NetworkStream 同步走 wire；
/// 不訂閱、不正規 MQTT broker 也相容（HA/Frigate）。剩餘長度多字節編碼符合 §2.2.3。
/// M108 增使用者/密碼驗證（CONNECT flags 0x80/0x40＋payload，RFC 3.1）。
/// M111 增訂閱控制面（SUBSCRIBE→SUBACK）與保留發布（retain flag，狀態保留）。
/// </summary>
public sealed class MqttClient : IMqttPublisher, IDisposable
{
    private readonly TimeSpan _timeout;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public MqttClient(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public MqttPublishResult Connect(
        string host,
        int port,
        string clientId,
        int keepAliveSeconds,
        string? username = null,
        string? password = null)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return MqttPublishResult.Fail("clientId 不得為空白");
        }

        try
        {
            var tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = (int)_timeout.TotalMilliseconds;
            tcp.SendTimeout = (int)_timeout.TotalMilliseconds;
            tcp.Connect(host, port);
            var stream = tcp.GetStream();

            var connectPacket = BuildConnect(clientId, keepAliveSeconds, username, password);
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
            var packet = BuildPublish(topic, payload, retained: false);
            Write(_stream, packet);
            return MqttPublishResult.Success();
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return MqttPublishResult.Fail($"發送失敗（{ex.Message}）");
        }
    }

    public MqttPublishResult PublishRetained(string topic, byte[] payload)
    {
        if (_stream is null)
        {
            return MqttPublishResult.Fail("尚未連接");
        }

        if (string.IsNullOrEmpty(topic))
        {
            return MqttPublishResult.Fail("topic 不得為空白");
        }

        try
        {
            var packet = BuildPublish(topic, payload, retained: true);
            Write(_stream, packet);
            return MqttPublishResult.Success();
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return MqttPublishResult.Fail($"發送失敗（{ex.Message}）");
        }
    }

    /// <summary>
    /// 訂閱（M111，§14.7 #15）：控制面 SUBSCRIBE（QoS0）→ 以 SUBACK 驗證 broker 接受訂閱
    /// （return code 0x80→拒絕）。訊息回傳（PUBLISH→client）屬訂閱數據面，L0 不在本同步客戶端
    /// 常駐讀取；保留訊息靠 subscriber 端訂閱 topic 由 broker 投遞。
    /// </summary>
    public MqttPublishResult Subscribe(string topic)
    {
        if (_stream is null)
        {
            return MqttPublishResult.Fail("尚未連接");
        }

        if (string.IsNullOrEmpty(topic))
        {
            return MqttPublishResult.Fail("topic 不得為空白");
        }

        try
        {
            var packet = BuildSubscribe(topic);
            Write(_stream, packet);
            var header = ReadFully(_stream, 2);
            if (header.Length < 2 || header[0] != 0x90)
            {
                return MqttPublishResult.Fail("broker 未回 SUBACK");
            }

            var body = ReadFully(_stream, header[1]);
            if (body.Length < header[1] || body[^1] == 0x80)
            {
                return MqttPublishResult.Fail("訂閱遭拒絕");
            }

            return MqttPublishResult.Success();
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return MqttPublishResult.Fail($"訂閱失敗（{ex.Message}）");
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

    private static byte[] BuildConnect(string clientId, int keepAliveSeconds, string? username, string? password)
    {
        var clientBytes = Encoding.UTF8.GetBytes(clientId);
        var userBytes = username is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(username);
        var passBytes = password is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(password);
        var hasUser = password is not null || userBytes.Length > 0;
        var hasPass = password is not null;
        if (hasPass && !hasUser)
        {
            // MQTT 3.1.1：password flag 需配合 username flag
            hasUser = true;
        }

        var remaining = 10 + 2 + clientBytes.Length;
        if (hasUser) remaining += 2 + userBytes.Length;
        if (hasPass) remaining += 2 + passBytes.Length;

        var flags = 0x02; // clean session
        if (hasUser) flags |= 0x80;
        if (hasPass) flags |= 0x40;

        var header = new List<byte> { 0x10 };
        header.AddRange(EncodeRemainingLength(remaining));
        header.AddRange([0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04, (byte)flags]);
        header.Add((byte)(keepAliveSeconds >> 8));
        header.Add((byte)(keepAliveSeconds & 0xFF));
        header.AddRange([(byte)(clientBytes.Length >> 8), (byte)(clientBytes.Length & 0xFF)]);
        header.AddRange(clientBytes);
        if (hasUser)
        {
            header.AddRange([(byte)(userBytes.Length >> 8), (byte)(userBytes.Length & 0xFF)]);
            header.AddRange(userBytes);
        }

        if (hasPass)
        {
            header.AddRange([(byte)(passBytes.Length >> 8), (byte)(passBytes.Length & 0xFF)]);
            header.AddRange(passBytes);
        }

        return header.ToArray();
    }

    private static byte[] BuildPublish(string topic, byte[] payload, bool retained)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var remaining = 2 + topicBytes.Length + payload.Length;
        var packet = new List<byte> { (byte)(retained ? 0x31 : 0x30) };
        packet.AddRange(EncodeRemainingLength(remaining));
        packet.AddRange([(byte)(topicBytes.Length >> 8), (byte)(topicBytes.Length & 0xFF)]);
        packet.AddRange(topicBytes);
        packet.AddRange(payload);
        return packet.ToArray();
    }

    private static byte[] BuildSubscribe(string topic)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var remaining = 2 + 2 + topicBytes.Length + 1;
        var packet = new List<byte> { 0x82 };
        packet.AddRange(EncodeRemainingLength(remaining));
        packet.AddRange([0x00, 0x01]);   // packet id (與 DISCONNECT 無關，控制面單一)
        packet.AddRange([(byte)(topicBytes.Length >> 8), (byte)(topicBytes.Length & 0xFF)]);
        packet.AddRange(topicBytes);
        packet.Add(0x00);                // requested QoS 0
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