namespace HeliVMS.Shared;

/// <summary>
/// 視覺搜索的排序核心（QC_Live 的 <c>PopupVisualSearch</c> 對齊）：
/// 樣本圖的感知雜湊 vs 事件快照雜湊，以漢明距離排序、門檻截斷。
/// 純函式，行為測試在 <c>VisualSearchRankTests</c>。
/// </summary>
/// <remarks>
/// 距離門檻的語意：<see cref="PerceptualHash"/> 是 64-bit dHash（9×8 相鄰比較），
/// 距離 = 不同位元數。0＝位圖全同；同一台攝影機同一場景的快照通常 ≤10；
/// 不同場景動輒 25+。預設門檻取 12——寧可少報（操作員放寬）也不出整頁雜訊。
/// </remarks>
public static class VisualSearchRank
{
    /// <summary>預設漢明距離門檻（64-bit dHash 的位元差）。</summary>
    public const int DefaultThreshold = 12;

    /// <summary>門檻上限：再寬鬆就等於不比對，回給操作員的是整頁雜訊。</summary>
    public const int MaxThreshold = 32;

    /// <summary>單一搜尋結果。</summary>
    public sealed record Match(long EventId, int Distance);

    /// <summary>把門檻夾到 0–<see cref="MaxThreshold"/>。</summary>
    public static int ClampThreshold(int threshold) => Math.Clamp(threshold, 0, MaxThreshold);

    /// <summary>
    /// 以樣本雜湊對候選排序：距離 ≤ <paramref name="threshold"/> 的留在結果裡，
    /// 近的在前；同距離以事件 id 升冪（穩定、可重現）。
    /// </summary>
    public static IReadOnlyList<Match> Rank(
        ulong sampleHash, IEnumerable<(long EventId, ulong Hash)> candidates, int threshold)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var limit = ClampThreshold(threshold);
        var matches = new List<Match>();
        foreach (var (eventId, hash) in candidates)
        {
            var distance = PerceptualHash.HammingDistance(sampleHash, hash);
            if (distance <= limit)
            {
                matches.Add(new Match(eventId, distance));
            }
        }

        return matches
            .OrderBy(m => m.Distance)
            .ThenBy(m => m.EventId)
            .ToList();
    }
}
