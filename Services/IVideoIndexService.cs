using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IVideoIndexService
{
    /// <summary>Add a video segment to the index</summary>
    Task AddSegmentAsync(VideoSegment segment);

    /// <summary>Add multiple video segments to the index</summary>
    Task AddSegmentsAsync(IEnumerable<VideoSegment> segments);

    /// <summary>Update EndTime and FileSize for a segment</summary>
    Task UpdateEndTimeAsync(int segmentId, DateTime endTime, long fileSize);

    /// <summary>Query segments by camera ID and time range</summary>
    Task<List<VideoSegment>> QuerySegmentsAsync(string cameraId, DateTime from, DateTime to);

    /// <summary>Query segments by multiple camera IDs and time range</summary>
    Task<List<VideoSegment>> QuerySegmentsByCamerasAsync(IEnumerable<string> cameraIds, DateTime from, DateTime to);

    /// <summary>Get the latest segment for a camera</summary>
    Task<VideoSegment?> GetLatestSegmentAsync(string cameraId);

    /// <summary>Delete a segment by ID</summary>
    Task DeleteSegmentAsync(int segmentId);

    /// <summary>Delete a segment by file path</summary>
    Task DeleteSegmentByPathAsync(string filePath);

    /// <summary>Update the file path for a segment</summary>
    Task UpdateFilePathAsync(int segmentId, string filePath);

    /// <summary>Bulk update EndTime and FileSize for multiple segments</summary>
    Task BulkUpdateEndTimeAsync(IEnumerable<(int SegmentId, DateTime EndTime, long FileSize)> updates);

    /// <summary>Bulk update FilePath for multiple segments</summary>
    Task BulkUpdateFilePathAsync(IEnumerable<(int SegmentId, string FilePath)> updates);

    /// <summary>Ensure the database exists and is created</summary>
    Task EnsureDatabaseCreatedAsync();

    /// <summary>Clean up orphan segments (EndTime==null or missing .ts files)</summary>
    Task<int> CleanupOrphanSegmentsAsync();

    /// <summary>取得各頻道錄影用量統計與磁碟空間資訊</summary>
    Task<StorageInfo> GetStorageInfoAsync(string storagePath);

    /// <summary>
    /// 依保留政策清除過期錄影檔案與 DB 記錄。
    /// retentionDays: 保留天數（過期刪除）
    /// maxStorageGB: 硬上限 GB（0=不限制），超過時從最舊開始刪
    /// storagePath: 錄影儲存根目錄（用於磁碟空間檢查）
    /// returns (deletedFiles, freedBytes)
    /// </summary>
    Task<(int DeletedSegments, long FreedBytes)> PurgeByRetentionPolicyAsync(
        int retentionDays, int maxStorageGB, string storagePath);

    /// <summary>批次更新 FilePath 中符合 oldPrefix 的路徑置換為 newPrefix</summary>
    Task<int> BulkUpdateFilePathPrefixAsync(string oldPrefix, string newPrefix);

    /// <summary>Batch update integrity status for segments</summary>
    Task UpdateIntegrityStatusAsync(IEnumerable<(int SegmentId, bool IsCorrupted)> updates);

    /// <summary>Scan recording directory for .ts files, add missing entries to DB, clean orphans</summary>
    Task<(int Added, int Deleted)> RebuildRecordingIndexAsync(string storagePath);

    /// <summary>Query segments that haven't been integrity-checked yet</summary>
    Task<List<VideoSegment>> QueryUncheckedSegmentsAsync(int limit = 50);

    /// <summary>Get integrity summary stats</summary>
    Task<(int TotalSegments, int CheckedSegments, int CorruptedSegments)> GetIntegrityStatsAsync();

    /// <summary>Update motion score for a segment</summary>
    Task UpdateMotionScoreAsync(int segmentId, double motionScore);

    /// <summary>Find segments with motion above threshold within time range</summary>
    Task<List<VideoSegment>> QueryMotionSegmentsAsync(string cameraId, DateTime from, DateTime to, double minMotionScore = 0.4);

    /// <summary>Get segments that haven't had motion analysis done</summary>
    Task<List<VideoSegment>> QueryUnanalyzedMotionSegmentsAsync(int limit = 20);

    /// <summary>Get daily recording stats for a month range (used in calendar heatmap)</summary>
    Task<Dictionary<DateTime, (int SegmentCount, long TotalBytes)>> GetDailyRecordingStatsAsync(DateTime monthStart, DateTime monthEnd, string? cameraId = null);
}
