using System.Net;
using System.Net.Sockets;
using System.Text;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// SNMPv2c trap 發送器：以裸 UDP 手編 BER 編碼之 SNMP message
/// （version=1(v2c)＋community＋SNMPv2-Trap PDU），完全不依賴第三方 NuGet。
/// PDU 內容：sysUpTime.0（TimeTicks）、snmpTrapOID.0（事件 OID）、
/// 事件 varbind（channel_id Int32、event_type String、detail String，企業 OID 1.3.6.1.4.1.99999）。
/// </summary>
public sealed class SnmpTrapSender
{
    private const string EnterpriseBase = "1.3.6.1.4.1.99999";

    /// <summary>送出 trap 至 cfg.SnmpHost:port。UDP 送出即視為成功（無確認面）。</summary>
    public Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord record)
    {
        var host = cfg.SnmpHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return Task.FromResult(false);
        }

        var port = cfg.SnmpPort > 0 ? cfg.SnmpPort : 162;
        var payload = BuildTrap(cfg.SnmpCommunity ?? "public", record);

        try
        {
            using var udp = new UdpClient();
            udp.Send(payload, payload.Length, host, port);
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>建構完整 SNMPv2c trap 訊息位元組（測試可直接驗 wire format）。</summary>
    internal static byte[] BuildTrap(string community, AlarmEventRecord record)
    {
        var trapOid = $"{EnterpriseBase}.0.1";

        var varbinds = new List<byte[]>
        {
            // sysUpTime.0 = TimeTicks(0)
            EncodeVarbind(ParseOid("1.3.6.1.2.1.1.3.0"), Tlv(0x43, new byte[] { 0x00, 0x00, 0x00, 0x00 })),
            // snmpTrapOID.0 = trapOid（ObjectIdentifier 值）
            EncodeVarbind(ParseOid("1.3.6.1.6.3.1.1.4.1.0"), EncodeOid(ParseOid(trapOid))),
            // 事件資料 varbind
            EncodeVarbind(ParseOid($"{EnterpriseBase}.2.1"), EncodeInteger(record.ChannelId)),
            EncodeVarbind(ParseOid($"{EnterpriseBase}.2.2"), EncodeOctetString(record.EventType)),
            EncodeVarbind(ParseOid($"{EnterpriseBase}.2.3"), EncodeOctetString(record.Detail ?? string.Empty)),
        };

        var varbindList = Tlv(0x30, Concat(varbinds));

        var pdu = Tlv(0xA7, Concat(new[]
        {
            EncodeInteger(1),      // request-id
            EncodeInteger(0),      // error-status
            EncodeInteger(0),      // error-index
            varbindList,
        }));

        var message = Tlv(0x30, Concat(new[]
        {
            EncodeInteger(1),      // SNMPv2c = version 1
            EncodeOctetString(community),
            pdu,
        }));

        return message;
    }

    private static byte[] EncodeVarbind(int[] oid, byte[] value)
        => Tlv(0x30, Concat(new[] { EncodeOid(oid), value }));

    private static byte[] EncodeInteger(int value)
    {
        var tmp = new List<byte>();
        unchecked
        {
            var v = (uint)value;
            do
            {
                tmp.Insert(0, (byte)(v & 0xFF));
                v >>= 8;
            }
            while (v != 0);
        }

        // 正數最高位設 1 需加 0x00；負數則確保前導位為 1
        if ((tmp[0] & 0x80) != 0)
        {
            tmp.Insert(0, value >= 0 ? (byte)0x00 : (byte)0xFF);
        }

        return Tlv(0x02, tmp.ToArray());
    }

    private static byte[] EncodeOctetString(string value)
        => Tlv(0x04, Encoding.UTF8.GetBytes(value));

    private static byte[] EncodeOid(int[] oid)
    {
        var body = new List<byte> { (byte)((oid[0] * 40) + oid[1]) };
        for (var i = 2; i < oid.Length; i++)
        {
            body.AddRange(EncodeSubId(oid[i]));
        }

        return Tlv(0x06, body.ToArray());
    }

    private static IEnumerable<byte> EncodeSubId(int value)
    {
        var tmp = new List<byte> { (byte)(value & 0x7F) };
        value >>= 7;
        while (value > 0)
        {
            tmp.Insert(0, (byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        return tmp;
    }

    internal static int[] ParseOid(string oid)
        => oid.Split('.').Select(int.Parse).ToArray();

    private static byte[] Tlv(byte tag, byte[] content)
    {
        var len = EncodeLength(content.Length);
        var body = new byte[1 + len.Length + content.Length];
        body[0] = tag;
        Buffer.BlockCopy(len, 0, body, 1, len.Length);
        Buffer.BlockCopy(content, 0, body, 1 + len.Length, content.Length);
        return body;
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return new[] { (byte)length };
        }

        var bytes = new List<byte>();
        var v = length;
        while (v > 0)
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }

        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return bytes.ToArray();
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var result = new List<byte>();
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        return result.ToArray();
    }
}