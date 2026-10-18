namespace HeliVMS.Shared;

/// <summary>
/// 回放選擇區間（§8.5「拖拽選取範圍／起止手柄」）的兩件純計算：手柄命中與單側拖曳結果。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要抽出來：這段邏輯原本埋在 <c>PlaybackWindow</c> 的滑鼠事件裡，只能靠掃原始碼
/// 的契約測試看有沒有寫，而「拖左緣卻兩邊一起動」「跨過對側後來回彈」都是掃不出來的
/// 行為錯誤。抽成純函式之後可以直接餵座標與時間斷言結果。
/// </para>
/// <para>
/// 跨過對側為什麼要交換手柄身分：拖著左緣往右越過右緣時，兩端會互換，若不把「正在拖的
/// 是哪一側」一起換，下一次 <see cref="Drag"/> 會用同一個身分把剛交換的兩端再拉回去，
/// 指標兩側的畫面會來回彈。
/// </para>
/// </remarks>
public static class PlaybackRangeHandle
{
    /// <summary>左緣手柄。</summary>
    public const int Left = -1;

    /// <summary>右緣手柄。</summary>
    public const int Right = 1;

    /// <summary>指標落在哪一側的手柄上；不在任何一側的容差內時回 0（＝要重新框選，不是調整）。</summary>
    /// <param name="pointerX">指標位置。</param>
    /// <param name="leftPx">左緣位置。</param>
    /// <param name="rightPx">右緣位置。</param>
    /// <param name="tolerancePx">抓取容差；描邊只有 1px，照描邊抓會抓不到，所以要放寬。</param>
    public static int HitEdge(double pointerX, double leftPx, double rightPx, double tolerancePx)
    {
        var toLeft = Math.Abs(pointerX - leftPx);
        var toRight = Math.Abs(pointerX - rightPx);
        if (Math.Min(toLeft, toRight) > tolerancePx)
        {
            return 0;
        }

        // 兩側都在容差內（極窄的區間）時取較近的一側；平手取左，讓行為可預期。
        return toLeft <= toRight ? Left : Right;
    }

    /// <summary>只動被抓的那一側；越過對側時交換兩端並回報交換後的手柄身分。</summary>
    /// <param name="start">目前區間起點。</param>
    /// <param name="end">目前區間終點。</param>
    /// <param name="dragged">被拖曳那一側的新時刻。</param>
    /// <param name="edge"><see cref="Left"/> 或 <see cref="Right"/>。</param>
    public static (DateTime Start, DateTime End, int Edge) Drag(
        DateTime start, DateTime end, DateTime dragged, int edge)
    {
        var newStart = edge == Left ? dragged : start;
        var newEnd = edge == Left ? end : dragged;
        if (newStart > newEnd)
        {
            (newStart, newEnd) = (newEnd, newStart);
            edge = -edge;
        }

        return (newStart, newEnd, edge);
    }
}
