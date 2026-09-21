using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class LdapDnValidatorTests
{
    private static LdapSettings Defaults() => new()
    {
        Host = "idc.example.com",
        Port = 636,
        BaseDn = "DC=example,DC=com",
    };

    [Fact]
    public void BuildUserDn_UsesSAMAccountNameAndBaseDn()
    {
        var settings = Defaults();

        Assert.Equal("sAMAccountName=alice,DC=example,DC=com", LdapDn.BuildUserDn(settings, "alice"));
    }

    [Fact]
    public void BuildUserDn_EscapesSpecialCharacters()
    {
        var settings = Defaults();

        Assert.Equal("sAMAccountName=smith\\,john,DC=example,DC=com", LdapDn.BuildUserDn(settings, "smith,john"));
        Assert.Equal("sAMAccountName=a\\=b,DC=example,DC=com", LdapDn.BuildUserDn(settings, "a=b"));
        Assert.Equal("sAMAccountName=\\#hash,DC=example,DC=com", LdapDn.BuildUserDn(settings, "#hash"));
        Assert.Equal("sAMAccountName=\\ lead,DC=example,DC=com", LdapDn.BuildUserDn(settings, " lead"));
    }

    [Fact]
    public void BuildUserDn_NoBaseDn_ReturnsAttributeOnly()
    {
        var settings = Defaults() with { BaseDn = string.Empty };

        Assert.Equal("sAMAccountName=alice", LdapDn.BuildUserDn(settings, "alice"));
    }

    [Fact]
    public void BuildUserDn_NullUsername_ProducesEmptyValue()
    {
        var settings = Defaults();

        Assert.Equal("sAMAccountName=,DC=example,DC=com", LdapDn.BuildUserDn(settings, null!));
    }

    [Fact]
    public void Escape_HandlesTrailingSpace()
    {
        Assert.Equal("space\\ ", LdapDn.Escape("space "));
        Assert.Equal("plain", LdapDn.Escape("plain"));
        Assert.Equal("a\\+b", LdapDn.Escape("a+b"));
        Assert.Equal("a\\<b", LdapDn.Escape("a<b"));
    }

    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LdapDn.Escape(null));
        Assert.Equal(string.Empty, LdapDn.Escape(string.Empty));
    }

    [Fact]
    public void Validate_RequiresHost()
    {
        var problems = LdapSettingsValidator.Validate(Defaults() with { Host = string.Empty });

        Assert.Contains(problems, p => p.Contains("伺服器"));
    }

    [Fact]
    public void Validate_RequiresBaseDn()
    {
        var problems = LdapSettingsValidator.Validate(Defaults() with { BaseDn = string.Empty });

        Assert.Contains(problems, p => p.Contains("基準 DN"));
    }

    [Fact]
    public void Validate_PortRange()
    {
        Assert.Contains(LdapSettingsValidator.Validate(Defaults() with { Port = 0 }), p => p.Contains("埠"));
        Assert.Contains(LdapSettingsValidator.Validate(Defaults() with { Port = 65536 }), p => p.Contains("埠"));
        Assert.DoesNotContain(LdapSettingsValidator.Validate(Defaults()), p => p.Contains("埠"));
    }

    [Fact]
    public void Validate_RequiresUserFilterPlaceholder()
    {
        var noFilter = LdapSettingsValidator.Validate(Defaults() with { UserFilter = string.Empty });
        Assert.Contains(noFilter, p => p.Contains("過濾器"));

        var noPlaceholder = LdapSettingsValidator.Validate(Defaults() with { UserFilter = "(objectClass=user)" });
        Assert.Contains(noPlaceholder, p => p.Contains("佔位"));
    }

    [Fact]
    public void Validate_ValidSettings_NoProblems()
    {
        Assert.Empty(LdapSettingsValidator.Validate(Defaults()));
    }
}