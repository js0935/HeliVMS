namespace HeliVMS.Storage;

/// <summary>遮蔽區域投影後的顯示方框（M279，§14.7 #5）：左上角為顯示框原點，單位為顯示像素；Filled＝實心遮罩，否則盒狀模糊。</summary>
public readonly record struct RedactionBox(double X, double Y, double Width, double Height, bool Filled);

/// <summary>
/// 回放即時遮蔽疊圖的投影（M279，§14.7 #5，純函式）：把以來源影像像素為單位的遮蔽區域，
/// 依比例換算到目前顯示框的大小並裁剪至框內。
///
/// <para>
/// 座標系與 <see cref="RedactionProcessor"/> 相同（左上角為原點、單位為來源影像像素），
/// 因此同一批區域在匯出（ffmpeg drawbox／boxblur）與回放疊圖會落在同一個位置；
/// 兩邊若各算一套縮放，操作員就會看到「疊圖框在這裡、匯出卻遮在那裡」。
/// </para>
/// </summary>
public static class RedactionOverlay
{
    /// <summary>
    /// 將 <paramref name="regions"/>（來源像素座標，<paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>）
    /// 投影到 <paramref name="targetWidth"/>×<paramref name="targetHeight"/> 的顯示框。越界者裁剪、空區域略過。
    /// 來源或目標尺寸非正時回空集合（尚無畫面可疊圖，不是錯誤）。
    /// </summary>
    public static IReadOnlyList<RedactionBox> Project(
        IReadOnlyList<RedactionRegion> regions,
        int sourceWidth,
        int sourceHeight,
        double targetWidth,
        double targetHeight)
    {
        if (regions is null)
        {
            throw new ArgumentNullException(nameof(regions));
        }

        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return [];
        }

        var scaleX = targetWidth / sourceWidth;
        var scaleY = targetHeight / sourceHeight;
        var boxes = new List<RedactionBox>(regions.Count);
        foreach (var region in regions)
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                continue;
            }

            var left = Math.Max(0.0, region.X * scaleX);
            var top = Math.Max(0.0, region.Y * scaleY);
            var right = Math.Min(targetWidth, (region.X + region.Width) * scaleX);
            var bottom = Math.Min(targetHeight, (region.Y + region.Height) * scaleY);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            boxes.Add(new RedactionBox(left, top, right - left, bottom - top, region.Filled));
        }

        return boxes;
    }
}
