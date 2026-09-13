using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class RetentionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _repo;

    public RetentionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ret-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-ret-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentRepository(_store);
        EnsureChannel(1);
        EnsureChannel(2);
    }

    private void EnsureChannel(int id)
    {
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", $"頻道 {id}");
                cmd.Parameters.AddWithValue("$m", $"rtsp://127.0.0.1:8554/ch{id}");
            });
    }

    [Fact]
    public void Apply_OverQuota_DeletesOldestUntilFit()
    {
        Seed("ch001", 0, 4_000);
        Seed("ch001", 60, 4_000);
        Seed("ch002", 120, 4_000);
        Seed("ch002", 180, 4_000);

        var svc = new RetentionService(_repo, _root);
        var now = DateTime.UtcNow;

        var report = svc.Apply(quotaBytes: 8_000, nowUtc: now);

        Assert.Equal(8_000, report.UsedBytes);
        Assert.Equal(8_000, report.FreedBytes);
        Assert.Equal(2, report.DeletedSegments);
        Assert.Equal(8_000, _repo.GetTotalUsage());
    }

    [Fact]
    public void Apply_UnderQuota_RemovesNothing()
    {
        Seed("ch001", 0, 2_000);
        Seed("ch001", 60, 2_000);

        var svc = new RetentionService(_repo, _root);
        var report = svc.Apply(quotaBytes: 10_000, nowUtc: DateTime.UtcNow);

        Assert.Equal(0, report.DeletedSegments);
        Assert.Equal(2, _repo.ListFinal(1).Count);
    }

    [Fact]
    public void PurgeTmp_RemovesOnlyStaleFiles()
    {
        var stale = Path.Combine(_root, "stale.tmp");
        var fresh = Path.Combine(_root, "fresh.tmp");
        File.WriteAllBytes(stale, [0x01]);
        File.WriteAllBytes(fresh, [0x02]);

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(stale, now.AddMinutes(-30));
        File.SetLastWriteTimeUtc(fresh, now);

        var svc = new RetentionService(_repo, _root);
        var purged = svc.PurgeStaleTmp(now);

        Assert.Equal(1, purged);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Apply_DeletesFilesAndRows()
    {
        var file = Path.Combine(_root, "seg-old.mp4");
        File.WriteAllBytes(file, new byte[100]);

        var id = _repo.BeginSegment(1, "main", file, DateTime.UtcNow.AddMinutes(-30));
        _repo.CompleteSegment(id, DateTime.UtcNow.AddMinutes(-29), 100, 60, new string('b', 64));

        var svc = new RetentionService(_repo, _root);
        svc.Apply(quotaBytes: 0, nowUtc: DateTime.UtcNow);

        Assert.False(File.Exists(file));
        Assert.Empty(_repo.ListFinal(1));
    }

    private void Seed(string channelDir, double offsetSeconds, long sizeBytes)
    {
        var dir = Path.Combine(_root, channelDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"seg-{offsetSeconds:000}.mp4");
        File.WriteAllBytes(path, new byte[sizeBytes]);

        var start = DateTime.UtcNow.AddMinutes(-10).AddSeconds(-offsetSeconds);
        var id = _repo.BeginSegment(channelDir == "ch001" ? 1 : 2, "main", path, start);
        _repo.CompleteSegment(id, start.AddSeconds(60), sizeBytes, 60, new string('c', 64));
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

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}