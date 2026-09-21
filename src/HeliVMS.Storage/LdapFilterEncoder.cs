using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// LDAP 過濾器（RFC 4515 字串）→ BER LDAPFilter 編碼（M85，§14.7 #1 L1）。
/// 支援 <c>(attr=value)</c>、<c>(attr=*)</c>（presence）與 <c>&amp;</c>／<c>|</c>／<c>!</c> 組合，
/// 並解開 RFC 4515 跳脫（<c>\XX</c> 十六進位與單字元跳脫）。
/// </summary>
public static class LdapFilterEncoder
{
    public static byte[] Encode(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new ArgumentException("過濾器不可為空", nameof(filter));
        }

        var i = 0;
        var tlv = ParseNode(filter, ref i);
        if (i != filter.Length)
        {
            throw new ArgumentException($"過濾器結尾有剩餘字元：{filter}", nameof(filter));
        }

        return tlv;
    }

    private static byte[] ParseNode(string filter, ref int i)
    {
        if (i >= filter.Length || filter[i] != '(')
        {
            throw new ArgumentException($"預期 '(' 位置 {i}：{filter}", nameof(filter));
        }

        i++;
        if (i >= filter.Length)
        {
            throw new ArgumentException($"過濾器不完整：{filter}", nameof(filter));
        }

        var c = filter[i];
        byte[] result;
        if (c is '&' or '|')
        {
            i++;
            var children = new List<byte[]>();
            while (i < filter.Length && filter[i] == '(')
            {
                children.Add(ParseNode(filter, ref i));
            }

            if (children.Count == 0)
            {
                throw new ArgumentException($"{c} 節點需至少一個子過濾器：{filter}", nameof(filter));
            }

            result = LdapBer.Tlv(c == '&' ? (byte)0xA0 : (byte)0xA1, LdapBer.Concat(children.ToArray()));
        }
        else if (c == '!')
        {
            i++;
            if (i >= filter.Length || filter[i] != '(')
            {
                throw new ArgumentException($"! 節點需一個子過濾器：{filter}", nameof(filter));
            }

            var child = ParseNode(filter, ref i);
            result = LdapBer.Tlv(0xA2, child);
        }
        else
        {
            var eq = filter.IndexOf('=', i);
            if (eq < 0)
            {
                throw new ArgumentException($"過濾器缺 '='：{filter}", nameof(filter));
            }

            var close = filter.IndexOf(')', eq + 1);
            if (close < 0)
            {
                throw new ArgumentException($"過濾器缺 ')'：{filter}", nameof(filter));
            }

            var attr = filter[i..eq];
            var value = filter.Substring(eq + 1, close - eq - 1);
            i = close;
            if (value == "*")
            {
                result = LdapBer.Tlv(0x87, Encoding.UTF8.GetBytes(attr));
            }
            else
            {
                result = LdapBer.Tlv(
                    0xA3,
                    LdapBer.Seq(
                        LdapBer.Octet(attr),
                        LdapBer.Tlv(0x04, Unescape(value))));
            }
        }

        i++;
        return result;
    }

    private static byte[] Unescape(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var k = 0; k < value.Length; k++)
        {
            var c = value[k];
            if (c != '\\')
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            if (k + 2 < value.Length && Uri.IsHexDigit(value[k + 1]))
            {
                bytes.Add(Convert.ToByte(value.Substring(k + 1, 2), 16));
                k += 2;
                continue;
            }

            if (k + 1 < value.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[++k].ToString()));
                continue;
            }

            throw new ArgumentException($"跳脫序列不完整：{value}", nameof(value));
        }

        return bytes.ToArray();
    }
}