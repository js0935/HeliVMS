using System.Globalization;

namespace HeliVMS.Shared;

/// <summary>
/// 巡航時段 "HH:mm" 的解析（M72／§47）。
/// </summary>
/// <remarks>
/// 為什麼不用 <c>TimeSpan.TryParseExact</c>：現場機器常見中文或波蘭文 locale，
/// 框架的 TryParseExact 在那種 culture 下連 "hh\\:mm" 這種固定格式都不認，
/// 結果是所有巡航時段被判成壞值。壞值的後果是「視為全天執行」，
/// 等於每台攝影機整天都在轉頭——比不排程更糟，而且畫面上看不出異常。
/// </remarks>
public static class PatrolWindowTime
{
    /// <summary>全天時窗，作為時段無法解析時的退避值。</summary>
    public static readonly TimeSpan AllDayEnd = new(23, 59, 0);

    /// <summary>
    /// 解析單一端點。接受 "H:mm"／"HH:mm"；分鐘與小時各自檢查範圍，
    /// 不用 ParseExact 的原因是寬鬆但範圍嚴格比寬鬆但默默接受 "25:00" 好。
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (text is null)
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts is not [var hoursText, var minutesText])
        {
            return false;
        }

        // NumberStyles.None：只收十進位整數，不接受前導正負號或千分位。
        // "+9:30" 通過的話，巡邏時段會有兩種寫法都代表同一時刻，
        // 操作員改設定時就會出現看不出差異卻行為不同的排程。
        if (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        if (hours is < 0 or > 23 || minutes is < 0 or > 59)
        {
            return false;
        }

        value = new TimeSpan(hours, minutes, 0);
        return true;
    }

    /// <summary>
    /// 解析一整段時窗。任一端壞掉就整段視為全天（<c>00:00</c>–<c>23:59</c>），
    /// 讓操作員至少看得到攝影機有在動，再靠事件記錄指出資料格式不對。
    /// </summary>
    public static (TimeSpan Start, TimeSpan End) Parse(string? windowStart, string? windowEnd)
        => TryParse(windowStart, out var start) && TryParse(windowEnd, out var end)
            ? (start, end)
            : (TimeSpan.Zero, AllDayEnd);
}
