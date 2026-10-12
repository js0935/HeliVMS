using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>遮蔽區域（絕對像素座標；多 ROI 假設同解析度）。<c>Filled</c>＝實心黑遮罩，否則盒狀模糊。</summary>
public sealed record RedactionRoi(int X, int Y, int Width, int Height, bool Filled = false)
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
/// 模糊分支 crop→boxblur→scale、實心分支 crop→drawbox(fill)→scale 回原位後依序 overlay。
/// 純字串建構，測試不需 ffmpeg。
/// </summary>
public static class RedactionFilter
{
    /// <summary>預設輸出標籤。</summary>
    public const string DefaultOutputLabel = "[vout]";

    /// <summary>
    /// 由已登錄的 <see cref="RedactionRegion"/>（M101 倉儲）轉為 filter 用的 ROI，保留實心/模糊語意。
    /// 供回放/匯出自動套用已儲存遮蔽區域。
    /// </summary>
    public static IReadOnlyList<RedactionRoi> FromRegions(IReadOnlyList<RedactionRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var rois = new List<RedactionRoi>(regions.Count);
        foreach (var r in regions)
        {
            rois.Add(new RedactionRoi(r.X, r.Y, r.Width, r.Height, r.Filled));
        }

        return rois;
    }

    /// <param name="rois">遮蔽區域（至少一個）。</param>
    /// <param name="outputLabel">
    /// 末端輸出標籤，預設 <c>[vout]</c>。呼叫端要再接浮水印等後續 filter 時可指定自有標籤，
    /// 避免與 <c>[vout]</c> 衝突（例如 <c>Build(rois, "[redact]") + ";[redact]drawtext=…[vout]"</c>）。
    /// </param>
    public static string Build(IReadOnlyList<RedactionRoi> rois, string outputLabel = DefaultOutputLabel)
    {
        if (rois is null || rois.Count == 0)
        {
            throw new InvalidOperationException("至少需一個遮蔽區域。");
        }

        if (string.IsNullOrWhiteSpace(outputLabel))
        {
            throw new ArgumentException("輸出標籤不可為空白。", nameof(outputLabel));
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
            if (r.Filled)
            {
                parts.Add(
                    $"[s{k}]crop={r.Width}:{r.Height}:{r.X}:{r.Y},drawbox=x=0:y=0:w={r.Width}:h={r.Height}:color=black@1.0:t=fill,scale={r.Width}:{r.Height}[r{k}]");
            }
            else
            {
                var radius = Math.Clamp(Math.Min(r.Width, r.Height) / 4, 1, 12);
                parts.Add(
                    $"[s{k}]crop={r.Width}:{r.Height}:{r.X}:{r.Y},boxblur={radius}:2:{radius}:2,scale={r.Width}:{r.Height}[r{k}]");
            }
        }

        var prev = "[s0]";
        for (var k = 1; k <= n; k++)
        {
            var r = rois[k - 1];
            var label = k == n ? outputLabel : $"[m{k}]";
            parts.Add($"{prev}[r{k}]overlay={r.X}:{r.Y}{label}");
            prev = label;
        }

        return string.Join(";", parts);
    }
}
