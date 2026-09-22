namespace HeliVMS.Storage;

/// <summary>
/// POS 交易（M113，§14.7 #8）：<see cref="ChannelId"/> 由呼叫端以「收銀機/登錄台→頻道」對應
/// 標記；其他欄位直接取自 <see cref="POSEvent"/>。
/// </summary>
public sealed record PosTransaction(
    long Id,
    int? ChannelId,
    DateTime AtUtc,
    long AmountCents);

/// <summary>VMS 事件（M113）：頻道＋發生時刻＋型別，供與 POS 交易關聯。</summary>
public sealed record VideoEvent(
    long EventId,
    int ChannelId,
    DateTime AtUtc,
    string EventType);

/// <summary>關聯配對：同一頻道 |Δt|≤窗內最接近者。</summary>
public sealed record PosLinkPair(
    long TxnId,
    long EventId,
    TimeSpan Offset);

/// <summary>
/// 關聯結果（M113）：MatchedTxns＝有事件佐證的 POS 交易數；UnmatchedTxns＝僅 POS 無事件；
/// OrphanEvents＝僅事件無 POS（可能漏帳/手動/賒欠）；Pairs＝逐筆配對。
/// </summary>
public sealed record PosLinkSummary(
    int MatchedTxns,
    int UnmatchedTxns,
    int OrphanEvents,
    IReadOnlyList<PosLinkPair> Pairs);

/// <summary>
/// POS↔事件自動關聯引擎（M113，§14.7 #8）：把 POS 交易與同一頻道、|Δt|≤窗內之 VMS 事件
/// 配對（每筆事件至多配一筆交易，取時差最小者），輸出 matched/unmatched/orphan 統計與配對清單。
/// 純 BCL，無 IO。Windows 採「左閉右閉」含端點。
/// </summary>
public sealed class PosEventLinker
{
    public static PosLinkSummary Link(
        IReadOnlyList<PosTransaction> txns,
        IReadOnlyList<VideoEvent> events,
        TimeSpan window)
    {
        var byChannel = events
            .GroupBy(e => e.ChannelId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.AtUtc).ToList());

        var used = new HashSet<long>();
        var pairs = new List<PosLinkPair>();
        var matched = 0;

        foreach (var txn in txns.OrderBy(t => t.AtUtc))
        {
            if (txn.ChannelId is not { } channel ||
                !byChannel.TryGetValue(channel, out var candidates))
            {
                continue;
            }

            VideoEvent? best = null;
            TimeSpan bestOffset = default;
            foreach (var ev in candidates)
            {
                if (used.Contains(ev.EventId))
                {
                    continue;
                }

                var offset = ev.AtUtc - txn.AtUtc;
                if (offset < -window || offset > window)
                {
                    continue;
                }

                if (best is null || Math.Abs(offset.Ticks) < Math.Abs(bestOffset.Ticks))
                {
                    best = ev;
                    bestOffset = offset;
                }
            }

            if (best is not null)
            {
                used.Add(best.EventId);
                pairs.Add(new PosLinkPair(txn.Id, best.EventId, bestOffset));
                matched++;
            }
        }

        var orphan = byChannel.Values.Sum(list => list.Count(e => !used.Contains(e.EventId)));
        return new PosLinkSummary(matched, txns.Count - matched, orphan, pairs);
    }
}