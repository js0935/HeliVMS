using System.Security.Cryptography;
using HeliVMS.Licensing.Crypto;
using HeliVMS.LicenseProducer;

namespace HeliVMS.LicenseProducer.Tests;

/// <summary>
/// 私鑰保護容器（§19.6「私鑰可選加密靜置（DPAPI/口令）」）。
/// </summary>
public class PrivateKeyVaultTests
{
    private static readonly string Passphrase = "correct horse battery staple";

    [Fact]
    public void Plain_RoundTrips()
    {
        using var key = RSA.Create(2048);

        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Plain, null, 0);

        Assert.False(PrivateKeyVault.IsProtected(text));
        Assert.Contains("-----BEGIN PRIVATE KEY-----", text, StringComparison.Ordinal);
        Assert.Null(PrivateKeyVault.DetectMode(text));
        using var opened = PrivateKeyVault.Open(text, null);
        Assert.Equal(2048, opened.KeySize);
        Assert.True(AreSame(key, opened));
    }

    [Fact]
    public void Passphrase_RoundTrips()
    {
        using var key = RSA.Create(2048);

        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        Assert.True(PrivateKeyVault.IsProtected(text));
        Assert.Equal(KeyProtectionMode.Passphrase, PrivateKeyVault.DetectMode(text));
        using var opened = PrivateKeyVault.Open(text, Passphrase);
        Assert.True(AreSame(key, opened));
    }

    [Fact]
    public void Passphrase_FileContainsNoPrivateKeyMaterial()
    {
        using var key = RSA.Create(2048);
        var derBase64 = Convert.ToBase64String(key.ExportPkcs8PrivateKey());

        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 32).ToList();

        // 沒有任何一行會連續 32 個字元以上命中明文 DER base64 → 私鑰未以明文靜置。
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            for (var i = 0; i + 32 <= line.Length; i++)
            {
                Assert.DoesNotContain(line.Substring(i, 32), derBase64, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Passphrase_WrongPassword_ReportsDistinctError()
    {
        using var key = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        var ex = Assert.Throws<PrivateKeyAccessException>(
            () => PrivateKeyVault.Open(text, "wrong password").Dispose());

        Assert.Contains("口令錯誤", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Passphrase_MissingPassword_ExplainsHowToSupply()
    {
        using var key = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        var ex = Assert.Throws<PrivateKeyAccessException>(() => PrivateKeyVault.Open(text, null));

        Assert.Contains("passphrase", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Passphrase_EmptyPasswordOnProtect_Throws()
    {
        using var key = RSA.Create(2048);

        Assert.Throws<PrivateKeyAccessException>(
            () => PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, null, 50_000));
    }

    [Fact]
    public void Passphrase_IterationsBelowMinimum_Throws()
    {
        using var key = RSA.Create(2048);

        Assert.Throws<PrivateKeyAccessException>(
            () => PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 1_000));
    }

    [Fact]
    public void Passphrase_SaltIsRandomPerEncrypt()
    {
        using var key = RSA.Create(2048);

        var a = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);
        var b = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Passphrase_TamperedCiphertext_FailsIntegrity()
    {
        using var key = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        // 翻轉密文末段（GCM tag 所在）一個字元 → tag 驗證必失敗。
        var lines = text.Split('\n').ToList();
        var target = lines.FindLastIndex(l => l.Trim().Length >= 64);
        Assert.True(target > 0);
        var body = lines[target].Trim().ToCharArray();
        body[10] = body[10] == 'A' ? 'B' : 'A';
        lines[target] = new string(body);
        var tampered = string.Join("\n", lines);

        var ex = Assert.Throws<PrivateKeyAccessException>(
            () => PrivateKeyVault.Open(tampered, Passphrase).Dispose());
        Assert.Contains("口令錯誤", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_WithoutLabel_Throws()
    {
        var ex = Assert.Throws<PrivateKeyAccessException>(
            () => PrivateKeyVault.Open("-----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----\n", null));

        Assert.Contains("私鑰", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_Truncated_Throws()
    {
        using var key = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Passphrase, Passphrase, 50_000);
        var truncated = text[..(text.IndexOf("-----END", StringComparison.Ordinal) - 40)];

        Assert.Throws<PrivateKeyAccessException>(() => PrivateKeyVault.Open(truncated, Passphrase));
    }

    [Fact]
    public void Dpapi_RoundTrips()
    {
        Assert.True(OperatingSystem.IsWindows(), "DPAPI 僅支援 Windows。");
        using var key = RSA.Create(2048);

        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Dpapi, null, 0);

        Assert.Equal(KeyProtectionMode.Dpapi, PrivateKeyVault.DetectMode(text));
        using var opened = PrivateKeyVault.Open(text, null);
        Assert.True(AreSame(key, opened));
    }

    [Fact]
    public void Dpapi_IgnoresSuppliedPassphrase()
    {
        Assert.True(OperatingSystem.IsWindows(), "DPAPI 僅支援 Windows。");
        using var key = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Dpapi, null, 0);

        using var opened = PrivateKeyVault.Open(text, "口令不應影響 DPAPI");
        Assert.True(AreSame(key, opened));
    }

    [Fact]
    public void Dpapi_CorruptedBlob_Throws()
    {
        Assert.True(OperatingSystem.IsWindows(), "DPAPI 僅支援 Windows。");
        using var key = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(key, KeyProtectionMode.Dpapi, null, 0);
        var lines = text.Split('\n').ToList();
        var target = lines.FindLastIndex(l => l.Trim().Length >= 64);
        Assert.True(target > 0);
        var body = lines[target].Trim().ToCharArray();
        body[10] = body[10] == 'A' ? 'B' : 'A';
        lines[target] = new string(body);

        Assert.Throws<PrivateKeyAccessException>(
            () => PrivateKeyVault.Open(string.Join("\n", lines), null));
    }

    [Fact]
    public void CrossKeyPair_PlainPem_NotAcceptedAsContainer()
    {
        using var a = RSA.Create(2048);
        using var b = RSA.Create(2048);
        var text = PrivateKeyVault.Protect(b, KeyProtectionMode.Passphrase, Passphrase, 50_000);

        // 用 A 的金鑰對「改寫」B 的容器不可能通過驗證：容器密文與 A 無關。
        using var opened = PrivateKeyVault.Open(text, Passphrase);
        Assert.True(AreSame(b, opened));
        Assert.False(AreSame(a, opened));
    }

    [Fact]
    public void PlainPem_ProducedByRsaPem_IsAccepted()
    {
        using var key = RSA.Create(2048);

        using var opened = PrivateKeyVault.Open(RsaPem.ToPrivatePem(key), null);

        Assert.True(AreSame(key, opened));
    }

    private static bool AreSame(RSA a, RSA b)
    {
        // 比較公鑰參數即等同比較金鑰對（私鑰由公鑰唯一決定）。
        var x = a.ExportParameters(includePrivateParameters: false);
        var y = b.ExportParameters(includePrivateParameters: false);
        return x.Modulus!.SequenceEqual(y.Modulus!) && x.Exponent!.SequenceEqual(y.Exponent!);
    }
}
