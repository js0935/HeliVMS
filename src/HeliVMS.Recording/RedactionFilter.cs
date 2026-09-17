namespace HeliVMS.Recording;

/// <summary>遮蔽區域（絕對像素座標；多 ROI 假設同解析度）。</summary>
public sealed record RedactionRoi(int X, int Y, int Width, int Height)
{
    internal void Validate()
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RedactionRoi), "ROI 座標需非負且寬高大於 0。");
        }
    }
}

/// <summary>
/// 產生 ffmpeg 錄影遮蔽 filter（§14.7 #5）：split 一段主畫面＋各 ROI 分支，
/// ROI 分支 crop→boxblur→scale 回原位後依序 overlay。純字串建構，測試不需 ffmpeg。
/// </summary>
public static class RedactionFilter
{
    public static string Build(IReadOnlyList<RedactionRoi> rois)
    {
        if (rois is null || rois.Count == 0)
        {
            throw new InvalidOperationException("至少需一個遮蔽區域。");
        }

        var n = rois.Count;
        var parts = new List<string>(n * 2 + 1);

        var splitLabels = new string[n + 1];
        for (var k = 0; k <= n; k++)
        {
            splitLabels[k] = $"[s{k}]";
        }

        parts.Add($"[0:v]split={n + 1}{string.Concat(splitLabels)}");

        for (var k = 1; k <= n; k++)
        {
            var r = rois[k - 1];
            r.Validate();
            var radius = Math.Clamp(Math.Min(r.Width, r.Height) / 4, 1, 12);
            parts.Add(
                $"[s{k}]crop={r.Width}:{r.Height}:{r.X}:{r.Y},boxblur={radius}:2:{radius}:2,scale={r.Width}:{r.Height}[r{k}]");
        }

        var prev = "[s0]";
        for (var k = 1; k <= n; k++)
        {
            var r = rois[k - 1];
            var label = k == n ? "[vout]" : $"[m{k}]";
            parts.Add($"{prev}[r{k}]overlay={r.X}:{r.Y}{label}");
            prev = label;
        }

        return string.Join(";", parts);
    }
}