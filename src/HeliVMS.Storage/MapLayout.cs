namespace HeliVMS.Storage;

/// <summary>
/// 電子地圖圖釘自動布局（M284，§14.7 #11）：把重疊的圖釘推開，讓每一支攝影機/IO 都看得見。
///
/// 為什麼需要：平面圖上兩支相機只要落點接近（同一個機房、同一個走道口），圖釘與扇形就
/// 完全疊在一起，操作員只看得到最上面那一個，還以為其他鏡頭沒設定。手動拖開一次之後只要
/// 再批次上版或重畫平面圖，重疊又會回來。
///
/// 演算法是純函式、具決定性，且**盡量保留操作員原本的相對位置**：只把落在間距內的圖釘沿
/// 黃金角螺旋往外找第一個不衝突的點，找不到就保留原點（best-effort，絕不因此丟掉圖釘）。
/// 座標一律正規化 0..1（與 <c>map_devices.x/y</c> 相同）。
/// </summary>
public static class MapLayout
{
    /// <summary>兩圖釘被視為重疊的最小正規化間距（約圖面短邊的 6%）。</summary>
    public const double DefaultMinSpacing = 0.06;

    /// <summary>圖釘與圖面邊緣保留的最小正規化邊距。</summary>
    public const double DefaultMargin = 0.03;

    /// <summary>往外搜尋的最多圈數；每圈取樣 <see cref="StepsPerRing"/> 個方向。</summary>
    private const int MaxRings = 60;
    private const int StepsPerRing = 8;

    /// <summary>黃金角（弧度），讓螺旋取樣方向不共線，避免推開後排成一整齊卻歪斜的直線。</summary>
    private static readonly double GoldenAngle = Math.PI * (3 - Math.Sqrt(5));

    /// <summary>輸入圖釘：識別碼與目前的正規化座標。</summary>
    public readonly record struct Pin(int Id, double X, double Y);

    /// <summary>輸出落點：識別碼與調整後的正規化座標。</summary>
    public readonly record struct Placement(int Id, double X, double Y);

    /// <summary>
    /// 重新安排所有圖釘：依 <see cref="Pin.Id"/> 排序逐一定位，落在既有圖釘
    /// <paramref name="minSpacing"/> 內者沿黃金角螺旋外推，直到找到不衝突的位置。
    /// 輸入順序不影響結果（具決定性），輸出保證每個輸入 Id 各出現一次、座標落在
    /// [margin, 1−margin]。間距為 0 或負值時視為預設值。
    /// </summary>
    public static IReadOnlyList<Placement> Resolve(
        IReadOnlyList<Pin> pins,
        double minSpacing = DefaultMinSpacing)
    {
        ArgumentNullException.ThrowIfNull(pins);

        var spacing = minSpacing > 0 ? minSpacing : DefaultMinSpacing;
        var margin = Math.Min(DefaultMargin, 0.49);
        var low = margin;
        var high = 1 - margin;

        var placed = new List<Placement>(pins.Count);
        foreach (var pin in pins.OrderBy(static p => p.Id))
        {
            var x = Math.Clamp(pin.X, low, high);
            var y = Math.Clamp(pin.Y, low, high);
            if (!Collides(x, y, placed, spacing))
            {
                placed.Add(new Placement(pin.Id, x, y));
                continue;
            }

            var resolved = false;
            for (var ring = 1; ring <= MaxRings && !resolved; ring++)
            {
                var radius = ring * spacing;
                for (var step = 0; step < StepsPerRing; step++)
                {
                    var angle = step * GoldenAngle;
                    var cx = Math.Clamp(pin.X + (radius * Math.Cos(angle)), low, high);
                    var cy = Math.Clamp(pin.Y + (radius * Math.Sin(angle)), low, high);
                    if (!Collides(cx, cy, placed, spacing))
                    {
                        placed.Add(new Placement(pin.Id, cx, cy));
                        resolved = true;
                        break;
                    }
                }
            }

            if (!resolved)
            {
                // 找不到完全不重疊的位置時保留原點；寧可仍有一點重疊，也不要把圖釘藏起來。
                placed.Add(new Placement(pin.Id, x, y));
            }
        }

        return placed;
    }

    private static bool Collides(double x, double y, List<Placement> placed, double spacing)
    {
        foreach (var other in placed)
        {
            var dx = other.X - x;
            var dy = other.Y - y;
            if ((dx * dx) + (dy * dy) < spacing * spacing)
            {
                return true;
            }
        }

        return false;
    }
}
