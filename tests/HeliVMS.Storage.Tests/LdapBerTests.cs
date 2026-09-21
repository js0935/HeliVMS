using System.Text;

namespace HeliVMS.Storage.Tests;

public class LdapBerTests
{
    [Fact]
    public void Integer_EncodesMinimal()
    {
        Assert.Equal(new byte[] { 0x02, 0x01, 0x00 }, LdapBer.Integer(0));
        Assert.Equal(new byte[] { 0x02, 0x01, 0x7F }, LdapBer.Integer(127));
        Assert.Equal(new byte[] { 0x02, 0x02, 0x00, 0x80 }, LdapBer.Integer(128));
        Assert.Equal(new byte[] { 0x02, 0x02, 0x03, 0xE8 }, LdapBer.Integer(1000));
    }

    [Fact]
    public void Enumerated_Octet_Boolean_Encode()
    {
        Assert.Equal(new byte[] { 0x0A, 0x01, 0x00 }, LdapBer.Enumerated(0));
        Assert.Equal(new byte[] { 0x04, 0x03, 0x61, 0x62, 0x63 }, LdapBer.Octet("abc"));
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00 }, LdapBer.Boolean(false));
        Assert.Equal(new byte[] { 0x01, 0x01, 0xFF }, LdapBer.Boolean(true));
    }

    [Fact]
    public void Tlv_LongLength_And_SeqStructure()
    {
        var content = new byte[200];
        var tlv = LdapBer.Tlv(0x30, content);
        Assert.Equal(3 + 200, tlv.Length);
        Assert.Equal(0x30, tlv[0]);
        Assert.Equal(0x81, tlv[1]);
        Assert.Equal(200, tlv[2]);

        var seq = LdapBer.Seq(LdapBer.Octet("a"), LdapBer.Octet("b"));
        Assert.Equal(2, LdapBer.Parse(seq.Body()).Count);
        Assert.Equal(0x04, LdapBer.Parse(seq.Body())[0].Tag);
    }

    [Fact]
    public void Parse_RoundTrips_Children()
    {
        var inner = LdapBer.Concat(LdapBer.Integer(1), LdapBer.Tlv(0x60, new byte[] { 0x00 }));
        var outer = LdapBer.Tlv(0x30, inner);
        var children = LdapBer.Parse(outer.Body());
        Assert.Equal(2, children.Count);
        Assert.Equal(0x02, children[0].Tag);
        Assert.Equal(new byte[] { 0x01 }, children[0].Body);
        Assert.Equal(0x60, children[1].Tag);
        Assert.Equal(new byte[] { 0x00 }, children[1].Body);
    }

    [Fact]
    public void TryReadTlv_Boundaries()
    {
        var shortLen = LdapBer.Tlv(0x04, Encoding.UTF8.GetBytes(new string('x', 127)));
        Assert.True(LdapBer.TryReadTlv(shortLen, 0, out _, out var s0, out var l0));
        Assert.Equal(127, l0);

        var longLen = LdapBer.Tlv(0x04, Encoding.UTF8.GetBytes(new string('y', 128)));
        Assert.True(LdapBer.TryReadTlv(longLen, 0, out _, out var s1, out var l1));
        Assert.Equal(0x81, longLen[1]);
        Assert.Equal(128, l1);

        var big = LdapBer.Tlv(0x30, new byte[300]);
        Assert.True(LdapBer.TryReadTlv(big, 0, out _, out var s2, out var l2));
        Assert.Equal(0x82, big[1]);
        Assert.Equal(300, l2);

        Assert.False(LdapBer.TryReadTlv(new byte[] { 0x30 }, 0, out _, out _, out _));
        Assert.False(LdapBer.TryReadTlv(new byte[] { 0x30, 0x05, 0x00 }, 0, out _, out _, out _));
    }

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        Assert.Empty(LdapBer.Parse(new byte[] { 0x00 }));
        Assert.Empty(LdapBer.Parse(Array.Empty<byte>()));
    }
}

public class LdapFilterEncoderTests
{
    [Fact]
    public void Presence_Encodes()
    {
        var tlv = LdapFilterEncoder.Encode("(objectClass=*)");
        Assert.Equal(0x87, tlv[0]);
        Assert.Equal(Encoding.UTF8.GetBytes("objectClass"), tlv.Body());
    }

    [Fact]
    public void Equality_Encodes()
    {
        var tlv = LdapFilterEncoder.Encode("(sAMAccountName=alice)");
        Assert.Equal(0xA3, tlv[0]);
        var seq = LdapBer.Parse(tlv.Body())[0];
        Assert.Equal(0x30, seq.Tag);
        var parts = LdapBer.Parse(seq.Body);
        Assert.Equal(Encoding.UTF8.GetBytes("sAMAccountName"), parts[0].Body);
        Assert.Equal(Encoding.UTF8.GetBytes("alice"), parts[1].Body);
    }

    [Fact]
    public void And_Or_Not_Encodes()
    {
        var and = LdapFilterEncoder.Encode("(&(objectClass=user)(sAMAccountName=alice))");
        Assert.Equal(0xA0, and[0]);
        Assert.Equal(2, LdapBer.Parse(and.Body()).Count);
        Assert.Equal(0xA3, LdapBer.Parse(and.Body())[0].Tag);
        Assert.Equal(0xA3, LdapBer.Parse(and.Body())[1].Tag);

        var or = LdapFilterEncoder.Encode("(|(a=1)(b=2))");
        Assert.Equal(0xA1, or[0]);
        Assert.Equal(2, LdapBer.Parse(or.Body()).Count);

        var not = LdapFilterEncoder.Encode("(!(sn=Doe))");
        Assert.Equal(0xA2, not[0]);
        Assert.Single(LdapBer.Parse(not.Body()));
        Assert.Equal(0xA3, LdapBer.Parse(not.Body())[0].Tag);
    }

    [Fact]
    public void Nested_And_Inside_Or_Encodes()
    {
        var filter = "(|(&(objectClass=user)(sAMAccountName=alice))(&(objectClass=group)(cn=admins)))";
        var tlv = LdapFilterEncoder.Encode(filter);
        Assert.Equal(0xA1, tlv[0]);
        var children = LdapBer.Parse(tlv.Body());
        Assert.Equal(2, children.Count);
        Assert.Equal(0xA0, children[0].Tag);
        Assert.Equal(0xA0, children[1].Tag);
    }

    [Fact]
    public void Escaped_Hex_Is_Decoded()
    {
        var tlv = LdapFilterEncoder.Encode("(cn=J\\6Fhn)");
        var eq = LdapBer.Parse(tlv.Body())[0];
        var parts = LdapBer.Parse(eq.Body);
        Assert.Equal(Encoding.UTF8.GetBytes("John"), parts[1].Body);
    }

    [Fact]
    public void Malformed_Throws()
    {
        Assert.Throws<ArgumentException>(() => LdapFilterEncoder.Encode("(a=*)extra"));
        Assert.Throws<ArgumentException>(() => LdapFilterEncoder.Encode("(!(a=b)))"));
        Assert.Throws<ArgumentException>(() => LdapFilterEncoder.Encode("(missingEquals)"));
        Assert.Throws<ArgumentException>(() => LdapFilterEncoder.Encode(""));
    }
}

internal static class TlvTestExtensions
{
    internal static byte[] Body(this byte[] tlv)
        => tlv.AsSpan(2).ToArray();
}