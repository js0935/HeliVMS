using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Storage.Tests;

public class LdapClientTests
{
    private static LdapSettings Settings(int port, bool serviceAccount = false) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        BaseDn = "DC=corp,DC=com",
        BindDn = serviceAccount ? "CN=svc,DC=corp,DC=com" : null,
        BindPassword = serviceAccount ? "svcpw" : null,
        UserFilter = "(&(objectClass=user)(sAMAccountName={user}))",
        AdminGroups = new[] { "CN=HeliVMS Admins,OU=Groups,DC=corp,DC=com" },
    };

    [Fact]
    public void Bind_ValidDirectBind_ReturnsMemberOfGroups()
    {
        const string groupA = "CN=Admins A,OU=Groups,DC=corp,DC=com";
        const string groupB = "CN=Admins B,OU=Groups,DC=corp,DC=com";
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            return ops[^1].Tag switch
            {
                0x60 => Messages.BindResponse(id, 0),
                0x63 => Messages.Bytes(
                    Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com", ("memberOf", new[] { groupA, groupB })),
                    Messages.SearchDone(id, 0)),
                _ => null,
            };
        });
        server.Start();

        var groups = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");

        Assert.NotNull(groups);
        Assert.Equal(new[] { groupA, groupB }, groups!);
    }

    [Fact]
    public void Bind_SendsExpectedBindWireFields()
    {
        byte[]? captured = null;
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            if (ops[^1].Tag == 0x60)
            {
                captured = msg;
                return Messages.BindResponse(id, 0);
            }

            return Messages.SearchDone(id, 0);
        });
        server.Start();

        new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");

        Assert.NotNull(captured);
        var ops = Messages.ParseMessage(captured!);
        Assert.Equal(2, ops.Count);
        Assert.Equal(0x02, ops[0].Tag);
        Assert.Equal(new byte[] { 0x01 }, ops[0].Body);
        Assert.Equal(0x60, ops[1].Tag);

        var inner = LdapBer.Parse(ops[1].Body);
        Assert.Equal(3, inner.Count);
        Assert.Equal(new byte[] { 0x03 }, inner[0].Body);
        Assert.Equal(0x04, inner[1].Tag);
        Assert.Equal("sAMAccountName=alice,DC=corp,DC=com", Encoding.UTF8.GetString(inner[1].Body));
        Assert.Equal(0x80, inner[2].Tag);
        Assert.Equal("p@ss", Encoding.UTF8.GetString(inner[2].Body));
    }

    [Fact]
    public void Bind_SendsExpectedSearchWireFields()
    {
        byte[]? searchMsg = null;
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            if (ops[^1].Tag == 0x63)
            {
                searchMsg = msg;
                return Messages.Bytes(
                    Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com", ("memberOf", new[] { "CN=G,DC=corp,DC=com" })),
                    Messages.SearchDone(id, 0));
            }

            return Messages.BindResponse(id, 0);
        });
        server.Start();

        new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");

        Assert.NotNull(searchMsg);
        var ops2 = Messages.ParseMessage(searchMsg!);
        var op = ops2[^1];
        Assert.Equal(0x63, op.Tag);
        var fields = LdapBer.Parse(op.Body);
        Assert.Equal(8, fields.Count);
        Assert.Equal("sAMAccountName=alice,DC=corp,DC=com", Encoding.UTF8.GetString(fields[0].Body));
        Assert.Equal(new byte[] { 0x00 }, fields[1].Body);
        Assert.Equal(0x87, fields[6].Tag);
        Assert.Equal(0x04, LdapBer.Parse(fields[7].Body)[0].Tag);
    }

    [Fact]
    public void Bind_WrongPassword_ReturnsNull()
    {
        using var server = new FakeLdapServer(_ => Messages.BindResponse(0x01, 49, "invalidCredentials"));
        server.Start();

        var result = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "bad");
        Assert.Null(result);
    }

    [Fact]
    public void Bind_ConnectionRefused_ReturnsNull()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var result = new LdapClient(TimeSpan.FromSeconds(2)).Bind(Settings(port), "alice", "p@ss");
        Assert.Null(result);
    }

    [Fact]
    public void Bind_MalformedResponse_ReturnsNull()
    {
        using var server = new FakeLdapServer(_ => new byte[] { 0x30, 0x81 });
        server.Start();

        var result = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");
        Assert.Null(result);
    }

    [Fact]
    public void Bind_InvalidSettings_ReturnsNull()
    {
        var result = new LdapClient().Bind(new LdapSettings(), "alice", "p@ss");
        Assert.Null(result);
    }

    [Fact]
    public void Bind_ServiceAccount_SearchesThenBindsUser()
    {
        var sequence = new List<byte>();
        var searchCount = 0;
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            if (ops[^1].Tag == 0x60)
            {
                sequence.Add(0x60);
                return Messages.BindResponse(id, 0);
            }

            searchCount++;
            sequence.Add(0x63);
            return searchCount == 1
                ? Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com", ("memberOf", new[] { "CN=G1,DC=corp,DC=com" }))
                : Messages.Bytes(
                    Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com", ("memberOf", new[] { "CN=G2,DC=corp,DC=com" })),
                    Messages.SearchDone(id, 0));
        });
        server.Start();

        var groups = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port, serviceAccount: true), "alice", "p@ss");

        Assert.NotNull(groups);
        Assert.Equal(new[] { "CN=G2,DC=corp,DC=com" }, groups!);
        Assert.Equal(new byte[] { 0x60, 0x63, 0x60, 0x63 }, sequence.ToArray());
    }

    [Fact]
    public void Bind_SearchGroupsError_ReturnsEmptyList()
    {
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            return ops[^1].Tag switch
            {
                0x60 => Messages.BindResponse(id, 0),
                0x63 => Messages.SearchDone(id, 32),
                _ => null,
            };
        });
        server.Start();

        var result = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Bind_UserWithoutMemberOf_ReturnsEmptyList()
    {
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            return ops[^1].Tag switch
            {
                0x60 => Messages.BindResponse(id, 0),
                0x63 => Messages.Bytes(Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com"), Messages.SearchDone(id, 0)),
                _ => null,
            };
        });
        server.Start();

        var result = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Bind_AdminGroupMembership_MapsToAdminRole()
    {
        const string adminGroup = "CN=HeliVMS Admins,OU=Groups,DC=corp,DC=com";
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            return ops[^1].Tag switch
            {
                0x60 => Messages.BindResponse(id, 0),
                0x63 => Messages.Bytes(
                    Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com", ("memberOf", new[] { adminGroup, "CN=Users,DC=corp,DC=com" })),
                    Messages.SearchDone(id, 0)),
                _ => null,
            };
        });
        server.Start();

        var settings = Settings(server.Port) with { AdminGroups = new[] { adminGroup } };
        var groups = new LdapClient(TimeSpan.FromSeconds(5)).Bind(settings, "alice", "p@ss");
        Assert.NotNull(groups);
        Assert.Equal("admin", RoleMapper.Map(groups!, settings.AdminGroups, settings.DefaultRole));
    }

    [Fact]
    public void Bind_NonAdminGroup_SetsViewer()
    {
        using var server = new FakeLdapServer(msg =>
        {
            var ops = Messages.ParseMessage(msg);
            var id = ops[0].Body.Single();
            return ops[^1].Tag switch
            {
                0x60 => Messages.BindResponse(id, 0),
                0x63 => Messages.Bytes(
                    Messages.SearchEntry(id, "CN=alice,DC=corp,DC=com", ("memberOf", new[] { "CN=Viewers,DC=corp,DC=com" })),
                    Messages.SearchDone(id, 0)),
                _ => null,
            };
        });
        server.Start();

        var result = new LdapClient(TimeSpan.FromSeconds(5)).Bind(Settings(server.Port), "alice", "p@ss");
        Assert.Equal("viewer", RoleMapper.Map(result!, Settings(server.Port).AdminGroups, "viewer"));
    }
}

