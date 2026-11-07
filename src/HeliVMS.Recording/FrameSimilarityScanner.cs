using System.Collections.Concurrent;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>以圖搜圖的參數。</summary>
public sealed record FrameSimilarityOptions
{
    /// <summary>抽樣間隔（秒）。愈小愈不容易漏掉短暫出現的畫面、成本愈高。</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>命中門檻（漢明距離 0–64，愈小愈嚴格）。</summary>
    public int MaxHammingDistance { get; init; } = 10;

    /// <summary>最多回傳幾筆命中（依相似度排序）。</summary>
    public int MaxHits { get; init; } = 200;

    /// <summary>同時抽樣的段數上限。搜尋通常在錄影進行中執行——預設留給錄影。</summary>
    public int MaxParallelism { get; init; } = 2;

    /// <summary>單段抽樣的逾時。</summary>
    public TimeSpan PerSegmentTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>可選的 ffmpeg 路徑。</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>可選的記錄（逐段失敗等）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>以圖搜圖的命中：某段錄影的某個時刻與查詢影像相似。</summary>
/// <param name="SegmentId">來源段。</param>
/// <param name="OccurredAtUtc">命中的錄影時刻（UTC）——可直接餵回放的跳轉。</param>
/// <param name="Distance">漢明距離（0–64；愈小愈像）。</param>
public sealed record FrameSimilarityHit(long SegmentId, DateTime OccurredAtUtc, int Distance);

/// <summary>以圖搜圖的查詢。</summary>
public sealed record FrameSimilarityQuery(int ChannelId, string Stream, DateTime FromUtc, DateTime ToUtc, ulong QueryHash)
{
    public FrameSimilarityOptions Options { get; init; } = new();
}

/// <summary>
/// 段與查詢窗的交集計算（純函式，行為測試釘住）。
/// </summary>
/// <remarks>
/// 段的時間語意有兩個坑：①<c>segments.start_time</c> 是 ffmpeg 的<b>啟動</b>時刻，
/// 檔案內容以 0 起算，所以「查詢窗相對檔案的偏移」＝查詢起點 − 段起點（可能為負，
/// 要夾到 0）；②<c>EndUtc</c> 在剛完成的段上可能還沒寫入，退回 <c>DurationSec</c>。
/// 這兩個坑寫錯的症狀都是「搜得到但跳轉到錯的時間」或「整段被跳過」，
/// 而且畫面上分不出來——所以獨立成純函式逐案測試。
/// </remarks>
public static class FrameSimilarityPlanner
{
    /// <summary>
    /// 查詢窗與段的交集（段內偏移、長度）；無交集回 <c>null</c>。
    /// 區間一律半開 <c>[start, end)</c>——與整份播放側的慣例一致。
    /// </summary>
    public static (TimeSpan From, TimeSpan Span)? IntersectWindow(
        DateTime segmentStartUtc,
        DateTime? segmentEndUtc,
        double? segmentDurationSec,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var effectiveEnd = segmentEndUtc
            ?? (segmentDurationSec is { } d and > 0
                ? segmentStartUtc + TimeSpan.FromSeconds(d)
                : toUtc);

        var start = segmentStartUtc > fromUtc ? segmentStartUtc : fromUtc;
        var end = effectiveEnd < toUtc ? effectiveEnd : toUtc;
        if (end <= start)
        {
            return null;
        }

        var fromInSegment = start - segmentStartUtc;
        if (fromInSegment < TimeSpan.Zero)
        {
            // 段起點晚於查詢起點時上面已夾過；這裡是防禦（時鐘回跳等）。
            fromInSegment = TimeSpan.Zero;
        }

        return (fromInSegment, end - start);
    }
}

/// <summary>
/// 以圖搜圖（QCV8001 <c>QCJpegSearch</c> 對應功能的 HeliVMS 實作）：
/// 給查詢影像的感知雜湊與時間範圍，掃描該頻道的錄影段、回傳相似時刻。
/// </summary>
/// <remarks>
/// <para>
/// 流程：查段（<see cref="SegmentRepository.ListPlayableByRange"/>）→ 逐段以
/// <see cref="FrameSampleExtractor"/> 等間隔抽 32×32 灰階樣本 → 每個樣本算
/// <see cref="PerceptualHash"/> → 漢明距離在門檻內即命中。
/// </para>
/// <para>
/// <b>單段失敗不放棄整場搜尋</b>：錄影磁碟在寫入中、個別檔案毀損、ffmpeg 對
/// 某段的 seek 失敗——都會發生在真實機隊上。逐段記 log 繼續掃，回傳「找到的」，
/// 而不是讓一個壞段讓整場搜尋變成例外。
/// </para>
/// <para>
/// <b>結果依距離排序且附距離值</b>：低紋理畫面（全黑、一面牆）天生距離就小，
/// UI 必須顯示距離讓操作員分辨「真命中」與「雜湊沒有判別力」——
/// 只顯示「找到 N 筆」會把兩者混在一起。
/// </para>
/// </remarks>
public sealed class FrameSimilarityScanner
{
    private readonly SegmentRepository _segments;

