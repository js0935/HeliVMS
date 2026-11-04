namespace HeliVMS.Shared;

/// <summary>
/// 事件清單的上下一個導覽（回放視窗的 N／P 與按鈕）。
/// </summary>
/// <remarks>
/// 抽成純函式是為了把「清單還沒選取任何一列」與「已經在最前／最後」這兩種邊界講清楚：
/// 前者要從頭（下一個）或從尾（上一個）開始，後者要回報「沒有更多」而不是原地不動讓
/// 操作員以為按鍵壞了。
/// </remarks>
public static class PlaybackEventNavigation
{
    /// <summary>
    /// 目標索引；沒有可移動的目標時回 <paramref name="currentIndex"/>（呼叫端據此提示「沒有更多」）。
    /// </summary>
    /// <param name="currentIndex">目前選取索引（尚未選取時為 -1）。</param>
    /// <param name="count">清單長度。</param>
    /// <param name="direction">+1＝下一個、-1＝上一個。</param>
    public static int NextIndex(int currentIndex, int count, int direction)
    {
        if (count <= 0)
        {
            return currentIndex;
        }

        if (currentIndex < 0 || currentIndex >= count)
        {
            // 還沒選取：下一個從第一筆、上一個從最後一筆（與清單「從頭看」的直覺一致）。
            return direction >= 0 ? 0 : count - 1;
        }

        return Math.Clamp(currentIndex + (direction >= 0 ? 1 : -1), 0, count - 1);
    }
}
