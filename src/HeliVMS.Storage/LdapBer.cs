using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// 最小 BER（ASN.1）編碼器／解碼器（M85，§14.7 #1 L1）：僅需 LDAPv3 用之 tag／長度
/// （短型＋長型）與 INTEGER／ENUMERATED／OCTET STRING／BOOLEAN／SEQUENCE／SET。
/// </summary>
public static class LdapBer
{
    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        var len = content.Length;
        var header = len switch
        {
            < 0x80 => 2,
            <= 0xFF => 3,
            <= 0xFFFF => 4,
            _ => 5,
        };
        var body = new byte[header + len];
        body[0] = tag;
        var p = 1;
        if (len < 0x80)
        {
            body[p++] = (byte)len;
        }
        else if (len <= 0xFF)
        {
            body[p++] = 0x81;
            body[p++] = (byte)len;
        }
        else if (len <= 0xFFFF)
        {
            body[p++] = 0x82;
            body[p++] = (byte)(len >> 8);
            body[p++] = (byte)len;
        }
        else
        {
            body[p++] = 0x83;
            body[p++] = (byte)(len >> 16);
            body[p++] = (byte)(len >> 8);
            body[p++] = (byte)len;
        }

        content.CopyTo(body.AsSpan(p));
        return body;
    }

    public static byte[] Integer(int value)
    {
        var raw = BitConverter.GetBytes(value);
        var end = 3;
        while (end > 0 && raw[end] == 0)
        {
            end--;
        }

        var needsPad = (raw[end] & 0x80) != 0;
        var body = new byte[1 + 1 + end + 1 + (needsPad ? 1 : 0)];
        body[0] = 0x02;
        body[1] = (byte)(end + 1 + (needsPad ? 1 : 0));
        var p = 2;
        if (needsPad)
        {
            body[p++] = 0;
        }

        for (var i = end; i >= 0; i--)
        {
            body[p++] = raw[i];
        }

        return body;
    }

    public static byte[] Enumerated(int value) => Tlv(0x0A, MinimalEncoded(value));

    public static byte[] Octet(string value) => Tlv(0x04, Encoding.UTF8.GetBytes(value));

    public static byte[] Boolean(bool value) => Tlv(0x01, new[] { value ? (byte)0xFF : (byte)0x00 });

    public static byte[] Seq(params byte[][] parts) => Tlv(0x30, Concat(parts));

    public static byte[] Set(params byte[][] parts) => Tlv(0x31, Concat(parts));

    public static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var buf = new byte[total];
        var p = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buf.AsSpan(p));
            p += part.Length;
        }

        return buf;
    }

    /// <summary>
    /// 讀取單一 TLV：回傳 <paramref name="bodyStart"/>（內容起點）與 <paramref name="bodyLength"/>。
    /// 資料不足或長度格式異常回 <c>false</c>。
    /// </summary>
    public static bool TryReadTlv(ReadOnlySpan<byte> data, int offset, out byte tag, out int bodyStart, out int bodyLength)
    {
        tag = 0;
        bodyStart = 0;
        bodyLength = 0;
        if (offset >= data.Length)
        {
            return false;
        }

        tag = data[offset];
        var p = offset + 1;
        if (p >= data.Length)
        {
            return false;
        }

        var lenByte = data[p++];
        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            var count = lenByte & 0x7F;
            if (count is < 1 or > 4 || p + count > data.Length)
            {
                return false;
            }

            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | data[p++];
            }
        }

        if (length < 0 || p + length > data.Length)
        {
            return false;
        }

        bodyStart = p;
        bodyLength = length;
        return true;
    }

    /// <summary>依序解出 <paramref name="content"/> 內容中所有 TLV。</summary>
    public static IReadOnlyList<BerTlv> Parse(ReadOnlySpan<byte> content)
    {
        var list = new List<BerTlv>();
        var p = 0;
        while (TryReadTlv(content, p, out var tag, out var start, out var len))
        {
            var body = new byte[len];
            content.Slice(start, len).CopyTo(body);
            list.Add(new BerTlv(tag, body));
            p = start + len;
        }

        return list;
    }

    public static byte[] Content(ReadOnlySpan<byte> tlv)
        => Parse(tlv).Count == 0 ? Array.Empty<byte>() : tlv.Slice(2, tlv.Length - 2).ToArray();

    private static byte[] MinimalEncoded(int value)
    {
        if (value == 0)
        {
            return new byte[] { 0x00 };
        }

        var raw = BitConverter.GetBytes(value);
        var end = 3;
        while (end > 0 && raw[end] == 0)
        {
            end--;
        }

        var bytes = new byte[end + 1];
        for (var i = end; i >= 0; i--)
        {
            bytes[end - i] = raw[i];
        }

        return bytes;
    }
}

/// <summary>解析後的一個 BER TLV。</summary>
public readonly record struct BerTlv(byte Tag, byte[] Body);