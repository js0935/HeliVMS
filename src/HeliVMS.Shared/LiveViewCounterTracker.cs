namespace HeliVMS.Shared;

/// <summary>
/// 每個頻道各自記住上一次的累計影格數，並算出這一輪的每秒率（診斷日誌用）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼不是一個純函式就好：率需要「上一次」的狀態，而那個狀態的生命週期本身就是一個坑——
/// 第一版在取樣迴圈<em>之前</em>清掉字典（想順手清掉不再顯示的頻道），於是每一輪都像第一次取樣，
/// 日誌上永遠是 <c>read=0/s</c>；那個 0 看起來只像「沒有影格」，實際上計數器一直在動。
/// </para>
/// <para>
/// 所以把「保留誰」與「算率」分成兩個動作：<see cref="Record"/> 只算率，
/// <see cref="KeepOnly"/> 在取樣<em>之後</em>才修剪，且由呼叫端明確說出還要哪些頻道。
/// </para>
/// </remarks>
public sealed class LiveViewCounterTracker
{
    private readonly Dictionary<int, (LiveFrameRates.Counters Counters, DateTime AtUtc)> _previous = [];

    /// <summary>這一輪的每秒率；該頻道第一次取樣（或上一筆已超過 <paramref name="staleAfter"/>）時回 0。</summary>
    /// <param name="channelId">頻道識別碼（各頻道各自記住上一筆）。</param>
    /// <param name="counters">這一輪的累計影格數。</param>
    /// <param name="utcNow">取樣時刻（用來算實際間隔）。</param>
    /// <param name="staleAfter">
    /// 超過這個間隔就不再與上一筆相減：中間可能有換台／暫停，相減得到的平均率沒有意義。
    /// </param>
    public LiveFrameRates.Rates Record(
        int channelId,
        LiveFrameRates.Counters counters,
        DateTime utcNow,
        TimeSpan? staleAfter = null)
    {
        var limit = staleAfter ?? TimeSpan.FromSeconds(30);
        _previous.TryGetValue(channelId, out var previous);
        var elapsed = previous.AtUtc == default ? TimeSpan.Zero : utcNow - previous.AtUtc;
        var usable = previous.AtUtc != default && elapsed <= limit;

        var rates = LiveFrameRates.PerSecond(usable ? previous.Counters : null, counters, elapsed);
        _previous[channelId] = (counters, utcNow);
        return rates;
    }

    /// <summary>只留下還需要的頻道（在取樣之後呼叫）。</summary>
    public void KeepOnly(IEnumerable<int> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        var keep = new HashSet<int>(channelIds);
        foreach (var stale in _previous.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            _previous.Remove(stale);
        }
    }
}
