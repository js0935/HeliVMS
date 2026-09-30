using HeliVMS.Storage;
using HeliVMS.WebApi;

namespace HeliVMS.Api.Tests;

/// <summary>
/// 錄影保留策略（M132）回歸：清除必須一併刪除實體檔案、不得動到保存鎖定（M66），
/// 且不得因未設定根目錄而留下孤兒檔。
/// </summary>
public sealed class RetentionRunTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly string _outside;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly SettingsRepository _settings;
    private readonly LegalHoldRepository _holds;

    public RetentionRunTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-retention-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-retention-rec-{Guid.NewGuid():N}");
        _outside = Path.Combine(Path.GetTempPath(), $"helivms-retention-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _settings = new SettingsRepository(_store);
        _holds = new LegalHoldRepository(_store);

        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'ch1', 'rtsp://127.0.0.1:8554/ch1', NULL, 'h264', 1, 'copy');
            """);
    }

    [Fact]
    public void RunOnce_AgeExpired_DeletesFileAndRow()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var (file, _, _) = SeedSegment(daysOld: 10, sizeBytes: 1_000);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AgePurged);
        Assert.Equal(0, run.Skipped);
        Assert.Equal(1_000L, run.BytesFreed);
        Assert.False(File.Exists(file));
        Assert.Empty(_segments.ListFinal(1));
    }

    [Fact]
    public void RunOnce_AgeExpiredButFileLockedByHold_KeepsFileAndRow()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var (file, start, end) = SeedSegment(daysOld: 10, sizeBytes: 1_000);
        _holds.Add(1, start.AddMinutes(-1), end.AddMinutes(1), "investigation", "tester", DateTime.UtcNow);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(0, run.AgePurged);
        Assert.True(File.Exists(file));
        Assert.Single(_segments.ListFinal(1));
    }

    [Fact]
    public void RunOnce_FileOutsideRecordingsRoot_IsNeverDeleted()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var file = Path.Combine(_outside, "rogue.mp4");
        File.WriteAllBytes(file, new byte[1_000]);
        var start = DateTime.UtcNow.AddDays(-10);
        var id = _segments.BeginSegment(1, "main", file, start);
        _segments.CompleteSegment(id, start.AddSeconds(60), 1_000, 60, new string('a', 64));

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(0, run.AgePurged);
        Assert.True(run.Skipped >= 1);
        Assert.True(File.Exists(file));
        Assert.Single(_segments.ListFinal(1));
    }

    [Fact]
    public void RunOnce_WithoutRecordingsRoot_PurgesOnlyRowsWhoseFileIsGone()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var (present, _, _) = SeedSegment(daysOld: 10, sizeBytes: 1_000, channelDir: "ch001");
        var (gone, _, _) = SeedSegment(daysOld: 10, sizeBytes: 1_000, channelDir: "ch002", offsetSeconds: 2);
        File.Delete(gone);

        var run = new RetentionService(_store, _holds, recordingsRoot: null).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AgePurged);
        Assert.True(run.Skipped >= 1);
        Assert.True(File.Exists(present));
        var remaining = Assert.Single(_segments.ListFinal(1));
        Assert.Equal(present, remaining.FilePath);
    }

    [Fact]
    public void RunOnce_WatermarkExceeded_SkipsHeldOldestAndPurgesNext()
    {
        _settings.Set(RetentionService.DaysKey, "0");
        _settings.Set(RetentionService.WatermarkGbKey, "0.0000001");
        var (held, start, end) = SeedSegment(daysOld: 10, sizeBytes: 1_000, channelDir: "ch001");
        var (other, _, _) = SeedSegment(daysOld: 5, sizeBytes: 1_000, channelDir: "ch002");
        _holds.Add(1, start.AddMinutes(-1), end.AddMinutes(1), "investigation", "tester", DateTime.UtcNow);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.WatermarkPurged);
        Assert.True(File.Exists(held));
        Assert.False(File.Exists(other));
        Assert.Single(_segments.ListFinal(1));
    }

    private (string File, DateTime Start, DateTime End) SeedSegment(int daysOld, long sizeBytes, string channelDir = "ch001", double offsetSeconds = 0)
    {
        var dir = Path.Combine(_root, channelDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"seg-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[sizeBytes]);

        // start_time 以毫秒精度寫入且 (channel_id, stream, start_time) 有 UNIQUE 索引；
        // 同一測試內多筆 seed 若在同一毫秒內取 UtcNow 會撞唯一鍵，故用 offsetSeconds 確保相接 seed 不同秒。
        var start = DateTime.UtcNow.AddDays(-daysOld).AddSeconds(offsetSeconds);
        var end = start.AddSeconds(60);
        var id = _segments.BeginSegment(1, "main", path, start);
        _segments.CompleteSegment(id, end, sizeBytes, 60, new string('c', 64));
        return (path, start, end);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }

        foreach (var dir in new[] { _root, _outside })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
