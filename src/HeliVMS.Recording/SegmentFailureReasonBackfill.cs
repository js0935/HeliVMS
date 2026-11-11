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
    /// <remarks>
    /// <para>
    /// 歸因以「成功收錄」劃定事件邊界：連續失敗事件從上一次成功之後開始，到下一次成功為止。
    /// 稽核在事件的第 3 次失敗才寫（failures=N 只反映報告時點），長事件的後續段落落在
    /// 報告之後很久也屬於同一事件。實測：線上 207 列無原因的 corrupt，純窗距版補到 104，
    /// 事件邊界版補到 156（ch107 長事件一次補上數十列）。
    /// </para>
    /// </remarks>
    private string? FindReason(SegmentRecord row)
    {
        var windowEnd = row.StartUtc + MaxAttributionWindow;
        var lastSuccess = _segments.GetLastFinalCompletionBefore(row.ChannelId, row.StartUtc);
        var candidates = _audit.List(new AuditLogQuery
        {
            Action = StalledAction,
            TargetId = row.ChannelId,
            FromUtc = lastSuccess,
            ToUtc = windowEnd,
            Limit = 50,
        });

        // 歸因以「成功收錄」劃定事件邊界：連續失敗事件從上一次成功之後開始，到下一次成功為止。
        // 稽核在事件的第 3 次失敗才寫（failures=N 只反映報告時點），長事件的後續段落
        // 在報告之後很久也屬於同一事件（中間沒有成功收錄）。實測：線上 207 列無原因的
        // corrupt，純窗距版補到 104，事件邊界版補到 156（ch107 長事件一次補上數十列）。
        // 兩個方向都要擋：稽核早於本段開始（長事件尾段）時，靠「最後一次成功在稽核之後」
        // 擋掉跨事件；稽核晚於本段開始（本段是事件的頭幾次失敗）時，靠窗距與
        // 「段與稽核之間沒有成功收錄」擋掉跨事件。
        AuditLogEntry? match = null;
        foreach (var entry in candidates)
        {
            if (entry.TargetId != row.ChannelId)
            {
                continue;
            }

            if (lastSuccess is { } success && entry.OccurredAtUtc <= success)
            {
                // 稽核早於最近一次成功＝屬於上一次事件。SQL 的 FromUtc 是同一條件的主過濾；
                // 這裡再擋一次等號邊界（稽核恰與成功收錄同時刻時，寧可不歸因）。
                continue;
            }

            if (entry.OccurredAtUtc > row.StartUtc + AttributionWindow(entry.Detail))
            {
                // 窗距由稽核自己的 failures 推導：夠不到本段的較晚事件不採用。
                continue;
            }

            if (_segments.HasFinalCompletionBetween(row.ChannelId, row.StartUtc, entry.OccurredAtUtc))
            {
                // 本段開始之後、稽核之前有成功收錄＝本段屬於更早的事件，這筆稽核是下一次的。
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
            var text = (end > start ? detail[start..end] : detail[start..].TrimEnd()).Trim();
            return StripFfmpegPrefix(text);
        }

        var err = detail.IndexOf("error=", StringComparison.Ordinal);
        return err >= 0 ? StripFfmpegPrefix(detail[(err + "error=".Length)..].TrimEnd()) : null;
    }

    /// <summary>
    /// 去掉 ffmpeg 記錄前綴（<c>[in#0 @ 000001ddfdb964c0]</c>）：方括號內的行程內部位址
    /// 是雜訊，每次執行都不同，寫進索引的證據欄只會妨礙比對。
    /// </summary>
    internal static string StripFfmpegPrefix(string text)
    {
        var trimmed = text.TrimStart();
        while (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close < 0)
            {
                break;
            }

            trimmed = trimmed[(close + 1)..].TrimStart();
        }

        return trimmed.Length > 0 ? trimmed : text.TrimStart();
    }
}
