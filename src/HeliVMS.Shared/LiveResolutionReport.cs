namespace HeliVMS.Shared;

/// <summary>
/// 格內狀態文字要顯示的解析度：<b>來源</b>尺寸為主，真的被縮小解碼時才附上解碼尺寸。
/// </summary>
/// <remarks>
/// <para>
/// 現場回報的症狀是「子碼流顯示 480x270，它應該是 640x480」。實測（ffprobe 量 10 支攝影機的
/// 次碼流）真正的來源是 640x360 與 640x480 兩族，而 <c>LiveWallBudget.For(10)</c> 把每一格
/// 縮到寬 480 解碼——於是同一面牆上同時出現 480x270 與 480x360。也就是說：那個數字本身
/// 沒有錯，<b>錯的是它被當成「這支攝影機的解析度」講出來</b>。操作員照著它去查攝影機的
/// 子碼流設定，只會查到一個完全正常的 640x360 或 640x480，然後判定系統壞了。
/// </para>
/// <para>
/// 所以這裡的規則只有一條：<b>報來源尺寸</b>（那是攝影機的事實，也是操作員在攝影機網頁上
/// 看得到的數字），差額另行標註（那是我們的決定，可以被質疑與調整）。來源未知時只報
/// 目前看到的尺寸——那正是「此刻畫面上這張影格多大」，不編造來源。
/// </para>
/// <para>
/// 抽成純函式是為了可測試：寫錯的方向（永遠報解碼尺寸）不會有任何錯誤訊息，只會讓每一面牆
/// 都繼續對操作員說一個他無法驗證的數字。
/// </para>
/// </remarks>
public static class LiveResolutionReport
{
    /// <summary>兩個尺寸是否相同（任何一個未知時視為不同）。</summary>
    /// <param name="sourceWidth">來源寬度（0＝還沒探測到）。</param>
    /// <param name="sourceHeight">來源高度。</param>
    /// <param name="decodedWidth">解碼後寬度。</param>
    /// <param name="decodedHeight">解碼後高度。</param>
    public static bool IsScaled(int sourceWidth, int sourceHeight, int decodedWidth, int decodedHeight)
        => sourceWidth > 0
            && sourceHeight > 0
            && decodedWidth > 0
            && decodedHeight > 0
            && (sourceWidth != decodedWidth || sourceHeight != decodedHeight);

    /// <summary>
    /// 組出要顯示的解析度字串（例如 <c>640×360（解碼 480×270）</c>）。
    /// </summary>
    /// <param name="sourceWidth">來源寬度（0＝還沒探測到）。</param>
    /// <param name="sourceHeight">來源高度。</param>
    /// <param name="decodedWidth">解碼後寬度（0＝還沒有影格）。</param>
    /// <param name="decodedHeight">解碼後高度。</param>
    /// <param name="scaledFormat">
    /// 「來源」與「解碼」的排版（i18n 文案，例如 <c>{0}（解碼 {1}）</c>）。
    /// 文案由呼叫端提供，這裡只決定「要不要用它、用哪兩個數字」。
    /// </param>
    /// <returns>沒有影格時回傳空字串（呼叫端據此整段省略）。</returns>
    public static string Format(
        int sourceWidth,
        int sourceHeight,
        int decodedWidth,
        int decodedHeight,
        string scaledFormat)
    {
        if (decodedWidth <= 0 || decodedHeight <= 0)
        {
            return string.Empty;
        }

        var decoded = $"{decodedWidth}×{decodedHeight}";
        if (!IsScaled(sourceWidth, sourceHeight, decodedWidth, decodedHeight))
        {
            return decoded;
        }

        return string.Format(
            string.IsNullOrWhiteSpace(scaledFormat) ? "{0} ({1})" : scaledFormat,
            $"{sourceWidth}×{sourceHeight}",
            decoded);
    }
}
