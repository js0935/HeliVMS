using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 用既有的 <c>recording.stalled</c> 稽核，替舊版索引的 corrupt 段補上失敗原因
/// （<see cref="SegmentFailureReasonBackfill"/>）。
///
/// <para>
/// 要守的核心是「寧缺勿錯」：稽核在失敗<b>之後</b>才寫，而它的 <c>failures=N</c> 只描述
/// 該次連續失敗事件的長度——窗距由稽核自己的 failures 推導，夠不到的段寧可留空，
/// 也不把別的事件的錯誤訊息寫成這一段的原因（那是把證據指到錯的地方）。
/// </para>
/// </summary>
public sealed class SegmentFailureReasonBackfillTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly AuditLogRepository _audit;

    /// <summary>歸因窗推導用：模擬現場的 300 秒分段。</summary>
    private const int SegmentSeconds = 300;

    public SegmentFailureReasonBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-freason-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _audit = new AuditLogRepository(_store);
        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES (1, 'ch-1', 'rtsp://test/1', 0);");
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void corrupt段的失敗原因會由窗內的停滯稽核補上()
    {
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        var id = CorruptSegment(start);
        StallAudit(channelId: 1, atUtc: start.AddMinutes(2), failures: 3,
            detail: "failures=3 url=rtsp://203.0.113.7:554/stream "
                + "ffmpeg=rtsp://203.0.113.7:554/stream: Connection refused error=System.IO.IOException: 連線中斷");

        var report = Run(start.AddHours(1));

        Assert.Equal(1, report.Examined);
        Assert.Equal(1, report.Backfilled);
        var row = _segments.Get(id)!;
        Assert.Equal(SegmentStatus.Corrupt, row.Status);
        Assert.NotNull(row.FailureReason);
        Assert.Contains("failures=3", row.FailureReason);
        Assert.Contains("Connection refused", row.FailureReason);
        // 例外的描述（error= 欄）不該被拿來當 ffmpeg 節錄。
        Assert.DoesNotContain("IOException", row.FailureReason);
    }

    [Fact]
    public void 歸因窗以外的停滯稽核不會被誤用()
    {
        // failures=3 ⇒ 窗 ≈ 3 × (300+60) + 300 ≈ 22 分鐘。稽核在 3 小時後＝屬於別次事件。
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        var id = CorruptSegment(start);
        StallAudit(1, start.AddHours(3), failures: 3, detail: "failures=3 url=rtsp://cam ffmpeg=Connection refused");

        var report = Run(start.AddHours(4));

        Assert.Equal(0, report.Backfilled);
        Assert.Null(_segments.Get(id)!.FailureReason);
    }

    [Fact]
    public void 其他頻道的停滯稽核不會被拿來補()
    {
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        var id = CorruptSegment(start);
        StallAudit(channelId: 2, atUtc: start.AddMinutes(2), failures: 3,
            detail: "failures=3 url=rtsp://cam ffmpeg=Connection refused");

        Run(start.AddHours(1));

        Assert.Null(_segments.Get(id)!.FailureReason);
    }

    [Fact]
    public void 解析不出failures的稽核不用來歸因()
    {
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        var id = CorruptSegment(start);
        StallAudit(1, start.AddMinutes(2), failures: null, detail: "url=rtsp://cam ffmpeg=Connection refused");

        Run(start.AddHours(1));

        // 沒有 failures 就無法界定事件時長：寧可不補，也不把窗外的錯誤寫進這一段。
        Assert.Null(_segments.Get(id)!.FailureReason);
    }

    [Fact]
    public void 已有原因的段不會被改寫()
    {
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        var id = CorruptSegment(start);
        _segments.MarkCorrupt(id, "攝影機拒絕連線（收尾時記錄）");
        StallAudit(1, start.AddMinutes(2), failures: 3, detail: "failures=3 url=rtsp://cam ffmpeg=Connection refused");

        // 回填只處理 failure_reason IS NULL 的列；ListCorruptWithoutReasonAfter 不會再回傳它。
        var report = Run(start.AddHours(1));

        Assert.Equal(0, report.Examined);
        Assert.Equal("攝影機拒絕連線（收尾時記錄）", _segments.Get(id)!.FailureReason);
    }

    [Fact]
    public void 稽核節錄會遮帳密並截斷()
    {
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        var id = CorruptSegment(start);
        // 舊稽核欄本應已遮蔽；回填仍一律再過 NormalizeFailureReason（先遮再截）。
        StallAudit(1, start.AddMinutes(2), failures: 3,
            detail: "failures=3 url=rtsp://admin:S3cret@203.0.113.7:554/stream "
                + "ffmpeg=rtsp://admin:S3cret@203.0.113.7:554/stream: Connection refused "
                + new string('x', 400));

        Run(start.AddHours(1));

        var reason = _segments.Get(id)!.FailureReason;
        Assert.NotNull(reason);
        Assert.DoesNotContain("S3cret", reason);
        Assert.True(reason!.Length <= SegmentRepository.FailureReasonMaxLength);
    }

    [Fact]
    public void 掃到尾後游標寫負一且不再掃描()
    {
        var start = new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc);
        CorruptSegment(start);
        CorruptSegment(start.AddMinutes(5));

        var first = Run(start.AddHours(1), batchSize: 10);
        Assert.Equal(2, first.Examined);
        Assert.True(first.Finished);

        // 之後的輪次直接跳過（回填是一次性工作，不是每小時重掃的常駐成本）。
        var second = Run(start.AddHours(2), batchSize: 10);
        Assert.Equal(0, second.Examined);
        Assert.True(second.Finished);
    }

    [Fact]
    public void 停用時不掃描且游標不變()
    {
        CorruptSegment(new DateTime(2026, 11, 9, 0, 33, 31, DateTimeKind.Utc));

        var report = Run(DateTime.UtcNow, batchSize: 0);

        Assert.Equal(0, report.Examined);
        Assert.Null(_segments.GetFailureBackfillCursor());
    }

    private FailureReasonBackfillReport Run(DateTime nowUtc, int batchSize = 100)
        => new SegmentFailureReasonBackfill(_segments, _audit, SegmentSeconds).Run(batchSize, nowUtc);

    private long CorruptSegment(DateTime startUtc)
    {
        var id = _segments.BeginSegment(1, "main", $@"D:\tmp\seg-{Guid.NewGuid():N}.mp4", startUtc);
        _segments.MarkCorrupt(id);
        return id;
    }

    private void StallAudit(int channelId, DateTime atUtc, int? failures, string detail)
        => _audit.Record(
            "system",
            "recording.stalled",
            AuditCategories.Recording,
            targetType: "channel",
            targetId: channelId,
            detail: detail,
            occurredAtUtc: atUtc);
}
