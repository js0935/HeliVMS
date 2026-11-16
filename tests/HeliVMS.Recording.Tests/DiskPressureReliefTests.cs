using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 磁碟滿時的即時壓力汰除（§11.1 第四階段）。
///
/// <para>
/// 這個類別會<b>真的刪除錄影檔</b>，所以測的重點是「什麼時候可以先刪、什麼時候必須停手」，
/// 以及「刪完之後的回答是不是磁碟的實況」。現場症狀是「磁碟一到門檻就整台停止錄影，
/// 要等下一個小時的保留迴圈才第一次刪檔」——實測停了 39 分鐘（12:00:51 進入
/// <c>recording.disk_full</c>、12:39:55 才解除），那段時間的畫面永遠補不回來。
/// </para>
/// </summary>
public sealed class DiskPressureReliefTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly LegalHoldRepository _legalHolds;
    private readonly AuditLogRepository _audit;

    // 注入式磁碟模型：可用空間＝基準 free ＋（種下時的索引用量 − 目前索引用量）。
    // 刪掉一列，索引用量下降，模型裡的可用空間等量回升——與真實磁碟「刪檔即騰出空間」一致，
    // 且不依賴檔案真實大小（種子檔只有 1 byte）。與 RetentionServiceTests 同一套做法。
    private long _baseFree;
    private long _seededUsage;

    public DiskPressureReliefTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-relief-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-relief-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _legalHolds = new LegalHoldRepository(_store);
        _audit = new AuditLogRepository(_store);
        EnsureChannel(1);

        // 門檻 40 bytes：測試用極小的數字，磁碟模型才不必真的灌滿一顆碟。
        new SettingsRepository(_store).Set(RecordingStorage.MinFreeSettingKey, "40");
    }

    /// <summary>
    /// 磁碟低於門檻時，閘門要<b>先淘汰最舊的錄影</b>，然後因為空間已經回來而允許開新錄影。
    ///
    /// <para>
    /// 這是整個修正的重點：舊行為是「低於門檻就停錄、等每小時的保留迴圈」，新行為是
    /// 「低於門檻就先刪最舊的，刪完還有空間就照錄」。順序反了（先回答停錄才刪）症狀一模一樣：
    /// 錄影停一小時，只是掉檔的時間點不同。
    /// </para>
    /// </summary>
    [Fact]
    public void 磁碟低於門檻時會先淘汰最舊錄影再回報可以錄影()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var old = SeedFinal(now.AddHours(-3), 1000);
        var recent = SeedFinal(now.AddMinutes(-10), 30);
        SeedDrive(baseFree: 30); // 低於門檻 40

        var stop = NewRelief(now).ShouldStopNewRecording(_store, _root, UsageFor, _audit);

        Assert.False(stop, "刪出空間之後就不該再停新錄");
        Assert.False(File.Exists(old.FilePath), "最舊的段要為了騰空間被淘汰");
        Assert.Null(_segments.Get(old.Id));
        Assert.True(File.Exists(recent.FilePath), "最新的段要保住");
    }

    /// <summary>
    /// 沒東西可淘汰（全受保存鎖定）時仍然要回報停新錄——不能把「刪不掉」說成「有空間」。
    /// </summary>
    [Fact]
    public void 沒有可淘汰的段時仍然回報停新錄()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var seg = SeedFinal(now.AddHours(-3), 1000);
        _legalHolds.Add(1, now.AddHours(-4), now, "案件 A", "tester", DateTime.UtcNow);
        SeedDrive(baseFree: 30);

        var stop = NewRelief(now).ShouldStopNewRecording(_store, _root, UsageFor, _audit);

        Assert.True(stop, "沒有東西可刪、空間仍低於門檻，就該停新錄");
        Assert.True(File.Exists(seg.FilePath), "被保全的錄影不能為了騰空間被刪");
    }

    /// <summary>
    /// 已經刪不出空間時，短時間內不要每 30 秒重掃一次索引與磁碟。
    ///
    /// <para>
    /// 排程器每 30 秒問一次；沒有節流時，一台「全部錄影都被保全鎖住」的機器會每 30 秒
    /// 走一次 SQLite 查詢＋目錄 I/O 卻永遠得到同一個答案，負載白花而且沒有任何好處。
    /// </para>
    /// </summary>
    [Fact]
    public void 刪不出空間後短時間內不會重複掃描()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedDrive(baseFree: 30);

        var attempts = 0;
        var relief = NewRelief(
            now,
            (store, root) =>
            {
                attempts++;
                return new RetentionService(new SegmentRepository(store), root, new LegalHoldRepository(store));
            });

        Assert.True(relief.ShouldStopNewRecording(_store, _root, UsageFor, _audit));
        Assert.Equal(1, attempts);

        // 一分鐘後（排程器已經問了兩輪）：不再嘗試淘汰，但答案仍是停新錄。
        Assert.True(relief.ShouldStopNewRecording(_store, _root, UsageFor, _audit));
        Assert.Equal(1, attempts);

        // 超過節流間隔：重新嘗試一次。
        Advance(TimeSpan.FromMinutes(6));
        Assert.True(relief.ShouldStopNewRecording(_store, _root, UsageFor, _audit));
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// 沒達到「門檻＋一小時寫入量」的補充水位、但可用空間已經回到門檻之上時，要恢復錄影。
    ///
    /// <para>
    /// <see cref="RetentionService.ApplyPressure"/> 的目標是「再撐一小時」，那個數字在只剩
    /// 少量可汰除的段時永遠到不了；能不能開新錄影問的卻是「有沒有高於門檻」。把兩者混用，
    /// 會讓一台明明已經騰出空間的機器繼續停錄——正是要修掉的症狀換一個地方重現。
    /// </para>
    /// </summary>
    [Fact]
    public void 沒達到補充水位但已高於門檻時會恢復錄影()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var seg = SeedFinal(now.AddHours(-3), 100);
        SeedDrive(baseFree: 30);

        var stop = NewRelief(now).ShouldStopNewRecording(_store, _root, UsageFor, _audit);

        Assert.False(stop, "130 > 門檻 40，即使不到補充水位也該恢復錄影");
        Assert.Null(_segments.Get(seg.Id));
    }

    /// <summary>
    /// 自動刪掉的錄影要留下稽核：檔案自己消失而沒有任何紀錄，操作員只會認為程式亂刪檔。
    /// </summary>
    [Fact]
    public void 淘汰錄影會留下一筆稽核()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedFinal(now.AddHours(-3), 1000);
        SeedFinal(now.AddMinutes(-10), 30);
        SeedDrive(baseFree: 30);

        NewRelief(now).ShouldStopNewRecording(_store, _root, UsageFor, _audit);

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = DiskPressureRelief.EvictionAuditAction }));
        Assert.Contains("segments=1", row.Detail);
        Assert.Contains("freed=1000", row.Detail);
    }

    /// <summary>門檻以上時閘門不該刪任何東西，也不該停錄。</summary>
    [Fact]
    public void 空間充足時不淘汰任何錄影()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var seg = SeedFinal(now.AddHours(-3), 1000);
        SeedDrive(baseFree: 5 * GiB);

        var stop = NewRelief(now).ShouldStopNewRecording(_store, _root, UsageFor, _audit);

        Assert.False(stop);
        Assert.True(File.Exists(seg.FilePath));
        Assert.Empty(_audit.List(new AuditLogQuery { Action = DiskPressureRelief.EvictionAuditAction }));
    }

    // ===== 測試替身 =====

    private DateTime _nowUtc = DateTime.MinValue;

    private DiskPressureRelief NewRelief(
        DateTime nowUtc,
        Func<SqliteStore, string, RetentionService>? serviceFor = null)
    {
        _nowUtc = nowUtc;
        return new DiskPressureRelief(
            serviceFor ?? (static (store, root) =>
                new RetentionService(new SegmentRepository(store), root, new LegalHoldRepository(store))),
            clock: () => _nowUtc);
    }

    private void Advance(TimeSpan by) => _nowUtc = _nowUtc.Add(by);

    private (long Total, long Free) UsageFor(string path)
    {
        var current = _segments.GetTotalUsage();
        return (10 * GiB, _baseFree + (_seededUsage - current));
    }

    private void SeedDrive(long baseFree)
    {
        _baseFree = baseFree;
        _seededUsage = _segments.GetTotalUsage();
    }

    /// <summary>建立一個已收尾（final）的區段：索引列與磁碟檔案都真的存在。</summary>
    private (long Id, string FilePath) SeedFinal(DateTime startUtc, long sizeBytes, int channelId = 1)
    {
        var file = Path.Combine(_root, $"seg-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(file, new byte[1]);
        var id = _segments.BeginSegment(channelId, "main", file, startUtc);
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
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 測試清理失敗不該讓測試紅掉
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
