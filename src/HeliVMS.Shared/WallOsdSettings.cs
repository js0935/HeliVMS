using System.Globalization;

namespace HeliVMS.Shared;

/// <summary>
/// 監看格「時間 OSD」的設定整備與格式化（QC_Live 的時間疊字對齊）。
/// 純函式，行為測試在 <c>WallOsdSettingsTests</c>。
/// </summary>
/// <remarks>
/// <para>
/// QC_Live 的每格 OSD 是「頻道名＋時間」疊字，時間有兩種模式（完整
/// <c>yyyy/MM/dd HH:mm:ss</c> 或只有 <c>HH:mm:ss</c>），字色/字級可設
/// （UsageHabit 的 <c>DispFontColor</c>/<c>DispFontSize</c>）。HeliVMS 對齊為：
/// 頻道名已在徽章上，這裡只補時間疊字；<b>預設關閉</b>——開關與參數都來自
/// <c>app_settings</c>（<c>wall.osd_time</c>/<c>wall.osd_font_color</c>/<c>wall.osd_font_size</c>），
/// 沒設定的安裝看起來與升級前完全一樣。
/// </para>
/// <para>
/// 讀不懂的設定一律回預設而不是丟例外：OSD 更新在每秒的 UI 計時器上，
/// 一個壞色碼不能把整個監看牆的每秒刷新打掉。
/// </para>
/// </remarks>
public static class WallOsdSettings
{
    /// <summary>時間 OSD 的模式：0＝關（預設）、1＝完整日期時間、2＝只有時分秒。</summary>
    public enum OsdTimeMode
    {
        /// <summary>關閉（預設；升級後零視覺變化）。</summary>
        Off = 0,

        /// <summary>完整 <c>yyyy/MM/dd HH:mm:ss</c>（QC_Live 的第一種模式）。</summary>
        Full = 1,

        /// <summary>只有 <c>HH:mm:ss</c>（QC_Live 的第二種模式）。</summary>
        Short = 2,
    }

    /// <summary>字級下限：再小在 64 分割的格子上就是一圓點。</summary>
    public const double MinFontSize = 8;

    /// <summary>字級上限：再大就蓋住整格畫面。</summary>
    public const double MaxFontSize = 48;

    /// <summary>字級預設：與既有名稱徽章同級的 12。</summary>
    public const double DefaultFontSize = 12;

    /// <summary>預設字色：白。</summary>
    public const string DefaultColorHex = "#FFFFFF";

    /// <summary>
    /// 解析 <c>wall.osd_time</c> 的值；整數 1＝完整、2＝簡短，其餘（含空與亂碼）＝關閉。
    /// </summary>
    public static OsdTimeMode ParseMode(string? value)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return OsdTimeMode.Off;
        }

        return parsed switch
        {
            1 => OsdTimeMode.Full,
            2 => OsdTimeMode.Short,
            _ => OsdTimeMode.Off,
        };
    }

    /// <summary>把本地時間格式化成 OSD 文字；關閉模式回空字串（呼叫端以空白隱藏元素）。</summary>
    public static string FormatTime(OsdTimeMode mode, DateTime localTime)
        => mode switch
        {
            OsdTimeMode.Full => localTime.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
            OsdTimeMode.Short => localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    /// <summary>字級整備：讀不懂回預設，其餘夾在 <see cref="MinFontSize"/>～<see cref="MaxFontSize"/>。</summary>
    public static double NormalizeFontSize(string? value)
    {
        if (!double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return DefaultFontSize;
        }

        return Math.Clamp(parsed, MinFontSize, MaxFontSize);
    }

    /// <summary>
    /// 色碼整備：只收 <c>#RGB</c>／<c>#RRGGBB</c>（大小寫皆可），標準化成大寫並補上 <c>#</c>；
    /// 其他值（顏色名、亂碼、長度不對）回 <see langword="null"/>，呼叫端用 <see cref="DefaultColorHex"/>。
    /// </summary>
    public static string? NormalizeColorHex(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var body = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        if (body.Length is not (3 or 6) || !body.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        return "#" + body.ToUpperInvariant();
    }
}
