using HeliVMS.Recording;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 剩餘可錄時間的量測入口（<see cref="RecordingCapacityService"/>）：把共用設定、磁碟實況與
/// 索引裡的實測寫入量接起來。
/// </summary>
/// <remarks>
/// <para>
/// 這裡不碰真實磁碟（<c>usageFor</c> 注入容量）也不跑 ffmpeg，但索引是真的 SQLite——
/// 「寫入量取自 final 段」這件事只有走真的查詢才驗得到，而它正是這個功能的輸入。
/// </para>
/// <para>
/// 兩個容易做錯的細節：量測窗外的段**不可以**列入寫入量（否則昨天的大量寫入會讓今天的
/// 估算過度樂觀），而保留水位必須取自共用設定（畫面顯示的門檻與壓力汰除實際用的門檻
/// 不可以是兩個數字）。
/// </para>
/// </remarks>
public class RecordingCapacityServiceTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly SettingsRepository _settings;

    public RecordingCapacityServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-capacity-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _settings = new SettingsRepository(_store);

        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES (1, 'ch-1', 'rtsp://test/1', 0);");
    }

    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 以實際寫入量估算且量測窗外的段不列入()
    {
        SeedFinal(Now.AddMinutes(-30), 5 * GiB);   // 窗內
        SeedFinal(Now.AddMinutes(-10), 5 * GiB);   // 窗內
        SeedFinal(Now.AddHours(-3), 90 * GiB);     // 窗外：昨天的量不該讓今天的估算變樂觀

        var report = Measure(freeBytes: 42 * GiB, reserveBytes: 2 * GiB);

        var drive = Assert.Single(report.Drives);
        Assert.Equal(10 * GiB, drive.WrittenBytes);

        // 最早的段在半小時前 → 基準半小時 → 速率 20 GB/小時（不是除以一小時的 10）。
        Assert.Equal(TimeSpan.FromMinutes(30), drive.Basis);
        Assert.Equal(RecordingCapacityState.Known, drive.Estimate.State);
        Assert.Equal(2, drive.Estimate.Remaining.TotalHours, precision: 3);
        Assert.Equal(2 * GiB, report.ReserveBytes);
    }

    [Fact]
    public void 保留水位取自共用設定而不是寫死()
    {
        SeedFinal(Now.AddMinutes(-30), 10 * GiB);

        // 基準半小時 → 20 GB/小時。
        // 保留 2 GB → 60−2 ＝ 58 GB 可用 → 2.9 小時
        _settings.Set(Segments.MinFreeKey, (2 * GiB).ToString());
        var tight = Measure(freeBytes: 60 * GiB, reserveBytes: null);

        // 保留 20 GB → 40 GB 可用 → 2 小時（同一個磁碟、同一份寫入量，只因門檻不同）
        _settings.Set(Segments.MinFreeKey, (20 * GiB).ToString());
        var loose = Measure(freeBytes: 60 * GiB, reserveBytes: null);

        Assert.Equal(2.9, tight.Drives[0].Estimate.Remaining.TotalHours, precision: 3);
        Assert.Equal(2.0, loose.Drives[0].Estimate.Remaining.TotalHours, precision: 3);
        Assert.Equal(2 * GiB, tight.ReserveBytes);
        Assert.Equal(20 * GiB, loose.ReserveBytes);
    }

    [Fact]
    public void 錄影滿一小時時基準就是量測窗()
    {
        // 最早的段在兩小時前（窗外，不列入）與剛好一小時前（窗內第一筆）。
        SeedFinal(Now.AddHours(-2), 90 * GiB);
        SeedFinal(Now.AddHours(-1), 10 * GiB);

        var report = Measure(freeBytes: 42 * GiB, reserveBytes: 2 * GiB);

        var drive = Assert.Single(report.Drives);
        Assert.Equal(Hour, drive.Basis);
        Assert.Equal(10 * GiB, drive.WrittenBytes);
        Assert.Equal(4, drive.Estimate.Remaining.TotalHours, precision: 3);
    }

    [Fact]
    public void 錄影涵蓋不滿一小時時基準跟著縮短()
    {
        // 55 分鐘前才開始錄：基準 55 分鐘，速率＝10 GB ÷ 55 分 ≒ 10.9 GB/小時。
        // 若除以一小時（10 GB/小時），剩餘時間會被報成 4 小時（真值約 3.67）。
        SeedFinal(Now.AddMinutes(-55), 10 * GiB);

        var report = Measure(freeBytes: 42 * GiB, reserveBytes: 2 * GiB);

        var drive = Assert.Single(report.Drives);
        Assert.Equal(TimeSpan.FromMinutes(55), drive.Basis);
        Assert.Equal(10.909, drive.Estimate.BytesPerHour / GiB, precision: 2);
        Assert.Equal(3.67, drive.Estimate.Remaining.TotalHours, precision: 2);
    }

    [Fact]
    public void 窗內沒有寫入回未知並且不編造數字()
    {
        SeedFinal(Now.AddHours(-6), 10 * GiB);

        var report = Measure(freeBytes: 42 * GiB, reserveBytes: 2 * GiB);

        var drive = Assert.Single(report.Drives);
        Assert.Equal(0, drive.WrittenBytes);
        Assert.Equal(RecordingCapacityState.Unknown, drive.Estimate.State);
        Assert.Equal(TimeSpan.Zero, drive.Estimate.Remaining);
        Assert.Equal(RecordingCapacityState.Unknown, report.Total.State);
    }

    [Fact]
    public void 可用空間低於保留水位時回可錄零小時()
    {
        SeedFinal(Now.AddMinutes(-30), 1 * GiB);

        var report = Measure(freeBytes: 1 * GiB, reserveBytes: 2 * GiB);

        var drive = Assert.Single(report.Drives);
        Assert.Equal(RecordingCapacityState.AtReserve, drive.Estimate.State);
        Assert.Equal(TimeSpan.Zero, drive.Estimate.Remaining);
        Assert.Equal(RecordingCapacityState.AtReserve, report.Total.State);
    }

    /// <summary>
    /// 量測一輪。<paramref name="reserveBytes"/> 為 null 表示不動設定（測試自己要先寫入）。
    /// </summary>
    private CapacityReport Measure(long freeBytes, long? reserveBytes)
    {
        if (reserveBytes is { } reserve)
        {
            _settings.Set(Segments.MinFreeKey, reserve.ToString());
        }

        return RecordingCapacityService.Measure(
            _store,
            _root,
            Now,
            Hour,
            _ => (100 * GiB, freeBytes));
    }

    /// <summary>建立一個已收尾（final）的區段；檔案真的存在（索引與磁碟一致）。</summary>
    private void SeedFinal(DateTime startUtc, long sizeBytes)
    {
        var file = Path.Combine(_root, $"seg-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(file, new byte[1]);
        var id = _segments.BeginSegment(1, "main", file, startUtc);
        _segments.CompleteSegment(id, startUtc.AddMinutes(10), sizeBytes, 600, "sha256");
    }

    /// <summary>設定與保留空間相關的鍵（避免測試散落字面字串）。</summary>
    private static class Segments
    {
        public const string MinFreeKey = RecordingStorage.MinFreeSettingKey;
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
