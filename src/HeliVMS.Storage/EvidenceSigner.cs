using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>
/// 數位簽章器（M53，§14.7 #5）：以 RSA-2048 對證據 manifest 內容簽屬 SHA-256 digest。
/// 私鑰存於 <see cref="SettingsRepository"/> 金鑰 <c>evidence.signing_key_pem</c>（首次自動生成）。
/// </summary>
public sealed class EvidenceSigner
{
    public const string SettingKey = "evidence.signing_key_pem";

    private readonly SettingsRepository _settings;
    private RSA? _keys;

    public EvidenceSigner(SqliteStore store)
    {
        _settings = new SettingsRepository(store);
    }

    /// <summary>確保私鑰存在並回傳其 PEM（首次會自動生成 RSA-2048）。</summary>
    public string EnsureKey()
    {
        var pem = _settings.Get(SettingKey);
        if (string.IsNullOrWhiteSpace(pem))
        {
            using var rsa = RSA.Create(2048);
            pem = rsa.ExportRSAPrivateKeyPem();
            _settings.Set(SettingKey, pem);
        }

        return pem;
    }

    /// <summary>公鑰指紋（SHA-256 of DER public key，hex）。</summary>
    public string Fingerprint()
    {
        LoadKeys();
        return Convert.ToHexString(SHA256.HashData(_keys!.ExportRSAPublicKey())).ToLowerInvariant();
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