internal static class Messages
{
    internal static IReadOnlyList<BerTlv> ParseMessage(byte[] msg) => LdapBer.Parse(msg.AsSpan(2));

    internal static byte[] Bytes(params byte[][] parts) => LdapBer.Concat(parts);

    internal static byte[] BindResponse(byte msgId, int code, string diagnostic = "")
        => LdapBer.Tlv(0x30, LdapBer.Concat(
            LdapBer.Integer(msgId),
            LdapBer.Tlv(0x61, LdapBer.Concat(LdapBer.Enumerated(code), LdapBer.Octet(""), LdapBer.Octet(diagnostic)))));

    internal static byte[] SearchDone(byte msgId, int code)
        => LdapBer.Tlv(0x30, LdapBer.Concat(
            LdapBer.Integer(msgId),
            LdapBer.Tlv(0x65, LdapBer.Concat(LdapBer.Enumerated(code), LdapBer.Octet(""), LdapBer.Octet("")))));

    internal static byte[] SearchEntry(byte msgId, string dn, params (string Attribute, string[] Values)[] attributes)
        => LdapBer.Tlv(0x30, LdapBer.Concat(
            LdapBer.Integer(msgId),
            LdapBer.Tlv(0x64, LdapBer.Concat(
                LdapBer.Tlv(0x30, LdapBer.Concat(
                    LdapBer.Octet(dn),
                    LdapBer.Seq(attributes.Select(a =>
                        LdapBer.Seq(
                            LdapBer.Octet(a.Attribute),
                            LdapBer.Set(a.Values.Select(LdapBer.Octet).ToArray()))).ToArray())))))));
}

internal sealed class FakeLdapServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<byte[], byte[]?> _respond;
    private Task? _task;

    public FakeLdapServer(Func<byte[], byte[]?> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public void Start()
    {
        _task = Task.Run(async () =>
        {
            var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var stream = client.GetStream();
            while (true)
            {
                var msg = await ReadMessageAsync(stream).ConfigureAwait(false);
                if (msg is null)
                {
                    return;
                }

                var response = _respond(msg);
                if (response is null || !stream.CanWrite)
                {
                    return;
                }

                await stream.WriteAsync(response).ConfigureAwait(false);
            }
        });
    }

    public void Dispose()
    {
        _listener.Stop();
        _task?.GetAwaiter().GetResult();
    }

    private static async Task<byte[]?> ReadMessageAsync(Stream stream)
    {
        var first = await stream.ReadAsync(new byte[1]).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        var len = await ReadByteAsync(stream).ConfigureAwait(false);
        if (len is null)
        {
            return null;
        }

        var length = 0;
        if ((len.Value & 0x80) != 0)
        {
            var count = len.Value & 0x7F;
            var raw = new byte[count];
            await ReadFullyAsync(stream, raw).ConfigureAwait(false);
            foreach (var b in raw)
            {
                length = (length << 8) | b;
            }
        }
        else
        {
            length = len.Value;
        }

        var body = new byte[length];
        await ReadFullyAsync(stream, body).ConfigureAwait(false);
        var full = new byte[2 + body.Length];
        full[0] = (byte)first;
        full[1] = len.Value;
        body.CopyTo(full, 2);
        return full;
    }

    private static async Task<byte?> ReadByteAsync(Stream stream)
    {
        var buf = new byte[1];
        var n = await stream.ReadAsync(buf).ConfigureAwait(false);
        return n == 0 ? null : buf[0];
    }

    private static async Task ReadFullyAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("假伺服器連線中斷");
            }

            read += n;
        }
    }
}