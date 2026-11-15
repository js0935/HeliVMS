namespace HeliVMS.Shared;

/// <summary>
/// 換解碼寬度時「靜默換手」可以關起門來的時間長度。
/// </summary>
/// <remarks>
/// <para>
/// 靜默換手的用途是消掉「切換分割／點放大時每一格閃『連線中』」：那次重開只是換解析度，
/// 畫面本來就會回來，不是故障。但「靜默多久」不能是固定值——實測同一個站台裡的兩端差距很大：
/// 區網攝影機換手 0.5–1 秒，十路網際網路攝影機同時重開時量到 5.2 秒。固定 2.5 秒的結果是
/// 慢的那一半每一格都在換手完成前就照實發布「連線中」（等於靜默根本沒生效，現場回報的
/// 「切換分割攝影機會出現重新連線」就是這樣來的），而快的那一半留著一段不必要的
/// 「定格卻標著串流中」。
/// </para>
/// <para>
/// 所以寬限取「這一路自己上一次連上花了多久」的兩倍（留一次抖動的餘裕），並夾在上下限之間。
/// 上限刻意低於健康檢查的停滯判定（<c>ChannelManager</c> 的 <c>StaleSeconds</c> ＝ 10 秒）：
/// 靜默撐得比停滯判定還久，就會出現「格位寫著串流中、健康檢查卻正在重啟它」的矛盾畫面。
/// </para>
/// <para>
/// 抽成純函式的理由與 <see cref="LiveWallBudget"/> 相同：這條界線錯了不會有任何錯誤訊息，
/// 只有現場看得到（太短＝閃「連線中」、太長＝定格卻說正常）。
/// </para>
/// </remarks>
public static class SilentHandoffGrace
{
    /// <summary>量到的啟動延遲要乘上的倍數（一次抖動的餘裕）。</summary>
    public const double StartupLatencyFactor = 2.0;

    /// <summary>預設的寬限下限：連啟動延遲都還沒量到時用它。</summary>
    public static readonly TimeSpan DefaultFloor = TimeSpan.FromSeconds(2.5);

    /// <summary>預設的寬限上限（必須低於停滯判定 10 秒）。</summary>
    public static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 算出這一次靜默換手可以撐多久。
    /// </summary>
    /// <param name="floor">最小值（呼叫端設定，測試可用很短的值）。</param>
    /// <param name="measuredStartupLatency">這一路上一次「連線中→串流中」花多久；null／0＝還沒量到。</param>
    /// <param name="ceiling">最大值；null＝<see cref="DefaultCeiling"/>。小於下限時以下限為準。</param>
    public static TimeSpan Resolve(
        TimeSpan floor,
        TimeSpan? measuredStartupLatency,
        TimeSpan? ceiling = null)
    {
        var min = floor > TimeSpan.Zero ? floor : TimeSpan.Zero;

        var max = ceiling ?? DefaultCeiling;
        if (max < min)
        {
            max = min;
        }

        // 還沒量到（第一次連線還沒成功過）就退回下限：憑空猜一個比設定值更大的數字更糟。
        if (measuredStartupLatency is not { } measured || measured <= TimeSpan.Zero)
        {
            return min;
        }

        var scaled = TimeSpan.FromSeconds(measured.TotalSeconds * StartupLatencyFactor);
        return scaled < min ? min : scaled > max ? max : scaled;
    }
}
