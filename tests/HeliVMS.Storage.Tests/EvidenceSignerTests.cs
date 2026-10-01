using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Storage.Tests;

/// <summary>M53（§14.7 #5）：數位簽章器。</summary>
public class EvidenceSignerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SettingsRepository _settings;
    private readonly EvidenceSigner _signer;

    public EvidenceSignerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-signer-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _settings = new SettingsRepository(_store);
        _signer = new EvidenceSigner(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void EnsureKey_GeneratesPersistentKey_StoredEncrypted()
    {
        var pem = _signer.EnsureKey();
        Assert.Contains("PRIVATE KEY", pem);

        // M241：私鑰落庫必須是 DPAPI 保護後的密文，不是明文 PEM。
        var stored = _settings.Get(EvidenceSigner.SettingKey)!;
        Assert.DoesNotContain("PRIVATE KEY", stored);
        Assert.Equal(pem, SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void LegacyPlaintextKey_IsUpgradedToEncryptedOnFirstRead()
    {
        // 模擬舊版：設定值是明文 PEM。
        using var rsa = RSA.Create(2048);
        var legacyPem = rsa.ExportRSAPrivateKeyPem();
        var legacyFingerprint = EvidenceSigner.FingerprintOf(rsa);
        _settings.Set(EvidenceSigner.SettingKey, legacyPem);

        var pem = new EvidenceSigner(_store).EnsureKey();

        // 舊金鑰必須原樣沿用（不能因為升級就換掉，否則既有收據全毀）…
        Assert.Equal(legacyPem, pem);
        Assert.Equal(legacyFingerprint, new EvidenceSigner(_store).Fingerprint());
        // …而且設定值已被就地升級為密文。
        var stored = _settings.Get(EvidenceSigner.SettingKey)!;
        Assert.DoesNotContain("PRIVATE KEY", stored);
    }

    [Fact]
    public void EnsureKey_IsStableAcrossInstances()
    {
        var first = new EvidenceSigner(_store).EnsureKey();
        var second = new EvidenceSigner(_store).EnsureKey();
        Assert.Equal(first, second);
    }

    [Fact]
    public void SignVerify_RoundTrip()
    {
        const string content = "{\"format\":\"helivms-evidence-manifest\",\"created_at\":\"2026-01-01T00:00:00Z\"}";
        var signature = _signer.SignDocument(content);
        Assert.False(string.IsNullOrWhiteSpace(signature));
        Assert.True(_signer.VerifySignature(content, signature));
    }

    [Fact]
    public void Verify_Fails_WhenContentChanged()
    {
        const string content = "original-manifest-content";
        var signature = _signer.SignDocument(content);
        Assert.False(_signer.VerifySignature("original-manifest-content-mod", signature));
    }

    [Fact]
    public void Verify_Fails_WhenSignatureGarbled()
    {
        var signature = _signer.SignDocument("payload");
        var garbage = signature + "AAAA";
        Assert.False(_signer.VerifySignature("payload", garbage));
    }

    [Fact]
    public void Fingerprint_Is64HexChars()
    {
        var fingerprint = _signer.Fingerprint();
        Assert.Equal(64, fingerprint.Length);
        foreach (var c in fingerprint)
        {
            Assert.True(Uri.IsHexDigit(c));
        }
    }

    [Fact]
    public void Fingerprint_IsStable()
    {
        Assert.Equal(_signer.Fingerprint(), new EvidenceSigner(_store).Fingerprint());
    }

    [Fact]
    public void RotateKey_ChangesTheFingerprint_ButKeepsTheOldPublicKeyVerifiable()
    {
        var before = _signer.Fingerprint();
        var rotation = _signer.RotateKey("admin", new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(before, rotation.PreviousFingerprint);
        Assert.NotEqual(before, rotation.NewFingerprint);
        Assert.Equal(rotation.NewFingerprint, _signer.Fingerprint());

        var history = _signer.KeyHistory();
        Assert.Single(history);
        Assert.Equal(before, history[0].Fingerprint);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), history[0].RetiredUtc);
        Assert.Contains("PUBLIC KEY", history[0].PublicKeyPem);
    }

    [Fact]
    public void RotateKey_IsAudited_WithBothFingerprints()
    {
        var before = _signer.Fingerprint();
        _signer.RotateKey("operator-jane", new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));

        var entry = new AuditLogRepository(_store).List(new AuditLogQuery { Action = "evidence.signing_key.rotate" })
            .Single();
        Assert.Equal("operator-jane", entry.Actor);
        Assert.Equal(AuditCategories.Evidence, entry.Category);
        Assert.Contains(before, entry.Detail);
        Assert.Contains(_signer.Fingerprint(), entry.Detail);
    }

    [Fact]
    public void RotateKey_RequiresAnActor()
    {
        Assert.Throws<ArgumentException>(() => _signer.RotateKey("   "));
    }

    [Fact]
    public void OldSignaturesStayVerifiableAfterRotation_ViaHistoryPublicKey()
    {
        const string content = "{\"format\":\"helivms-evidence-manifest\"}";
        var oldSignature = _signer.SignDocument(content);
        var oldFingerprint = _signer.Fingerprint();

        _signer.RotateKey("admin");

        // 換發後舊金鑰已不能簽新東西…
        Assert.False(_signer.VerifySignature(content, oldSignature));
        // …但用保留的舊公鑰仍能驗證既有簽章，這才是換發不破壞既有證據的關鍵。
        var oldPem = _signer.PublicKeyPemFor(oldFingerprint);
        Assert.NotNull(oldPem);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(oldPem);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        Assert.True(rsa.VerifyHash(
            digest, Convert.FromBase64String(oldSignature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        // 同一份內容改用新金鑰重簽後，舊公鑰就不能再驗它了——否則歷史金鑰等於沒有被停用。
        var newSignature = _signer.SignDocument(content);
        Assert.False(rsa.VerifyHash(
            digest, Convert.FromBase64String(newSignature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void TrustedFingerprints_IncludeCurrentAndHistoricalKeys()
    {
        var first = _signer.Fingerprint();
        _signer.RotateKey("admin");
        var second = _signer.Fingerprint();
        _signer.RotateKey("admin");
        var third = _signer.Fingerprint();

        var trusted = _signer.TrustedFingerprints();
        Assert.Equal(3, trusted.Count);
        Assert.Contains(second, trusted);
        Assert.Contains(first, trusted);
        Assert.Contains(third, trusted);

        // 新→舊排序：現行金鑰永遠在最前面。
        Assert.Equal(third, trusted[0]);
    }

    [Fact]
    public void PublicKeyPemFor_UnknownFingerprint_ReturnsNull()
    {
        _signer.RotateKey("admin");
        Assert.Null(_signer.PublicKeyPemFor(new string('a', 64)));
    }

    [Fact]
    public void CorruptKeyHistory_DoesNotBreakSigning()
    {
        _signer.RotateKey("admin");
        _settings.Set(EvidenceSigner.HistoryKey, "{not json");

        // 歷史清單是輔助資料，損毀不該讓新的簽章機制停擺。
        Assert.Empty(_signer.KeyHistory());
        Assert.Single(_signer.TrustedFingerprints());
        Assert.False(string.IsNullOrWhiteSpace(_signer.SignDocument("payload")));
    }
}