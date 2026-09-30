using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.LicenseProducer;

namespace HeliVMS.LicenseProducer.Tests;

/// <summary>
/// 簽發端稽核軌跡（§19.6「登入與稽核：管理員操作全部留痕（誰簽了哪張）」）：
/// 每列以簽發私鑰簽章且以雜湊鏈串接，故竄改／刪除／重排皆可驗出。
/// </summary>
public class IssuanceAuditTrailTests : IDisposable
{
    // xunit 會為每個測試方法建立新實例，故簽發金鑰必須為 static 且不得於 Dispose 釋放。
    private static readonly RSA SigningKey = RSA.Create(2048);
    private static readonly RSA OtherKey = RSA.Create(2048);

    private readonly string _dir;
    private readonly string _path;

    public IssuanceAuditTrailTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"helivms-issue-audit-{Guid.NewGuid():N}");
        _path = Path.Combine(_dir, IssuanceAuditTrail.DefaultFileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Append_FirstEntry_Seq1AndEmptyPrev()
    {
        var entry = IssuanceAuditTrail.Append(_path, Draft(), SigningKey);

        Assert.Equal(1, entry.Seq);
        Assert.Equal(string.Empty, entry.Prev);
        Assert.NotEmpty(entry.Signature);
        Assert.Equal(IssuanceActions.Issue, entry.Action);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Append_CreatesParentDirectory()
    {
        var nested = Path.Combine(_dir, "sub", "deeper", "audit.jsonl");

        IssuanceAuditTrail.Append(nested, Draft(), SigningKey);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Append_IncrementsSeqAndChainsPrev()
    {
        var first = IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        var second = IssuanceAuditTrail.Append(_path, Draft(), SigningKey);

        Assert.Equal(2, second.Seq);
        Assert.NotEqual(string.Empty, second.Prev);
        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void Append_BlankOperator_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => IssuanceAuditTrail.Append(_path, Draft(operatorName: "  "), SigningKey));
    }

    [Fact]
    public void Read_RoundTripsAllFields()
    {
        var when = new DateTime(2026, 9, 30, 4, 5, 6, DateTimeKind.Utc);
        IssuanceAuditTrail.Append(
            _path,
            new IssuanceAuditDraft(
                IssuanceActions.Issue,
                "admin",
                LicenseId: "abc123",
                Tier: "進階版",
                Cameras: 32,
                Features: ["core", "ai", "gis"],
                Machine: "0123456789ABCDEF0123456789ABCDEF",
                ExpiresUtc: when,
                Issuer: "禾秝軟體開發團隊",
                TokenSha256: "tok-hash",
                PublicKeySha256: null),
            SigningKey);

        var entry = Assert.Single(IssuanceAuditTrail.Read(_path));

        Assert.Equal(1, entry.Seq);
        Assert.Equal(IssuanceActions.Issue, entry.Action);
        Assert.Equal("admin", entry.Operator);
        Assert.Equal("abc123", entry.LicenseId);
        Assert.Equal("進階版", entry.Tier);
        Assert.Equal(32, entry.Cameras);
        Assert.Equal(["core", "ai", "gis"], entry.Features);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", entry.Machine);
        Assert.Equal(when, entry.ExpiresUtc);
        Assert.Equal("禾秝軟體開發團隊", entry.Issuer);
        Assert.Equal("tok-hash", entry.TokenSha256);
        Assert.Null(entry.PublicKeySha256);
        Assert.NotEmpty(entry.Signature);
    }

    [Fact]
    public void Read_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(IssuanceAuditTrail.Read(Path.Combine(_dir, "nope.jsonl")));
    }

    [Fact]
    public void Read_NullFields_StayNull()
    {
        IssuanceAuditTrail.Append(
            _path,
            new IssuanceAuditDraft(IssuanceActions.GenerateKey, "vendor", PublicKeySha256: "pk-hash"),
            SigningKey);

        var entry = Assert.Single(IssuanceAuditTrail.Read(_path));

        Assert.Null(entry.LicenseId);
        Assert.Null(entry.Tier);
        Assert.Null(entry.Cameras);
        Assert.Null(entry.Features);
        Assert.Null(entry.Machine);
        Assert.Null(entry.ExpiresUtc);
        Assert.Null(entry.Issuer);
        Assert.Null(entry.TokenSha256);
        Assert.Equal("pk-hash", entry.PublicKeySha256);
    }

    [Fact]
    public void Verify_CleanTrail_ReturnsNoProblems()
    {
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);

        Assert.Empty(IssuanceAuditTrail.Verify(_path, SigningKey));
    }

    [Fact]
    public void Verify_MissingFile_ReportsProblem()
    {
        var problems = IssuanceAuditTrail.Verify(Path.Combine(_dir, "nope.jsonl"), SigningKey);

        Assert.Single(problems);
    }

    [Fact]
    public void Verify_EmptyFile_ReportsProblem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, string.Empty);

        var problems = IssuanceAuditTrail.Verify(_path, SigningKey);

        Assert.Contains(problems, p => p.Contains("空", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_SignedByOtherKey_Fails()
    {
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);

        var problems = IssuanceAuditTrail.Verify(_path, OtherKey);

        Assert.Contains(problems, p => p.Contains("簽章驗證失敗", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_TamperedOperator_Fails()
    {
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        var lines = ReadLines();
        var original = lines[0];
        lines[0] = original.Replace("\"operator\":\"admin\"", "\"operator\":\"intruder\"", StringComparison.Ordinal);
        Assert.NotEqual(original, lines[0]);
        WriteLines(lines);

        var problems = IssuanceAuditTrail.Verify(_path, SigningKey);

        Assert.Contains(problems, p => p.Contains("簽章驗證失敗", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_TamperedCameras_Fails()
    {
        IssuanceAuditTrail.Append(_path, Draft(cameras: 32), SigningKey);
        var lines = ReadLines();
        lines[0] = lines[0].Replace("\"cameras\":32", "\"cameras\":1024", StringComparison.Ordinal);
        WriteLines(lines);

        Assert.NotEmpty(IssuanceAuditTrail.Verify(_path, SigningKey));
    }

    [Fact]
    public void Verify_DeletedMiddleRow_BreaksChain()
    {
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        var lines = ReadLines();
        lines.RemoveAt(1);
        WriteLines(lines);

        var problems = IssuanceAuditTrail.Verify(_path, SigningKey);

        Assert.Contains(problems, p => p.Contains("序號不連續", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("雜湊鏈斷裂", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReorderedRows_BreaksChain()
    {
        IssuanceAuditTrail.Append(_path, Draft(cameras: 4), SigningKey);
        IssuanceAuditTrail.Append(_path, Draft(cameras: 8), SigningKey);
        var lines = ReadLines();
        (lines[0], lines[1]) = (lines[1], lines[0]);
        WriteLines(lines);

        Assert.NotEmpty(IssuanceAuditTrail.Verify(_path, SigningKey));
    }

    [Fact]
    public void Verify_TruncatedFile_StillDetectsTamperingOfRemainingRows()
    {
        for (var i = 0; i < 3; i++)
        {
            IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        }

        var lines = ReadLines().Take(2).ToList();
        lines[1] = lines[1].Replace("\"seq\":2", "\"seq\":9", StringComparison.Ordinal);
        WriteLines(lines);

        Assert.NotEmpty(IssuanceAuditTrail.Verify(_path, SigningKey));
    }

    [Fact]
    public void Verify_CorruptedJson_ReportsUnparsableRow()
    {
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        var lines = ReadLines();
        lines[0] = "{not json";
        WriteLines(lines);

        var problems = IssuanceAuditTrail.Verify(_path, SigningKey);

        Assert.Contains(problems, p => p.Contains("無法剖析", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_AppendedUnsignedForgedRow_Fails()
    {
        IssuanceAuditTrail.Append(_path, Draft(), SigningKey);
        var lines = ReadLines();
        lines.Add(lines[0].Replace("\"seq\":1", "\"seq\":2", StringComparison.Ordinal).Replace(
            ",\"signature\":\"", ",\"signature\":\"forged", StringComparison.Ordinal));
        WriteLines(lines);

        Assert.NotEmpty(IssuanceAuditTrail.Verify(_path, SigningKey));
    }

    [Fact]
    public void Canonicalize_UsesPlaceholderForEmptyValues()
    {
        var text = IssuanceAuditTrail.Canonicalize(
            1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "license.issue", "admin",
            null, null, null, null, null, null, null, null, null, string.Empty);

        Assert.Contains("licenseId=-\n", text, StringComparison.Ordinal);
        Assert.Contains("cameras=-\n", text, StringComparison.Ordinal);
        Assert.Contains("features=-\n", text, StringComparison.Ordinal);
        Assert.EndsWith("prev=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonicalize_SanitizesNewlines()
    {
        var text = IssuanceAuditTrail.Canonicalize(
            1, DateTime.UtcNow, "license.issue", "admin\r\nintruder",
            null, null, null, null, null, null, null, null, null, string.Empty);

        // 欄位值不得含換行，否則簽章涵蓋範圍會與行數不一致。
        Assert.DoesNotContain('\r', text);
        Assert.Contains("operator=admin  intruder\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonicalize_IsStableAcrossEquivalentTimestamps()
    {
        var utc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var local = utc.ToLocalTime();

        var a = IssuanceAuditTrail.Canonicalize(
            1, utc, "license.issue", "admin", null, null, null, null, null, null, null, null, null, "x");
        var b = IssuanceAuditTrail.Canonicalize(
            1, local, "license.issue", "admin", null, null, null, null, null, null, null, null, null, "x");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_IsStableAndBase64Url()
    {
        var data = "HELVMS"u8.ToArray();

        var a = IssuanceAuditTrail.Fingerprint(data);
        var b = IssuanceAuditTrail.Fingerprint(data);

        Assert.Equal(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.NotEqual(a, IssuanceAuditTrail.Fingerprint("HELVMS2"u8.ToArray()));
    }

    [Fact]
    public void Signature_CoversEveryFieldExceptItself()
    {
        var entry = IssuanceAuditTrail.Append(_path, Draft(), SigningKey);

        var tampered = entry with { Operator = "intruder" };
        var valid = SigningKey.VerifyData(
            System.Text.Encoding.UTF8.GetBytes(Canonical(tampered)),
            Base64Url.Decode(entry.Signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Assert.False(valid);
    }

    private static string Canonical(IssuanceAuditEntry entry)
        => IssuanceAuditTrail.Canonicalize(
            entry.Seq, entry.AtUtc, entry.Action, entry.Operator, entry.LicenseId, entry.Tier,
            entry.Cameras, entry.Features, entry.Machine, entry.ExpiresUtc, entry.Issuer,
            entry.TokenSha256, entry.PublicKeySha256, entry.Prev);

    private static IssuanceAuditDraft Draft(
        string operatorName = "admin",
        int? cameras = 32) => new(
            IssuanceActions.Issue,
            operatorName,
            LicenseId: "lic-1",
            Tier: "進階版",
            Cameras: cameras,
            Features: ["core", "ai"],
            Machine: "0123456789ABCDEF0123456789ABCDEF",
            ExpiresUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            Issuer: "禾秝軟體開發團隊",
            TokenSha256: "tok",
            PublicKeySha256: null);

    private List<string> ReadLines()
        => File.ReadAllLines(_path).Where(l => l.Length > 0).ToList();

    private void WriteLines(List<string> lines)
        => File.WriteAllText(_path, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
}
