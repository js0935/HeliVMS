using System.Globalization;
using System.Text;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.WebApi;

/// <summary>錄影回放的 HLS VOD 播放清單（§14.3 遠程串流 P0）。</summary>
/// <remarks>
/// 只做「已錄影片段」的點播（VOD），不做即時轉檔：那需要拉流與轉碼佇列，
/// 屬於另一個量級的基礎設施。播放器端用 HLS.js／原生 HLS 皆可。
/// </remarks>
public static class HlsPlaylist
{
    public const string ContentType = "application/vnd.apple.mpegurl";

    /// <summary>EXT-X-MAP 需要的初始化段：fMP4 的 ftyp＋moov。</summary>
    public const string InitContentType = "video/mp4";

    /// <summary>EXTINF 的媒體段：只有 moof/mdat。</summary>
    public const string MediaContentType = "video/iso.segment";

    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string InitUri(long segmentId) => $"/api/stream/segment/{segmentId}/init.mp4";

    public static string MediaUri(long segmentId) => $"/api/stream/segment/{segmentId}/media.m4s";

    public static string Build(IReadOnlyList<SegmentRecord> segments)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:7\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(TargetDuration(segments)).Append('\n');

        if (segments.Count > 0)
        {
            sb.Append("#EXT-X-MAP:URI=\"").Append(InitUri(segments[0].Id)).Append("\"\n");
            AppendSegment(sb, segments[0]);
            for (var i = 1; i < segments.Count; i++)
            {
                AppendSegment(sb, segments[i]);
            }
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// EXTINF 秒數：以索引的結束時間為準，缺結束時間才退回 <c>duration_sec</c>，
    /// 兩者都沒有時用與桌面回放、時間軸相同的估算值。
    /// </summary>
    /// <remarks>
    /// 舊寫法在結束時間與段長都缺時回 0，清單於是出現 <c>#EXTINF:0.000</c>。EXTINF 是分段的
    /// 長度，播放器拿到 0 會把這一段當成不存在直接跳過（或整條清單卡住），而磁碟上明明有檔案、
    /// 桌機回放也看得到——只有遠端 HLS 少一段，且沒有任何錯誤訊息可查。估算值一定要跟
    /// <see cref="PlaybackSegmentTiming"/> 同一個，否則同一段影片在兩個介面上長度不同。
    /// </remarks>
    public static double DurationSeconds(SegmentRecord segment)
    {
        if (segment.EndUtc is { } end && end > segment.StartUtc)
        {
            return (end - segment.StartUtc).TotalSeconds;
        }

        // 結束時間不可信（等於或早於起點）時改用索引的段長；連段長都沒寫才估算。
        return segment.DurationSec is > 0 and var durationSec
            ? durationSec
            : PlaybackSegmentTiming.FallbackDurationSeconds;
    }

    /// <summary>EXT-X-TARGETDURATION 必須是所有片段時長向上取整後的最大值。</summary>
    public static int TargetDuration(IReadOnlyList<SegmentRecord> segments)
    {
        var max = 0d;
        foreach (var segment in segments)
        {
            max = Math.Max(max, DurationSeconds(segment));
        }

        return Math.Max(1, (int)Math.Ceiling(max));
    }

    private static void AppendSegment(StringBuilder sb, SegmentRecord segment)
    {
        sb.Append("#EXT-X-PROGRAM-DATE-TIME:")
            .Append(segment.StartUtc.ToUniversalTime().ToString(DateTimeFormat, CultureInfo.InvariantCulture))
            .Append('\n');
        sb.Append("#EXTINF:")
            .Append(DurationSeconds(segment).ToString("0.000", CultureInfo.InvariantCulture))
            .Append(",\n");
        sb.Append(MediaUri(segment.Id)).Append('\n');
    }
}
