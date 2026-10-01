using HeliVMS.WebApi;
using Microsoft.Extensions.Configuration;

namespace HeliVMS.Api.Tests;

/// <summary>
/// <see cref="PathAccessPolicy"/>（§14.7 #4）的邊界單元測試。
/// 這種類別的錯法都是「靜默放寬」——沒設定根目錄卻全放行、後綴前綴誤判、
/// 證據包名稱能帶出目錄分隔符——所以每條都針對一個會 fail-open 的方向。
/// </summary>
public sealed class PathAccessPolicyTests
{
    private static string TempRoot(string name) => Path.Combine(Path.GetTempPath(), name);

    private static string NewRoot() => TempRoot($"helivms-root-{Guid.NewGuid():N}");

    [Fact]
    public void NoRootsConfigured_DeniesEverything()
    {
        var policy = new PathAccessPolicy(Array.Empty<string>());

        Assert.Empty(policy.Roots);
        Assert.False(policy.IsAllowed(null));
        Assert.False(policy.IsAllowed("   "));
        Assert.False(policy.IsAllowed(TempRoot("anything")));
        Assert.Throws<UnauthorizedAccessException>(() => policy.EnsureAllowed(TempRoot("anything")));
        Assert.Contains(PathAccessPolicy.RootsConfigKey, policy.DenyMessage("x"));
    }

    [Fact]
    public void Roots_ParsedFromConfig_AcrossSemicolonsAndNewlines()
    {
        var a = NewRoot();
        var b = NewRoot();
        var c = NewRoot();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PathAccessPolicy.RootsConfigKey] = $"{a};{b}{Environment.NewLine}{c}",
            })
            .Build();

        var policy = new PathAccessPolicy(config);

        Assert.Equal(3, policy.Roots.Count);
        Assert.Contains(Path.GetFullPath(a), policy.Roots);
    }

    [Fact]
    public void Roots_NormalizedToAbsoluteWithoutTrailingSeparator()
    {
        var root = NewRoot();
        var policy = new PathAccessPolicy(new[] { root + Path.DirectorySeparatorChar });

        Assert.Equal(Path.GetFullPath(root), policy.Roots[0]);
    }

    [Fact]
    public void Roots_DedupedCaseInsensitively()
    {
        var root = NewRoot();
        var policy = new PathAccessPolicy(new[] { root, root.ToUpperInvariant() });

        Assert.Single(policy.Roots);
    }

    [Fact]
    public void IsAllowed_RequiresTrueDescendant_NotSiblingPrefix()
    {
        var root = NewRoot();
        var policy = new PathAccessPolicy(new[] { root });

        Assert.True(policy.IsAllowed(root));
        Assert.True(policy.IsAllowed(Path.Combine(root, "sub", "file.bin")));

        Assert.False(policy.IsAllowed(root + "-evil"));
        Assert.False(policy.IsAllowed(Path.Combine(root, "..", "escape")));
        Assert.False(policy.IsAllowed(Path.GetTempPath()));
    }

    [Fact]
    public void EvidencePath_AllowsServerEvidenceDir_ButPlainAllowedDoesNot()
    {
        var root = NewRoot();
        var policy = new PathAccessPolicy(new[] { root });
        var inEvidence = Path.Combine(policy.EvidenceDir, "bundle.zip");

        Assert.True(policy.IsEvidencePath(inEvidence));
        Assert.False(policy.IsAllowed(inEvidence));

        Assert.True(policy.IsEvidencePath(Path.Combine(root, "inside.zip")));
        Assert.False(policy.IsEvidencePath(Path.Combine(policy.EvidenceDir, "..", "escaped.zip")));
        Assert.False(policy.IsEvidencePath(null));
        Assert.Throws<UnauthorizedAccessException>(() => policy.EnsureEvidencePath(TempRoot("elsewhere.zip")));
    }

    [Fact]
    public void NoRoots_EvidenceDirStillAllowedForEvidence_ButNotForGeneralAccess()
    {
        var policy = new PathAccessPolicy(Array.Empty<string>());
        var inEvidence = Path.Combine(policy.EvidenceDir, "bundle.zip");

        Assert.True(policy.IsEvidencePath(inEvidence));
        Assert.False(policy.IsAllowed(inEvidence));
    }

    [Fact]
    public void Normalize_DropsUnusableRoot_ButKeepsValid()
    {
        var valid = NewRoot();
        var policy = new PathAccessPolicy(new[] { valid, "\0bad", "   " });

        Assert.Single(policy.Roots);
        Assert.Equal(Path.GetFullPath(valid), policy.Roots[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\evil")]
    [InlineData("bad:name")]
    [InlineData("star*")]
    [InlineData("q?")]
    [InlineData("pipe|")]
    [InlineData("tab\tname")]
    public void ValidateBundleName_RejectsUnsafeNames(string bad)
    {
        Assert.Throws<ArgumentException>(() => PathAccessPolicy.ValidateBundleName(bad));
    }

    [Fact]
    public void ValidateBundleName_RejectsOverLength()
    {
        var tooLong = new string('a', PathAccessPolicy.MaxBundleNameLength + 1);

        Assert.Throws<ArgumentException>(() => PathAccessPolicy.ValidateBundleName(tooLong));
    }

    [Fact]
    public void ValidateBundleName_AcceptsSafeName()
    {
        Assert.Equal("bundle_2026-10-01.v1", PathAccessPolicy.ValidateBundleName("bundle_2026-10-01.v1"));
    }
}
