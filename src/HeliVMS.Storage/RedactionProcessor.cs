using System.Buffers;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影遮蔽處理器（M101，§14.7 #5，純 BCL）：輸入 RGBA（BGRA 位元組序）32bpp 像素，
/// 以矩形區域實作盒狀模糊（filled=false）或實心遮罩（filled=true）。區域超出影像範圍時
/// 自動裁剪至界內（個資法遮蔽：絕不替越界區域寫出影像外記憶體）。
/// </summary>
public static class RedactionProcessor
{
    /// <summary>
    /// 對 <paramref name="rgba"/> 套用遮蔽（in-place）。像素序＝BGRA（每像素 4 位元組）。
    /// <paramref name="x"/>/<paramref name="y"/> 為左上角（含）；區域與影像交集外的座標忽略。
    /// </summary>
    public static void Apply(
        byte[] rgba,
        int width,
        int height,
        int x,
        int y,
        int rectWidth,
        int rectHeight,
        bool filled,
        int blurRadius = 3)
    {
        if (rgba is null)
        {
            throw new ArgumentNullException(nameof(rgba));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "影像尺寸必須為正。");
        }

        if (rgba.Length < checked(width * height * 4))
        {
            throw new ArgumentException("像素緩衝區長度與影像尺寸不符。", nameof(rgba));
        }

        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = Math.Min(width, x + rectWidth);
        var bottom = Math.Min(height, y + rectHeight);
        if (left >= right || top >= bottom)
        {
            return;
        }

        var stride = width * 4;
        if (filled)
        {
            for (var yy = top; yy < bottom; yy++)
            {
                Array.Fill(rgba, (byte)0, yy * stride + left * 4, (right - left) * 4);
            }

            return;
        }

        var radius = Math.Max(1, blurRadius);
        var invArea = 1.0 / ((2.0 * radius + 1.0) * (2.0 * radius + 1.0));
        var copy = ArrayPool<byte>.Shared.Rent(rgba.Length);
        try
        {
            Buffer.BlockCopy(rgba, 0, copy, 0, rgba.Length);
            for (var yy = top; yy < bottom; yy++)
            {
                for (var xx = left; xx < right; xx++)
                {
                    ApplyBlurAt(copy, rgba, width, height, xx, yy, radius, invArea);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    private static void ApplyBlurAt(
        byte[] source,
        byte[] target,
        int width,
        int height,
        int cx,
        int cy,
        int radius,
        double invArea)
    {
        var rowTop = Math.Max(0, cy - radius);
        var rowBottom = Math.Min(height, cy + radius + 1);
        var colLeft = Math.Max(0, cx - radius);
        var colRight = Math.Min(width, cx + radius + 1);
        const int channels = 4;
        var sums = new int[channels];
        var count = 0;

        for (var yy = rowTop; yy < rowBottom; yy++)
        {
            var rowBase = yy * width * channels;
            for (var xx = colLeft; xx < colRight; xx++)
            {
                var off = rowBase + xx * channels;
                sums[0] += source[off];
                sums[1] += source[off + 1];
                sums[2] += source[off + 2];
                sums[3] += source[off + 3];
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        var outOff = cy * width * channels + cx * channels;
        target[outOff] = (byte)(sums[0] * invArea);
        target[outOff + 1] = (byte)(sums[1] * invArea);
        target[outOff + 2] = (byte)(sums[2] * invArea);
        target[outOff + 3] = (byte)(sums[3] * invArea);
    }
}