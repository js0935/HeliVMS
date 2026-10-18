namespace HeliVMS.Storage;

/// <summary>
/// 時間軸檢視窗（§3.4/§8.5）：帶子上看到的 UTC 區間。未縮放時＝全日窗；
/// 滾輪縮放後夾在當日窗內。所有帶子 x 座標換算（色塊、標記、游標、點擊）都走這一個窗，
/// 尺寸改變或縮放時只要換窗重畫，不用重查資料庫。
/// </summary>
public sealed record TimelineViewWindow(DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>窗寬。</summary>
    public TimeSpan Span => EndUtc - StartUtc;

    /// <summary>畫面位置（0..1）換算回時刻。</summary>
    public DateTime TimeAtFraction(double fraction) => StartUtc + TimeSpan.FromSeconds(fraction * Span.TotalSeconds);

    /// <summary>時刻換算為畫面位置（0..1，不夾限；窗外的值用來決定要不要畫）。</summary>
    public double FractionOf(DateTime utc) => (utc - StartUtc).TotalSeconds / Span.TotalSeconds;
}

/// <summary>主時間列上的一根刻度：`Label` 為空字串時只畫刻線不放標籤（次要刻度）。</summary>
public sealed record RulerTick(DateTime Utc, bool IsMajor, string Label);

/// <summary>
/// 時間軸縮放與刻度階梯（§3.4「時間刻度依縮放層級切換」、§8.5「滾輪無級縮放，縮放中心跟隨游標」）。
/// 純 BCL、無 WPF，行為以 <c>PlaybackTimelineScaleTests</c> 驗證。
/// </summary>
public static class PlaybackTimelineScale
{
    /// <summary>縮放下限（§8.5：10 秒）。再放大就只會看到幾秒內的資料，沒有導航意義。</summary>
    public const double MinSpanSeconds = 10;

    /// <summary>每一格滾輪的縮放倍率；無級縮放用小數格數（一次滑兩格＝倍率平方）。</summary>
    public const double ZoomFactorPerNotch = 1.5;

