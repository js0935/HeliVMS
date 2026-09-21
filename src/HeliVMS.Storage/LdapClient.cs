using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// LDAPv3 連線層（M85，§14.7 #1 L1）：實作 <see cref="ILdapBinder"/>，以裸 LDAPv3
/// wire protocol（TCP＋BER，無第三方依賴）完成 simple bind 與群組（memberOf）擷取。
/// 驗證失敗或流程錯誤一律回 <c>null</c>（符合 ILdapBinder 契約）。
/// </summary>
public sealed class LdapClient : ILdapBinder
{
    private readonly TimeSpan _timeout;

    public LdapClient(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public IReadOnlyList<string>? Bind(LdapSettings settings, string username, string password)
    {
        if (LdapSettingsValidator.Validate(settings).Count > 0)
        {
            return null;
        }

        try
        {
            using var tcp = new TcpClient();
            tcp.Connect(settings.Host, settings.Port);
            tcp.ReceiveTimeout = (int)_timeout.TotalMilliseconds;
            tcp.SendTimeout = (int)_timeout.TotalMilliseconds;
            using var stream = tcp.GetStream();

            string? userDn;
            var msgId = 0;
            if (!string.IsNullOrWhiteSpace(settings.BindDn))
            {
                if (!BindOnce(stream, ++msgId, settings.BindDn, settings.BindPassword ?? string.Empty))
                {
                    return null;
                }

                userDn = SearchUserDn(stream, ++msgId, settings, username);
                if (userDn is null)
                {
                    return null;
                }

                if (!BindOnce(stream, ++msgId, userDn, password))
                {
                    return null;
                }
            }
            else
            {
                userDn = LdapDn.BuildUserDn(settings, username);
                if (!BindOnce(stream, ++msgId, userDn, password))
                {
                    return null;
                }
            }

            return SearchGroups(stream, ++msgId, userDn);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static byte[] EncodeMessage(int msgId, byte[] protocolOp)
        => LdapBer.Tlv(0x30, LdapBer.Concat(LdapBer.Integer(msgId), protocolOp));

    private static byte[] BuildBind(int msgId, string dn, string password)
    {
        var op = LdapBer.Tlv(0x60, LdapBer.Concat(
            LdapBer.Integer(3),
            LdapBer.Octet(dn),
            LdapBer.Tlv(0x80, Encoding.UTF8.GetBytes(password))));
        return EncodeMessage(msgId, op);
    }

    private bool BindOnce(NetworkStream stream, int msgId, string dn, string password)
    {
        Write(stream, BuildBind(msgId, dn, password));
        var packet = ReadMessage(stream);
        foreach (var tlv in LdapBer.Parse(packet.Body))
        {
            if (tlv.Tag != 0x61)
            {
                continue;
            }

            foreach (var part in LdapBer.Parse(tlv.Body))
            {
                if (part.Tag == 0x0A)
                {
                    return DecodeInteger(part.Body) == 0;
                }
            }
        }

        return false;
    }

    private string? SearchUserDn(NetworkStream stream, int msgId, LdapSettings settings, string username)
    {
        var op = LdapBer.Tlv(0x63, LdapBer.Concat(
            LdapBer.Octet(settings.BaseDn),
            LdapBer.Enumerated(2),
            LdapBer.Enumerated(0),
            LdapBer.Integer(1),
            LdapBer.Integer(0),
            LdapBer.Boolean(false),
            LdapFilterEncoder.Encode(LdapFilter.Build(settings.UserFilter, username)),
            LdapBer.Seq()));
        Write(stream, EncodeMessage(msgId, op));

        while (true)
        {
            var packet = ReadMessage(stream);
            foreach (var tlv in LdapBer.Parse(packet.Body))
            {
                if (tlv.Tag == 0x65)
                {
                    return null;
                }

                if (tlv.Tag != 0x64)
                {
                    continue;
                }

                foreach (var node in LdapBer.Parse(tlv.Body))
                {
                    if (node.Tag != 0x30)
                    {
                        continue;
                    }

                    var members = LdapBer.Parse(node.Body);
                    if (members.Count > 0 && members[0].Tag == 0x04)
                    {
                        return Encoding.UTF8.GetString(members[0].Body);
                    }
                }
            }
        }
    }

    private IReadOnlyList<string> SearchGroups(NetworkStream stream, int msgId, string userDn)
    {
        var op = LdapBer.Tlv(0x63, LdapBer.Concat(
            LdapBer.Octet(userDn),
            LdapBer.Enumerated(0),
            LdapBer.Enumerated(0),
            LdapBer.Integer(0),
            LdapBer.Integer(0),
            LdapBer.Boolean(false),
            LdapFilterEncoder.Encode("(objectClass=*)"),
            LdapBer.Seq(LdapBer.Octet("*"), LdapBer.Octet("+"))));
        Write(stream, EncodeMessage(msgId, op));

        var groups = new List<string>();
        while (true)
        {
            var packet = ReadMessage(stream);
            foreach (var tlv in LdapBer.Parse(packet.Body))
            {
                if (tlv.Tag == 0x64)
                {
                    ParseEntry(tlv.Body, groups);
                }
                else if (tlv.Tag == 0x65)
                {
                    return groups;
                }
            }
        }
    }

    private static void ParseEntry(byte[] opBody, List<string> groups)
    {
        foreach (var node in LdapBer.Parse(opBody))
        {
            if (node.Tag != 0x30)
            {
                continue;
            }

            foreach (var member in LdapBer.Parse(node.Body))
            {
                if (member.Tag != 0x30)
                {
                    continue;
                }

                foreach (var entry in LdapBer.Parse(member.Body))
                {
                    if (entry.Tag != 0x30)
                    {
                        continue;
                    }

                    var parts = LdapBer.Parse(entry.Body);
                    if (parts.Count < 2 || parts[0].Tag != 0x04 ||
                        Encoding.UTF8.GetString(parts[0].Body) != "memberOf" ||
                        parts[1].Tag != 0x31)
                    {
                        continue;
                    }

                    foreach (var value in LdapBer.Parse(parts[1].Body))
                    {
                        if (value.Tag == 0x04)
                        {
                            groups.Add(Encoding.UTF8.GetString(value.Body));
                        }
                    }
                }
            }
        }
    }

    private static int DecodeInteger(ReadOnlySpan<byte> body)
    {
        var value = 0;
        foreach (var b in body)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private BerTlv ReadMessage(NetworkStream stream)
    {
        var tag = stream.ReadByte();
        if (tag < 0)
        {
            throw new IOException("連線中斷");
        }

        var lenByte = stream.ReadByte();
        if (lenByte < 0)
        {
            throw new IOException("連線中斷");
        }

        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            var count = lenByte & 0x7F;
            var raw = new byte[count];
            ReadFully(stream, raw, 0, count);
            length = 0;
            foreach (var b in raw)
            {
                length = (length << 8) | b;
            }
        }

        var body = new byte[length];
        ReadFully(stream, body, 0, length);
        return new BerTlv((byte)tag, body);
    }

    private static void ReadFully(NetworkStream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0)
            {
                throw new IOException("連線中斷");
            }

            read += n;
        }
    }

    private static void Write(NetworkStream stream, byte[] bytes)
    {
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }
}