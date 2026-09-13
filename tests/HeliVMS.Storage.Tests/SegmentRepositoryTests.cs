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
        EnsureChannel(1);
        EnsureChannel(2);
        EnsureChannel(3);
        EnsureChannel(4);
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
        var start = DateTime.UtcNow.AddMinutes(-10);
        var end = start.AddSeconds(60);

        var id = _repo.BeginSegment(1, "main", @"D:\tmp\seg-001.mp4", start);
        _repo.CompleteSegment(id, end, 4096, 60, new string('a', 64));

        var records = _repo.ListByRange(1, "main", start.AddSeconds(-1), end.AddSeconds(1));

        var seg = Assert.Single(records);
        Assert.Equal(id, seg.Id);
        Assert.Equal(1, seg.ChannelId);
        Assert.Equal("main", seg.Stream);
        Assert.Equal(@"D:\tmp\seg-001.mp4", seg.FilePath);
        Assert.Equal(4096, seg.SizeBytes);
        Assert.Equal(60, seg.DurationSec);
        Assert.Equal(new string('a', 64), seg.Sha256);
        Assert.Equal(SegmentStatus.Final, seg.Status);
        Assert.Equal("mp4", seg.Format);
        Assert.Equal(start, seg.StartUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(end, seg.EndUtc!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ListByRange_FiltersByTimeAndStream()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(2, "main", @"D:\tmp\a.mp4", t.AddHours(-3));
        _repo.CompleteSegment(id, t.AddHours(-2), 100, 3600, "h1");
        var id2 = _repo.BeginSegment(2, "main", @"D:\tmp\b.mp4", t.AddHours(-1));
        _repo.CompleteSegment(id2, t, 200, 3600, "h2");
        _repo.BeginSegment(2, "sub", @"D:\tmp\c.mp4", t.AddHours(-3));

        var early = _repo.ListByRange(2, "main", t.AddHours(-4), t.AddHours(-2));
        var late = _repo.ListByRange(2, "main", t.AddHours(-2), t.AddHours(1));

        Assert.Single(early);
        Assert.Equal("D:\\tmp\\a.mp4", early[0].FilePath);
        Assert.Single(late);
        Assert.Equal("D:\\tmp\\b.mp4", late[0].FilePath);

        // sub 串流不混入 main 查詢
        Assert.Empty(_repo.ListByRange(2, "sub", t.AddHours(-2.5), t.AddHours(-2)));
    }

    [Fact]
    public void MarkCorrupt_UpdatesStatus()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(3, "main", @"D:\tmp\bad.mp4", t);
        _repo.MarkCorrupt(id);

        var records = _repo.ListByRange(3, "main", t.AddSeconds(-1), t.AddSeconds(1));

        Assert.Equal(SegmentStatus.Corrupt, Assert.Single(records).Status);
    }

    [Fact]
    public void GetChannelTotalSize_SumsFinalOnly()
    {
        var t = DateTime.UtcNow;
        var idA = _repo.BeginSegment(4, "main", @"D:\tmp\a.mp4", t);
        _repo.CompleteSegment(idA, t.AddSeconds(30), 150, 30, "h1");
        var idB = _repo.BeginSegment(4, "main", @"D:\tmp\b.mp4", t.AddSeconds(60));
        _repo.CompleteSegment(idB, t.AddSeconds(90), 350, 30, "h2");
        var idC = _repo.BeginSegment(4, "main", @"D:\tmp\c.mp4", t.AddSeconds(120));
        _repo.MarkCorrupt(idC);
        var idD = _repo.BeginSegment(4, "main", @"D:\tmp\d.mp4", t.AddSeconds(180));

        // 只有 final 計入（tmp 不計）
        Assert.Equal(500, _repo.GetChannelTotalSize(4));
    }

    [Fact]
    public void Iso_And_FromIso_RoundTrip()
    {
        var utc = new DateTime(2026, 9, 13, 7, 30, 15, 123, DateTimeKind.Utc);
        var iso = SqliteStore.Iso(utc);
        Assert.Equal("2026-09-13T07:30:15.123Z", iso);
        Assert.Equal(utc, SqliteStore.FromIso(iso));
    }

    [Fact]
    public void ChannelRepository_AddAndList_Works()
    {
        var channels = new ChannelRepository(_store);
        var id = channels.Add("測試", "rtsp://127.0.0.1/x", "rtsp://127.0.0.1/y");
        var list = channels.List();

        Assert.Contains(list, c => c.Id == id && c.Name == "測試" && c.SubStreamUrl == "rtsp://127.0.0.1/y");
    }

    [Fact]
    public void DeviceRepository_DpapiProtect_RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string secret = "NVR-密碼-123";
        var encrypted = DeviceRepository.Protect(secret);
        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, DeviceRepository.Unprotect(encrypted));
    }

    private void EnsureChannel(int id)
    {
        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES ($id, $n, $u, 0);",
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