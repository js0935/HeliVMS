using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeliVMS.Licensing;

namespace HeliVMS.LicenseProducer;

/// <summary>一筆簽發端稽核項目（§19.6「登入與稽核：管理員操作全部留痕（誰簽了哪張）」）。</summary>
/// <param name="Seq">順序號，自 1 連續遞增（刪行／插入行皆會被驗證抓到）。</param>
/// <param name="AtUtc">作業時間（UTC）。</param>
/// <param name="Action"><see cref="IssuanceActions.Issue"/> 或 <see cref="IssuanceActions.GenerateKey"/>。</param>
/// <param name="Operator">操作者（--operator／HELIVMS_ISSUANCE_OPERATOR／OS 帳戶）。</param>
/// <param name="LicenseId">授權唯一識別碼（<see cref="IssuanceActions.GenerateKey"/> 為 null）。</param>
/// <param name="Tier">簽發等級名稱；自訂組合記為「自訂」。</param>
/// <param name="Cameras">授權通道數上限。</param>
/// <param name="Features">授權功能旗標。</param>
/// <param name="Machine">綁定之 32 碼設備碼；未綁定機器為 null。</param>
/// <param name="ExpiresUtc">到期時間（UTC）；永久授權為 null。</param>
/// <param name="Issuer">發行者名稱。</param>
/// <param name="TokenSha256">授權碼全文之 SHA-256（Base64URL）；不落明文授權碼。</param>
/// <param name="PublicKeySha256">金鑰 SPKI DER 之 SHA-256（Base64URL），用以識別金鑰輪替。</param>
/// <param name="Prev">前一行的 SHA-256（Base64URL）；首行為空字串。</param>
/// <param name="Signature">以簽發私鑰對本項目正規化文字之簽章（Base64URL），可獨立驗證。</param>
public sealed record IssuanceAuditEntry(
    int Seq,
    DateTime AtUtc,
    string Action,
    string Operator,
    string? LicenseId,
    string? Tier,
    int? Cameras,
    IReadOnlyList<string>? Features,
    string? Machine,
    DateTime? ExpiresUtc,
    string? Issuer,
    string? TokenSha256,
    string? PublicKeySha256,
    string Prev,
    string Signature);

/// <summary>簽發端稽核動作代碼。</summary>
public static class IssuanceActions
{
    /// <summary>簽發一張授權。</summary>
    public const string Issue = "license.issue";

    /// <summary>產生新金鑰對。</summary>
    public const string GenerateKey = "keypair.generate";
}

/// <summary>一筆稽核項目的欄位（尚未指派 Seq/Prev/Signature）。</summary>
public sealed record IssuanceAuditDraft(
    string Action,
    string Operator,
    string? LicenseId = null,
    string? Tier = null,
    int? Cameras = null,
    IReadOnlyList<string>? Features = null,
    string? Machine = null,
    DateTime? ExpiresUtc = null,
    string? Issuer = null,
    string? TokenSha256 = null,
    string? PublicKeySha256 = null);

/// <summary>
/// 簽發端稽核軌跡（§19.6）：append-only JSON Lines，每行由簽發私鑰簽章且以雜湊鏈串接。
/// 因此即使稽核檔被竄改、刪除或重排也能驗出——竄改者沒有私鑰即無法重簽。
/// </summary>
/// <remarks>
/// 每行格式為 JSON 物件（camelCase）；<c>signature</c> 之外的欄位即為簽章涵蓋範圍，
/// 驗證時由 <see cref="Canonicalize"/> 產生相同文字重新計算，故不受 JSON 空白／屬性順序影響。
/// </remarks>
public static class IssuanceAuditTrail
{
    public const string DefaultFileName = "issuance-audit.jsonl";

