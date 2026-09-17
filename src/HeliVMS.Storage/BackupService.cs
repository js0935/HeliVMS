using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>一次備份執行的統計結果。</summary>
public sealed record BackupRunResult(int Scanned, int Copied, long CopiedBytes, int Failed, bool Advanced);

/// <summary>
/// 異地備援（§14.4 P1）：將 final 錄影區段增量複製至目標目錄（第二磁碟／異地掛載），
/// 複製後以資料庫 SHA-256 比對確保完整性；有任一失敗即不推進檢查點，下次重跑再試。
/// </summary>
public sealed class BackupService
{
    private readonly SqliteStore _store;
    private readonly BackupRepository _backups;

    public BackupService(SqliteStore store)
    {
        _store = store;
        _backups = new BackupRepository(store);
    }

    public BackupService(SqliteStore store, BackupRepository backups)
    {
        _store = store;
        _backups = backups;
    }

    /// <summary>
    /// 執行一次備份：列出所有 final 區段中 start_time 晚於檢查點者，逐一複製至 targetRoot 下之相對路徑。
    /// </summary>
    public BackupRunResult Run(string sourceRoot, string targetRoot, DateTime? checkpointUtc = null)
    {
        var srcRoot = Path.GetFullPath(sourceRoot);
        var dstRoot = Path.GetFullPath(targetRoot);
        if (string.Equals(srcRoot.TrimEnd(Path.DirectorySeparatorChar),
                dstRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("備份目標不得等於來源目錄。");
        }

        Directory.CreateDirectory(dstRoot);
        var start = checkpointUtc ?? _backups.LastCheckpoint(srcRoot, dstRoot) ?? DateTime.MinValue;
        var candidates = new SegmentRepository(_store).ListAllFinal()
            .Where(s => s.StartUtc > start)
            .ToList();

        var copied = 0;
        long bytes = 0;
        var failed = 0;
        var detail = new List<string>();

        foreach (var seg in candidates)
        {
            var rel = ToRelative(srcRoot, seg.FilePath, seg.ChannelId);
            var target = Path.Combine(dstRoot, rel);
            try
            {
                if (!File.Exists(seg.FilePath))
                {
                    failed++;
                    detail.Add($"ch{seg.ChannelId} {seg.FilePath}：來源檔不存在");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(seg.FilePath, target, overwrite: true);
                if (!string.IsNullOrWhiteSpace(seg.Sha256))
                {
                    var actual = Sha256Hex(target);
                    if (!string.Equals(actual, seg.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        failed++;
                        detail.Add($"ch{seg.ChannelId} {Path.GetFileName(target)}：SHA-256 不符");
                        continue;
                    }
                }

                copied++;
                bytes += new FileInfo(target).Length;
            }
            catch (Exception ex)
            {
                failed++;
                detail.Add($"ch{seg.ChannelId} {Path.GetFileName(target)}：{ex.Message}");
            }
        }

        var now = DateTime.UtcNow;
        var detailText = string.Join(Environment.NewLine, detail);
        if (detailText.Length > 5000)
        {
            detailText = detailText[..5000];
        }

        _backups.RecordRun(
            now,
            srcRoot,
            dstRoot,
            failed == 0 ? now : null,
            copied,
            bytes,
            failed,
            detailText.Length == 0 ? null : detailText);

        return new BackupRunResult(candidates.Count, copied, bytes, failed, failed == 0);
    }

    /// <summary>來源絕對路徑→目標相對路徑；來源目錄外之檔案以 ch{n}/ 開頭鏡像。</summary>
    private static string ToRelative(string sourceRoot, string filePath, int channelId)
    {
        var full = Path.GetFullPath(filePath);
        var rel = Path.GetRelativePath(sourceRoot, full);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(rel))
        {
            return Path.Combine($"ch{channelId}", Path.GetFileName(full));
        }

        return rel;
    }

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}