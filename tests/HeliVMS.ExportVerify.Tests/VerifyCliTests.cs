using System.IO;
using System.Security.Cryptography;
using System.Text;
using HeliVMS.Shared.Models;
using HeliVmsVerify;

namespace HeliVMS.ExportVerify.Tests;

/// <summary>
/// M240：離線驗證工具 HeliVmsVerify。
/// 這支工具存在的唯一理由是「匯出檔離開了這台 VMS」——所以它的行為必須完全由收據與檔案決定，
/// 不能依賴任何主機狀態；退出碼要能直接用在腳本或 CI 裡。
/// </summary>
public sealed class VerifyCliTests : IDisposable
{
    private const string ClipBody = "clip-bytes";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"helivms-verifycli-{Guid.NewGuid():N}");
    private readonly RSA _key = RSA.Create(2048);

    public VerifyCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string NewClip(string body = ClipBody)
    {
        var path = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(path, body);
        return path;
    }

    private string WriteReceipt(string clipPath, string? signerPem = null)
    {
        var pem = signerPem ?? _key.ExportSubjectPublicKeyInfoPem();
        var payload = new ExportReceiptPayload(
            Path.GetFileName(clipPath),
            ExportReceiptCodec.ComputeSha256(clipPath)!,
            new FileInfo(clipPath).Length,
            1,
            1,
            "main",
            "2026-09-30T12:00:00.000Z",
            "2026-09-30T12:00:01.000Z",
            "2026-09-30T12:00:01.000Z");
        var doc = ExportReceiptCodec.Compose(
            payload,
            ExportReceiptCodec.Fingerprint(pem),
            pem,
            ExportReceiptCodec.EncodeSignature(_key.SignHash(
                SHA256.HashData(Encoding.UTF8.GetBytes(ExportReceiptCodec.Canonical(payload))),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)));
        var path = ExportReceiptCodec.ReceiptPath(clipPath);
        File.WriteAllText(path, ExportReceiptCodec.Serialize(doc));
        return path;
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = VerifyCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void ValidReceipt_ExitsZero_AndSaysTheKeyWasCompared()
    {
        var clip = NewClip();
        WriteReceipt(clip);
        var fingerprint = ExportReceiptCodec.Fingerprint(_key.ExportSubjectPublicKeyInfoPem());

        var (code, output, _) = Run(clip, "--signer", fingerprint);

        Assert.Equal(0, code);
        Assert.Contains("雜湊相符：是", output);
        Assert.Contains("簽章有效：是", output);
        Assert.Contains("金鑰已比對：是", output);
        Assert.Contains("結論　：有效", output);
    }

    [Fact]
    public void ValidReceiptWithoutSigner_StillExitsZero_ButWarnsTheKeyIsSelfAsserted()
    {
        var clip = NewClip();
        WriteReceipt(clip);

        var (code, output, _) = Run(clip);

        Assert.Equal(0, code);
        Assert.Contains("金鑰已比對：否（金鑰由收據自行宣稱）", output);
    }

    [Fact]
    public void TamperedClip_ExitsOne()
    {
        var clip = NewClip();
        WriteReceipt(clip);
        File.WriteAllText(clip, ClipBody + "-tampered");

        var (code, output, _) = Run(clip);

        Assert.Equal(1, code);
        Assert.Contains("雜湊相符：否", output);
        Assert.Contains("結論　：無效", output);
    }

    [Fact]
    public void MismatchedSigner_ExitsOne()
    {
        var clip = NewClip();
        WriteReceipt(clip);

        var (code, output, _) = Run(clip, "--signer", new string('b', 64));

        Assert.Equal(1, code);
        Assert.Contains("結論　：無效", output);
    }

    [Fact]
    public void MissingFile_ExitsOneWithoutThrowing()
    {
        var (code, output, _) = Run(Path.Combine(_dir, "absent.mp4"));

        Assert.Equal(1, code);
        Assert.Contains("檔案存在：否", output);
    }

    [Fact]
    public void NoArguments_PrintsUsageToStderr_AndExitsTwo()
    {
        var (code, _, err) = Run();

        Assert.Equal(2, code);
        Assert.Contains("用法", err);
    }

    [Fact]
    public void Help_PrintsUsageToStdout_AndExitsZero()
    {
        var (code, output, _) = Run("--help");

        Assert.Equal(0, code);
        Assert.Contains("用法", output);
    }

    [Fact]
    public void SignerWithoutValue_IsAUsageError()
    {
        var (code, _, err) = Run(NewClip(), "--signer");

        Assert.Equal(2, code);
        Assert.Contains("--signer 需要參數", err);
    }

    [Fact]
    public void UnknownArgument_IsAUsageError()
    {
        var (code, _, err) = Run(NewClip(), "--insecure");

        Assert.Equal(2, code);
        Assert.Contains("不明的參數", err);
    }
}