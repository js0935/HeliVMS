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

    /// <summary>
    /// 解碼端縮小尺寸用的 ffmpeg 濾鏡字串（即時監看與多路回放共用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一定要帶 <c>flags=area</c>：swscale 預設的 bicubic 在 3:1 以上的縮小（1080p → 640/480）
    /// 是「取樣」而不是「平均」，實測同一組 6 路 1080p15（每路 300 幀）總 CPU：
    /// bicubic 22.84 s、bilinear 19.92 s、<b>area 17.56 s（−23%）</b>、fast_bilinear 17.39 s。
    /// 而品質（縮到 640×360 再放大回 1080p 對原圖的 PSNR）：bicubic 32.44 dB、
    /// <b>area 32.37 dB</b>、bilinear 32.14 dB、fast_bilinear 26.43 dB。也就是 <c>area</c>
    /// 用 fast_bilinear 的速度換到 bicubic 的品質，而 fast_bilinear 少掉的 6 dB 就是
    /// 監看畫面裡欄杆、車牌邊緣的鋸齒。搭硬體解碼時同樣有效（bicubic 9.77 s → area 8.06 s）。
    /// </para>
    /// <para>
    /// 只有縮小會走這條路徑（<see cref="Fit"/> 在上限不小於來源時回 <c>(0, 0)</c>），
    /// 所以不必擔心 <c>area</c> 在放大時的行為。
    /// </para>
    /// </remarks>
    /// <param name="width">目標寬度（像素，必須是 <see cref="Fit"/> 算出來的偶數高度搭配）。</param>
    /// <param name="height">目標高度（像素）。</param>
    public static string DownscaleFilter(int width, int height)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "scale={0}:{1}:flags=area",
            width,
            height);

    /// <summary>
    /// 縮小解碼時跳過解環濾波器（deblocking）用的解碼器引數；<b>必須放在 <c>-i</c> 之前</b>。
    /// </summary>
    /// <remarks>
    /// 即時監看與多路回放共用同一份（<see cref="ShouldSkipLoopFilter"/> 決定要不要加），
    /// 理由與 <see cref="DownscaleFilter"/> 相同：解碼端的行為只能有一份定義。
    /// </remarks>
    public static IReadOnlyList<string> SkipLoopFilterArguments { get; } = ["-skip_loop_filter", "all"];

    /// <summary>
    /// 這一輪解碼是否要連解環濾波器一起跳過：<b>只有真的把畫面縮小</b>（目標寬度小於來源）才跳。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 量測（本機 i5-1035G1、6 路 1080p15、每路 300 幀、<c>scale=640:360:flags=area</c>、
    /// 以 ffmpeg <c>bench: utime+stime</c> 取總 CPU、A/B 交錯配對 10 次取中位數）：
    /// <c>-skip_loop_filter all</c> 讓總 CPU 少 <b>15.2%</b>（10/10 次都較快，最差 −7.4%、
    /// 最好 −19.8%）。背景負載會讓單次量測在 30.7–39.7 s 之間跳動，所以只有交錯配對的中位數可信。
    /// </para>
    /// <para>
    /// 為什麼以「有沒有縮小」為界，而不是「解碼成本高不高」：解環濾波器修的是 8×8 方塊的邊界，
    /// 而 <c>flags=area</c> 的縮小本身就是對每個輸出像素平均掉一整塊輸入像素，方塊邊界跟著被平均。
    /// 實測跳過前後（對同一張基準畫面的 PSNR／SSIM）：縮到 640×360 的 1080p 來源
    /// （低碼率 300k/800k/2500k）43.2–44.0 dB／≥0.9938，640×360 次碼流縮到牆上要的
    /// 480/384/320/256 是 42.9–45.7 dB／SSIM Y ≥0.9906。這一格在畫面上只有一兩百像素寬，
    /// 看的是「有沒有人、車在動」，不是車牌。
    /// </para>
    /// <para>
    /// <b>原生尺寸一律不跳</b>：1–4 格的即時監看與單格回放就是操作員放大看細節的情境，
    /// 而且那時只有少數幾條解碼行程，省下來的 CPU 換不到任何東西。
    /// 第二個理由同樣重要：這一格可以點開放大成單格，放大走的是原生尺寸那條路，
    /// 所以小格數看的永遠是完整解環的畫面。
    /// </para>
    /// <para>
    /// 來源寬度未知時回 <c>false</c>：跳錯的方向是「畫面變差卻沒有任何錯誤訊息」，
    /// 而這個選項換到的是 CPU，不值得拿不確定的畫質去賭。
    /// </para>
    /// </remarks>
    /// <param name="sourceWidth">來源寬度（像素；未知時傳 0）。</param>
    /// <param name="decodeWidth">這一輪實際要解碼的寬度（0＝原生）。</param>
    public static bool ShouldSkipLoopFilter(int sourceWidth, int decodeWidth)
        => decodeWidth > 0 && sourceWidth > 0 && decodeWidth < sourceWidth;
}
