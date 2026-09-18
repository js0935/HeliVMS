using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>
/// 電子地圖幾何輔助（M49，§14.7 #11）：角度正規化、FOV/深度範圍、方位名稱，
/// 以及「覆蓋深度（公尺）→ 像素半徑」換算。純函式，測試不需 UI。
/// 角度以畫面座標為準：0°＝正右（東），順時針增加。
/// </summary>
public static class MapGeometry
{
    public const double MinFovDeg = 1;
    public const double MaxFovDeg = 360;
    public const double MaxDepthMeters = 1000;
    public const double DefaultSectorRadiusPixels = 70;
    public const double MinSectorRadiusPixels = 8;
    public const double MaxSectorRadiusPixels = 100000;

    private static readonly string[] Bearings =
    {
        "東", "東南", "南", "西南", "西", "西北", "北", "東北",
    };

    /// <summary>將角度正規化至 [0, 360)。</summary>
    public static double NormalizeAngle(double angleDeg)
    {
        if (double.IsNaN(angleDeg) || double.IsInfinity(angleDeg))
        {
            throw new ArgumentOutOfRangeException(nameof(angleDeg), "角度必須為有限數值。");
        }

        var a = angleDeg % 360;
        return a < 0 ? a + 360 : a;
    }

    /// <summary>將 FOV 夾在 [1, 360]。</summary>
    public static double ClampFov(double fovDeg) => Math.Clamp(fovDeg, MinFovDeg, MaxFovDeg);

    /// <summary>將覆蓋深度（公尺）夾在 [0, 1000]。</summary>
    public static double ClampDepth(double meters) => Math.Clamp(meters, 0, MaxDepthMeters);

    /// <summary>依畫面角度取得八方位名稱（0°＝東、順時針）。</summary>
    public static string Bearing(double angleDeg)
    {
        var index = (int)((NormalizeAngle(angleDeg) + 22.5) / 45) % 8;
        return Bearings[index];
    }

    /// <summary>
    /// 覆蓋深度換算為扇形像素半徑：比例尺（每像素公尺）與深度皆為正時，
    /// 半徑＝深度 ÷ 比例尺（夾在 8–100000 px）；否則回退固定像素。
    /// </summary>
    public static double SectorRadiusPixels(double fovDepthMeters, double scaleMPerPx, double fallbackPixels = DefaultSectorRadiusPixels)
    {
        if (double.IsNaN(fovDepthMeters) || double.IsNaN(scaleMPerPx))
        {
            return fallbackPixels;
        }

        if (scaleMPerPx > 0 && fovDepthMeters > 0)
        {
            return Math.Clamp(fovDepthMeters / scaleMPerPx, MinSectorRadiusPixels, MaxSectorRadiusPixels);
        }

        return fallbackPixels;
    }

    /// <summary>格式比例尺文字（0 或負＝未標定）。</summary>
    public static string ScaleLabel(double scaleMPerPx) => scaleMPerPx > 0
        ? $"{scaleMPerPx.ToString("0.###", CultureInfo.InvariantCulture)} m/px"
        : "未標定";

    /// <summary>驗證圖釘幾何並回傳正規化角度（FOV/深度超界時 throw）。</summary>
    public static double ValidateGeometry(double angleDeg, double fovDeg, double fovDepth)
    {
        if (double.IsNaN(fovDeg) || fovDeg < MinFovDeg || fovDeg > MaxFovDeg)
        {
            throw new ArgumentOutOfRangeException(nameof(fovDeg), "FOV 需介於 1 至 360 度。");
        }

        if (double.IsNaN(fovDepth) || fovDepth < 0 || fovDepth > MaxDepthMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(fovDepth), "覆蓋深度需介於 0 至 1000 公尺。");
        }

        return NormalizeAngle(angleDeg);
    }
}
