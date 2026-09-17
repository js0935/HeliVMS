using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>遮蔽種類（M39 §14.4）。</summary>
public enum TamperKind
{
    /// <summary>正常。</summary>
    None,

    /// <summary>全黑（鏡頭被遮／被噴漆）。</summary>
    Blackout,

    /// <summary>全白（鏡頭被擋強光／被糊）。</summary>
    Whiteout,

    /// <summary>畫面被遮（亮度尚可但邊緣能量急降＝失焦／被遮蓋）。</summary>
    Covered,
}

/// <summary>單幀遮蔽觀察值（診斷用）。</summary>
public readonly record struct TamperObservation(TamperKind Kind, double Brightness, double EdgeEnergy);

/// <summary>
/// L0 遮蔽偵測（M39 §14.4）：以低解析度灰階網格計算平均亮度與邊緣能量。
/// 全黑／全白直接判定；「被遮」則與暖機期建立的邊緣能量基準比較（急降＝失焦或被蓋）。
/// 純 CPU、零佈署，供次流抽幀取樣使用。單幀只回瞬時判定，持續性由 <see cref="TamperEventEngine"/> 把關。
/// </summary>
public sealed class TamperDetector
{
    private const int GridWidth = 16;
    private const int GridHeight = 16;

    private readonly int _baselineFrames;
    private readonly List<double> _warmupEdges = [];
    private double _baselineEdge;
    private bool _ready;

    public TamperDetector(
        double blackLevel = 12,
        double whiteLevel = 243,
        double edgeDropRatio = 0.2,
        double minBaselineEdge = 6,
        int baselineFrames = 8)
    {
        BlackLevel = blackLevel;
        WhiteLevel = whiteLevel;
        EdgeDropRatio = edgeDropRatio;
        MinBaselineEdge = minBaselineEdge;
        _baselineFrames = Math.Max(1, baselineFrames);
    }

    /// <summary>平均亮度低於此值即視為全黑。</summary>
    public double BlackLevel { get; }

    /// <summary>平均亮度高於此值即視為全白。</summary>
    public double WhiteLevel { get; }

    /// <summary>邊緣能量低於基準乘上此比例即視為被遮。</summary>
    public double EdgeDropRatio { get; }

    /// <summary>基準邊緣能量低於此值時不判「被遮」（避免低紋理場景誤報）。</summary>
    public double MinBaselineEdge { get; }

    /// <summary>是否已完成基準暖機。</summary>
    public bool IsBaselineReady => _ready;

    /// <summary>目前邊緣能量基準（暖機後才有意義）。</summary>
    public double BaselineEdge => _baselineEdge;

    /// <summary>送入一幀並回傳瞬時遮蔽判定（暖機期僅累積基準，仍可偵測全黑／全白）。</summary>
    public TamperObservation Update(VideoFrame frame)
    {
        var grid = FrameDifferenceMotionDetector.DownsampleToGrid(frame, GridWidth, GridHeight);

        var brightness = 0.0;
        foreach (var g in grid)
        {
            brightness += g;
        }

        brightness /= grid.Length;
        var edge = EdgeEnergy(grid, GridWidth, GridHeight);

        TamperKind kind;
        if (brightness <= BlackLevel)
        {
            kind = TamperKind.Blackout;
        }
        else if (brightness >= WhiteLevel)
        {
            kind = TamperKind.Whiteout;
        }
        else if (_ready && _baselineEdge >= MinBaselineEdge && edge <= _baselineEdge * EdgeDropRatio)
        {
            kind = TamperKind.Covered;
        }
        else
        {
            kind = TamperKind.None;
        }

        // 基準只用「正常」幀累積：暖機期取前 N 幀均值，之後以慢速 EMA 跟隨場景變化。
        if (kind == TamperKind.None)
        {
            if (!_ready)
            {
                _warmupEdges.Add(edge);
                if (_warmupEdges.Count >= _baselineFrames)
                {
                    _baselineEdge = _warmupEdges.Average();
                    _ready = true;
                }
            }
            else
            {
                _baselineEdge = (_baselineEdge * 0.98) + (edge * 0.02);
            }
        }

        return new TamperObservation(kind, brightness, edge);
    }

    /// <summary>清除暖機與基準狀態。</summary>
    public void Reset()
    {
        _warmupEdges.Clear();
        _baselineEdge = 0;
        _ready = false;
    }

    /// <summary>灰階網格的平均相鄰梯度（水平＋垂直），代表畫面紋理／邊緣能量。</summary>
    public static double EdgeEnergy(byte[] grid, int gw, int gh)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < gh; y++)
        {
            for (var x = 0; x < gw; x++)
            {
                var i = (y * gw) + x;
                if (x + 1 < gw)
                {
                    sum += Math.Abs(grid[i] - grid[i + 1]);
                    count++;
                }

                if (y + 1 < gh)
                {
                    sum += Math.Abs(grid[i] - grid[i + gw]);
                    count++;
                }
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
