namespace HeliVMS.Shared;

/// <summary>
/// 回放牆可同時播放的路數：等於要求的分割數，但不會超過實際頻道數。
/// </summary>
/// <remarks>
/// <para>
/// 每格是一條播放工作（各自一個解碼程序），沒有頻道的格子沒有東西可解，
/// 所以「能同時播幾路」的上限是 <c>min(分割數, 頻道數)</c>，不是分割數。
/// </para>
/// <para>
/// 為什麼不畫出空格：9／16 分割是為頻道擴充後準備的，現況 4 台時畫出 9 個空格
/// 只會讓人以為其餘 5 路壞了。UI 改為顯示「牆 4/9 路」，上限與原因都寫在同一行。
/// </para>
/// </remarks>
public static class PlaybackWallCapacity
{
    /// <summary>實際會開起來的格數（沒有頻道時為 0，此時牆不開任何格）。</summary>
    public static int Effective(int requestedSplit, int channelCount)
    {
        if (requestedSplit < 1 || channelCount < 1)
        {
            return 0;
        }

        return Math.Min(requestedSplit, channelCount);
    }
}
