using System.Security.Cryptography;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>一筆歷史簽章金鑰（已換發但仍需用來驗證舊收據）。</summary>
public sealed record SigningKeyRecord(string Fingerprint, string PublicKeyPem, DateTime RetiredUtc);

/// <summary>金鑰換發結果。</summary>
public sealed record SigningKeyRotation(string PreviousFingerprint, string NewFingerprint, DateTime RotatedUtc);

/// <summary>
/// 數位簽章器（M53，§14.7 #5）：以 RSA-2048 對證據 manifest 內容簽屬 SHA-256 digest。
/// 私鑰存於 <see cref="SettingsRepository"/> 金鑰 <c>evidence.signing_key_pem</c>（首次自動生成）。
/// </summary>
/// <remarks>
/// M241 起私鑰以 DPAPI（<see cref="SecretProtector"/>）保護後落庫，不再是明文 PEM——
/// 能讀到資料庫檔案的人原本就能對外簽發「合法」證據，簽章金鑰的保管層級不能低於其他憑證。
/// 舊版明文會在第一次讀取時自動升級為加密格式（<see cref="SecretProtector.Unprotect"/> 對明文原樣返回）。
///
/// 換發（<see cref="RotateKey"/>）會保留舊金鑰的**公鑰**到 <c>evidence.signing_key_history</c>：
/// 已交付出去的匯出收據不會因為換發而變成「無法驗證」，這一點比新金鑰本身更重要。
/// 私鑰不保留——舊私鑰若外洩正是換發的原因，留著只會繼續冒險。
/// </remarks>
public sealed class EvidenceSigner
{
    public const string SettingKey = "evidence.signing_key_pem";
    public const string HistoryKey = "evidence.signing_key_history";

    private readonly SettingsRepository _settings;
    private readonly AuditLogRepository _audit;
    private RSA? _keys;

    public EvidenceSigner(SqliteStore store)
    {
        _settings = new SettingsRepository(store);
        _audit = new AuditLogRepository(store);
    }

    /// <summary>確保私鑰存在並回傳其 PEM（首次會自動生成 RSA-2048）。</summary>
    public string EnsureKey()
    {
        var stored = _settings.Get(SettingKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return CreateAndStoreKey();
        }

        // 舊版是明文 PEM；Unprotect 遇到無法解密的內容會原樣返回，因此這裡同時處理兩種格式。
        var pem = SecretProtector.Unprotect(stored);
        if (!string.Equals(pem, stored, StringComparison.Ordinal))
        {
            return pem;
        }

        // 是明文 → 順手升級為 DPAPI 保護，之後就不再有明文私鑰落庫。
        _settings.Set(SettingKey, Protect(pem));
        return pem;
    }

    /// <summary>公鑰指紋（SHA-256 of DER public key，hex）。</summary>
    public string Fingerprint()
    {
        LoadKeys();
        return FingerprintOf(_keys!);
    }

    /// <summary>
    /// 公鑰 PEM（SubjectPublicKeyInfo）。簽章產物要能離線驗證，就必須把公鑰一併輸出——
    /// 驗證方不需要資料庫，也不需要這台 VMS 的私鑰。
    /// </summary>
    public string PublicKeyPem()
    {
        LoadKeys();
        return _keys!.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>對字串內容簽屬（SHA-256 digest，PKCS#1 v1.5），回傳 base64 簽章。</summary>
    public string SignDocument(string content)
    {
        LoadKeys();
        var digest = SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToBase64String(_keys!.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    /// <summary>驗證簽章（非法 base64 視為無效）。</summary>
    public bool VerifySignature(string content, string signatureBase64)
    {
        LoadKeys();
        var digest = SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(content));
        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            return _keys!.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>歷史簽章金鑰（已換發，公鑰仍可用於驗證舊收據），新→舊。</summary>
    public IReadOnlyList<SigningKeyRecord> KeyHistory()
    {
        var json = _settings.Get(HistoryKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SigningKeyRecord>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SigningKeyRecord>>(json) ?? new List<SigningKeyRecord>();
        }
        catch (JsonException)
        {
            // 歷史清單損毀不該讓整個簽章機制停擺：新的收據仍要簽得出來。
            return Array.Empty<SigningKeyRecord>();
        }
    }

    /// <summary>
    /// 換發簽章金鑰：新收據用新金鑰簽，舊金鑰的**公鑰**進歷史清單以維持舊收據可驗證。
    /// 私鑰不保留，並寫入稽核日誌（§11.5）——誰在什麼時候換了金鑰必須查得到。
    /// </summary>
    public SigningKeyRotation RotateKey(string actor, DateTime? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var at = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var previous = Fingerprint();
        var previousPem = PublicKeyPem();

        _keys?.Dispose();
        _keys = null;

        CreateAndStoreKey();
        LoadKeys();
        var current = Fingerprint();

        var history = KeyHistory().ToList();
        history.Insert(0, new SigningKeyRecord(previous, previousPem, at));
        _settings.Set(HistoryKey, JsonSerializer.Serialize(history));

        _audit.Record(
            actor,
            "evidence.signing_key.rotate",
            AuditCategories.Evidence,
            targetType: "signing_key",
            targetId: null,
            detail: $"old={previous} new={current}",
            occurredAtUtc: at);

        return new SigningKeyRotation(previous, current, at);
    }

    /// <summary>所有仍被信任的簽章金鑰指紋（現行＋歷史），供匯出收據驗證時比對。</summary>
    public IReadOnlyList<string> TrustedFingerprints()
    {
        var list = new List<string> { Fingerprint() };
        list.AddRange(KeyHistory().Select(k => k.Fingerprint));
        return list;
    }

    /// <summary>公鑰指紋演算法；與 <c>ExportReceiptCodec.Fingerprint</c> 必須一致。</summary>
    public static string FingerprintOf(RSA rsa)
        => Convert.ToHexString(SHA256.HashData(rsa.ExportRSAPublicKey())).ToLowerInvariant();

    /// <summary>依指紋查出公鑰 PEM（現行或歷史）；查無回傳 null。</summary>
    public string? PublicKeyPemFor(string fingerprint)
    {
        if (string.Equals(Fingerprint(), fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return PublicKeyPem();
        }

        return KeyHistory()
            .FirstOrDefault(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            ?.PublicKeyPem;
    }

    private string CreateAndStoreKey()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        _settings.Set(SettingKey, Protect(pem));
        return pem;
    }

    private static string Protect(string pem)
        => OperatingSystem.IsWindows() ? SecretProtector.Protect(pem) : pem;

    private void LoadKeys()
    {
        if (_keys is not null)
        {
            return;
        }

        var pem = EnsureKey();
        _keys = RSA.Create();
        _keys.ImportFromPem(pem);
    }
}