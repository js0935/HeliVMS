using System.Globalization;

namespace HeliVMS.Shared;

/// <summary>
/// 時刻輸入的解析（匯出精靈的起訖欄位）。
/// </summary>
/// <remarks>
/// <para>
/// 接受 <c>HH:mm</c>、<c>HH:mm:ss</c>、<c>HH:mm:ss.f+</c>，以及現場慣用的緊湊寫法
/// <c>HHmm</c>、<c>HHmmss</c>、<c>HHmmssff</c>、<c>HHmmssfff</c>——與分段檔名
/// <c>seg-HHmmssfff.mp4</c> 同一種寫法，操作員看著檔名打的就是這個。
/// </para>
/// <para>
/// 存在的理由：操作員把 <c>11:04:07.25</c> 打成 <c>11040725</c>，而
/// <c>TimeSpan.TryParse</c> 讀不懂它；舊程式碼在解析失敗時<b>默默</b>把起訖當成
/// 00:00–23:59:59，於是「匯出 5 秒」變成「匯出一整天」（現場回報：設定 5 秒、拿到 50 分鐘）。
/// 解析這種事寧可擋下來說格式不對，也不要猜一個範圍——猜錯的成品是有紀錄價值的檔案，
/// 代價比一句錯誤訊息高得多。
/// </para>
/// </remarks>
public static class ClockTime
{
    /// <summary>解析時刻字串；無法解析或超出 00:00:00–23:59:59.9999999 時回 false。</summary>
    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        return value.Contains(':', StringComparison.Ordinal)
            ? TryParseSeparated(value, out time)
            : TryParseCompact(value, out time);
    }

    /// <summary><c>HH:mm</c>／<c>HH:mm:ss</c>／<c>HH:mm:ss.fff</c>。</summary>
    private static bool TryParseSeparated(string value, out TimeSpan time)
    {
        time = default;
        var parts = value.Split(':');
        if (parts.Length is not (2 or 3))
        {
            return false;
        }

        if (!TryParsePart(parts[0], 23, out var hours) || !TryParsePart(parts[1], 59, out var minutes))
        {
            return false;
        }

        var seconds = 0;
        var fractionTicks = 0L;
        if (parts.Length == 3)
        {
            // 秒可以帶小數（.fff 或 ,fff）：分段檔名有毫秒，範圍也該給到毫秒。
            var secondPart = parts[2];
            var dot = secondPart.IndexOfAny(['.', ',']);
            var wholePart = dot < 0 ? secondPart : secondPart[..dot];
            var fractionPart = dot < 0 ? string.Empty : secondPart[(dot + 1)..];

            if (!TryParsePart(wholePart, 59, out seconds))
            {
                return false;
            }

            if (fractionPart.Length > 0)
            {
                if (fractionPart.Length > 7 || !fractionPart.All(char.IsAsciiDigit))
                {
                    return false;
                }

                // 0.25 秒＝2,500,000 ticks，而不是 25 ticks：右補零到 7 位再解析。
                fractionTicks = long.Parse(fractionPart.PadRight(7, '0'), CultureInfo.InvariantCulture);
            }
        }

        time = new TimeSpan(0, hours, minutes, seconds).Add(TimeSpan.FromTicks(fractionTicks));
        return true;
    }

    /// <summary><c>HHmm</c>／<c>HHmmss</c>／<c>HHmmssff</c>／<c>HHmmssfff</c>（純數字）。</summary>
    private static bool TryParseCompact(string value, out TimeSpan time)
    {
        time = default;
        if (!value.All(char.IsAsciiDigit) || value.Length is not (4 or 6 or 8 or 9))
        {
            return false;
        }

        var hours = int.Parse(value[..2], CultureInfo.InvariantCulture);
        var minutes = int.Parse(value[2..4], CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59)
        {
            return false;
        }

        var seconds = 0;
        var fractionTicks = 0L;
        if (value.Length >= 6)
        {
            seconds = int.Parse(value[4..6], CultureInfo.InvariantCulture);
            if (seconds > 59)
            {
                return false;
            }
        }

        if (value.Length > 6)
        {
            // 8 位＝百分秒、9 位＝毫秒（與 seg-HHmmssfff.mp4 一致）。
            var fractionPart = value[6..];
            fractionTicks = long.Parse(fractionPart.PadRight(7, '0'), CultureInfo.InvariantCulture);
        }

        time = new TimeSpan(0, hours, minutes, seconds).Add(TimeSpan.FromTicks(fractionTicks));
        return true;
    }

    private static bool TryParsePart(string text, int max, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
           value >= 0 && value <= max &&
           text.Length > 0;
}
