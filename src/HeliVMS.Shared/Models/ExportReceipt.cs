using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeliVMS.Shared.Models;

/// <summary>匯出簽章收據的被簽屬內容（§14.3(2) 匯出即驗證）。</summary>
/// <remarks>
/// 只有這些欄位會被簽署；<c>signer</c>／<c>public_key_pem</c>／<c>signature</c> 隨收據一併輸出但不含在簽章範圍內——
/// 金鑰指紋本身可由公鑰推導，把它簽進去不會增加任何保證，卻會讓「換掉整組金鑰」這種攻擊看起來像合法重簽。
/// 真要確認簽署者身分，須由外部（§11.5 稽核或當事人）提供預期指紋比對。
/// </remarks>
public sealed record ExportReceiptPayload(
    string FileName,
    string Sha256,
    long SizeBytes,
    double DurationSeconds,
    int ChannelId,
    string Stream,
    string RangeStartUtc,
    string RangeEndUtc,
    string IssuedAtUtc);

/// <summary>匯出簽章收據的完整內容（輸出到 <c>&lt;匯出檔&gt;.receipt.json</c>）。</summary>
public sealed record ExportReceiptDocument(
    string Format,
    ExportReceiptPayload Payload,
    string Signer,
    string PublicKeyPem,
    string Signature);

/// <summary>匯出收據驗證結果。</summary>
/// <remarks>
/// <c>Valid</c>：檔案存在＋雜湊相符＋簽章有效＋（若有提供）簽署者指紋相符。
/// <c>SelfAssertedKey</c>：未提供預期指紋時，金鑰只由收據自己宣稱，無法證明簽署者身分。
/// </remarks>
public sealed record ExportReceiptReport(
    string ClipPath,
    string? ReceiptPath,
    bool FileExists,
    bool ReceiptExists,
    bool HashMatches,
    bool SignatureValid,
    bool SignerMatched,
    bool SelfAssertedKey,
    string? Signer,
    string? Detail)
{
    /// <summary>整體可信度：所有條件皆成立才算有效。</summary>
    public bool Valid => FileExists && ReceiptExists && HashMatches && SignatureValid && SignerMatched;
}

/// <summary>
/// 一組被信任的簽署者指紋（M241 換發後要同時涵蓋現行與歷史金鑰）。
/// </summary>
public sealed record TrustedSignerSet(IReadOnlyList<string> Fingerprints)
{
    public static readonly TrustedSignerSet Empty = new(Array.Empty<string>());

