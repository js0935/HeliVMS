using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 錄影留存清理。這個類別會<b>刪除使用者的錄影檔</b>，所以測的重點不是「有沒有呼叫
/// Delete」，而是「什麼情況下絕對不可以刪」。
///
/// 全部只碰真實檔案系統，不需要 ffmpeg：這裡沒有解碼、沒有 RTSP、沒有行程。
/// </summary>
public class RetentionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly string _outsideRoot;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly LegalHoldRepository _legalHolds;

    public RetentionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-retention-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-retention-{Guid.NewGuid():N}");
        _outsideRoot = Path.Combine(Path.GetTempPath(), $"helivms-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _legalHolds = new LegalHoldRepository(_store);
        EnsureChannel(1);
    }

    /// <summary>
    /// 配額超出時必須<b>從最舊的</b> 開始刪。
    ///
    /// 順序反了的症狀：系統保留最近一小時、刪掉上個月的重要片段，容量一樣、
    /// 時間軸一樣，操作員完全看不出差別——只是該留的沒留。
    /// </summary>
    [Fact]
    public void 配額超出時從最舊的區段開始刪()
    {
        var baseUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldest = SeedFinal(baseUtc, 1000);
        var middle = SeedFinal(baseUtc.AddHours(1), 1000);
        var newest = SeedFinal(baseUtc.AddHours(2), 1000);

        // 總量 3000、配額 1500：刪到剩 1000 才低於配額，所以剛好刪兩個。
        var report = NewService().Apply(quotaBytes: 1500, nowUtc: baseUtc.AddHours(3));

        Assert.Equal(2, report.DeletedSegments);
        Assert.Null(_segments.Get(oldest.Id));
        Assert.Null(_segments.Get(middle.Id));
        Assert.NotNull(_segments.Get(newest.Id));
        Assert.True(File.Exists(newest.FilePath), "保留下來的區段其實被刪了");
        Assert.False(File.Exists(oldest.FilePath));
    }

    /// <summary>配額以內時一個檔案都不能少。</summary>
    [Fact]
    public void 配額以內時不刪任何區段()
    {
        var seg = SeedFinal(DateTime.UtcNow.AddHours(-1), 1000);

        var report = NewService().Apply(quotaBytes: 5000);

        Assert.Equal(0, report.DeletedSegments);
        Assert.NotNull(_segments.Get(seg.Id));
        Assert.True(File.Exists(seg.FilePath));
    }

    /// <summary>
    /// 索引列若被污染成指向錄影根目錄<b>之外</b> 的路徑，清理必須拒絕刪檔。
    ///
    /// 這是資料庫變成攻擊面的那條路：任何能寫 segments.file_path 的途徑
    /// （匯入的備份、被竄改的資料庫）都會讓這個服務去刪根目錄以外的檔案。
    /// 預期是保留索引列並停止，留給人去查，而不是刪檔。
    /// </summary>
    [Fact]
    public void 路徑在錄影根目錄外的區段不會被刪掉()
    {
        var victim = Path.Combine(_outsideRoot, "important.txt");
        File.WriteAllText(victim, "不可刪");
        var seg = SeedFinal(DateTime.UtcNow.AddHours(-1), 1000, victim);

        var report = NewService().Apply(quotaBytes: 0);

        Assert.Equal(0, report.DeletedSegments);
        Assert.True(File.Exists(victim), "錄影根目錄外的檔案被刪了");
        Assert.True(_segments.Get(seg.Id) is not null, "索引列應保留待查");
    }

    /// <summary>
    /// 受法律保存覆蓋的時段不得被淘汰。
    ///
    /// 這是資料遺失裡最不能重來的一種：保存通常是為了訴訟或調查取的證據，刪掉拿不回來，
    /// 而且配額壓力下不會有人注意到。
    /// </summary>
    [Fact]
    public void 受法律保存的時段不會被淘汰()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var held = SeedFinal(start, 1000);
        var other = SeedFinal(start.AddHours(2), 1000);

        // 保存窗只剛好蓋住第一個區段（00:00-00:10），第二個在 02:00 必須照常被淘汰。
        _legalHolds.Add(1, start.AddMinutes(-1), start.AddMinutes(11), "訴訟保全", "admin", start);

        var report = NewService().Apply(quotaBytes: 0, nowUtc: start.AddHours(3));

        Assert.Equal(1, report.DeletedSegments);
        Assert.True(_segments.Get(held.Id) is not null, "受保存的區段被淘汰了");
        Assert.True(File.Exists(held.FilePath));
        Assert.Null(_segments.Get(other.Id));
    }

    /// <summary>
    /// 尚未收尾（大小為 0）的區段不能拿來抵配額，否則配額永遠降不下來。
    /// 這裡也順便驗證不會陷入無限迴圈。
    /// </summary>
    [Fact]
    public void 尚未收尾的區段不會被拿來抵配額()
    {
        var recording = _segments.BeginSegment(
            1, "main", Path.Combine(_root, "in-progress.mp4"), DateTime.UtcNow.AddMinutes(-1));

        var report = NewService().Apply(quotaBytes: 0);

        Assert.Equal(0, report.DeletedSegments);
        Assert.NotNull(_segments.Get(recording));
    }

    /// <summary>
    /// 檔案已不在磁碟上的區段，索引列仍要清掉。
    ///
    /// 現場症狀：磁碟早就手動清過，但時間軸與容量統計一直對不上，而且配額永遠觸發不了
    /// （這些列的 size 還算在內），於是磁碟真的滿了卻沒有任何清理動作。
    /// </summary>
    [Fact]
    public void 檔案已不存在的區段仍會從索引移除()
    {
        var seg = SeedFinal(DateTime.UtcNow.AddHours(-1), 1000);
        File.Delete(seg.FilePath);
        Assert.False(File.Exists(seg.FilePath));

        var report = NewService().Apply(quotaBytes: 0);

        Assert.Equal(1, report.DeletedSegments);
        Assert.Null(_segments.Get(seg.Id));
    }

    /// <summary>仍在錄影中的 *.tmp 暫存檔不能被清掉，否則會把正在寫的錄影砍斷。</summary>
    [Fact]
    public void 只清掉逾時的暫存檔()
    {
        var stale = Path.Combine(_root, "stale.tmp");
        var fresh = Path.Combine(_root, "fresh.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(stale, now.AddHours(-1));
        File.SetLastWriteTimeUtc(fresh, now);

        var purged = NewService().PurgeStaleTmp(now);

        Assert.Equal(1, purged);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh), "還在寫的暫存檔被刪了");
    }

    /// <summary>快照只清理早於門檻的檔案，並回報實際釋放的位元組數。</summary>
    [Fact]
    public void 快照只清理早於門檻的檔案()
    {
        var snaps = Path.Combine(_root, "snapshots");
        Directory.CreateDirectory(snaps);
        var old = Path.Combine(snaps, "old.jpg");
        var keep = Path.Combine(snaps, "keep.jpg");
        File.WriteAllText(old, new string('x', 100));
        File.WriteAllText(keep, new string('x', 100));
        var threshold = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(old, threshold.AddHours(-2));
        File.SetLastWriteTimeUtc(keep, threshold.AddHours(1));

        var report = NewService().PurgeSnapshots(snaps, threshold);

        Assert.Equal(1, report.DeletedFiles);
        Assert.Equal(100, report.FreedBytes);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(keep));
    }

    /// <summary>錄影根目錄不存在時不能丟例外——背景服務不該因為磁碟沒接就死掉。</summary>
    [Fact]
    public void 錄影根目錄不存在時不會丟例外()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"helivms-absent-{Guid.NewGuid():N}");

        var service = new RetentionService(_segments, absent);

        Assert.Equal(0, service.PurgeStaleTmp(DateTime.UtcNow));
    }

    private RetentionService NewService() => new(_segments, _root, _legalHolds);

    /// <summary>建立一個已收尾（final）的區段：索引列與磁碟檔案都真的存在。</summary>
    private (long Id, string FilePath) SeedFinal(
        DateTime startUtc, long sizeBytes, string? filePath = null)
    {
        var file = filePath ?? Path.Combine(_root, $"seg-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(file, new byte[1]);
        var id = _segments.BeginSegment(1, "main", file, startUtc);
        _segments.CompleteSegment(id, startUtc.AddMinutes(10), sizeBytes, 600, "sha256");
        return (id, file);
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
        foreach (var dir in new[] { _root, _outsideRoot })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試清理失敗不該讓測試紅掉
            }
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}