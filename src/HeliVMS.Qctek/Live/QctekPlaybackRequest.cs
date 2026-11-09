using System.Buffers.Binary;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// QCC 遠端回放的查詢回應組裝與參數解析。
/// </summary>
/// <remarks>
/// <para>
/// 回應格式由 QCC 端的解析器（dvrProxy_pb::SendMonthDateRequest／SendCatalogRequest
/// 的 param_2==2 分支）還原：兩者都是 NEMO 框、payload 以 "OK" 開頭；MONTH_DATE 的
/// 月曆資料在 payload+0x10（48 bytes），CATALOG 的矩陣在 payload+0x14（32 天×24 小時，
/// 每 byte 一小時的錄影量）。
/// </para>
/// <para>
/// <b>參數解析是寬鬆的</b>：QCC 的命令參數順序在反編譯中無法完全確定（sprintf 的
/// varargs 顯示不全），且 OPENFILE/CATALOG/GET_MONTH_DATE 的頻道與時間欄位順序
/// 彼此不同（CATALOG 的年在前、GET_MONTH_DATE 的頻道在前）。解析規則：數字中
/// 「≥1970 的是年、1–12 可為月、1–31 可為日、第一個非年數為頻道」；解析不出完整
/// 年月時回 null（呼叫端回 FAIL 語意的空回應）。這些規則由測試釘住，真機抓包後
/// 若有出入以此為修正點。
/// </para>
/// </remarks>
public static class QctekPlaybackRequest
{
    /// <summary>解析出的回放查詢條件。</summary>
    public sealed record PlaybackQuery(int ChannelIndex, int Year, int Month, int Day);

    /// <summary>從命令的數字參數解析（頻道、年、月、日）。解析不出年月回 null。</summary>
    public static PlaybackQuery? Parse(int[] numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);

        var year = 0;
        var month = 0;
        var day = 0;
        var channel = 0;

        foreach (var n in numbers)
        {
            if (n is >= 1970 and <= 2200)
            {
                year = n;
            }
            else if (n is >= 1 and <= 12 && month == 0 && !(n <= 12 && numbers.Any(o => o is >= 13 and <= 31) && numbers.Count(o => o == n) > 1))
            {
                month = n;
            }
            else if (n is >= 1 and <= 31 && day == 0)
            {
                day = n;
            }
            else if (channel == 0 && n is >= 0 and <= 1024)
            {
                channel = n;
            }
        }

        if (year == 0 || month == 0)
        {
            return null;
        }

        return new PlaybackQuery(channel, year, month, day == 0 ? 1 : day);
    }
}
