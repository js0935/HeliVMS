using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeliVMS.Storage;

public enum EvidenceItemStatus
{
    Ok,
    Tampered,
    Missing,
    Extra,
}

/// <summary>證據檔完整性項目（M53）。</summary>
public sealed record EvidenceItem(string RelPath, string Sha256, long Size, EvidenceItemStatus Status);

/// <summary>證據集驗證結果（M53）。</summary>
public sealed record EvidenceVerifyResult(
    bool OverallOk,
    int OkCount,
    int TamperedCount,
    int MissingCount,
    int ExtraCount,
    IReadOnlyList<EvidenceItem> Items);

/// <summary>
/// 證據 manifest 服務（M53，§14.7 #5；§14.3 證物線）：為證據目錄建立 SHA-256 清單、
/// 以 <see cref="EvidenceSigner"/> 簽屬並驗證完整性（OK／TAMPERED／MISSING／EXTRA）。
/// </summary>
public sealed class EvidenceManifestService
{
    public const string ManifestFileName = "manifest.json";

    private readonly EvidenceManifestRepository _repo;
    private readonly EvidenceSigner _signer;

    public EvidenceManifestService(SqliteStore store)
    {
        _repo = new EvidenceManifestRepository(store);
        _signer = new EvidenceSigner(store);
    }

    /// <summary>建立未簽屬之 manifest JSON（format／created_at／files）。</summary>
    public string BuildRaw(string directory)
    {
        var json = new ManifestJson { created_at = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var (rel, (sha, size)) in CollectFiles(directory))
        {
            json.files[rel] = new FileEntryJson { sha256 = sha, size = size };
        }

        if (json.files.Count == 0)
        {
            throw new ArgumentException("目錄中沒有可收錄的檔案（需至少 1 個非 manifest 檔）。", nameof(directory));
        }

        return JsonSerializer.Serialize(json);
    }

    /// <summary>建立已簽屬之 manifest JSON。</summary>
    public string Build(string directory)
        => AttachSignature(BuildRaw(directory));

    /// <summary>將簽章與簽署者指紋附加到 manifest。</summary>
    public string AttachSignature(string rawManifest)
    {
        var doc = JsonSerializer.Deserialize<ManifestJson>(rawManifest)
            ?? throw new InvalidDataException("manifest 無法解析");
        doc.signature = _signer.SignDocument(rawManifest);
        doc.signer = _signer.Fingerprint();
        return JsonSerializer.Serialize(doc);
    }

    /// <summary>儲存：在目錄寫入 <c>manifest.json</c>（含簽章）並記錄至 DB。</summary>
    public EvidenceManifestRecord Save(string directory, DateTime? utc = null)
    {
        var dir = Path.GetFullPath(directory);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException(dir);
        }

        var signed = Build(dir);
        File.WriteAllText(Path.Combine(dir, ManifestFileName), signed, new UTF8Encoding(false));
        var record = _repo.Upsert(dir, signed, "created", utc ?? DateTime.UtcNow);
        return new EvidenceManifestRecord(
            record,
            dir,
            signed,
            "created",
            (utc ?? DateTime.UtcNow).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            null);
    }

    /// <summary>驗證：比對 <c>manifest.json</c> 清單與現況（含數位簽章）。</summary>
    public EvidenceVerifyResult Verify(string directory)
    {
        var dir = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("找不到 manifest.json", manifestPath);
        }

        ManifestJson doc;
        try
        {
            doc = JsonSerializer.Deserialize<ManifestJson>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("manifest 無法解析");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("manifest.json 格式錯誤", ex);
        }

        var okCount = 0;
        var tamperedCount = 0;
        var missingCount = 0;
        var extraCount = 0;
        var items = new List<EvidenceItem>();

        if (!string.IsNullOrEmpty(doc.signature))
        {
            var signatureValid = _signer.VerifySignature(Raw(doc), doc.signature!);
            if (!signatureValid)
            {
                tamperedCount++;
                items.Add(new EvidenceItem($"manifest.{ManifestFileName}", string.Empty, 0, EvidenceItemStatus.Tampered));
            }
        }

        var expected = doc.files;
        var actual = CollectFiles(dir);

        foreach (var (rel, entry) in expected)
        {
            if (!actual.TryGetValue(rel, out var current))
            {
                missingCount++;
                items.Add(new EvidenceItem(rel, entry.sha256, entry.size, EvidenceItemStatus.Missing));
            }
            else if (!string.Equals(current.Sha, entry.sha256, StringComparison.OrdinalIgnoreCase) || current.Size != entry.size)
            {
                tamperedCount++;
                items.Add(new EvidenceItem(rel, current.Sha, current.Size, EvidenceItemStatus.Tampered));
            }
            else
            {
                okCount++;
                items.Add(new EvidenceItem(rel, current.Sha, current.Size, EvidenceItemStatus.Ok));
            }
        }

        foreach (var (rel, current) in actual)
        {
            if (!expected.ContainsKey(rel))
            {
                extraCount++;
                items.Add(new EvidenceItem(rel, current.Sha, current.Size, EvidenceItemStatus.Extra));
            }
        }

        return new EvidenceVerifyResult(
            tamperedCount == 0 && missingCount == 0,
            okCount,
            tamperedCount,
            missingCount,
            extraCount,
            items);
    }

    /// <summary>只驗證指定 manifest 字串的簽章是否有效（簽章存在與否、是否相符）。</summary>
    public (bool Signed, bool Valid) VerifySigned(string signedManifest)
    {
        var doc = JsonSerializer.Deserialize<ManifestJson>(signedManifest);
        if (doc is null || string.IsNullOrEmpty(doc.signature))
        {
            return (false, false);
        }

        return (true, _signer.VerifySignature(Raw(doc), doc.signature!));
    }

    public EvidenceVerifyResult? LastResult(string directory)
    {
        var record = _repo.GetByDirectory(Path.GetFullPath(directory));
        return record is null ? null : new EvidenceVerifyResult(
            record.Status == "verified",
            record.Status == "verified" ? 1 : 0,
            record.Status == "tampered" ? 1 : 0,
            0,
            0,
            Array.Empty<EvidenceItem>());
    }

    private static IReadOnlyDictionary<string, (string Sha, long Size)> CollectFiles(string directory)
    {
        var map = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (rel.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileInfo = new FileInfo(file);
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(file);
            map[rel] = (Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant(), fileInfo.Length);
        }

        return map;
    }

    /// <summary>重組未簽屬之原始 manifest（保持 files 插入序，確保與簽屬時位元組一致）。</summary>
    private static string Raw(ManifestJson doc)
        => JsonSerializer.Serialize(new ManifestJson
        {
            format = doc.format,
            created_at = doc.created_at,
            files = doc.files,
        });

    private sealed class ManifestJson
    {
        public string format { get; set; } = "helivms-evidence-manifest";
        public string created_at { get; set; } = string.Empty;
        public Dictionary<string, FileEntryJson> files { get; set; } = new(StringComparer.Ordinal);
        public string? signature { get; set; }
        public string? signer { get; set; }
    }

    private sealed class FileEntryJson
    {
        public string sha256 { get; set; } = string.Empty;
        public long size { get; set; }
    }
}