    /// <summary>正規化待簽文字：每行 <c>key=value</c>，值一律不換行；空值以「-」表示。</summary>
    public static string Canonicalize(
        int seq,
        DateTime atUtc,
        string action,
        string operatorName,
        string? licenseId,
        string? tier,
        int? cameras,
        IReadOnlyList<string>? features,
        string? machine,
        DateTime? expiresUtc,
        string? issuer,
        string? tokenSha256,
        string? publicKeySha256,
        string prev)
    {
        var sb = new StringBuilder();
        sb.Append("seq=").Append(seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("atUtc=").Append(Iso(atUtc)).Append('\n');
        sb.Append("action=").Append(Sanitize(action)).Append('\n');
        sb.Append("operator=").Append(Sanitize(operatorName)).Append('\n');
        sb.Append("licenseId=").Append(Value(licenseId)).Append('\n');
        sb.Append("tier=").Append(Value(tier)).Append('\n');
        sb.Append("cameras=").Append(cameras?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
        sb.Append("features=")
            .Append(features is null || features.Count == 0
                ? "-"
                : Sanitize(string.Join(',', features))).Append('\n');
        sb.Append("machine=").Append(Value(machine)).Append('\n');
        sb.Append("expiresUtc=").Append(expiresUtc is { } e ? Iso(e) : "-").Append('\n');
        sb.Append("issuer=").Append(Value(issuer)).Append('\n');
        sb.Append("tokenSha256=").Append(Value(tokenSha256)).Append('\n');
        sb.Append("publicKeySha256=").Append(Value(publicKeySha256)).Append('\n');
        sb.Append("prev=").Append(prev ?? string.Empty);
        return sb.ToString();
    }

    /// <summary>對一筆項目簽章（回 Base64URL）。</summary>
    public static string Sign(IssuanceAuditEntry entry, RSA privateKey)
        => Base64Url.Encode(
            privateKey.SignData(SignBytes(entry), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    /// <summary>
    /// 追加一筆稽核。回傳寫入的項目（含指派後的 Seq/Prev/Signature）。
    /// 檔案以 UTF-8（無 BOM）、\n 結尾追加；父目錄不存在會擲出（簽發流程必須留痕，fail-closed）。
    /// </summary>
    public static IssuanceAuditEntry Append(string path, IssuanceAuditDraft draft, RSA privateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Operator);

        var seq = 1;
        var prev = string.Empty;
        if (File.Exists(path))
        {
            var last = ReadLines(path).LastOrDefault();
            if (last is not null)
            {
                var lastEntry = Parse(last) ?? throw new PrivateKeyAccessException(
                    $"稽核檔最後一列無法剖析（損毀或遭竄改）：{Path.GetFileName(path)}");
                seq = lastEntry.Seq + 1;
                prev = Base64Url.Encode(LineHash(last));
            }
        }

        var atUtc = DateTime.UtcNow;
        var unsigned = new IssuanceAuditEntry(
            seq, atUtc, draft.Action, draft.Operator, draft.LicenseId, draft.Tier, draft.Cameras,
            draft.Features, draft.Machine, draft.ExpiresUtc, draft.Issuer, draft.TokenSha256,
            draft.PublicKeySha256, prev, string.Empty);

        var entry = unsigned with { Signature = Sign(unsigned, privateKey) };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(path, ToLine(entry) + "\n", new UTF8Encoding(false));
        return entry;
    }

    /// <summary>讀取稽核檔全部項目；檔案不存在回空清單。</summary>
    public static IReadOnlyList<IssuanceAuditEntry> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var list = new List<IssuanceAuditEntry>();
        foreach (var line in ReadLines(path))
        {
            var entry = Parse(line)
                ?? throw new PrivateKeyAccessException($"稽核項目無法剖析：{Truncate(line, 80)}");
            list.Add(entry);
        }

        return list;
    }

    /// <summary>
    /// 驗證稽核檔：以簽發公鑰逐行驗簽章、檢查序號連續與雜湊鏈銜接。
    /// 回傳問題清單；空清單代表完整無竄改。
    /// </summary>
    public static IReadOnlyList<string> Verify(string path, RSA publicKey)
    {
        if (!File.Exists(path))
        {
            return [$"稽核檔不存在：{path}"];
        }

        var lines = ReadLines(path);
        if (lines.Count == 0)
        {
            return ["稽核檔為空，無任何簽發紀錄。"];
        }

        var problems = new List<string>();
        var expectedPrev = string.Empty;
        for (var i = 0; i < lines.Count; i++)
        {
            var entry = Parse(lines[i]);
            if (entry is null)
            {
                problems.Add($"第 {i + 1} 列無法剖析（JSON 損毀）。");
                return problems;
            }

            if (entry.Seq != i + 1)
            {
                problems.Add($"第 {i + 1} 列序號為 {entry.Seq}，序號不連續（可能有列遭刪除或重排）。");
            }

            if (!string.Equals(entry.Prev, expectedPrev, StringComparison.Ordinal))
            {
                problems.Add($"第 {i + 1} 列 prev 不符前一列（雜湊鏈斷裂，該列可能遭竄改或刪除）。");
            }

            if (!VerifySignature(entry, publicKey, out var sigError))
            {
                problems.Add($"第 {i + 1} 列（seq={entry.Seq}）簽章驗證失敗：{sigError}");
            }

            expectedPrev = Base64Url.Encode(LineHash(lines[i]));
        }

        return problems;
    }

    private static bool VerifySignature(IssuanceAuditEntry entry, RSA publicKey, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(entry.Signature))
        {
            error = "缺少簽章。";
            return false;
        }

        byte[] signature;
        try
        {
            signature = Base64Url.Decode(entry.Signature);
        }
        catch (FormatException)
        {
            error = "簽章 Base64URL 解碼失敗。";
            return false;
        }

        var valid = publicKey.VerifyData(
            SignBytes(entry),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        if (!valid)
        {
            error = "內容與簽章不符（遭竄改或簽章金鑰不符）。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 待簽位元組：把 <c>signature</c> 欄位換成其自身值重算——簽章涵蓋除簽章外全部欄位，
    /// 故驗證時以「同一筆項目」重建文字即可，不必重排 JSON。
    /// </summary>
    private static byte[] SignBytes(IssuanceAuditEntry entry)
        => Encoding.UTF8.GetBytes(Canonicalize(
            entry.Seq, entry.AtUtc, entry.Action, entry.Operator, entry.LicenseId, entry.Tier,
            entry.Cameras, entry.Features, entry.Machine, entry.ExpiresUtc, entry.Issuer,
            entry.TokenSha256, entry.PublicKeySha256, entry.Prev));

    private static byte[] LineHash(string line) => SHA256.HashData(Encoding.UTF8.GetBytes(line));

    /// <summary>簽章來源位元組的雜湊（供測試與外部校驗使用）。</summary>
    public static string Fingerprint(byte[] data) => Base64Url.Encode(SHA256.HashData(data));

    private static List<string> ReadLines(string path)
    {
        var raw = File.ReadAllText(path);
        return raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string ToLine(IssuanceAuditEntry entry)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", entry.Seq);
            writer.WriteString("atUtc", Iso(entry.AtUtc));
            writer.WriteString("action", entry.Action);
            writer.WriteString("operator", entry.Operator);
            WriteNullable(writer, "licenseId", entry.LicenseId);
            WriteNullable(writer, "tier", entry.Tier);
            if (entry.Cameras is { } cameras)
            {
                writer.WriteNumber("cameras", cameras);
            }
            else
            {
                writer.WriteNull("cameras");
            }

            if (entry.Features is { } features)
            {
                writer.WriteStartArray("features");
                foreach (var f in features)
                {
                    writer.WriteStringValue(f);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNull("features");
            }

            WriteNullable(writer, "machine", entry.Machine);
            WriteNullable(writer, "expiresUtc", entry.ExpiresUtc is { } exp ? Iso(exp) : null);
            WriteNullable(writer, "issuer", entry.Issuer);
            WriteNullable(writer, "tokenSha256", entry.TokenSha256);
            WriteNullable(writer, "publicKeySha256", entry.PublicKeySha256);
            writer.WriteString("prev", entry.Prev);
            writer.WriteString("signature", entry.Signature);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IssuanceAuditEntry? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return new IssuanceAuditEntry(
                root.GetProperty("seq").GetInt32(),
                ParseUtc(root.GetProperty("atUtc").GetString()),
                root.GetProperty("action").GetString() ?? string.Empty,
                root.GetProperty("operator").GetString() ?? string.Empty,
                NullableString(root, "licenseId"),
                NullableString(root, "tier"),
                root.GetProperty("cameras").ValueKind == JsonValueKind.Number
                    ? root.GetProperty("cameras").GetInt32()
                    : null,
                root.GetProperty("features").ValueKind == JsonValueKind.Array
                    ? root.GetProperty("features").EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .ToList()
                    : null,
                NullableString(root, "machine"),
                root.GetProperty("expiresUtc").ValueKind == JsonValueKind.String
                    ? ParseUtc(root.GetProperty("expiresUtc").GetString())
                    : null,
                NullableString(root, "issuer"),
                NullableString(root, "tokenSha256"),
                NullableString(root, "publicKeySha256"),
                root.GetProperty("prev").GetString() ?? string.Empty,
                root.GetProperty("signature").GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? NullableString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>ISO-8601 UTC、固定 7 位小數、Z 結尾（與授權 payload 一致，便於排序比對）。</summary>
    private static string Iso(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string? text)
        => DateTime.ParseExact(
            text ?? string.Empty, "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string Value(string? text)
        => string.IsNullOrEmpty(text) ? "-" : Sanitize(text);

    /// <summary>防偽正規化：欄位值若含換行會破壞「一行一屬性」結構，故以空白取代。</summary>
    private static string Sanitize(string text)
    {
        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        foreach (var c in text)
        {
            buffer[length++] = c is '\r' or '\n' ? ' ' : c;
        }

        return new string(buffer[..length]);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}