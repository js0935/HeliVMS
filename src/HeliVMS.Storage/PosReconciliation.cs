namespace HeliVMS.Storage;

/// <summary>POS 對帳結果（M104，§14.7 #8）：同區間交易相對於門禁/事件候選之配對統計。</summary>
public sealed record PosReconSummary(
    int Total,
    int Matched,
    int Unmatched,
    int Duplicates)
{
    public static PosReconSummary None() => new(0, 0, 0, 0);
}

/// <summary>
/// POS 對帳器（M104，§14.7 #8，純 BCL）：把 POS 交易與同設備之事件候選（門禁 etc.，M93 時間窗
/// 語意）逐一配對，產出 matched/unmatched/duplicates 統計；重複判斷＝同收銀機＋交易號＋金額。
/// </summary>
public static class PosReconciliation
{
    /// <summary>
    /// 對 <paramref name="transactions"/> 逐筆配對 <paramref name="candidates"/>（|Δt|≤window）。
    /// 一筆交易只對映到一事件（最接近者，沿用 M93 <see cref="POSEventMatcher"/> 排序語意）；
    /// 交易連重複計入 <see cref="PosReconSummary.Duplicates"/>（不重複計 matched）。
    /// </summary>
    public static PosReconSummary Compute<TEvent>(
        IReadOnlyList<POSEvent> transactions,
        IReadOnlyList<TEvent> candidates,
        Func<TEvent, DateTime> timestampOf,
        TimeSpan window)
    {
        if (transactions.Count == 0)
        {
            return PosReconSummary.None();
        }

        var byTimestamp = candidates
            .Select(t => timestampOf(t))
            .OrderBy(t => t)
            .ToList();

        var matched = 0;
        foreach (var pos in transactions)
        {
            var nearest = byTimestamp
                .Select(t => t - pos.OccurredAtUtc)
                .Where(d => d.Duration() <= window)
                .OrderBy(d => d.Duration())
                .FirstOrDefault();
            if (nearest != default(TimeSpan))
            {
                matched++;
            }
        }

        var duplicates = 0;
        foreach (var group in transactions
            .GroupBy(t => (t.RegisterId, t.TransactionNo, t.AmountCents))
            .Where(g => g.Count() > 1))
        {
            duplicates += group.Count() - 1;
        }

        return new PosReconSummary(transactions.Count, matched, transactions.Count - matched, duplicates);
    }
}