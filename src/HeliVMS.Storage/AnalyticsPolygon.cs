using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>
/// 分析情境多邊形點列（M52，§14.7 #6）：以 <c>"x,y;x,y;…"</c> 表示正規化 0..1 座標。
/// </summary>
public static class AnalyticsPolygon
{
    public const int MinPoints = 3;

    /// <summary>線段（跨線）最少點數。</summary>
    public const int MinLinePoints = 2;

    /// <summary>解析點列；格式或範圍不正確時回傳 false。</summary>
    public static bool TryParse(string? text, out IReadOnlyList<(double X, double Y)> points, int minPoints = MinPoints)
    {
        points = Array.Empty<(double, double)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parsed = new List<(double X, double Y)>();
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                x is < 0 or > 1 || y is < 0 or > 1)
            {
                return false;
            }

            parsed.Add((x, y));
        }

        if (parsed.Count < minPoints)
        {
            return false;
        }

        points = parsed;
        return true;
    }

    public static bool IsValid(string? text, int minPoints = MinPoints)
        => TryParse(text, out var points, minPoints) && points.Count >= minPoints;

    public static string Format(IEnumerable<(double X, double Y)> points)
        => string.Join(
            ";",
            points.Select(p =>
                $"{p.X.ToString("0.#####", CultureInfo.InvariantCulture)},{p.Y.ToString("0.#####", CultureInfo.InvariantCulture)}"));
}