    public FrameSimilarityScanner(SegmentRepository segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments;
    }

    /// <summary>由圖片檔（JPEG/PNG）算查詢指紋——與掃描樣本走同一條縮放管線。</summary>
    public static ulong HashImage(
        string imagePath,
        string? ffmpegPath = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var gray = FrameSampleExtractor.ExtractSingleFrame(imagePath, ffmpegPath, timeout, cancellationToken);
        return PerceptualHash.FromGrayImage(gray, FrameSampleExtractor.SampleSize, FrameSampleExtractor.SampleSize);
    }

    /// <summary>掃描（阻塞——呼叫端自行放背景執行緒）。</summary>
    public IReadOnlyList<FrameSimilarityHit> Scan(
        FrameSimilarityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ToUtc <= query.FromUtc)
        {
            return [];
        }

        var options = query.Options;
        var interval = options.SampleInterval > TimeSpan.Zero
            ? options.SampleInterval
            : TimeSpan.FromSeconds(15);
        var threshold = Math.Clamp(options.MaxHammingDistance, 0, PerceptualHash.Bits);
        var parallelism = Math.Clamp(options.MaxParallelism, 1, 8);

        var segments = _segments.ListPlayableByRange(query.ChannelId, query.Stream, query.FromUtc, query.ToUtc);
        if (segments.Count == 0)
        {
            return [];
        }

        var hits = new ConcurrentBag<FrameSimilarityHit>();
        Parallel.ForEach(
            segments,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            segment =>
            {
                var window = FrameSimilarityPlanner.IntersectWindow(
                    segment.StartUtc, segment.EndUtc, segment.DurationSec, query.FromUtc, query.ToUtc);
                if (window is not { } w)
                {
                    return;
                }

                if (!File.Exists(segment.FilePath))
                {
                    options.Log?.Invoke($"以圖搜圖：段 {segment.Id} 的檔案不在，略過（{segment.FilePath}）");
                    return;
                }

                var maxSamples = (int)Math.Ceiling(w.Span.TotalSeconds / interval.TotalSeconds) + 1;
                try
                {
                    FrameSampleExtractor.ExtractSamples(
                        segment.FilePath,
                        w.From,
                        w.Span,
                        interval.TotalSeconds,
                        maxSamples,
                        sample =>
                        {
                            var hash = PerceptualHash.FromGrayImage(
                                sample.Gray32x32, FrameSampleExtractor.SampleSize, FrameSampleExtractor.SampleSize);
                            var distance = PerceptualHash.HammingDistance(hash, query.QueryHash);
                            if (distance <= threshold)
                            {
                                hits.Add(new FrameSimilarityHit(
                                    segment.Id,
                                    segment.StartUtc + w.From + sample.Offset,
                                    distance));
                            }
                        },
                        options.FfmpegPath,
                        options.PerSegmentTimeout,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 單段失敗（磁碟忙、檔案毀損、seek 失敗）不該讓整場搜尋失敗——記 log 續掃。
                    options.Log?.Invoke($"以圖搜圖：段 {segment.Id} 抽樣失敗：{ex.Message}");
                }
            });

        return hits
            .OrderBy(h => h.Distance)
            .ThenBy(h => h.OccurredAtUtc)
            .Take(Math.Max(1, options.MaxHits))
            .ToList();
    }
}
