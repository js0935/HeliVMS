using System;
using System.Collections.Generic;
using System.Linq;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording;

/// <summary>由實際錄影檔推估出的單一頻道碼率與其可信度。</summary>
/// <param name="ChannelId">頻道 ID。</param>
/// <param name="ObservedBps">實際平均碼率（bps）；沒有可用樣本時為 0。</param>
/// <param name="SampleCount">納入計算的區段數。</param>
/// <param name="WindowStartUtc">樣本區間起點。</param>
/// <param name="WindowEndUtc">樣本區間終點。</param>
public readonly record struct ObservedBitrate(
    int ChannelId,
    long ObservedBps,
    int SampleCount,
    DateTime? WindowStartUtc,
    DateTime? WindowEndUtc)
{
    /// <summary>樣本不足以推估（單一區段或時長過短時不採信）。</summary>
    public bool IsReliable => SampleCount >= 2;
}

/// <summary>
/// 用已完成的錄影檔推估每台攝影機的實際碼率，供 <see cref="BitrateGovernor"/> 使用。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BitrateGovernor"/> 需要「現況碼率」才能分配，但系統裡沒有任何地方
/// 回報實際碼率——它的測試全是餵假數字。沒有這個推估，頻寬治理就沒有輸入。</para>
/// <para>
/// 刻意用「已完成區段的檔案大小 ÷ 長度」而不是 ffmpeg 的 <c>bitrate=</c> 輸出：
/// 前者不需改動 ffmpeg 啟動參數（那會影響所有既有錄影流程），
/// 而且量到的就是磁碟實際佔用，與磁碟容量告警用的是同一個數字。</para>
/// <para>
/// 只統計時間完整的區段：被刪到一半或正在寫的區段，長度會被低估而讓碼率被高估，
/// 高估的碼率會讓治理器算出偏低的配額，等於在最該省頻寬時反而給太少。</para>
/// </remarks>
public static class ObservedBitrateEstimator
{
    /// <summary>樣本數低於此值就不採信推估值。</summary>
    public const int MinReliableSamples = 2;

    /// <summary>單一頻道的實際碼率。</summary>
    public static ObservedBitrate ForChannel(int channelId, IReadOnlyList<SegmentRecord> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var usable = segments
            .Where(s => s.SizeBytes > 0
                && s.EndUtc is { } end
                && s.StartUtc != end
                && end > s.StartUtc
                && (end - s.StartUtc).TotalSeconds > 0)
            .ToList();

        if (usable.Count == 0)
        {
            return new ObservedBitrate(channelId, 0, 0, null, null);
        }

        // 用「總位元組 ÷ 總秒數」而不是「各段碼率平均」：平均會讓長短區段等權，
        // 而實際佔用的是加總流量，兩者在區段長度不一致時差很多。
        var totalBytes = usable.Aggregate(0L, (s, x) => s + x.SizeBytes);
        var totalSeconds = usable.Aggregate(0d, (s, x) => s + (x.EndUtc!.Value - x.StartUtc).TotalSeconds);

        return totalSeconds <= 0
            ? new ObservedBitrate(channelId, 0, usable.Count, usable.Min(s => s.StartUtc), usable.Max(s => s.EndUtc))
            : new ObservedBitrate(
                channelId,
                (long)(totalBytes * 8 / totalSeconds),
                usable.Count,
                usable.Min(s => s.StartUtc),
                usable.Max(s => s.EndUtc));
    }

    /// <summary>各頻道的實際碼率，依頻道 ID 回傳。</summary>
    public static IReadOnlyDictionary<int, ObservedBitrate> ForChannels(IReadOnlyList<SegmentRecord> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return segments
            .GroupBy(s => s.ChannelId)
            .ToDictionary(g => g.Key, g => ForChannel(g.Key, g.ToList()));
    }

    /// <summary>依頻寬預算算出各頻道建議碼率；輸入是實際碼率與優先權。</summary>
    public static ReallocateResult Govern(
        long budgetBps,
        IReadOnlyList<SegmentRecord> segments,
        Func<int, int> priority,
        Func<int, bool>? inEvent = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(priority);

        var channels = segments
            .GroupBy(s => s.ChannelId)
            .Select(g => new GoChannel(
                g.Key,
                ForChannel(g.Key, g.ToList()).ObservedBps,
                priority(g.Key),
                inEvent?.Invoke(g.Key) == true))
            .ToList();

        return new BitrateGovernor().Adapt(budgetBps, channels);
    }
}
