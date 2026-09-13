using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// L0 運動偵測（§5.1）：以低解析度灰階網格做幀差比對，回傳是否有顯著像素變動。
/// 純 CPU、零佈署；解析度與網格越小成本越低，供次流抽幀取樣使用。
/// </summary>
public sealed class FrameDifferenceMotionDetector
{
    private const int GridWidth = 16;
    private const int GridHeight = 16;
    private const byte BrightnessThreshold = 14;

    private readonly double _sensitivity;
    private byte[]? _previous;

    public FrameDifferenceMotionDetector(double sensitivity = 0.5)
    {
        _sensitivity = Math.Clamp(sensitivity, 0.05, 0.95);
    }

    /// <summary>
    /// 送入一幀（首次預熱不判定），回傳「變動格數／總格數」比例；比例 ≥ 靈敏度即偵測到運動。
    /// </summary>
    public double Update(VideoFrame frame)
    {
        var grid = DownsampleToGrid(frame, GridWidth, GridHeight);

        if (_previous is null)
        {
            _previous = grid;
            return 0;
        }

        var changed = 0;
        for (var i = 0; i < grid.Length; i++)
        {
            var diff = Math.Abs(grid[i] - _previous[i]);
            if (diff >= BrightnessThreshold)
            {
                changed++;
            }
        }

        Array.Copy(grid, _previous, grid.Length);
        return (double)changed / grid.Length;
    }

    /// <summary>是否符合運動觸發門檻。</summary>
    public bool IsMotion(double ratio) => ratio >= _sensitivity;

    /// <summary>清除前幀狀態（首次幀僅預熱不判定）。</summary>
    public void Reset() => _previous = null;

    /// <summary>以平均值降採樣為灰階網格。</summary>
    internal static byte[] DownsampleToGrid(VideoFrame frame, int gw, int gh)
    {
        var grid = new byte[gw * gh];
        var stride = frame.Width * 3;
        for (var gy = 0; gy < gh; gy++)
        {
            for (var gx = 0; gx < gw; gx++)
            {
                var px = (frame.Width - 1) * gx / gw;
                var py = (frame.Height - 1) * gy / gh;
                var sum = 0;
                var count = 0;
                for (var oy = -1; oy <= 1; oy++)
                {
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var x = px + ox;
                        var y = py + oy;
                        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
                        {
                            continue;
                        }

                        var i = y * stride + x * 3;
                        sum += (frame.Pixels[i + 2] + frame.Pixels[i + 1] + frame.Pixels[i]) / 3;
                        count++;
                    }
                }

                grid[gy * gw + gx] = (byte)(count > 0 ? sum / count : 0);
            }
        }

        return grid;
    }
}