using System.Text.Json;

namespace HeliVMS.Storage.Tests;

/// <summary>M50（§14.7 #1）：LDAP 過濾器（RFC 4515）與設定序列化測試。</summary>
public class LdapFilterTests
{
    [Fact]
    public void Escape_SpecialCharacters()
    {
        Assert.Equal(@"\28cn=foo\29", LdapFilter.Escape("(cn=foo)"));
        Assert.Equal(@"a\2ab", LdapFilter.Escape("a*b"));
        Assert.Equal(@"\5c", LdapFilter.Escape("\\"));
        Assert.Equal(@"x\00y", LdapFilter.Escape("x\0y"));
        Assert.Equal("alice", LdapFilter.Escape("alice"));
    }

    [Fact]
    public void Build_SubstitutesUserPlaceholder()
    {
        var filter = LdapFilter.Build("(&(objectClass=user)(sAMAccountName={user}))", "alice");

        Assert.Equal("(&(objectClass=user)(sAMAccountName=alice))", filter);
    }

    [Fact]
    public void Build_SubstitutesNumericPlaceholder()
    {
        Assert.Equal("(uid=alice)", LdapFilter.Build("(uid={0})", "alice"));
    }

    [Fact]
    public void Build_EscapesInjectedUsername()
    {
        var filter = LdapFilter.Build("(uid={user})", "a*)(|(uid=*)");

        Assert.Equal(@"(uid=a\2a\29\28|\28uid=\2a\29)", filter);
    }

    [Fact]
    public void Build_TemplateWithoutPlaceholder_Throws()
    {
        Assert.Throws<ArgumentException>(() => LdapFilter.Build("(objectClass=user)", "alice"));
    }

    [Fact]
    public void Build_EmptyTemplate_Throws()
    {
        Assert.Throws<ArgumentException>(() => LdapFilter.Build(string.Empty, "alice"));
    }

    [Fact]
    public void LdapSettings_JsonRoundTrip()
    {
        var settings = new LdapSettings
        {
            Host = "dc.corp.example.com",
            Port = 636,
            BaseDn = "DC=corp,DC=example,DC=com",
            BindDn = "CN=svc,DC=corp,DC=example,DC=com",
            AdminGroups = new[] { "VMS Admins" },
            DefaultRole = "viewer",
        };

        var parsed = LdapSettings.FromJson(settings.ToJson());

        Assert.Equal(settings.Host, parsed.Host);
        Assert.Equal(636, parsed.Port);
        Assert.Equal(settings.BaseDn, parsed.BaseDn);
        Assert.Equal(settings.BindDn, parsed.BindDn);
        Assert.Equal(new[] { "VMS Admins" }, parsed.AdminGroups);
        Assert.Contains("{user}", parsed.UserFilter);
    }

    [Fact]
    public void LdapSettings_EmptyJson_ReturnsDefaults()
    {
        var parsed = LdapSettings.FromJson("");

        Assert.Equal(389, parsed.Port);
        Assert.Equal("viewer", parsed.DefaultRole);
    }

    [Fact]
    public void OidcOptions_JsonRoundTrip()
    {
        var options = new OidcOptions
        {
            Issuer = "https://idp.example.com",
            Audience = "helivms",
            ClientId = "helivms-web",
            AdminGroups = new[] { "vms-admins" },
            DefaultRole = "viewer",
            ClockSkewSeconds = 30,
            JwksJson = """{"keys":[]}""",
        };

        var json = options.ToJson();
        var parsed = JsonSerializer.Deserialize<OidcOptions>(json);

        Assert.NotNull(parsed);
        Assert.Equal(options.Issuer, parsed!.Issuer);
        Assert.Equal(options.ClientId, parsed.ClientId);
        Assert.Equal(new[] { "vms-admins" }, parsed.AdminGroups);
        Assert.Equal(30, parsed.ClockSkewSeconds);
        Assert.Equal(options.JwksJson, parsed.JwksJson);
    }
}
