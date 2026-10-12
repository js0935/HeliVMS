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

    /// <summary>
    /// 用量摘要要同時給出位元組與段數，且只算 final 段。
    ///
    /// 設定畫面以前是自己列舉錄影目錄算用量，但用
    /// <c>SearchOption.TopDirectoryOnly</c> 掃根層，而檔案在
    /// <c>recordings/chXXX/yyyyMMdd/</c> 兩層底下，所以用量長期顯示 0B。
    /// 改成讀區段索引之後，這裡就是那道口徑的唯一守護：
    /// 一旦把 tmp／corrupt 算進去，畫面上的數字會比配額清理實際使用的數字大，
    /// 使用者會看到「畫面寫 5GB、清理卻到 20GB 才動」，然後認為系統壞了。
    /// </summary>
    [Fact]
    public void 用量摘要只算final段並同時給出段數()
    {
        EnsureChannel(7);
        var t = DateTime.UtcNow;
        var a = _repo.BeginSegment(7, "main", @"D:\tmp\u1.mp4", t.AddHours(-3));
        _repo.CompleteSegment(a, t.AddHours(-2), 4096, 3600, "u1");
        var b = _repo.BeginSegment(7, "main", @"D:\tmp\u2.mp4", t.AddHours(-1));
        _repo.CompleteSegment(b, t, 1024, 3600, "u2");

        // 錄製中與損壞的段落不該被算進「已完成的錄影用量」。
        _repo.BeginSegment(7, "main", @"D:\tmp\u3.tmp", t.AddMinutes(-5));

        var (bytes, count) = _repo.GetTotalUsageSummary();

        Assert.Equal(4096 + 1024, bytes);
        Assert.Equal(2, count);
    }

    /// <summary>沒有任何錄影時用量是 0 與 0，不是例外。</summary>
    [Fact]
    public void 沒有錄影時用量摘要為零()
        => Assert.Equal((0L, 0), _repo.GetTotalUsageSummary());

    /// <summary>
    /// 用量摘要必須與 <see cref="SegmentRepository.GetTotalUsage"/> 同一個口徑。
    ///
    /// 兩者只要有一個納入 non-final 段，設定畫面顯示的數字就會和配額清理
    /// 實際使用的數字分岔，而這正是使用者看不出磁碟為什麼快滿的原因。
    /// </summary>
    [Fact]
    public void 用量摘要與總用量口徑一致()
    {
        EnsureChannel(8);
        var t = DateTime.UtcNow;
        var ok = _repo.BeginSegment(8, "main", @"D:\tmp\v1.mp4", t.AddHours(-1));
        _repo.CompleteSegment(ok, t, 2048, 3600, "v1");
        _repo.BeginSegment(8, "main", @"D:\tmp\v2.tmp", t.AddMinutes(-1));

        Assert.Equal(_repo.GetTotalUsage(), _repo.GetTotalUsageSummary().Bytes);
    }

    [Fact]
    public void Get_ReturnsSingleSegmentById()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(3, "main", @"D:\tmp\get-me.mp4", t);
        _repo.CompleteSegment(id, t.AddSeconds(30), 900, 30, "sha-get");

        var seg = _repo.Get(id);

        Assert.NotNull(seg);
        Assert.Equal(id, seg!.Id);
        Assert.Equal(@"D:\tmp\get-me.mp4", seg.FilePath);
        Assert.Equal("sha-get", seg.Sha256);
        Assert.Equal(SegmentStatus.Final, seg.Status);
    }

    [Fact]
    public void Get_UnknownIdReturnsNull()
    {
        Assert.Null(_repo.Get(987654321));
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