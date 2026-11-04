using System.Globalization;

namespace HeliVMS.Shared;

/// <summary>
/// 播放讀數的時刻格式：<c>18:25:32:015</c>（時:分:秒:毫秒）。
/// </summary>
/// <remarks>
/// <para>
/// 現場要求「秒數後面再加上張數」：同一秒內有幾十張影格，只到秒的話兩台攝影機的畫面
/// 對不到同一張，核對與寫報告時只能猜。最後一欄是那一張影格的毫秒——段內位置本身就是
/// 由影格的 PTS 累加而來（<c>PlaybackWindow._posInside = BasePos + frame.PtsMs / 1000</c>），
/// 所以那是「這張影格確實拍在幾點」而不是牆鐘估計值。
/// </para>
/// <para>
/// 刻意沿用冒號分隔（不是 <c>18:25:32.015</c>）：現場抄讀數時的形狀與既有的時分秒一致，
/// 而小數點在監控畫面縮小後比冒號更容易被漏看。
/// </para>
/// </remarks>
public static class PlaybackClock
{
    /// <summary>時刻格式：時:分:秒:毫秒（毫秒固定三位、補零）。</summary>
    public const string InstantFormat = @"HH\:mm\:ss\:fff";

    /// <summary>把錄影時刻格式化成讀數用字串。</summary>
    public static string FormatInstant(DateTime instant)
        => instant.ToString(InstantFormat, CultureInfo.InvariantCulture);
}
