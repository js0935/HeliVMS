using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 以檔案自己的長度修正既有索引的段長（M319）。
///
/// <para>
/// 要守的是一條「舊資料不會自己變好」的保證：新錄的段已由 <c>SegmentRecorder</c> 用檔案長度收尾，
/// 但改動前寫進去的列（量到 716 段裡有 15 段與檔案差超過 60 秒，最壞兩段記著 6853／6849 秒）
/// 會讓時間軸出現幾小時的色塊、方向鍵跳到沒有影像的地方。掃描必須真的把那幾列改回來，
/// 而且<b>掃完就停</b>——每小時重探 100 個檔案是白花的 I/O，探測本身每段還要 ~90 ms。
/// </para>
/// <para>
/// 探測器以委派注入，測試不碰真實 ffprobe：要驗的是「拿哪個數字覆蓋哪一列、游標怎麼前進」，
/// 不是 ffprobe 本身。
/// </para>
/// </summary>
public sealed class SegmentDurationRepairTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly string _root;

    public SegmentDurationRepairTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-durrepair-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-durrepair-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        EnsureChannel(1);
    }

    /// <summary>
    /// 索引記著幾千秒、檔案只有幾百秒時，掃描把段長換成檔案長度，<c>end_time</c> 一起換成
    /// <c>start + 長度</c>，並留一筆稽核說明「哪一段、從多少改成多少」。
    ///
    /// <para>
    /// <c>end_time</c> 沒跟著改的症狀：時間軸用「起點＋長度」換算、索引另一套，兩者對同一段
    /// 說出不同的結束時刻，回放找不到落點。稽核是日後追「這段為什麼變短了」的唯一線索。
    /// </para>
    /// </summary>
    [Fact]
    public void 以檔案長度修正舊索引的段長與結束時刻()
    {
        var start = new DateTime(2026, 11, 11, 0, 42, 17, DateTimeKind.Utc);
        var id = Segment(start, indexedSeconds: 7105.5840971);

        var report = Repair(start.AddDays(1), batchSize: 10, _ => 260.4);

        Assert.Equal(1, report.Probed);
        Assert.Equal(1, report.Repaired);
        Assert.Equal(0, report.Skipped);
        Assert.True(report.Finished);

        var row = _segments.Get(id)!;
        Assert.Equal(260.4, row.DurationSec!.Value, 3);

        // end_time 與段長必須是同一件事：時間軸用「起點＋長度」換算，兩者不一致就會各說各話。
        Assert.Equal(260.4, (row.EndUtc!.Value - row.StartUtc).TotalSeconds, 2);

        var detail = _store.Query(
            "SELECT detail FROM audit_log WHERE action = 'recording.duration.repair' AND target_id = $id;",
            r => r.Read() ? r.GetString(0) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
        Assert.NotNull(detail);
        Assert.Contains("old=7105.584", detail);
        Assert.Contains("new=260.4", detail);
    }

    /// <summary>
    /// 檔案長度與索引一致（差距在容差內）時不寫入、也不留稽核。
    ///
    /// <para>
    /// 健康段的索引原本就多出 0.2–1.0 秒（RTSP 暖機），那是同一個數字量兩次；為它改寫
    /// 每一列只會讓稽核日誌被幾百筆「改了 0.4 秒」洗掉。小時合併檔（<c>merged-*.mp4</c>）
    /// 也走這條：它們合法地是 3600 秒，不能被當成膨脹的段長再「修」一次。
    /// </para>
    /// </summary>
    [Fact]
    public void 段長一致時不寫入()
    {
        var start = new DateTime(2026, 11, 11, 1, 0, 0, DateTimeKind.Utc);
        var id = Segment(start, indexedSeconds: 60);
        var merged = Segment(start.AddHours(1), indexedSeconds: 3600, fileName: "merged-010000000.mp4");
        var mergedName = _segments.Get(merged)!.FilePath;

        var report = Repair(start.AddDays(1), batchSize: 10,
            path => path == mergedName ? 3600 : 60.4);

        Assert.Equal(0, report.Repaired);

        Assert.Equal(60, _segments.Get(id)!.DurationSec!.Value, 3);
        Assert.Equal(3600, _segments.Get(merged)!.DurationSec!.Value, 3);
        Assert.Equal(0, CountAudits());
    }

    /// <summary>
    /// 探測不到的段跳過而不是停住：ffprobe 不在、檔案被鎖、檔案已被外部刪掉時，
    /// 同一批後面的段仍要被處理，游標也要前進，否則每一輪都卡在同一個檔案上，
    /// 後面的段永遠輪不到（症狀是「修了一整晚只修好最舊那幾段」）。
    /// </summary>
    [Fact]
    public void 探測失敗的段跳過並繼續前進()
    {
        var start = new DateTime(2026, 11, 11, 2, 0, 0, DateTimeKind.Utc);
        var first = Segment(start, indexedSeconds: 4000);
        var second = Segment(start.AddMinutes(5), indexedSeconds: 60);
        var secondPath = _segments.Get(second)!.FilePath;

        var report = Repair(start.AddDays(1), batchSize: 5, path => path == secondPath ? 100 : null);

        Assert.Equal(2, report.Probed);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Repaired);
        Assert.True(report.Finished);

        Assert.Equal(100, _segments.Get(second)!.DurationSec!.Value, 3);

        // 探測失敗那一段保持原樣（沒有可用的事實可以覆蓋它）。
        Assert.Equal(4000, _segments.Get(first)!.DurationSec!.Value, 3);
    }

    /// <summary>
    /// 整份索引掃完一次後游標記為 <c>-1</c>，後續輪次不再探測任何檔案。
    ///
    /// <para>
    /// 這是「修歷史資料的一次性工作」與「每小時都重掃」的分界：少了它，100 段 × ~90 ms
    /// 會變成永久性的背景 I/O，而它要修的資料早就修完了（新寫入的列由收尾路徑保證正確）。
    /// </para>
    /// </summary>
    [Fact]
    public void 游標到結尾後不再掃描()
    {
        var start = new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);
        Segment(start, indexedSeconds: 5000);
        Segment(start.AddMinutes(5), indexedSeconds: 5000);

        var first = Repair(start.AddDays(1), batchSize: 10, _ => 60);
        Assert.Equal(2, first.Probed);
        Assert.True(first.Finished);
        Assert.Equal(-1, _segments.GetDurationRepairCursor());

        var probes = 0;
        var second = Repair(start.AddDays(1), batchSize: 10, _ =>
        {
            probes++;
            return 60;
        });

        Assert.Equal(0, probes);
        Assert.Equal(0, second.Probed);
        Assert.True(second.Finished);
    }

    /// <summary>設定為 0 時整支停用：不探測、不改列，游標維持原狀。</summary>
    [Fact]
    public void RunConfigured段數為零時停用段長修正()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Segment(now, indexedSeconds: 7105);
        new SettingsRepository(_store).Set(SegmentDurationRepairCoordinator.BatchSizeKey, "0");

        var report = SegmentDurationRepairCoordinator.RunConfigured(_store, now, _ => 60);

        Assert.Equal(0, report.Probed);
        Assert.Equal(0, report.Repaired);
        Assert.Null(_segments.GetDurationRepairCursor());
    }

    /// <summary>未設定時採用預設段數（備妥超過預設值的段，證明預設值真的有被拿來當上限）。</summary>
    [Fact]
    public void RunConfigured未設定時採用預設段數()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < SegmentDurationRepairCoordinator.DefaultBatchSize + 3; i++)
        {
            Segment(now.AddSeconds(i * 60), indexedSeconds: 60);
        }

        var report = SegmentDurationRepairCoordinator.RunConfigured(_store, now, _ => 60);

        Assert.Equal(SegmentDurationRepairCoordinator.DefaultBatchSize, report.Probed);
    }

    private DurationRepairReport Repair(DateTime nowUtc, int batchSize, Func<string, double?> probe)
        => new SegmentDurationRepair(_segments, probe, new AuditLogRepository(_store)).Run(batchSize, nowUtc);

    private int CountAudits() => _store.Query(
        "SELECT COUNT(*) FROM audit_log WHERE action = 'recording.duration.repair';",
        static r =>
        {
            r.Read();
            return r.GetInt32(0);
        });

    private long Segment(DateTime startUtc, double indexedSeconds, string? fileName = null)
    {
        var file = Path.Combine(_root, fileName ?? $"seg-{Guid.NewGuid():N}.mp4");
        var id = _segments.BeginSegment(1, "main", file, startUtc);
        _segments.CompleteSegment(id, startUtc.AddSeconds(indexedSeconds), 16, indexedSeconds, "sha");
        return id;
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
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存目錄清不掉不影響測試結果。
        }
    }
}
