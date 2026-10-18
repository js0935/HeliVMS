namespace HeliVMS.Shared;

/// <summary>
/// 回放「時間跳轉」輸入的解析（§8.5）：吃完整日期時間（<c>2026-09-12 14:30:00</c>）或當日時刻。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼日期可給可不給：日期平常由日曆選著，操作員從事件清單抄時間時只抄時刻最快；
/// 但規格給的是完整日期時間那一串，複製貼上必須能用，否則貼上＝無效輸入。
/// </para>
/// <para>
/// 為什麼不吞只有日期的輸入：<c>2026-09-12</c> 少了時刻，猜一個時刻跳過去會讓操作員
/// 以為自己跳到了指定的時間點；寧可回報無效輸入。
/// </para>
/// </remarks>
public static class PlaybackTimeJump
{
    private static readonly string[] DateAndTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm",
    ];

    /// <summary>
    /// 解析成功時回傳當日時刻與（輸入含日期才有的）日期；無法解析時回傳 null。
    /// </summary>
    public static (TimeSpan Clock, DateTime? Date)? TryParse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (DateTime.TryParseExact(
                trimmed,
                DateAndTimeFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var stamp))
        {
            return (stamp.TimeOfDay, stamp.Date);
        }

        if (TimeSpan.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var clock)
            && clock >= TimeSpan.Zero
            && clock < TimeSpan.FromDays(1))
        {
            return (clock, null);
        }

        return null;
    }
}
