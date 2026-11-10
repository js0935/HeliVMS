namespace HeliVMS.Shared;

/// <summary>
/// 第二螢幕鏡像牆每格左下角文字的組字（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// 文字內容由主牆<b>推</b>過來（同一個 <see cref="WallBadgeTextPlanner"/> 組出來的徽章字串，
/// 含錄影方式與「為什麼沒錄」），鏡像端只負責接上時刻。刻意不在鏡像端自己算錄影方式與
/// 阻塞原因：那是第二份判斷，遲早與主牆不一致——而兩面牆對同一台給出不同答案時，
/// 操作員唯一能做的就是兩邊都不信。
/// </para>
/// <para>
/// 時刻一定要留著：鏡像牆在第二螢幕上，主牆的讀數與時間軸都不在那裡，一個只有名稱的格子
/// 沒有辦法回答「這是不是現在的畫面」——而回報時最常被問的就是這一句。徽章字串還沒送來
/// （剛開窗、或該格沒有頻道）時退回名稱；兩個都沒有就整條留白。
/// </para>
/// </remarks>
public static class WallMirrorLabel
{
    /// <summary>組出「徽章文字 · HH:mm:ss」；兩者皆空時回空字串。</summary>
    /// <param name="badgeText">主牆推來的徽章字串（已含頻道名稱）；尚未送達時傳空字串。</param>
    /// <param name="channelName">該格目前的頻道名稱（退回用）。</param>
    /// <param name="localTime">該格的本地時刻（影格時間或現在）。</param>
    public static string Compose(string? badgeText, string? channelName, DateTime localTime)
    {
        var head = string.IsNullOrWhiteSpace(badgeText) ? channelName : badgeText;
        if (string.IsNullOrWhiteSpace(head))
        {
            return string.Empty;
        }

        return $"{head} · {localTime:HH:mm:ss}";
    }
}
