namespace HeliVMS.Shared;

/// <summary>
/// 視覺搜索的雜湊管線（QC_Live 的 <c>PopupVisualSearch</c> 對齊）：
/// Bgra 像素 → 灰階 →（可選 ROI 裁切）→ 感知雜湊。
/// 純函式，行為測試在 <c>VisualHashTests</c>；WPF 解碼在 App 層（<c>VisualSearchService</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>樣本與事件快照必須走同一條管線</b>（同一縮放、同一灰階、同一裁切）——
/// 比對的兩側若用不同管線算出的雜湊，漢明距離毫無意義。快照索引存的
/// <c>snapshot_hash</c>＝roi 為 null 的結果；框選搜索＝對候選快照以同一個相對
/// ROI 重算。相對座標（0–1）讓不同解析度的樣本與快照框在同一個位置。
/// </para>
/// <para>
/// 解碼建議先縮到 <see cref="ScaleWidth"/> 寬：感知雜湊只需要 9×8 區塊的趨勢，
/// 原生解析度只是浪費 CPU 與記憶體；兩側同樣縮放即可。
/// </para>
/// </remarks>
public static class VisualHash
{
    /// <summary>解碼建議寬度（像素）：640 寬對 9×8 區塊趨勢已經過剩。</summary>
    public const int ScaleWidth = 640;

    /// <summary>
    /// 事件快照索引的解碼寬：9 欄 × 8 像素。
    /// </summary>
    /// <remarks>
    /// 索引存的是 <c>alarm_events.snapshot_hash</c>。這個尺寸決定了每個 9×8 區塊的取樣面積，
    /// 也就是雜湊值本身：<b>改它就等於換掉雜湊的定義</b>，資料庫裡既有的雜湊會全部變成
    /// 「用另一條管線算出來的指紋」，與新樣本比較出來的漢明距離毫無意義——而且沒有任何
    /// 錯誤訊息，只表現成「以圖搜索找出來的都不像」。要改就得同時做一次索引重建。
    /// </remarks>
    public const int IndexWidth = PerceptualHash.GrayColumns * 8;

    /// <summary>事件快照索引的解碼高：8 列 × 8 像素（理由同 <see cref="IndexWidth"/>）。</summary>
    public const int IndexHeight = PerceptualHash.GrayRows * 8;

    /// <summary>相對裁切框（0–1；超出範圍的部分在像素裁切時夾回）。</summary>
    public sealed record Roi(double X, double Y, double W, double H);

    /// <summary>框選範圍的最小邊長（來源像素）：比它小的選取視為「沒有框選」。</summary>
    public const int MinSelectionPixels = 8;

    /// <summary>
    /// 把畫面上的框選換算成相對 ROI（<paramref name="x"/>／<paramref name="y"/> 為左上角，
    /// 寬高可為負＝反向拖曳）；範圍太小或影像尺寸無效時回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 太小的框選刻意回 <c>null</c> 而不是「夾成 1×1」：1×1 的裁切沒有任何明暗變化，
    /// 雜湊恆為 0，而任何樣本與 0 的漢明距離都等於它自己的位元數——以圖搜索會回「全部事件」，
    /// 操作員只看到自己框了一小角，卻得到整頁結果。
    /// </remarks>
    /// <param name="x">選取起點（來源像素座標）。</param>
    /// <param name="y">選取起點（來源像素座標）。</param>
    /// <param name="width">選取寬（可為負）。</param>
    /// <param name="height">選取高（可為負）。</param>
    /// <param name="imageWidth">影像寬（像素）。</param>
    /// <param name="imageHeight">影像高（像素）。</param>
    public static Roi? FromPixels(
        double x, double y, double width, double height, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return null;
        }

        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(width) || double.IsNaN(height))
        {
            return null;
        }

        var left = Math.Clamp(Math.Min(x, x + width), 0, imageWidth);
        var top = Math.Clamp(Math.Min(y, y + height), 0, imageHeight);
        var right = Math.Clamp(Math.Max(x, x + width), 0, imageWidth);
        var bottom = Math.Clamp(Math.Max(y, y + height), 0, imageHeight);

        var w = right - left;
        var h = bottom - top;
        if (w < MinSelectionPixels || h < MinSelectionPixels)
        {
            return null;
        }

        var rx = left / imageWidth;
        var ry = top / imageHeight;
        return new Roi(
            rx,
            ry,
            Math.Min(w / imageWidth, 1 - rx),
            Math.Min(h / imageHeight, 1 - ry));
    }

    /// <summary>
    /// 把相對 ROI 換算成像素矩形（夾在影像邊界內、至少 1×1）。
    /// roi 為 null＝整張圖。
    /// </summary>
    public static (int X, int Y, int W, int H) PixelRect(Roi? roi, int width, int height)
    {
        if (roi is null || width <= 0 || height <= 0)
        {
            return (0, 0, Math.Max(0, width), Math.Max(0, height));
        }

        var x = Math.Clamp((int)Math.Round(roi.X * width), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(roi.Y * height), 0, height - 1);
        var w = Math.Clamp((int)Math.Round(roi.W * width), 1, width - x);
        var h = Math.Clamp((int)Math.Round(roi.H * height), 1, height - y);
        return (x, y, w, h);
    }

    /// <summary>Bgra32 → 灰階（Rec.709 加權；每像素 1 byte）。</summary>
    public static byte[] BgraToGray(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var b = bgra[i * 4];
            var g = bgra[i * 4 + 1];
            var r = bgra[i * 4 + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }

        return gray;
    }

    /// <summary>裁切灰階影像（像素矩形由 <see cref="PixelRect"/> 提供）。</summary>
    public static (byte[] Gray, int Width, int Height) CropGray(
        byte[] gray, int width, int height, int x, int y, int w, int h)
    {
        var cropped = new byte[w * h];
        for (var row = 0; row < h; row++)
        {
            Array.Copy(gray, (y + row) * width + x, cropped, row * w, w);
        }

        return (cropped, w, h);
    }

    /// <summary>
    /// 完整管線：Bgra 像素 →（ROI 裁切）→ 灰階 → 感知雜湊。
    /// 樣本與事件快照都呼叫這一個方法——兩側同一管線，漢明距離才有意義。
    /// </summary>
    public static ulong FromBgra(byte[] bgra, int width, int height, Roi? roi)
    {
        var gray = BgraToGray(bgra, width, height);
        var (x, y, w, h) = PixelRect(roi, width, height);
        var (cropped, cw, ch) = CropGray(gray, width, height, x, y, w, h);
        return PerceptualHash.FromGrayImage(cropped, cw, ch);
    }
}
