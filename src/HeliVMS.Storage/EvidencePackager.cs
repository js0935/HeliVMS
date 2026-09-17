using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// 數位證據安全包（M43，§14.3／§14.7 #4「安全共享」）：
/// SHA-256 清單＋選用密碼保護（PBKDF2＋AES-256-GCM）＋選用到期。
/// 無密碼＝直接 ZipArchive；有密碼＝加密整包，檔頭 HELIVMS-EVP1。
/// <c>Verify</c> 可驗證完整性（逐項重算 SHA-256）、到期與密碼正確性；GCM tag 亦為篡改偵測。
/// </summary>
public static class EvidencePackager
{
    private const string Magic = "HELIVMS-EVP"; // 11 bytes；緊接 Version(1B)
    private const byte Version = 1;
    private const int KdfIterations = 200_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 11 + 1 + SaltSize + NonceSize + 16; // Magic+ver+salt+nonce+tag

    /// <summary>清單中的單一檔案。<c>SourcePath</c> 為建包時的來源絕對路徑，不寫入 manifest。</summary>
    public sealed record BundleItem(string RelativePath, string Sha256, long SizeBytes, string Kind, string SourcePath = "");

    /// <summary>證據包描述檔（manifest.json 內容）。</summary>
    public sealed record BundleManifest(
        Guid BundleId,
        DateTime CreatedUtc,
        DateTime? ExpiresUtc,
        string BundleName,
        IReadOnlyList<BundleItem> Items);