    /// <summary>主刻度階梯（小→大）。選刻度時由小到大找第一個「刻度數不超過上限」的，</summary>
    public static readonly TimeSpan[] MajorSteps =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
    };

    /// <summary>主刻度（有標籤）數上限。12 個 "HH:mm" 在 800px 的帶子上間距約 66px，不會互相擠壓。</summary>
    public const int MaxMajorTicks = 12;

    /// <summary>全日窗（未縮放時的檢視窗，與 PlaybackTimelineBuilder 的日窗同口徑）。</summary>
    public static TimelineViewWindow FullDay(DateTime dayStartUtc)
        => new(dayStartUtc, dayStartUtc.AddSeconds(PlaybackTimeline.DaySeconds));

    /// <summary>
    /// 滾輪縮放：`notches` 正數＝放大（窗變窄，游標下鑽看更細），負數＝縮小（窗變寬）。
    /// `anchorFraction`（游標在帶上的 0..1 位置）對應的時刻在縮放前後留在畫面同一位置，
    /// 縮放後的窗夾在 `[dayStartUtc, dayEndUtc]` 內且寬度不低於 <see cref="MinSpanSeconds"/>。
    /// </summary>
    public static TimelineViewWindow Zoom(
        TimelineViewWindow current,
        DateTime dayStartUtc,
        DateTime dayEndUtc,
        double anchorFraction,
        double notches)
    {
        var daySpanSeconds = (dayEndUtc - dayStartUtc).TotalSeconds;
        if (daySpanSeconds <= MinSpanSeconds || current.Span.TotalSeconds <= 0)
        {
            return new TimelineViewWindow(dayStartUtc, dayEndUtc);
        }

        anchorFraction = Math.Clamp(anchorFraction, 0, 1);
        var newSpanSeconds = Math.Clamp(
            current.Span.TotalSeconds / Math.Pow(ZoomFactorPerNotch, notches),
            MinSpanSeconds,
            daySpanSeconds);

        var anchorUtc = current.TimeAtFraction(anchorFraction);
        var startUtc = anchorUtc.AddSeconds(-newSpanSeconds * anchorFraction);
        var endUtc = startUtc.AddSeconds(newSpanSeconds);

        // 夾回當日窗：貼到邊界時整窗平移，縮放中心的錨點時刻仍留在畫面上。
        if (startUtc < dayStartUtc)
        {
            startUtc = dayStartUtc;
            endUtc = startUtc.AddSeconds(newSpanSeconds);
        }

        if (endUtc > dayEndUtc)
        {
            endUtc = dayEndUtc;
            startUtc = endUtc.AddSeconds(-newSpanSeconds);
        }

        return new TimelineViewWindow(startUtc, endUtc);
    }

    /// <summary>
    /// 縮放滑桿（§8.5 主時間列）的值：以 log10(秒) 表示窗寬，
    /// 下限 <see cref="MinSpanSeconds"/>、上限一整天——與滾輪縮放同一組邊界，
    /// 滑桿與滾輪改的是同一個檢視窗，不會出現滑桿在中段、滾輪卻已貼底的錯位。
    /// </summary>
    public static double SliderValueFromSpan(TimeSpan span)
        => Math.Log10(Math.Clamp(span.TotalSeconds, MinSpanSeconds, PlaybackTimeline.DaySeconds));

    /// <summary>縮放滑桿值換回窗寬（秒），同樣夾在 [MinSpanSeconds, 一天]；越界拖曳不會拉出無意義的窗。</summary>
    public static double SpanSecondsFromSliderValue(double value)
        => Math.Pow(10, Math.Clamp(
            value,
            Math.Log10(MinSpanSeconds),
            Math.Log10(PlaybackTimeline.DaySeconds)));

    /// <summary>
    /// 依窗寬選主刻度：由小到大找第一個「主刻度數 ≤ 上限」的階梯
    /// （24 小時窗→2 小時、12 小時→1 小時、2 小時→10 分鐘、10 分鐘→1 分鐘、10 秒→1 秒）。
    /// </summary>
    public static TimeSpan SelectMajorStep(TimeSpan span, int maxMajorTicks = MaxMajorTicks)
    {
        if (maxMajorTicks < 1)
        {
            maxMajorTicks = 1;
        }

        if (span.TotalSeconds <= 0)
        {
            return MajorSteps[0];
        }

        foreach (var step in MajorSteps)
        {
            if (span.TotalSeconds / step.TotalSeconds <= maxMajorTicks)
            {
                return step;
            }
        }

        return MajorSteps[^1];
    }

    /// <summary>
    /// 主時間列的刻度：主刻度對齊「當地時間」的整刻度（貼齊午夜起算的階梯倍數，不是 UTC——
    /// 否則 +05:30 之類的時區會把 14:00 的標籤畫在 14:30 的格線上）；窗寬 ≥1 個主刻度時
    /// 在兩根主刻度中點加一根無標籤的次要刻度（秒級階梯不加，畫面已經太密）。
    /// 每根刻度各自轉回 UTC，日光節氣切換的那一天也不會整排偏移。
    /// </summary>
    public static IReadOnlyList<RulerTick> Ticks(
        TimelineViewWindow view,
        TimeZoneInfo timeZone,
        int maxMajorTicks = MaxMajorTicks)
    {
        if (view.Span.TotalSeconds <= 0)
        {
            return [];
        }

        var step = SelectMajorStep(view.Span, maxMajorTicks);
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(view.StartUtc, timeZone);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(view.EndUtc, timeZone);
        var labelFormat = step < TimeSpan.FromMinutes(1) ? "HH:mm:ss" : "HH:mm";
        var minor = step >= TimeSpan.FromMinutes(1) ? TimeSpan.FromTicks(step.Ticks / 2) : TimeSpan.Zero;
        var stride = minor > TimeSpan.Zero ? minor : step;

        var ticks = new List<RulerTick>();
        var isMajor = true;
        for (var t = CeilingToLocal(startLocal, step); t <= endLocal; t += stride)
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(t, timeZone);
            if (utc < view.StartUtc)
            {
                continue;
            }

            ticks.Add(new RulerTick(utc, isMajor, isMajor ? t.ToString(labelFormat) : string.Empty));

            // 只有畫次要刻度時才交替升降；秒級階梯沒有次要刻度，每一根都保持主刻度。
            if (minor > TimeSpan.Zero)
            {
                isMajor = !isMajor;
            }
        }

        return ticks;
    }

    /// <summary>下一個 ≥ `local` 的階梯倍數（從當地午夜起算）。</summary>
    private static DateTime CeilingToLocal(DateTime local, TimeSpan step)
    {
        if (step >= TimeSpan.FromMinutes(1))
        {
            var stepMinutes = (int)step.TotalMinutes;
            var minutesOfDay = (int)(local - local.Date).TotalMinutes;
            var aligned = ((minutesOfDay + stepMinutes - 1) / stepMinutes) * stepMinutes;
            return local.Date.AddMinutes(aligned);
        }

        var stepSeconds = (int)step.TotalSeconds;
        var minuteBase = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        var alignedSeconds = ((local.Second + stepSeconds - 1) / stepSeconds) * stepSeconds;
        return minuteBase.AddSeconds(alignedSeconds);
    }
}
