using System.IO;

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
    public void EnsureKey_GeneratesPersistentKey_InAppSettings()
    {
        var pem = _signer.EnsureKey();
        Assert.Contains("PRIVATE KEY", pem);
        Assert.Equal(pem, _settings.Get(EvidenceSigner.SettingKey));
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
}