namespace HeliVMS.Shared;

/// <summary>
/// 影格縮放的尺寸口徑：解碼端送給 ffmpeg 的數字與呼叫端算出的影格大小必須完全一致。
/// </summary>
/// <remarks>
/// <para>
/// 抽成一份的理由：解碼端吐出來的尺寸與呼叫端算的只要差一個像素，每一張影格就錯位
/// （畫面全黑或斜切），而日誌完全乾淨——這是「連上了、沒畫面」最難查的一種。
/// 即時監看與多路回放都靠這一份口徑，兩邊各算一次遲早漂移。
/// </para>
/// <para>
/// 高度一定要自己算，不可交給 ffmpeg 的 <c>scale=W:-2</c>：<c>-2</c> 的取整規則
/// （四捨五入到偶數）與呼叫端的算法只要有一格不同，就整段錯位。
/// </para>
/// </remarks>
public static class VideoScaling
{
    /// <summary>
    /// 縮到指定寬度後的偶數高度（yuv420p 需要）；來源尺寸未知或不需要縮放時回 0。
    /// </summary>
    /// <param name="displayWidth">目標寬度（像素）。</param>
    /// <param name="sourceWidth">來源寬度（像素）。</param>
    /// <param name="sourceHeight">來源高度（像素）。</param>
    public static int EvenHeight(int displayWidth, int sourceWidth, int sourceHeight)
    {
        if (displayWidth <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return 0;
        }

        var height = (int)Math.Round(sourceHeight * (double)displayWidth / sourceWidth);
        if (height < 2)
        {
            height = 2;
        }

        return height % 2 == 0 ? height : height + 1;
    }

    /// <summary>
    /// 依寬度上限求出實際要解碼的尺寸；<b>不需要縮放時回 <c>(0, 0)</c></b>。
    /// </summary>
    /// <remarks>
    /// 回 <c>(0, 0)</c> 而不是「原尺寸」是刻意的：呼叫端要能分辨「上限比來源寬，所以不要縮」，
    /// 才不會為了縮小而多開一次 ffmpeg、也不會把一個沒有意義的 <c>scale=1920:1080</c>
    /// 塞進參數（那會讓每一格多一次無謂的縮放濾鏡）。
    /// </remarks>
    /// <param name="sourceWidth">來源寬度（像素）。</param>
    /// <param name="sourceHeight">來源高度（像素）。</param>
    /// <param name="maxWidth">允許的最大寬度（0 或負值＝不縮）。</param>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (maxWidth <= 0 || sourceWidth <= 0 || sourceHeight <= 0 || maxWidth >= sourceWidth)
        {
            return (0, 0);
        }

        return (maxWidth, EvenHeight(maxWidth, sourceWidth, sourceHeight));
    }
}
