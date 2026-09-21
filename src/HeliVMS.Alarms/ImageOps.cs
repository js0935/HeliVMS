namespace HeliVMS.Alarms;

/// <summary>純 C# 位圖運算（M63 影片摘要縮放）。零外部依賴。</summary>
public static class ImageOps
{
    /// <summary>
    /// 最近鄰縮放：對每個目標像素取 `srcX = x * srcW / dstW` 之來源像素，逐 3-byte（BGR）複製。
    /// </summary>
    public static byte[] ResizeNearest(byte[] srcBgr, int srcW, int srcH, int dstW, int dstH)
    {
        if (dstW <= 0 || dstH <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstW), "目標寬高必須為正數。");
        }

        var srcStride = srcW * 3;
        var dst = new byte[dstW * dstH * 3];
        var dstStride = dstW * 3;

        for (var y = 0; y < dstH; y++)
        {
            var sy = (y * srcH) / dstH;
            var srcRow = sy * srcStride;
            var dstRow = y * dstStride;
            for (var x = 0; x < dstW; x++)
            {
                var sx = (x * srcW) / dstW;
                var s = srcRow + sx * 3;
                var d = dstRow + x * 3;
                dst[d] = srcBgr[s];
                dst[d + 1] = srcBgr[s + 1];
                dst[d + 2] = srcBgr[s + 2];
            }
        }

        return dst;
    }
}