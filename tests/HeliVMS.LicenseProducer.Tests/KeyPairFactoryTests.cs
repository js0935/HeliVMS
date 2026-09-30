using System.Security.Cryptography;
using HeliVMS.Licensing.Crypto;
using HeliVMS.LicenseProducer;

namespace HeliVMS.LicenseProducer.Tests;

/// <summary>金鑰對產生（§19.6「金鑰對產生（2048/3072/4096 可選）→ 產出 public.key 與受保護私鑰」）。</summary>
public class KeyPairFactoryTests : IDisposable
{
    private const string Passphrase = "vendor master passphrase";

    private readonly string _dir;

    public KeyPairFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"helivms-keygen-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(3072)]
    public void Generate_SupportedKeySize_ProducesUsablePair(int keySize)
    {
        var pair = KeyPairFactory.Generate(keySize, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        Assert.Equal(keySize, pair.KeySizeBits);
        Assert.Equal(KeyProtectionMode.Passphrase, pair.Mode);
        using var opened = PrivateKeyVault.Open(pair.PrivateKeyText, Passphrase);
        using var expected = RsaPem.ParsePublicKey(pair.PublicKeyPem);
        Assert.Equal(keySize, opened.KeySize);
        Assert.True(SamePublicKey(opened, expected));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2047)]
    [InlineData(8192)]
    public void Generate_UnsupportedKeySize_Throws(int keySize)
    {
        var ex = Assert.Throws<PrivateKeyAccessException>(
            () => KeyPairFactory.Generate(keySize, KeyProtectionMode.Passphrase, Passphrase, 50_000));

        Assert.Contains("3072", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_PublicPem_IsLoadableAndFingerprinted()
    {
        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        using var pub = RsaPem.ParsePublicKey(pair.PublicKeyPem);
        var expected = IssuanceAuditTrail.Fingerprint(pub.ExportSubjectPublicKeyInfo());
        Assert.Equal(expected, pair.PublicKeySha256);
    }

    [Fact]
    public void Generate_Snippet_EmbedsPemForEmbeddedPublicKey()
    {
        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        Assert.Contains("public const string Value =", pair.PublicKeySnippet, StringComparison.Ordinal);
        Assert.Contains("\"\"\"", pair.PublicKeySnippet, StringComparison.Ordinal);
        Assert.Contains("    -----BEGIN PUBLIC KEY-----", pair.PublicKeySnippet + "\n", StringComparison.Ordinal);
        Assert.Contains("    -----END PUBLIC KEY-----", pair.PublicKeySnippet + "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Dpapi_ProducesContainerNoPassphrase()
    {
        Assert.True(OperatingSystem.IsWindows(), "DPAPI 僅支援 Windows。");

        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Dpapi, null, 0);

        Assert.Equal(KeyProtectionMode.Dpapi, pair.Mode);
        using var opened = PrivateKeyVault.Open(pair.PrivateKeyText, null);
        Assert.Equal(2048, opened.KeySize);
    }

    [Fact]
    public void Generate_DistinctKeysPerInvocation()
    {
        var a = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);
        var b = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        Assert.NotEqual(a.PublicKeySha256, b.PublicKeySha256);
    }

    [Fact]
    public void WriteTo_WritesPrivatePublicAndSnippet()
    {
        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        KeyPairFactory.WriteTo(pair, _dir);

        Assert.True(File.Exists(Path.Combine(_dir, KeyPairFactory.PrivateFileName)));
        Assert.True(File.Exists(Path.Combine(_dir, KeyPairFactory.PublicFileName)));
        Assert.True(File.Exists(Path.Combine(_dir, KeyPairFactory.SnippetFileName)));
        Assert.Equal(pair.PublicKeyPem, File.ReadAllText(Path.Combine(_dir, KeyPairFactory.PublicFileName)));
    }

    [Fact]
    public void WriteTo_RefusesToOverwriteExistingPrivateKey()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, KeyPairFactory.PrivateFileName), "既有私鑰");
        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        var ex = Assert.Throws<PrivateKeyAccessException>(() => KeyPairFactory.WriteTo(pair, _dir));

        Assert.Contains("誤覆", ex.Message, StringComparison.Ordinal);
        Assert.Equal("既有私鑰", File.ReadAllText(Path.Combine(_dir, KeyPairFactory.PrivateFileName)));
    }

    [Fact]
    public void WriteTo_CreatesDirectory()
    {
        var nested = Path.Combine(_dir, "prod");
        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        KeyPairFactory.WriteTo(pair, nested);

        Assert.True(File.Exists(Path.Combine(nested, KeyPairFactory.PrivateFileName)));
    }

    [Fact]
    public void GeneratedPair_CanIssueLicenseVerifiableByEmbeddedStylePublicKey()
    {
        // 產生的公鑰片段貼入產品端後即可驗簽；此處以直接匯出的公鑰驗證端到端。
        var pair = KeyPairFactory.Generate(2048, KeyProtectionMode.Passphrase, Passphrase, 50_000);
        using var privateKey = PrivateKeyVault.Open(pair.PrivateKeyText, Passphrase);
        var payload = new HeliVMS.Licensing.LicensePayload
        {
            Id = "test-license",
            Machine = "0123456789ABCDEF0123456789ABCDEF",
            IssuedUtc = DateTime.UtcNow,
            Cameras = 32,
            Features = ["core", "ai"],
        };

        var token = HeliVMS.Licensing.LicenseSerializer.Sign(payload, privateKey);
        using var publicKey = RsaPem.ParsePublicKey(pair.PublicKeyPem);
        var ok = HeliVMS.Licensing.LicenseSerializer.TryVerify(token, publicKey, out var loaded, out _);

        Assert.True(ok);
        Assert.Equal("test-license", loaded!.Id);
    }

    private static bool SamePublicKey(RSA a, RSA b)
    {
        var x = a.ExportParameters(includePrivateParameters: false);
        var y = b.ExportParameters(includePrivateParameters: false);
        return x.Modulus!.SequenceEqual(y.Modulus!) && x.Exponent!.SequenceEqual(y.Exponent!);
    }
}
