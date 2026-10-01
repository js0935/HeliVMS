using System.Security.Cryptography;
using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// 匯出簽章收據（§14.3(2) 匯出即驗證；M240）。
/// 匯出完成後在檔案旁寫出 <c>&lt;匯出檔&gt;.receipt.json</c>，內含匯出檔的 SHA-256、來源時間窗與 RSA 簽章。
/// </summary>
/// <remarks>
/// 為什麼需要簽章：原本只寫 <c>.sha256</c> 文字檔，任何人都能在改完影片後重算一份看起來一樣的雜湊，
/// 那只能證明「檔案沒變」，不能證明「檔案來自這套系統」。簽章才是後者的證據。
/// 驗證一律走 <see cref="ExportReceiptCodec"/>（只依賴 BCL），所以第三方能離線驗證。
/// </remarks>
public sealed class ExportReceiptService
{
    private readonly EvidenceSigner _signer;

    public ExportReceiptService(SqliteStore store)
    {
        _signer = new EvidenceSigner(store);
    }

    /// <summary>
    /// 為匯出檔建立簽章收據，回傳收據路徑。
    /// <paramref name="sha256"/> 由匯出端算好傳入，避免整支影片再讀一次。
    /// </summary>
    public string Write(
        string clipPath,
        string sha256,
        long sizeBytes,
        double durationSeconds,
        int channelId,
        string stream,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        DateTime issuedAtUtc)
    {
        var payload = new ExportReceiptPayload(
            Path.GetFileName(clipPath),
            sha256,
            sizeBytes,
            durationSeconds,
            channelId,
            stream,
            ExportReceiptCodec.IsoUtc(rangeStartUtc),
            ExportReceiptCodec.IsoUtc(rangeEndUtc),
            ExportReceiptCodec.IsoUtc(issuedAtUtc));

        var pem = _signer.PublicKeyPem();
        var doc = ExportReceiptCodec.Compose(
            payload,
            _signer.Fingerprint(),
            pem,
            ToBase64Url(_signer.SignDocument(ExportReceiptCodec.Canonical(payload))));

        var path = ExportReceiptCodec.ReceiptPath(clipPath);
        File.WriteAllText(path, ExportReceiptCodec.Serialize(doc));
        return path;
    }

    /// <summary>
    /// <see cref="EvidenceSigner.SignDocument"/> 沿用標準 base64（證據清單沿用既有格式），
    /// 匯出收據改存 base64url：簽章欄位因此不會被 JSON 編碼器轉義成 <c>\/</c>。
    /// </summary>
    private static string ToBase64Url(string standardBase64)
        => ExportReceiptCodec.EncodeSignature(
            ExportReceiptCodec.TryDecodeSignature(standardBase64) ?? Array.Empty<byte>());

    /// <summary>目前簽署金鑰的指紋（寫入收據供外部比對）。</summary>
    public string SignerFingerprint() => _signer.Fingerprint();

    /// <summary>目前簽署金鑰的公鑰 PEM。</summary>
    public string SignerPublicKeyPem() => _signer.PublicKeyPem();

    /// <summary>全部仍被信任的簽署者指紋（現行＋M241 換發後保留的歷史金鑰）。</summary>
    public TrustedSignerSet TrustedSigners() => new(_signer.TrustedFingerprints());

    /// <summary>離線驗證匯出檔，信任單一指紋（見 <c>ExportReceiptCodec.Verify</c>）。</summary>
    public ExportReceiptReport Verify(string clipPath, string? expectedSigner)
        => ExportReceiptCodec.Verify(clipPath, expectedSigner);

    /// <summary>以本機信任的整組指紋驗證，涵蓋換發前的歷史收據（M241 正式用法）。</summary>
    public ExportReceiptReport Verify(string clipPath, TrustedSignerSet? trusted = null)
        => ExportReceiptCodec.Verify(clipPath, trusted ?? TrustedSigners());

    /// <summary>換發簽章金鑰（M241）：新收據改用新金鑰，舊公鑰保留供驗證歷史收據，並寫入稽核日誌。</summary>
    public SigningKeyRotation RotateSigningKey(string actor, DateTime? nowUtc = null)
        => _signer.RotateKey(actor, nowUtc);

    /// <summary>歷史簽章金鑰（已換發，公鑰仍可驗證舊收據）。</summary>
    public IReadOnlyList<SigningKeyRecord> SigningKeyHistory() => _signer.KeyHistory();
}