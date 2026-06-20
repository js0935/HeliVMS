using System.IO;
using Serilog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using HeliVMS.Data;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class VideoIndexService : IVideoIndexService, IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(90);

    public VideoIndexService()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "video_index.db");
    }

    private const int MaxRetries = 3;

    private VideoIndexDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<VideoIndexDbContext>()
            .UseSqlite(MakeConnectionString())
            .Options;
        return new VideoIndexDbContext(options);
    }

    private VideoIndexDbContext CreateDbContextWithRetry()
    {
        var options = new DbContextOptionsBuilder<VideoIndexDbContext>()
            .UseSqlite(MakeConnectionString())
            .Options;
        return new VideoIndexDbContext(options);
    }

    private string MakeConnectionString()
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    private async Task<T> ExecWithRetryAsync<T>(Func<VideoIndexDbContext, Task<T>> operation)
    {
        for (int i = 0; i < MaxRetries; i++)
        {
            try
            {
                using var db = CreateDbContextWithRetry();
                return await operation(db).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
            {
                if (i == MaxRetries - 1) { throw; }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (i + 1))).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("Unreachable");
    }

    private async Task ExecWithRetryAsync(Func<VideoIndexDbContext, Task> operation)
    {
        for (int i = 0; i < MaxRetries; i++)
        {
            try
            {
                using var db = CreateDbContextWithRetry();
                await operation(db).ConfigureAwait(false);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
            {
                if (i == MaxRetries - 1) { throw; }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (i + 1))).ConfigureAwait(false);
            }
        }
    }

    public async Task EnsureDatabaseCreatedAsync()
    {
        using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);

        // Ensure new composite index for existing databases
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_VideoSegments_CameraId_StartTime_EndTime " +
            "ON VideoSegments(CameraId, StartTime, EndTime);").ConfigureAwait(false);

        // Drop old composite index (replaced by the new one above)
        await db.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS IX_VideoSegments_CameraId_StartTime;").ConfigureAwait(false);

        // Ensure FilePath index
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_VideoSegments_FilePath " +
            "ON VideoSegments(FilePath);").ConfigureAwait(false);

        // Ensure EndTime index (for PurgeOldRecordsAsync)
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_VideoSegments_EndTime " +
            "ON VideoSegments(EndTime);").ConfigureAwait(false);

        // Ensure new columns for integrity checking
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE VideoSegments ADD COLUMN IsCorrupted INTEGER NOT NULL DEFAULT 0;").ConfigureAwait(false);
        }
        catch { /* column may already exist */ }
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE VideoSegments ADD COLUMN IntegrityCheckedAt TEXT;").ConfigureAwait(false);
        }
        catch { /* column may already exist */ }

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE VideoSegments ADD COLUMN MotionScore REAL NOT NULL DEFAULT 0.0;").ConfigureAwait(false);
        }
        catch { }
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE VideoSegments ADD COLUMN MotionAnalysisDone INTEGER NOT NULL DEFAULT 0;").ConfigureAwait(false);
        }
        catch { }

        Log.Debug("[HeliVMS] VideoIndex DB ready at {DbPath}", _dbPath);
    }

    public async Task AddSegmentAsync(VideoSegment segment)
    {
        using var db = CreateDbContext();
        db.VideoSegments.Add(segment);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task AddSegmentsAsync(IEnumerable<VideoSegment> segments)
    {
        using var db = CreateDbContext();
        db.VideoSegments.AddRange(segments);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateEndTimeAsync(int segmentId, DateTime endTime, long fileSize)
    {
        using var db = CreateDbContext();
        var segment = await db.VideoSegments.FindAsync(segmentId).ConfigureAwait(false);
        if (segment is not null)
        {
            segment.EndTime = endTime;
            segment.FileSize = fileSize;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public async Task<List<VideoSegment>> QuerySegmentsAsync(string cameraId, DateTime from, DateTime to)
    {
        using var db = CreateDbContext();
        return await db.VideoSegments
            .Where(s => s.CameraId == cameraId
                     && s.StartTime < to
                     && (s.EndTime == null || s.EndTime > from))
            .OrderBy(s => s.StartTime)
            .AsNoTracking()
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<List<VideoSegment>> QuerySegmentsByCamerasAsync(
        IEnumerable<string> cameraIds, DateTime from, DateTime to)
    {
        var ids = cameraIds.ToList();
        if (ids.Count == 0) return new List<VideoSegment>();

        using var db = CreateDbContext();
        return await db.VideoSegments
                .Where(s => ids.Contains(s.CameraId)
                         && s.StartTime < to
                         && (s.EndTime == null || s.EndTime > from))
            .OrderBy(s => s.CameraId)
            .ThenBy(s => s.StartTime)
            .AsNoTracking()
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<VideoSegment?> GetLatestSegmentAsync(string cameraId)
    {
        using var db = CreateDbContext();
        return await db.VideoSegments
            .Where(s => s.CameraId == cameraId)
            .OrderByDescending(s => s.StartTime)
            .AsNoTracking()
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task DeleteSegmentAsync(int segmentId)
    {
        using var db = CreateDbContext();
        var segment = await db.VideoSegments.FindAsync(segmentId).ConfigureAwait(false);
        if (segment is not null)
        {
            db.VideoSegments.Remove(segment);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public async Task DeleteSegmentByPathAsync(string filePath)
    {
        using var db = CreateDbContext();
        var segments = await db.VideoSegments
            .Where(s => s.FilePath == filePath)
            .ToListAsync().ConfigureAwait(false);
        if (segments.Count > 0)
        {
            db.VideoSegments.RemoveRange(segments);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public async Task UpdateFilePathAsync(int segmentId, string filePath)
    {
        using var db = CreateDbContext();
        var segment = await db.VideoSegments.FindAsync(segmentId).ConfigureAwait(false);
        if (segment is not null)
        {
            segment.FilePath = filePath;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public async Task BulkUpdateEndTimeAsync(IEnumerable<(int SegmentId, DateTime EndTime, long FileSize)> updates)
    {
        var list = updates.ToList();
        if (list.Count == 0) { return; }

        using var db = CreateDbContext();
        foreach (var (segId, endTime, fileSize) in list)
        {
            var segment = await db.VideoSegments.FindAsync(segId).ConfigureAwait(false);
            if (segment is not null)
            {
                segment.EndTime = endTime;
                segment.FileSize = fileSize;
            }
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task BulkUpdateFilePathAsync(IEnumerable<(int SegmentId, string FilePath)> updates)
    {
        var list = updates.ToList();
        if (list.Count == 0) { return; }

        using var db = CreateDbContext();
        foreach (var (segId, filePath) in list)
        {
            var segment = await db.VideoSegments.FindAsync(segId).ConfigureAwait(false);
            if (segment is not null)
            {
                segment.FilePath = filePath;
            }
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Purge old records beyond the retention period</summary>
    public async Task PurgeOldRecordsAsync()
    {
        var cutoff = DateTime.UtcNow.Subtract(RetentionPeriod);
        using var db = CreateDbContext();
        var old = await db.VideoSegments
            .Where(s => s.EndTime != null && s.EndTime < cutoff)
            .ToListAsync().ConfigureAwait(false);
        if (old.Count > 0)
        {
            db.VideoSegments.RemoveRange(old);
            await db.SaveChangesAsync().ConfigureAwait(false);
            Log.Debug("[HeliVMS] Purged {Count} old video index records", old.Count);
        }
    }

    /// <summary>Clean up orphan segments (EndTime==null or missing .ts files)</summary>
    public async Task<int> CleanupOrphanSegmentsAsync()
    {
        using var db = CreateDbContext();
        var allSegments = await db.VideoSegments.ToListAsync().ConfigureAwait(false);

        int deleted = 0;
        int fixedCount = 0;

        foreach (var seg in allSegments)
        {
            var isRealFile = !string.IsNullOrEmpty(seg.FilePath)
                          && seg.FilePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                          && File.Exists(seg.FilePath);

            if (isRealFile)
            {
                if (seg.EndTime is null)
                {
                    var lastWrite = File.GetLastWriteTime(seg.FilePath);
                    var fileInfo = new FileInfo(seg.FilePath);
                    seg.EndTime = lastWrite > seg.StartTime ? lastWrite : seg.StartTime.AddMinutes(5);
                    seg.FileSize = fileInfo.Exists ? fileInfo.Length : 0;
                    fixedCount++;
                }
            }
            else
            {
                db.VideoSegments.Remove(seg);
                deleted++;
            }
        }

        if (fixedCount > 0 || deleted > 0)
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            Log.Debug("[HeliVMS] VideoIndex: cleaned up {Deleted} orphan segments, fixed {FixedCount} incomplete segments", deleted, fixedCount);
        }

        return deleted;
    }

    public async Task<(int DeletedSegments, long FreedBytes)> PurgeByRetentionPolicyAsync(
        int retentionDays, int maxStorageGB, string storagePath)
    {
        int totalDeleted = 0;
        long totalFreed = 0;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        using var db = CreateDbContext();

        // Phase 1: Delete old files beyond retention days
        var expired = await db.VideoSegments
            .Where(s => s.EndTime != null && s.EndTime < cutoff)
            .OrderBy(s => s.StartTime)
            .AsNoTracking()
            .ToListAsync().ConfigureAwait(false);

        foreach (var seg in expired)
        {
            if (DeleteFileIfExists(seg.FilePath))
                totalFreed += seg.FileSize;
            db.VideoSegments.Remove(seg);
            totalDeleted++;
        }

        // Phase 2: If hard limit is set, delete oldest segments when exceeded
        if (maxStorageGB > 0)
        {
            long maxBytes = (long)maxStorageGB * 1024L * 1024L * 1024L;

            // Query current total recording usage
            long currentBytes = await db.VideoSegments.SumAsync(s => (long?)s.FileSize ?? 0)
                .ConfigureAwait(false);

            if (currentBytes > maxBytes)
            {
                long excess = currentBytes - maxBytes;
                // Delete from oldest segment (exclude already-removed expired entries)
                var toDelete = await db.VideoSegments
                    .Where(s => s.EndTime != null)
                    .OrderBy(s => s.StartTime)
                    .AsNoTracking()
                    .ToListAsync().ConfigureAwait(false);

                foreach (var seg in toDelete)
                {
                    if (excess <= 0) { break; }
                    if (DeleteFileIfExists(seg.FilePath))
                    {
                        totalFreed += seg.FileSize;
                        excess -= seg.FileSize;
                    }
                    db.VideoSegments.Remove(seg);
                    totalDeleted++;
                }
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // Phase 3: Clean up empty directories (recursive, from date level up to root)
        try
        {
            if (Directory.Exists(storagePath))
                CleanEmptyDirectories(storagePath);
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] VideoIndex: empty dir cleanup error: {Msg}", ex.Message);
        }

        if (totalDeleted > 0)
        {
            var freedMb = totalFreed / (1024.0 * 1024);
            Log.Debug("[HeliVMS] VideoIndex: purged {TotalDeleted} segments, freed {FreedMb:F1} MB", totalDeleted, freedMb);
        }

        return (totalDeleted, totalFreed);
    }

    private static bool DeleteFileIfExists(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        try
        {
            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                long size = fi.Length;
                File.Delete(filePath);
                var sizeKb = size / 1024.0;
                Log.Debug("[HeliVMS] VideoIndex: deleted {FilePath} ({SizeKb:F1} KB)", filePath, sizeKb);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] VideoIndex: failed to delete {FilePath}: {Msg}", filePath, ex.Message);
        }
        return false;
    }

    private static void CleanEmptyDirectories(string root)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            CleanEmptyDirectories(dir);
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
            {
                try { Directory.Delete(dir); } catch { }
            }
        }
    }

    public async Task<StorageInfo> GetStorageInfoAsync(string storagePath)
    {
        var info = new StorageInfo { StoragePath = storagePath };

        try
        {
            var root = Path.GetPathRoot(storagePath);
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    info.TotalBytes = drive.TotalSize;
                    info.FreeBytes = drive.AvailableFreeSpace;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] VideoIndexService: DriveInfo error: {Msg}", ex.Message);
        }

        try
        {
            using var db = CreateDbContext();
            var perCamera = await db.VideoSegments
                .GroupBy(s => s.CameraId)
                .Select(g => new CameraStorageUsage
                {
                    CameraId = g.Key,
                    TotalBytes = g.Sum(s => s.FileSize),
                    SegmentCount = g.Count()
                })
                .AsNoTracking()
                .ToListAsync().ConfigureAwait(false);

            info.PerCamera = perCamera;
            info.TotalRecordingBytes = perCamera.Sum(c => c.TotalBytes);
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] VideoIndexService: per-camera stats error: {Msg}", ex.Message);
        }

        return info;
    }

    public async Task UpdateIntegrityStatusAsync(IEnumerable<(int SegmentId, bool IsCorrupted)> updates)
    {
        await ExecWithRetryAsync(async db =>
        {
            var list = updates.ToList();
            if (list.Count == 0) { return; }

            var ids = new List<long>(list.Count);
            for (int ui = 0; ui < list.Count; ui++) { ids.Add(list[ui].SegmentId); }
            var segments = await db.VideoSegments
                .Where(s => ids.Contains(s.Id))
                .ToListAsync().ConfigureAwait(false);

            var updateDict = list.ToDictionary(u => u.SegmentId, u => u.IsCorrupted);
            foreach (var seg in segments)
            {
                if (updateDict.TryGetValue(seg.Id, out var isCorrupted))
                {
                    seg.IsCorrupted = isCorrupted;
                    seg.IntegrityCheckedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<List<VideoSegment>> QueryUncheckedSegmentsAsync(int limit = 50)
    {
        return await ExecWithRetryAsync(async db =>
        {
            return await db.VideoSegments
                .Where(s => s.IntegrityCheckedAt == null)
                .OrderBy(s => s.Id)
                .Take(limit)
                .AsNoTracking()
                .ToListAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<(int TotalSegments, int CheckedSegments, int CorruptedSegments)> GetIntegrityStatsAsync()
    {
        return await ExecWithRetryAsync(async db =>
        {
            var total = await db.VideoSegments.CountAsync().ConfigureAwait(false);
            var checkedCount = await db.VideoSegments.CountAsync(s => s.IntegrityCheckedAt != null).ConfigureAwait(false);
            var corrupted = await db.VideoSegments.CountAsync(s => s.IsCorrupted).ConfigureAwait(false);
            return (total, checkedCount, corrupted);
        }).ConfigureAwait(false);
    }

    public async Task UpdateMotionScoreAsync(int segmentId, double motionScore)
    {
        using var db = CreateDbContext();
        var segment = await db.VideoSegments.FindAsync(segmentId).ConfigureAwait(false);
        if (segment is not null)
        {
            segment.MotionScore = motionScore;
            segment.MotionAnalysisDone = true;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public async Task<List<VideoSegment>> QueryMotionSegmentsAsync(string cameraId, DateTime from, DateTime to, double minMotionScore = 0.4)
    {
        return await ExecWithRetryAsync(async db =>
        {
            return await db.VideoSegments
                .Where(s => s.CameraId == cameraId
                         && s.StartTime < to
                         && (s.EndTime == null || s.EndTime > from)
                         && s.MotionAnalysisDone
                         && s.MotionScore >= minMotionScore)
                .OrderBy(s => s.StartTime)
                .AsNoTracking()
                .ToListAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<List<VideoSegment>> QueryUnanalyzedMotionSegmentsAsync(int limit = 20)
    {
        return await ExecWithRetryAsync(async db =>
        {
            return await db.VideoSegments
                .Where(s => !s.MotionAnalysisDone && s.EndTime != null)
                .OrderByDescending(s => s.EndTime)
                .Take(limit)
                .AsNoTracking()
                .ToListAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<int> BulkUpdateFilePathPrefixAsync(string oldPrefix, string newPrefix)
    {
        using var db = CreateDbContext();
        // Normalize separators
        oldPrefix = oldPrefix.Replace('/', '\\').TrimEnd('\\');
        newPrefix = newPrefix.Replace('/', '\\').TrimEnd('\\');

        var segments = await db.VideoSegments
            .Where(s => s.FilePath.StartsWith(oldPrefix))
            .AsNoTracking()
            .ToListAsync().ConfigureAwait(false);

        foreach (var seg in segments)
        {
            var relative = seg.FilePath.Substring(oldPrefix.Length);
            seg.FilePath = newPrefix + relative;
            db.VideoSegments.Update(seg);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        Log.Debug("[HeliVMS] VideoIndex: bulk updated {Count} file paths: {OldPrefix} -> {NewPrefix}", segments.Count, oldPrefix, newPrefix);
        return segments.Count;
    }

    public async Task<Dictionary<DateTime, (int SegmentCount, long TotalBytes)>> GetDailyRecordingStatsAsync(
        DateTime monthStart, DateTime monthEnd, string? cameraId = null)
    {
        return await ExecWithRetryAsync(async db =>
        {
            var query = db.VideoSegments
                .Where(s => s.StartTime >= monthStart && s.StartTime < monthEnd);

            if (!string.IsNullOrEmpty(cameraId))
            {
                query = query.Where(s => s.CameraId == cameraId);
            }

            var segments = await query
                .Select(s => new { s.StartTime, s.EndTime, s.FileSize })
                .AsNoTracking()
                .ToListAsync().ConfigureAwait(false);

            var daily = new Dictionary<DateTime, (int SegmentCount, long TotalBytes)>(31);
            foreach (var seg in segments)
            {
                var day = seg.StartTime.Date;
                if (daily.TryGetValue(day, out var existing))
                    daily[day] = (existing.SegmentCount + 1, existing.TotalBytes + seg.FileSize);
                else
                    daily[day] = (1, seg.FileSize);
            }
            return daily;
        }).ConfigureAwait(false);
    }

    private static IEnumerable<string> CollectTsFilesRecursive(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            for (int si = 0; si < subDirs.Length; si++)
                stack.Push(subDirs[si]);

            string[] files;
            try { files = Directory.GetFiles(dir, "*.ts"); }
            catch { continue; }

            for (int fi = 0; fi < files.Length; fi++)
                yield return files[fi];
        }
    }

    public async Task<(int Added, int Deleted)> RebuildRecordingIndexAsync(string storagePath)
    {
        int added = 0;
        int deleted = 0;

        if (!Directory.Exists(storagePath))
        {
            Log.Debug("[HeliVMS] VideoIndex: storage path {Path} does not exist, skipping rebuild", storagePath);
            return (0, 0);
        }

        using var db = CreateDbContext();
        var knownPaths = new HashSet<string>(
            await db.VideoSegments
                .Where(s => s.FilePath != null)
                .Select(s => s.FilePath!)
                .ToListAsync().ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        var tsFiles = CollectTsFilesRecursive(storagePath);
        var batch = new List<VideoSegment>();

        const int chunkSize = 100;

        foreach (var filePath in tsFiles)
        {
            if (knownPaths.Contains(filePath)) { continue; }

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)_(\d{8})_(\d{6})$");
            if (!match.Success) { continue; }

            var cameraId = match.Groups[1].Value;
            var dateStr = match.Groups[2].Value;
            var timeStr = match.Groups[3].Value;

            if (!DateTime.TryParseExact(dateStr + timeStr, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var startTime))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            batch.Add(new VideoSegment
            {
                CameraId = cameraId,
                StartTime = startTime,
                EndTime = fileInfo.LastWriteTime,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                RecordType = 0,
            });

            if (batch.Count >= chunkSize)
            {
                db.VideoSegments.AddRange(batch);
                await db.SaveChangesAsync().ConfigureAwait(false);
                added += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            db.VideoSegments.AddRange(batch);
            await db.SaveChangesAsync().ConfigureAwait(false);
            added += batch.Count;
        }

        deleted = await CleanupOrphanSegmentsAsync().ConfigureAwait(false);

        Log.Information("[HeliVMS] VideoIndex: rebuild complete — added {Added}, deleted {Deleted} segments", added, deleted);
        return (added, deleted);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
