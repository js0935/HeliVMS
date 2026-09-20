using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>正規化座標點（0..1）（M52，§14.7 #6）。</summary>
public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>分析情境幾何運算（M52，§14.7 #6）：多邊形、線段、方向、面積。</summary>
public static class AnalyticsGeometry
{
    /// <summary>解析 <c>"x,y;x,y;…"</c>（允許 2 點線段）；格式錯誤時回傳空清單。</summary>
    public static IReadOnlyList<NormalizedPoint> ParsePoints(string? text)
        => AnalyticsPolygon.TryParse(text, out var points, AnalyticsPolygon.MinLinePoints)
            ? points.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray()
            : Array.Empty<NormalizedPoint>();

    public static string FormatPoints(IEnumerable<NormalizedPoint> points)
        => AnalyticsPolygon.Format(points.Select(p => (p.X, p.Y)));

    public static bool IsValidPolygon(IReadOnlyList<NormalizedPoint> points)
        => points.Count >= AnalyticsPolygon.MinPoints &&
           points.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1);

    /// <summary>多邊形面積（鞋帶公式，絕對值）。</summary>
    public static double Area(IReadOnlyList<NormalizedPoint> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2.0;
    }

    /// <summary>點是否在多邊形內或邊界上（射線法＋邊界判定）。</summary>
    public static bool PointInPolygon(IReadOnlyList<NormalizedPoint> polygon, NormalizedPoint point)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (PointOnSegment(a, b, point))
            {
                return true;
            }
        }

        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > point.Y) != (pj.Y > point.Y) &&
                point.X < ((pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>兩線段是否相交（含端點接觸）。</summary>
    public static bool SegmentIntersects(NormalizedPoint a, NormalizedPoint b, NormalizedPoint c, NormalizedPoint d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);

        if (o1 != o2 && o3 != o4)
        {
            return true;
        }

        return (o1 == 0 && PointOnSegment(a, b, c)) ||
               (o2 == 0 && PointOnSegment(a, b, d)) ||
               (o3 == 0 && PointOnSegment(c, d, a)) ||
               (o4 == 0 && PointOnSegment(c, d, b));
    }

    /// <summary>點相對有向線段（a→b）的側向：&gt;0 左側、&lt;0 右側、0 在線上。</summary>
    public static int SignedSide(NormalizedPoint a, NormalizedPoint b, NormalizedPoint point)
    {
        var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
        return Math.Sign(cross);
    }

    private static int Orientation(NormalizedPoint a, NormalizedPoint b, NormalizedPoint c)
    {
        var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return Math.Sign(cross);
    }

    private static bool PointOnSegment(NormalizedPoint a, NormalizedPoint b, NormalizedPoint p)
    {
        if (Orientation(a, b, p) != 0)
        {
            return false;
        }

        const double epsilon = 1e-9;
        return p.X >= Math.Min(a.X, b.X) - epsilon && p.X <= Math.Max(a.X, b.X) + epsilon &&
               p.Y >= Math.Min(a.Y, b.Y) - epsilon && p.Y <= Math.Max(a.Y, b.Y) + epsilon;
    }
}
