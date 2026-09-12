using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class SegmentRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _repo;

    public SegmentRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentRepository(_store);
    }

    [Fact]
    public void Initialize_SetsWalMode()
    {
        var mode = _store.Query(
            "PRAGMA journal_mode;",
            static r =>
            {
                r.Read();
                return r.GetString(0);
            });

        Assert.Equal("wal", mode);
    }

    [Fact]
    public void BeginAndComplete_RoundTrips()
    {
        EnsureChannel(1);
        var start = DateTime.UtcNow.AddMinutes(-10);
        var end = start.AddSeconds(60);

        var id = _repo.BeginSegment(1, @"D:\tmp\seg-001.ts", start);
        _repo.CompleteSegment(id, end, 4096);

        var records = _repo.ListByRange(1, start.AddSeconds(-1), end.AddSeconds(1));

        var seg = Assert.Single(records);
        Assert.Equal(id, seg.Id);
        Assert.Equal(1, seg.ChannelId);
        Assert.Equal(@"D:\tmp\seg-001.ts", seg.FilePath);
        Assert.Equal(4096, seg.SizeBytes);
        Assert.Equal(SegmentStatus.Completed, seg.Status);
        Assert.Equal(start, seg.StartUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(end, seg.EndUtc!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ListByRange_FiltersByTime()
    {
        EnsureChannel(2);
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(2, @"D:\tmp\a.ts", t.AddHours(-3));
        _repo.CompleteSegment(id, t.AddHours(-2), 100);
        var id2 = _repo.BeginSegment(2, @"D:\tmp\b.ts", t.AddHours(-1));
        _repo.CompleteSegment(id2, t, 200);

        var early = _repo.ListByRange(2, t.AddHours(-4), t.AddHours(-2));
        var late = _repo.ListByRange(2, t.AddHours(-2), t.AddHours(1));

        Assert.Single(early);
        Assert.Equal("D:\\tmp\\a.ts", early[0].FilePath);
        Assert.Single(late);
        Assert.Equal("D:\\tmp\\b.ts", late[0].FilePath);
    }

    [Fact]
    public void MarkCorrupt_UpdatesStatus()
    {
        EnsureChannel(3);
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(3, @"D:\tmp\bad.ts", t);
        _repo.MarkCorrupt(id);

        var records = _repo.ListByRange(3, t.AddSeconds(-1), t.AddSeconds(1));

        Assert.Equal(SegmentStatus.Corrupt, Assert.Single(records).Status);
    }

    [Fact]
    public void GetChannelTotalSize_SumsCompletedOnly()
    {
        EnsureChannel(4);
        var t = DateTime.UtcNow;
        var idA = _repo.BeginSegment(4, @"D:\tmp\a.ts", t);
        _repo.CompleteSegment(idA, t.AddSeconds(10), 150);
        var idB = _repo.BeginSegment(4, @"D:\tmp\b.ts", t.AddSeconds(20));
        _repo.CompleteSegment(idB, t.AddSeconds(30), 350);
        var idC = _repo.BeginSegment(4, @"D:\tmp\c.ts", t.AddSeconds(40));
        _repo.MarkCorrupt(idC);

        Assert.Equal(500, _repo.GetChannelTotalSize(4));
    }

    private void EnsureChannel(int id)
    {
        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_url, enabled) VALUES ($id, $n, $u, 1);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", $"ch-{id}");
                cmd.Parameters.AddWithValue("$u", $"rtsp://test/{id}");
            });
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
            // 測試清理
        }
    }
}