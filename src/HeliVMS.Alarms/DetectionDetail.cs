using System.Globalization;
using System.Text.RegularExpressions;

namespace HeliVMS.Alarms;

/// <summary>
/// AiEventEngine 明細字串（如 "person conf=0.88 bbox=(0.39,0.59,0.11,0.42)"）之解析器，
/// 供事件中心快照疊加框使用。
/// </summary>
public static class DetectionDetail
{
    private static readonly Regex Pattern = new(
        @"^(?<cls>[a-z_]+) conf=(?<c>[0-9.]+) bbox=\((?<x>[0-9.]+),(?<y>[0-9.]+),(?<w>[0-9.]+),(?<h>[0-9.]+)\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string? detail, out Detection detection)
    {
        detection = null!;
        if (detail is null)
        {
            return false;
        }

        var m = Pattern.Match(detail);
        if (!m.Success)
        {
            return false;
        }

        detection = new Detection(
            m.Groups["cls"].Value,
            float.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture));
        return true;
    }
}