    /// <summary>是否信任此指紋（不區分大小寫）。</summary>
    public bool Contains(string? fingerprint)
        => fingerprint is not null
        && Fingerprints.Any(f => string.Equals(f, fingerprint, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 匯出簽章收據的格式、正規化與離線驗證（§14.3(2)）。
/// 這裡刻意只依賴 BCL：第三方或警方在多年後離線驗證時只需要這支程式，不需要資料庫或私鑰。
/// </summary>
public static class ExportReceiptCodec
{
    public const string FormatId = "helivms-export-receipt-v1";

    /// <summary>UTC 時間的固定字串格式；簽章內容必須與文化無關且可重現。</summary>
    public static string IsoUtc(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// 正規化被簽署內容：固定欄位順序的 JSON，不含簽章欄位，因此重新輸出與重算雜湊的結果一致。
    /// </summary>
    public static string Canonical(ExportReceiptPayload payload) => JsonSerializer.Serialize(new CanonicalJson
    {
        file = payload.FileName,
        sha256 = payload.Sha256,
        size_bytes = payload.SizeBytes,
        duration_seconds = payload.DurationSeconds,
        channel_id = payload.ChannelId,
        stream = payload.Stream,
        range_start_utc = payload.RangeStartUtc,
        range_end_utc = payload.RangeEndUtc,
        issued_at_utc = payload.IssuedAtUtc,
    });

    /// <summary>組出完整收據文件。</summary>
    public static ExportReceiptDocument Compose(
        ExportReceiptPayload payload, string signer, string publicKeyPem, string signature)
        => new(FormatId, payload, signer, publicKeyPem, signature);

    /// <summary>輸出成 JSON（給磁碟上的 <c>.receipt.json</c>）。</summary>
    public static string Serialize(ExportReceiptDocument doc) => JsonSerializer.Serialize(new DocumentJson
    {
        format = doc.Format,
        file = doc.Payload.FileName,
        sha256 = doc.Payload.Sha256,
        size_bytes = doc.Payload.SizeBytes,
        duration_seconds = doc.Payload.DurationSeconds,
        channel_id = doc.Payload.ChannelId,
        stream = doc.Payload.Stream,
        range_start_utc = doc.Payload.RangeStartUtc,
        range_end_utc = doc.Payload.RangeEndUtc,
        issued_at_utc = doc.Payload.IssuedAtUtc,
        signer = doc.Signer,
        public_key_pem = doc.PublicKeyPem,
        signature = doc.Signature,
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>解析收據；格式不符或 JSON 損毀回傳 null（不丟例外）。</summary>
    public static ExportReceiptDocument? TryParse(string json)
    {
        DocumentJson? doc;
        try
        {
            doc = JsonSerializer.Deserialize<DocumentJson>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }

        if (doc is null
            || !string.Equals(doc.format, FormatId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(doc.file)
            || string.IsNullOrWhiteSpace(doc.sha256)
            || string.IsNullOrWhiteSpace(doc.signature)
            || string.IsNullOrWhiteSpace(doc.public_key_pem))
        {
            return null;
        }

        return new ExportReceiptDocument(
            doc.format!,
            new ExportReceiptPayload(
                doc.file,
                doc.sha256,
                doc.size_bytes,
                doc.duration_seconds,
                doc.channel_id,
                doc.stream ?? "main",
                doc.range_start_utc ?? string.Empty,
                doc.range_end_utc ?? string.Empty,
                doc.issued_at_utc ?? string.Empty),
            doc.signer ?? string.Empty,
            doc.public_key_pem,
            doc.signature);
    }

    /// <summary>
    /// 簽章一律用 base64url 編碼：標準 base64 的 <c>/</c> 會被 JSON 編碼器轉義成 <c>\/</c>，
    /// 收據檔就變成不能直接複製貼上比對、也容易被外部工具誤讀的樣子。
    /// </summary>
    public static string EncodeSignature(byte[] signature) => Base64Url.EncodeToString(signature);

    /// <summary>解碼簽章；接受 base64url 與標準 base64，任一格式損毀都回傳 null。</summary>
    public static byte[]? TryDecodeSignature(string signature)
    {
        try
        {
            return Base64Url.DecodeFromChars(signature);
        }
        catch (FormatException)
        {
            try
            {
                return Convert.FromBase64String(signature);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>以收據內附的公鑰驗證簽章；任何格式或密鑰錯誤都視為無效。</summary>
    public static bool VerifySignature(ExportReceiptDocument doc)
    {
        if (!string.Equals(doc.Format, FormatId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(doc.Signature)
            || string.IsNullOrWhiteSpace(doc.PublicKeyPem))
        {
            return false;
        }

        var signature = TryDecodeSignature(doc.Signature);
        if (signature is null)
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(doc.PublicKeyPem);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(doc.Payload)));
            return rsa.VerifyHash(
                digest,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>重算匯出檔的 SHA-256（小寫 hex）；檔案不存在回傳 null。</summary>
    public static string? ComputeSha256(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 公鑰指紋（64 位小寫 hex）。
    /// 刻意與 <c>EvidenceSigner.Fingerprint()</c> 同一種雜湊輸入（PKCS#1 RSAPublicKey），
    /// 同一把金鑰簽出的證據清單與匯出收據才會顯示同一個指紋，作業員不必搞懂兩套演算法。
    /// </summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToHexString(SHA256.HashData(rsa.ExportRSAPublicKey())).ToLowerInvariant();
    }

    /// <summary>
    /// 離線驗證匯出檔：重新計算雜湊、驗簽、比對被信任的簽署者指紋。
    /// </summary>
    /// <remarks>
    /// <paramref name="expectedSigner"/> 為 null 時只證明「這份收據由收據內那把金鑰簽的」。
    /// M241 起換發金鑰會讓歷史收據帶著舊指紋，所以此處只認單一指紋會把正當的舊收據判成無效——
    /// 需要驗證跨多次換發的收據時，請用 <see cref="Verify(string, TrustedSignerSet)"/> 帶入整組信任指紋。
    /// </remarks>
    public static ExportReceiptReport Verify(string clipPath, string? expectedSigner = null)
        => Verify(clipPath, expectedSigner is null ? TrustedSignerSet.Empty : new TrustedSignerSet(new[] { expectedSigner }));

    /// <summary>以一組被信任的指紋驗證匯出檔（M241 換發後的正式用法）。</summary>
    public static ExportReceiptReport Verify(string clipPath, TrustedSignerSet trusted)
    {
        var receiptPath = ReceiptPath(clipPath);
        var fileExists = File.Exists(clipPath);
        var receiptExists = File.Exists(receiptPath);
        var selfAsserted = trusted.Fingerprints.Count == 0;

        if (!fileExists)
        {
            return new ExportReceiptReport(clipPath, receiptPath, false, receiptExists,
                HashMatches: false, SignatureValid: false, SignerMatched: false,
                SelfAssertedKey: selfAsserted, Signer: null,
                Detail: "匯出檔不存在");
        }

        if (!receiptExists)
        {
            return new ExportReceiptReport(clipPath, receiptPath, true, false,
                HashMatches: false, SignatureValid: false, SignerMatched: false,
                SelfAssertedKey: selfAsserted, Signer: null,
                Detail: "找不到簽章收據（.receipt.json）");
        }

        var doc = TryParse(File.ReadAllText(receiptPath));
        if (doc is null)
        {
            return new ExportReceiptReport(clipPath, receiptPath, true, true,
                HashMatches: false, SignatureValid: false, SignerMatched: false,
                SelfAssertedKey: selfAsserted, Signer: null,
                Detail: "簽章收據格式不符或已損毀");
        }

        var signatureValid = VerifySignature(doc);
        var actual = ComputeSha256(clipPath);
        var hashMatches = actual is not null
            && string.Equals(actual, doc.Payload.Sha256, StringComparison.OrdinalIgnoreCase);
        var signerMatched = selfAsserted || trusted.Contains(doc.Signer);

        var detail = (hashMatches, signatureValid, signerMatched) switch
        {
            (false, _, _) => "匯出檔雜湊與收據不符（檔案已被修改）",
            (_, false, _) => "簽章驗證失敗（收據遭竄改或金鑰不符）",
            (_, _, false) => $"簽署者指紋不在信任清單（收據={doc.Signer}）",
            _ => selfAsserted ? "有效（金鑰未經外部比對）" : "有效",
        };

        return new ExportReceiptReport(clipPath, receiptPath, true, true,
            hashMatches, signatureValid, signerMatched,
            SelfAssertedKey: selfAsserted, Signer: doc.Signer, Detail: detail);
    }

    /// <summary>收據檔名（與匯出檔同目錄）。</summary>
    public static string ReceiptPath(string clipPath) => clipPath + ".receipt.json";

    private sealed class CanonicalJson
    {
        public string file { get; set; } = string.Empty;
        public string sha256 { get; set; } = string.Empty;
        public long size_bytes { get; set; }
        public double duration_seconds { get; set; }
        public int channel_id { get; set; }
        public string stream { get; set; } = "main";
        public string range_start_utc { get; set; } = string.Empty;
        public string range_end_utc { get; set; } = string.Empty;
        public string issued_at_utc { get; set; } = string.Empty;
    }

    private sealed class DocumentJson
    {
        [JsonPropertyName("format")]
        public string? format { get; set; }

        [JsonPropertyName("file")]
        public string? file { get; set; }

        [JsonPropertyName("sha256")]
        public string? sha256 { get; set; }

        [JsonPropertyName("size_bytes")]
        public long size_bytes { get; set; }

        [JsonPropertyName("duration_seconds")]
        public double duration_seconds { get; set; }

        [JsonPropertyName("channel_id")]
        public int channel_id { get; set; }

        [JsonPropertyName("stream")]
        public string? stream { get; set; }

        [JsonPropertyName("range_start_utc")]
        public string? range_start_utc { get; set; }

        [JsonPropertyName("range_end_utc")]
        public string? range_end_utc { get; set; }

        [JsonPropertyName("issued_at_utc")]
        public string? issued_at_utc { get; set; }

        [JsonPropertyName("signer")]
        public string? signer { get; set; }

        [JsonPropertyName("public_key_pem")]
        public string? public_key_pem { get; set; }

        [JsonPropertyName("signature")]
        public string? signature { get; set; }
    }
}