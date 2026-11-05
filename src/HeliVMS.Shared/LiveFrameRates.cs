namespace HeliVMS.Shared;

/// <summary>
/// 把兩次取樣的累計影格數換算成每秒率（診斷日誌用）。
/// </summary>
/// <remarks>
/// <para>
/// 累計值本身看不出「現在掉幾格」：一台已經跑了三小時的攝影機，<c>FramesRead</c> 是十幾萬，
/// 而問題永遠發生在「剛剛那一秒」。所以日誌要的是率，不是總量。
/// </para>
/// <para>
/// 抽成純函式是因為有一個不明顯的邊界：**重連會讓計數器歸零**（新的 ffmpeg 行程），
/// 此時差值為負。直接拿負的差值去除時間，日誌上就會出現「-140/s」這種沒有人看得懂、
/// 而且會誤導判讀的數字——夾在 0 才是事實（那一段時間沒有可比較的取樣）。
/// </para>
/// </remarks>
public static class LiveFrameRates
{
    /// <summary>某一刻的累計影格數。</summary>
    public readonly record struct Counters(long Read, long Emitted, long Dropped)
    {
        public static Counters Empty => default;
    }

    /// <summary>每秒率（可以有小數）。</summary>
    public readonly record struct Rates(double Read, double Emitted, double Dropped)
    {
        public static Rates Empty => default;
    }

    /// <summary>每秒率；沒有前一筆取樣或間隔為零時回全 0。</summary>
    public static Rates PerSecond(Counters? previous, Counters current, TimeSpan elapsed)
    {
        if (previous is not { } before || elapsed <= TimeSpan.Zero)
        {
            return Rates.Empty;
        }

        var seconds = elapsed.TotalSeconds;
        return new Rates(
            Delta(before.Read, current.Read) / seconds,
            Delta(before.Emitted, current.Emitted) / seconds,
            Delta(before.Dropped, current.Dropped) / seconds);
    }

    /// <summary>差值；計數器歸零（重連）時回 0，不回負值。</summary>
    private static double Delta(long before, long after)
        => after >= before ? after - before : 0;
}
