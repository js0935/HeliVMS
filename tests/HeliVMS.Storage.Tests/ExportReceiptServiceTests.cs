using System.IO;
using System.Security.Cryptography;
using System.Text;
using HeliVMS.Shared.Models;

namespace HeliVMS.Storage.Tests;

/// <summary>M240（§14.3(2)）：匯出簽章收據——簽署、竄改偵測與離線驗證。</summary>
public class ExportReceiptServiceTests : IDisposable
{
    private const string ClipBody = "fake-mp4-bytes-for-signing-test";

    private readonly string _dbPath;
    private readonly string _dir;
    private readonly SqliteStore _store;
    private readonly ExportReceiptService _receipts;

    public ExportReceiptServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-receipt-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _receipts = new ExportReceiptService(_store);
        _dir = Path.Combine(Path.GetTempPath(), $"helivms-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string NewClip()
    {
        var path = Path.Combine(_dir, "evidence.mp4");
        File.WriteAllText(path, ClipBody);
        return path;
    }

    private string WriteReceipt(string clipPath, string? sha = null, DateTime? issued = null)
    {
        sha ??= ExportReceiptCodec.ComputeSha256(clipPath)!;
        return _receipts.Write(
            clipPath,
            sha,
            new FileInfo(clipPath).Length,
            12.5,
            channelId: 3,
            stream: "main",
            rangeStartUtc: new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
            rangeEndUtc: new DateTime(2026, 9, 30, 12, 15, 0, DateTimeKind.Utc),
            issuedAtUtc: issued ?? new DateTime(2026, 9, 30, 12, 20, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Write_PlacesReceiptNextToClip_AndVerificationPasses()
    {
        var clip = NewClip();
        var receipt = WriteReceipt(clip);

        Assert.Equal(clip + ".receipt.json", receipt);
        Assert.True(File.Exists(receipt));

        var report = _receipts.Verify(clip, _receipts.SignerFingerprint());
        Assert.True(report.Valid);
        Assert.True(report.HashMatches);
        Assert.True(report.SignatureValid);
        Assert.True(report.SignerMatched);
        Assert.False(report.SelfAssertedKey);
    }

    [Fact]
    public void Receipt_WithoutExpectedSigner_IsReportedAsSelfAsserted()
    {
        var clip = NewClip();
        WriteReceipt(clip);

        var report = _receipts.Verify(clip, expectedSigner: null);
        Assert.True(report.Valid);
        Assert.True(report.SelfAssertedKey);
        Assert.Contains("未經外部比對", report.Detail);
    }

    [Fact]
    public void TamperedClip_FailsHashAndOverallVerdict()
    {
        var clip = NewClip();
        WriteReceipt(clip);
        File.WriteAllText(clip, ClipBody + "-tampered");

        var report = _receipts.Verify(clip, _receipts.SignerFingerprint());
        Assert.False(report.HashMatches);
        Assert.False(report.Valid);
        Assert.Contains("雜湊", report.Detail);
    }

    [Fact]
    public void TamperedReceiptField_FailsSignature()
    {
        var clip = NewClip();
        var receipt = WriteReceipt(clip);
        var text = File.ReadAllText(receipt).Replace("\"channel_id\": 3", "\"channel_id\": 4");
        File.WriteAllText(receipt, text);

        var report = _receipts.Verify(clip, expectedSigner: null);
        Assert.False(report.SignatureValid);
        Assert.False(report.Valid);
    }

    [Fact]
    public void TamperedSignature_FailsVerification()
    {
        var clip = NewClip();
        var receipt = WriteReceipt(clip);
        var text = File.ReadAllText(receipt);
        var doc = ExportReceiptCodec.TryParse(text)!;
        var flipped = (doc.Signature[0] == 'A' ? 'B' : 'A') + doc.Signature[1..];
        Assert.NotEqual(doc.Signature, flipped);
        File.WriteAllText(receipt, text.Replace(doc.Signature, flipped));

        var reread = ExportReceiptCodec.TryParse(File.ReadAllText(receipt))!;
        Assert.Equal(flipped, reread.Signature);
        Assert.False(ExportReceiptCodec.VerifySignature(reread));
    }

    [Fact]
    public void SignatureIsBase64Url_SoItSurvivesJsonEscapingAndCopyPaste()
    {
        var clip = NewClip();
        var receipt = WriteReceipt(clip);
        var text = File.ReadAllText(receipt);
        var signature = ExportReceiptCodec.TryParse(text)!.Signature;

        // JSON 預設編碼器會把 '/' 轉義成 '\/'；簽章若用標準 base64，檔案裡就會出現不可直接複製的字串。
        Assert.DoesNotContain('\\', signature);
        Assert.DoesNotContain('/', signature);
        Assert.DoesNotContain('+', signature);
        Assert.Contains(signature, text);
    }

    [Fact]
    public void ReceiptSignedByAnotherKey_IsOnlyRejectedWhenFingerprintsAreCompared()
    {
        var clip = NewClip();
        WriteReceipt(clip);
        var receipt = clip + ".receipt.json";

        // 收據自帶公鑰，所以「這把金鑰簽的」永遠成立——這也正是金鑰身分必須外部比對的原因。
        var payload = new ExportReceiptPayload("evidence.mp4", ExportReceiptCodec.ComputeSha256(clip)!, 1, 1, 1, "main",
            "2026-01-01T00:00:00.000Z", "2026-01-01T00:00:01.000Z", "2026-01-01T00:00:01.000Z");
        using var rsa = RSA.Create(2048);
        var foreign = ExportReceiptCodec.Compose(
            payload,
            ExportReceiptCodec.Fingerprint(rsa.ExportSubjectPublicKeyInfoPem()),
            rsa.ExportSubjectPublicKeyInfoPem(),
            ExportReceiptCodec.EncodeSignature(rsa.SignHash(
                SHA256.HashData(Encoding.UTF8.GetBytes(ExportReceiptCodec.Canonical(payload))),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)));
        File.WriteAllText(receipt, ExportReceiptCodec.Serialize(foreign));

        var selfAsserted = _receipts.Verify(clip, expectedSigner: null);
        Assert.True(selfAsserted.SignatureValid);
        Assert.True(selfAsserted.SelfAssertedKey);

        var compared = _receipts.Verify(clip, _receipts.SignerFingerprint());
        Assert.False(compared.SignerMatched);
        Assert.False(compared.Valid);
    }

    [Fact]
    public void KeyStatus_ExposesOnlyPublicMaterial()
    {
        // 遠端要拿得到公鑰（那是給驗證方的），但私鑰絕不能離開本機。
        var pem = _receipts.SignerPublicKeyPem();
        Assert.Contains("PUBLIC KEY", pem);
        Assert.DoesNotContain("PRIVATE KEY", pem);
        Assert.Equal(64, _receipts.SignerFingerprint().Length);

        _receipts.RotateSigningKey("admin");
        var history = _receipts.SigningKeyHistory();
        Assert.Single(history);
        Assert.Contains("PUBLIC KEY", history[0].PublicKeyPem);
    }

    [Fact]
    public void ReceiptsFromBeforeRotation_StayVerifiableViaRetainedPublicKeys()
    {
        // M241 的核心保證：換發金鑰不能讓已交付出去的收據變成「無法驗證」。
        var oldClip = NewClip();
        WriteReceipt(oldClip);
        var oldFingerprint = _receipts.SignerFingerprint();

        _receipts.RotateSigningKey("admin");
        var newFingerprint = _receipts.SignerFingerprint();
        Assert.NotEqual(oldFingerprint, newFingerprint);

        var newClip = Path.Combine(_dir, "new.mp4");
        File.WriteAllText(newClip, ClipBody);
        WriteReceipt(newClip, issued: DateTime.UtcNow);

        // 只認現行金鑰 → 舊收據被判無效（這就是 M241 要修的陷阱）。
        var strict = _receipts.Verify(oldClip, new TrustedSignerSet(new[] { newFingerprint }));
        Assert.False(strict.SignerMatched);
        Assert.False(strict.Valid);

        // 信任現行＋歷史 → 舊收據與新收據都有效。
        var trusted = _receipts.TrustedSigners();
        Assert.Equal(2, trusted.Fingerprints.Count);
        Assert.True(_receipts.Verify(oldClip, trusted).Valid);
        Assert.True(_receipts.Verify(newClip, trusted).Valid);
        Assert.False(_receipts.Verify(oldClip, trusted).SelfAssertedKey);

        // 預設驗證就走信任清單，呼叫端不必自己拼。
        Assert.True(_receipts.Verify(oldClip).Valid);
    }

    [Fact]
    public void RotateSigningKey_IsRecordedInTheAuditLog()
    {
        var before = _receipts.SignerFingerprint();
        _receipts.RotateSigningKey("operator-jane");

        var entry = new AuditLogRepository(_store).List(new AuditLogQuery { Action = "evidence.signing_key.rotate" })
            .Single();
        Assert.Equal("operator-jane", entry.Actor);
        Assert.Contains(before, entry.Detail);
        Assert.Contains(_receipts.SignerFingerprint(), entry.Detail);
    }

    [Fact]
    public void UnexpectedSignerFingerprint_IsNotTrusted()
    {
        var clip = NewClip();
        WriteReceipt(clip);

        var report = _receipts.Verify(clip, new string('a', 64));
        Assert.True(report.SignatureValid);
        Assert.False(report.SignerMatched);
        Assert.False(report.Valid);
        Assert.Contains("不在信任清單", report.Detail);
    }

    [Fact]
    public void MissingClip_OrMissingReceipt_FailsClosedWithoutThrowing()
    {
        var missingClip = Path.Combine(_dir, "nope.mp4");
        var noReceipt = _receipts.Verify(missingClip, expectedSigner: null);
        Assert.False(noReceipt.Valid);
        Assert.Contains("匯出檔不存在", noReceipt.Detail);

        var clip = NewClip();
        var orphan = _receipts.Verify(clip, expectedSigner: null);
        Assert.False(orphan.Valid);
        Assert.Contains("找不到簽章收據", orphan.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"something-else\",\"file\":\"a.mp4\",\"sha256\":\"x\",\"signature\":\"y\",\"public_key_pem\":\"z\"}")]
    [InlineData("{\"format\":\"helivms-export-receipt-v1\"}")]
    public void UnusableReceiptContent_FailsClosed(string json)
    {
        var clip = NewClip();
        File.WriteAllText(clip + ".receipt.json", json);

        var report = _receipts.Verify(clip, expectedSigner: null);
        Assert.False(report.Valid);
        Assert.False(report.SignatureValid);
    }

    [Fact]
    public void ReceiptWithBrokenPem_FailsClosedInsteadOfThrowing()
    {
        var clip = NewClip();
        var receipt = WriteReceipt(clip);
        File.WriteAllText(receipt, File.ReadAllText(receipt).Replace(
            "-----BEGIN PUBLIC KEY-----",
            "-----BEGIN NOT A KEY-----"));

        var report = _receipts.Verify(clip, expectedSigner: null);
        Assert.False(report.SignatureValid);
        Assert.False(report.Valid);
    }

    [Fact]
    public void CanonicalForm_IsStableAndCultureIndependent()
    {
        var payload = new ExportReceiptPayload("evidence.mp4", "AB", 10, 1.5, 7, "main",
            "2026-09-30T12:00:00.000Z", "2026-09-30T12:00:15.000Z", "2026-09-30T12:20:00.000Z");

        var first = ExportReceiptCodec.Canonical(payload);
        Assert.Equal(first, ExportReceiptCodec.Canonical(payload with { }));
        Assert.Contains("\"channel_id\":7", first);
        Assert.Contains("\"duration_seconds\":1.5", first);
        Assert.DoesNotContain("signature", first);
        Assert.DoesNotContain("signer", first);
    }

    [Fact]
    public void RoundTripThroughJson_KeepsEverySignedField()
    {
        var payload = new ExportReceiptPayload("evidence.mp4", "abc", 1234, 9.75, 2, "sub",
            "2026-09-30T12:00:00.000Z", "2026-09-30T12:00:09.750Z", "2026-09-30T12:00:10.000Z");
        var json = ExportReceiptCodec.Serialize(
            ExportReceiptCodec.Compose(payload, "fingerprint", "pem", "signature"));

        var parsed = ExportReceiptCodec.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Equal(ExportReceiptCodec.Canonical(payload), ExportReceiptCodec.Canonical(parsed.Payload));
    }

    [Fact]
    public void IsoUtc_NormalisesToUtcWithFixedPrecision()
    {
        var local = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Local);
        Assert.Equal(ExportReceiptCodec.IsoUtc(local.ToUniversalTime()), ExportReceiptCodec.IsoUtc(local));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", ExportReceiptCodec.IsoUtc(local));
    }

    [Fact]
    public void SignerKey_IsStableAcrossServiceInstances()
    {
        var other = new ExportReceiptService(_store);
        Assert.Equal(_receipts.SignerFingerprint(), other.SignerFingerprint());
        Assert.Equal(_receipts.SignerPublicKeyPem(), other.SignerPublicKeyPem());
    }

    [Fact]
    public void ReceiptFingerprint_MatchesTheEvidenceSignerForTheSameKey()
    {
        // 同一把金鑰簽出的證據清單與匯出收據必須顯示同一個指紋，否則作業員無從比對。
        var evidence = new EvidenceSigner(_store);
        Assert.Equal(evidence.Fingerprint(), _receipts.SignerFingerprint());
        Assert.Equal(_receipts.SignerFingerprint(), ExportReceiptCodec.Fingerprint(_receipts.SignerPublicKeyPem()));

        var clip = NewClip();
        WriteReceipt(clip);
        Assert.Equal(evidence.Fingerprint(), _receipts.Verify(clip, expectedSigner: null).Signer);
    }
}