    /// <summary>驗證結果。</summary>
    public sealed record VerifyResult(
        bool Valid,
        Guid BundleId,
        DateTime CreatedUtc,
        DateTime? ExpiresUtc,
        bool Expired,
        IReadOnlyList<BundleItem> Items,
        IReadOnlyList<string> Failures);

/// <summary>
    /// 依檔案清單產出描述檔：逐檔流式 SHA-256＋種類（依副檔名）。展平為檔名、重名加後綴。
    /// </summary>
    public static BundleManifest BuildManifest(string bundleName, IEnumerable<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleName);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<BundleItem>();

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("證據包來源檔不存在。", file);
            }

            var name = Path.GetFileName(file);
            var baseName = name;
            for (var i = 2; !names.Add(name); i++)
            {
                name = $"{Path.GetFileNameWithoutExtension(baseName)}-{i}{Path.GetExtension(baseName)}";
            }

            var sha = ComputeSha256(file);
            items.Add(new BundleItem(name, sha, new FileInfo(file).Length, KindFor(file), file));
        }

        if (items.Count == 0)
        {
            throw new ArgumentException("證據包至少需包含一個檔案。", nameof(files));
        }

        return new BundleManifest(
            Guid.NewGuid(),
            DateTime.UtcNow,
            null,
            bundleName,
            items);
    }

    /// <summary>
    /// 打包成 .evp。回傳包檔本身的 SHA-256；含密碼時密碼不可空白。
    /// </summary>
    public static string Create(string outputPath, BundleManifest manifest, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Items.Count == 0)
        {
            throw new ArgumentException("證據包至少需包含一個檔案。", nameof(manifest));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var zipPath = Path.Combine(Path.GetTempPath(), $"helivms-evp-{Guid.NewGuid():N}.bin");
        try
        {
            using (var zipFs = File.Create(zipPath))
            using (var zip = new ZipArchive(zipFs, ZipArchiveMode.Create))
            {
                WriteManifestEntry(zip, manifest);
                foreach (var item in manifest.Items)
                {
                    var entry = zip.CreateEntry(item.RelativePath, CompressionLevel.Fastest);
                    using var es = entry.Open();
                    using var fs = File.OpenRead(string.IsNullOrEmpty(item.SourcePath) ? item.RelativePath : item.SourcePath);
                    fs.CopyTo(es);
                }
            }

            if (string.IsNullOrEmpty(password))
            {
                File.Copy(zipPath, outputPath, overwrite: true);
            }
            else
            {
                EncryptZip(zipPath, outputPath, password!);
            }

            return ComputeSha256(outputPath);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    /// <summary>
    /// 驗證 .evp：解包（含密碼解密）→ 解析 manifest.json → 逐項重算 SHA-256／大小比對。
    /// 任一失敗（含 GCM tag 不符、檔案缺失、hash 差異）=> Valid=false 並於 Failures 列明。
    /// </summary>
    public static VerifyResult Verify(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var failures = new List<string>();
        var items = new List<BundleItem>();
        Guid bundleId = Guid.Empty;
        var createdUtc = default(DateTime);
        DateTime? expiresUtc = null;

        try
        {
            using var stream = OpenPackage(path, password, failures);
            if (stream is null)
            {
                return new VerifyResult(false, Guid.Empty, default, null, false, [], failures);
            }

            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                failures.Add("缺少 manifest.json。");
            }
            else
            {
                try
                {
                    using var es = manifestEntry.Open();
                    using var doc = JsonDocument.Parse(es);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("BundleId", out var bid) && bid.TryGetGuid(out bundleId))
                    {
                    }

                    if (root.TryGetProperty("CreatedUtc", out var cu))
                    {
                        createdUtc = cu.GetDateTime();
                    }

                    if (root.TryGetProperty("ExpiresUtc", out var eu) && eu.ValueKind != JsonValueKind.Null)
                    {
                        expiresUtc = eu.GetDateTime();
                    }

                    if (!root.TryGetProperty("Items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                    {
                        failures.Add("manifest.json 缺少 Items 清單。");
                    }
                    else
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var it in itemsEl.EnumerateArray())
                        {
                            var rel = it.TryGetProperty("RelativePath", out var rp) ? rp.GetString() : null;
                            var sha = it.TryGetProperty("Sha256", out var sh) ? sh.GetString() : null;
                            long size = 0;
                            var sizeOk = it.TryGetProperty("SizeBytes", out var sz) && sz.TryGetInt64(out size);
                            var kind = it.TryGetProperty("Kind", out var kd) ? kd.GetString() : null;
                            if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(sha))
                            {
                                failures.Add("manifest.json 內含無效項目。");
                                continue;
                            }

                            var entry = zip.GetEntry(rel);
                            if (entry is null)
                            {
                                failures.Add($"缺少檔案：{rel}。");
                                continue;
                            }

                            if (!seen.Add(rel))
                            {
                                failures.Add($"重複路徑：{rel}。");
                                continue;
                            }

                            string actualSha;
                            long actualSize;
                            using (var zs = entry.Open())
                            using (var ms = new MemoryStream())
                            {
                                zs.CopyTo(ms);
                                using var shaAlg = SHA256.Create();
                                actualSha = Convert.ToHexStringLower(shaAlg.ComputeHash(ms.ToArray()));
                                actualSize = ms.Length;
                            }

                            if (actualSha != sha)
                            {
                                failures.Add($"SHA-256 不符：{rel}。");
                            }

                            if (sizeOk && actualSize != size)
                            {
                                failures.Add($"大小不符：{rel}。");
                            }

                            items.Add(new BundleItem(rel, sha, sizeOk ? size : actualSize, kind ?? "other"));
                        }
                    }
                }
                catch (JsonException)
                {
                    failures.Add("manifest.json 無法解析。");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        var expired = expiresUtc.HasValue && DateTime.UtcNow > expiresUtc.Value;
        return new VerifyResult(
            failures.Count == 0,
            bundleId,
            createdUtc,
            expiresUtc,
            expired,
            items,
            failures);
    }

    /// <summary>承上。</summary>
    private static void WriteManifestEntry(ZipArchive zip, BundleManifest manifest)
    {
        var json = JsonSerializer.Serialize(new
        {
            manifest.BundleId,
            manifest.CreatedUtc,
            manifest.ExpiresUtc,
            manifest.BundleName,
            Items = manifest.Items.Select(i => new
            {
                i.RelativePath,
                i.Sha256,
                i.SizeBytes,
                i.Kind,
            }),
        });

        var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using var es = entry.Open();
        using var writer = new StreamWriter(es, System.Text.Encoding.UTF8);
        writer.Write(json);
    }

    private static void EncryptZip(string zipPath, string outputPath, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = File.ReadAllBytes(zipPath);

        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag);

        using var fs = File.Create(outputPath);
        fs.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
        fs.WriteByte(Version);
        fs.Write(salt);
        fs.Write(nonce);
        fs.Write(tag);
        fs.Write(cipher);
    }

    private static Stream? OpenPackage(string path, string? password, List<string> failures)
    {
        using var headStream = File.OpenRead(path);
        var head = new byte[11];
        headStream.ReadExactly(head);
        if (System.Text.Encoding.ASCII.GetString(head) == Magic)
        {
            if (string.IsNullOrEmpty(password))
            {
                failures.Add("包已加密，需提供密碼。");
                return null;
            }

            headStream.Position = 0;
            var header = new byte[HeaderSize];
            headStream.ReadExactly(header);
            if (header[11] != Version)
            {
                failures.Add("不支援的證據包版本。");
                return null;
            }

            var salt = header[12..(12 + SaltSize)];
            var nonce = header[(12 + SaltSize)..(12 + SaltSize + NonceSize)];
            var tag = header[(12 + SaltSize + NonceSize)..HeaderSize];
            var cipher = new byte[headStream.Length - HeaderSize];
            headStream.ReadExactly(cipher);

            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, TagSize);
            try
            {
                gcm.Decrypt(nonce, cipher, tag, plain);
            }
            catch (CryptographicException)
            {
                failures.Add("密碼錯誤或包已損毀。");
                return null;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"helivms-evun-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(temp, plain);
            return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                FileOptions.DeleteOnClose);
        }

        return File.OpenRead(path);
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }

    private static string KindFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp4" or ".mov" or ".avi" or ".mkv" => "video",
        ".bmp" or ".jpg" or ".jpeg" or ".png" => "snapshot",
        ".json" => "manifest",
        ".sha256" => "hash",
        ".txt" or ".log" => "note",
        _ => "other",
    };
}