using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>MQTT 3.1.1 裸協議發送器（不依賴第三方 NuGet，僅 TCP 傳送 CONNECT/PUBLISH）。</summary>
public sealed class MqttNotifier
{
    public async Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord e)
    {
        var host = cfg.MqttHost;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(cfg.MqttTopic))
        {
            return false;
        }

        var port = cfg.MqttPort > 0 ? cfg.MqttPort : 1883;
        var clientId = $"helivms-{Guid.NewGuid():N}".Substring(0, 20);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();

            // === CONNECT ===
            var connectFlags = 0x02; // clean session
            if (!string.IsNullOrWhiteSpace(cfg.MqttUser))
                connectFlags |= 0x80;
            if (!string.IsNullOrWhiteSpace(cfg.MqttPassword))
                connectFlags |= 0x40;

            var variable = new List<byte>();
            // protocol name
            variable.Add(0x00); variable.Add(0x04);
            variable.AddRange(Encoding.ASCII.GetBytes("MQTT"));
            variable.Add(0x04); // level 4
            variable.Add((byte)connectFlags);
            variable.AddRange(new[] { (byte)0x00, (byte)0x00 }); // keepalive 0

            var payload = new List<byte>();
            var clientIdBytes = Encoding.UTF8.GetBytes(clientId);
            payload.AddRange(new[] { (byte)(clientIdBytes.Length >> 8), (byte)(clientIdBytes.Length & 0xFF) });
            payload.AddRange(clientIdBytes);
            if (!string.IsNullOrWhiteSpace(cfg.MqttUser))
            {
                var ub = Encoding.UTF8.GetBytes(cfg.MqttUser);
                payload.AddRange(new[] { (byte)(ub.Length >> 8), (byte)(ub.Length & 0xFF) });
                payload.AddRange(ub);
            }
            if (!string.IsNullOrWhiteSpace(cfg.MqttPassword))
            {
                var pb = Encoding.UTF8.GetBytes(cfg.MqttPassword);
                payload.AddRange(new[] { (byte)(pb.Length >> 8), (byte)(pb.Length & 0xFF) });
                payload.AddRange(pb);
            }

            var totalLen = variable.Count + payload.Count;
            var connect = new List<byte>();
            connect.Add(0x10);
            connect.AddRange(EncodeRemainingLength(totalLen));
            connect.AddRange(variable);
            connect.AddRange(payload);
            await stream.WriteAsync(connect.ToArray());

            // === CONNACK ===
            var ack = new byte[4];
            await ReadExactly(stream, ack, 4);
            var rc = ack[3]; // 0 = 成功
            if (rc != 0)
            {
                return false;
            }

            // === PUBLISH QoS0 ===
            var payloadJson = JsonSerializer.Serialize(new
            {
                channel_id = e.ChannelId,
                event_type = e.EventType,
                start_utc = e.StartUtc.ToString("o"),
                detail = e.Detail,
            });
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            var topicBytes = Encoding.UTF8.GetBytes(cfg.MqttTopic!);
            var pubVarLen = 2 + topicBytes.Length + payloadBytes.Length;

            var pub = new List<byte>();
            pub.Add(0x30);
            pub.AddRange(EncodeRemainingLength(pubVarLen));
            pub.AddRange(new[] { (byte)(topicBytes.Length >> 8), (byte)(topicBytes.Length & 0xFF) });
            pub.AddRange(topicBytes);
            pub.AddRange(payloadBytes);
            await stream.WriteAsync(pub.ToArray());

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<byte> EncodeRemainingLength(int length)
    {
        var result = new List<byte>();
        var value = length;
        do
        {
            var b = (byte)(value % 128);
            value /= 128;
            if (value > 0)
                b |= 0x80;
            result.Add(b);
        }
        while (value > 0);
        return result;
    }

    private static async Task ReadExactly(NetworkStream s, byte[] buf, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await s.ReadAsync(buf.AsMemory(offset, count - offset));
            if (read == 0)
                throw new IOException("MQTT stream closed unexpectedly");
            offset += read;
        }
    }
}