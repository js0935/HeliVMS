using System.Globalization;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 一輪段長修正的結果。
/// </summary>
/// <param name="Probed">本輪探測過的段數。</param>
/// <param name="Repaired">其中索引被改寫的段數（其餘檔案長度與索引一致）。</param>
/// <param name="Skipped">探測不到（ffprobe 不在、檔案被鎖、檔案已不在磁碟）而跳過的段數。</param>
/// <param name="Cursor">下一輪要用的游標；<c>-1</c> 表示整份索引已掃完。</param>
public readonly record struct DurationRepairReport(int Probed, int Repaired, int Skipped, long Cursor)
{
    /// <summary>整份索引是否已掃完。</summary>
    public bool Finished => Cursor < 0;
}

/// <summary>
/// 以檔案自己的長度修正既有索引的 <c>duration_sec</c>／<c>end_time</c>（M319）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這支：<c>duration_sec</c> 曾記的是「ffmpeg 行程被排程了多久」，Windows 睡眠或校時
/// 一次就膨脹成幾千秒。新錄的段已由 <see cref="SegmentRecorder"/> 用檔案長度收尾，但**既有索引
/// 不會自己變好**——時間軸上那幾條幾小時的色塊、方向鍵跳到沒有影像的地方、多路回放每一格都在等
/// 一個不存在的時刻，全部來自改動前寫進去的列。現場量到 716 段裡有 15 段與檔案差超過 60 秒。
/// </para>
/// <para>
/// 為什麼獨立成一支而不是搭在 <see cref="MediaIntegrityChecker"/> 上：那支有明文契約「只觀察、
/// 不改狀態」（把壞段標成 corrupt 會讓它退出用量統計與汰除候選），而且它用
/// <c>checked_at_utc</c> 分頁——那 369 段早就標記過了，永遠輪不到要修的那幾段。
/// 這裡改用主鍵游標分頁，每次跑一小批，進度寫在 <c>app_settings</c>。
/// </para>
/// <para>
/// 掃完一次就停（游標寫 <c>-1</c>）：這是修歷史資料的一次性工作，不是每小時都值得再花
/// 100 × 90 ms 重新探測同一批檔案。之後新寫入的列由收尾路徑保證正確。
/// </para>
/// </remarks>
public sealed class SegmentDurationRepair
{
    /// <summary>未設定時每輪掃描的段數。100 段約 9 秒（實測每段約 90 ms）。</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// 差異多少秒才值得改寫。健康段的索引原本就多 0.2–1.0 秒（RTSP 暖機），
    /// 那是同一個數字量兩次，不值得為它寫一列；真正要修的是分鐘、小時級的偏差。
    /// </summary>
    public const double ToleranceSeconds = 0.5;

    private readonly SegmentRepository _segments;
    private readonly Func<string, double?> _probeDuration;
    private readonly AuditLogRepository? _audit;

    public SegmentDurationRepair(
        SegmentRepository segments,
        Func<string, double?>? probeDuration = null,
        AuditLogRepository? audit = null)
    {
        _segments = segments;
        _probeDuration = probeDuration ?? (path => MediaDurationProbe.ProbeSeconds(path));
        _audit = audit;
    }

    /// <summary>
    /// 從上次的游標接著掃一批。<paramref name="batchSize"/> 為 0 或負值時什麼都不做
    /// （回傳游標不變，供不想讓背景掃描佔用 I/O 的部署關掉）。
    /// </summary>
    public DurationRepairReport Run(int batchSize, DateTime nowUtc)
    {
        if (batchSize <= 0)
        {
            return new DurationRepairReport(0, 0, 0, _segments.GetDurationRepairCursor() ?? 0);
        }

        var cursor = _segments.GetDurationRepairCursor();

        // 已經掃完：不要再把整份索引重探一次。新寫入的列由收尾路徑保證長度正確。
        if (cursor < 0)
        {
            return new DurationRepairReport(0, 0, 0, -1);
        }

        var rows = _segments.ListFinalAfter(cursor ?? 0, batchSize);
        var probed = 0;
        var repaired = 0;
        var skipped = 0;
        var lastId = cursor ?? 0;

        foreach (var row in rows)
        {
            lastId = row.Id;
            probed++;

            double? fileSeconds;
            try
            {
                fileSeconds = _probeDuration(row.FilePath);
            }
            catch (Exception)
            {
                // 單一檔案探測失敗（被鎖住、路徑暫時不可用）不該讓整輪停擺；
                // 跳過它並繼續前進，下一圈的游標不會再卡在同一個檔案上。
                fileSeconds = null;
            }

            if (fileSeconds is not > 0)
            {
                skipped++;
                continue;
            }

            var indexed = row.DurationSec ?? 0;
            var resolved = RecordedSegmentLength.Resolve(fileSeconds, indexed);
            if (Math.Abs(resolved - indexed) <= ToleranceSeconds)
            {
                continue;
            }

            _segments.RepairDuration(row.Id, row.StartUtc.AddSeconds(resolved), resolved);
            repaired++;

            // 稽核是「證據指向錯誤的攝影機」這類問題唯一的追查線索：記下改了哪一段、
            // 從多少改成多少，以及那時候索引說的是什麼。
            _audit?.Record(
                "system",
                "recording.duration.repair",
                "recording",
                "segment",
                row.Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"channel={row.ChannelId} old={indexed:0.###} new={resolved:0.###} path={row.FilePath}"),
                nowUtc);
        }

        // 撈不滿一批＝已經到底了。寫 -1 讓後續輪次直接跳過，而不是每小時從第一段重掃。
        var next = rows.Count < batchSize ? -1 : lastId;
        _segments.SetDurationRepairCursor(next, nowUtc);

        return new DurationRepairReport(probed, repaired, skipped, next);
    }
}
