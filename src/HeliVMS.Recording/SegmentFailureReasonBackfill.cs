using System.Globalization;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>一輪失敗原因回填的結果。</summary>
/// <param name="Examined">本輪檢查過的段數。</param>
/// <param name="Backfilled">其中成功寫入原因的段數。</param>
/// <param name="Cursor">下一輪要用的游標；<c>-1</c> 表示整份索引已掃完。</param>
public readonly record struct FailureReasonBackfillReport(int Examined, int Backfilled, long Cursor)
{
    /// <summary>整份索引是否已掃完。</summary>
    public bool Finished => Cursor < 0;
}

/// <summary>
/// 用既有的 <c>recording.stalled</c> 稽核，替「新版之前」寫入的 corrupt 段補上失敗原因。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這支：<c>segments.failure_reason</c> 是新功能，之前標成 corrupt 的段只有狀態
/// 沒有原因——現場 207 列 corrupt 裡 ch107 一個人 118 列，時間軸上的洞說不出是「攝影機拒絕
/// 連線」還是「磁碟寫入失敗」。但那個答案**已經在稽核裡**：錄影連續失敗時
/// <see cref="RecordingScheduler"/> 會寫 <c>recording.stalled</c>，內容帶遮蔽過的
/// ffmpeg stderr 節錄。把兩邊接起來，歷史的洞就有得解釋，而錄影端什麼都不用改。
/// </para>
/// <para>
/// 歸因規則（寧缺勿錯）：稽核在失敗<b>之後</b>才寫，所以只考慮「段開始之後」的稽核；
/// 一筆稽核代表一次連續失敗事件（<c>failures=N</c>），它的涵蓋時長約
/// N ×（一段時間＋最長退避），**窗距由稽核自己的 failures 推導**——較晚事件的稽核
/// 夠不到更早的段，就不會誤用。取窗內<b>最早</b>的一筆（相鄰事件屬於同一失敗模式，
/// 錯配的內容也相同）。解析不出 failures 的稽核一律不用。同一段的原因只補一次；
/// 掃完整份索引就停（游標 <c>-1</c>），與段長修正同一套一次性模式。
/// </para>
/// </remarks>
public sealed class SegmentFailureReasonBackfill
{
    /// <summary>未設定時每輪掃描的段數。</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>歸因窗的絕對上限：再長的連續失敗事件也不跨這條線。</summary>
    public static readonly TimeSpan MaxAttributionWindow = TimeSpan.FromHours(6);

    /// <summary>單次錄影重試退避的上限（與 <see cref="SegmentRecorder.RetryDelayFor"/> 的上限同量級）。</summary>
    private const int MaxRetrySeconds = 60;

    private const string StalledAction = "recording.stalled";

    private readonly SegmentRepository _segments;
    private readonly AuditLogRepository _audit;
    private readonly int _segmentSeconds;

    public SegmentFailureReasonBackfill(
        SegmentRepository segments,
        AuditLogRepository audit,
        int segmentSeconds = RecordingSegmentPolicy.DefaultSeconds)
    {
        _segments = segments;
        _audit = audit;
        _segmentSeconds = Math.Max(RecordingSegmentPolicy.MinSeconds, segmentSeconds);
    }

    /// <summary>從上次的游標接著掃一批。batchSize ≤ 0 時什麼都不做（停用）。</summary>
    public FailureReasonBackfillReport Run(int batchSize, DateTime nowUtc)
    {
        if (batchSize <= 0)
        {
            return new FailureReasonBackfillReport(0, 0, _segments.GetFailureBackfillCursor() ?? 0);
        }

        var cursor = _segments.GetFailureBackfillCursor();
        if (cursor < 0)
        {
            return new FailureReasonBackfillReport(0, 0, -1);
        }

        var rows = _segments.ListCorruptWithoutReasonAfter(cursor ?? 0, batchSize);
        var examined = 0;
        var backfilled = 0;
        var lastId = cursor ?? 0;

        foreach (var row in rows)
        {
            lastId = row.Id;
            examined++;

            var reason = FindReason(row);
            if (reason is null)
            {
                continue;
            }

            // MarkCorrupt 對已是 corrupt 的列只會更新原因（COALESCE：空原因不覆蓋）。
            _segments.MarkCorrupt(row.Id, reason);
            backfilled++;
        }

        var next = rows.Count < batchSize ? -1 : lastId;
        _segments.SetFailureBackfillCursor(next, nowUtc);

        return new FailureReasonBackfillReport(examined, backfilled, next);
    }

    /// <summary>找出這個 corrupt 段對應的失敗描述；找不到（窗內沒有稽核）回 null。</summary>
    private string? FindReason(SegmentRecord row)
    {
        var windowEnd = row.StartUtc + MaxAttributionWindow;
        var candidates = _audit.List(new AuditLogQuery
        {
            Action = StalledAction,
            TargetId = row.ChannelId,
            FromUtc = row.StartUtc,
            ToUtc = windowEnd,
            Limit = 50,
        });

        // List 以時間 DESC 排序；歸因取窗內最早的一筆。窗距由稽核自己的 failures 推導：
        // 較晚事件的稽核（窗距超出該事件時長）不夠成歸因證據。
        AuditLogEntry? match = null;
        foreach (var entry in candidates)
        {
            if (entry.TargetId != row.ChannelId ||
                entry.OccurredAtUtc < row.StartUtc ||
                entry.OccurredAtUtc > row.StartUtc + AttributionWindow(entry.Detail))
            {
                continue;
            }

            match = entry;
        }

        if (match is null)
        {
            return null;
        }

        var excerpt = ExtractExcerpt(match.Detail);
        if (excerpt is null)
        {
            return null;
        }

        var failures = ParseFailures(match.Detail);
        var reason = failures is { } n
            ? string.Create(CultureInfo.InvariantCulture, $"錄影連續失敗（failures={n}）：{excerpt}")
            : $"錄影連續失敗：{excerpt}";
        return SegmentRepository.NormalizeFailureReason(reason);
    }

    /// <summary>由一筆停滯稽核推導它的歸因窗：failures ×（一段時間＋最長退避）＋緩衝。</summary>
    internal TimeSpan AttributionWindow(string? detail)
    {
        var failures = ParseFailures(detail);
        if (failures is null)
        {
            // 解析不出 failures＝無法界定事件時長＝不用（寧缺勿錯）。
            return TimeSpan.Zero;
        }

        var window = TimeSpan.FromSeconds(
            (failures.Value * (_segmentSeconds + MaxRetrySeconds)) + 300);
        return window > MaxAttributionWindow ? MaxAttributionWindow : window;
    }

    private static int? ParseFailures(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        const string marker = "failures=";
        var idx = detail.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = start;
        while (end < detail.Length && char.IsDigit(detail[end]))
        {
            end++;
        }

        return end > start &&
               int.TryParse(detail[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>從稽核明細抽出 ffmpeg stderr 節錄；沒有時退回例外的描述（error= 欄）。</summary>
    internal static string? ExtractExcerpt(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        const string ffmpegMarker = "ffmpeg=";
        const string errorMarker = " error=";
        var idx = detail.IndexOf(ffmpegMarker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var start = idx + ffmpegMarker.Length;
            var end = detail.IndexOf(errorMarker, start, StringComparison.Ordinal);
            var text = end > start ? detail[start..end] : detail[start..].TrimEnd();
            return text.Length > 0 ? text : null;
        }

        var err = detail.IndexOf("error=", StringComparison.Ordinal);
        return err >= 0 ? detail[(err + "error=".Length)..].TrimEnd() : null;
    }
}
