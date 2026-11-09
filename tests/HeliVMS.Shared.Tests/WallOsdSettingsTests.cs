using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看格「時間 OSD」（QC_Live 的時間疊字對齊）的設定整備與格式化。
/// QC_Live 的 OSD 有兩種時間模式（完整 yyyy/MM/dd HH:mm:ss 與只有 HH:mm:ss）與
/// 可設字色字級（DispFontColor/DispFontSize）；本測試釘住：讀不懂的設定回預設而不是炸、
/// 格式只落在這兩種、字級夾在可讀範圍、色碼只收合法 hex。
/// </summary>
public sealed class WallOsdSettingsTests
{
    private static readonly DateTime T = new(2026, 11, 10, 18, 25, 32);

    [Fact]
    public void 預設關閉_讀不懂的模式也回關閉()
    {
        Assert.Equal(WallOsdSettings.OsdTimeMode.Off, WallOsdSettings.ParseMode(null));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Off, WallOsdSettings.ParseMode(""));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Off, WallOsdSettings.ParseMode("0"));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Off, WallOsdSettings.ParseMode("9"));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Off, WallOsdSettings.ParseMode("abc"));
    }

    [Fact]
    public void 模式只有完整與簡短兩種()
    {
        Assert.Equal(WallOsdSettings.OsdTimeMode.Full, WallOsdSettings.ParseMode("1"));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Short, WallOsdSettings.ParseMode("2"));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Full, WallOsdSettings.ParseMode("01"));
        Assert.Equal(WallOsdSettings.OsdTimeMode.Short, WallOsdSettings.ParseMode(" 2 "));
    }

    [Fact]
    public void 時間格式與QC_Live相同_完整或只有時分秒()
    {
        Assert.Equal("2026/11/10 18:25:32", WallOsdSettings.FormatTime(WallOsdSettings.OsdTimeMode.Full, T));
        Assert.Equal("18:25:32", WallOsdSettings.FormatTime(WallOsdSettings.OsdTimeMode.Short, T));
    }

    [Fact]
    public void 關閉模式不產生文字()
    {
        Assert.Equal(string.Empty, WallOsdSettings.FormatTime(WallOsdSettings.OsdTimeMode.Off, T));
    }

    [Fact]
    public void 字級讀不懂回預設並夾在可讀範圍()
    {
        Assert.Equal(WallOsdSettings.DefaultFontSize, WallOsdSettings.NormalizeFontSize(null));
        Assert.Equal(WallOsdSettings.DefaultFontSize, WallOsdSettings.NormalizeFontSize("x"));
        Assert.Equal(WallOsdSettings.MinFontSize, WallOsdSettings.NormalizeFontSize("1"));
        Assert.Equal(WallOsdSettings.MaxFontSize, WallOsdSettings.NormalizeFontSize("99"));
        Assert.Equal(16, WallOsdSettings.NormalizeFontSize("16"));
    }

    [Fact]
    public void 色碼只收合法hex並標準化_壞值回空()
    {
        Assert.Equal("#FFFFFF", WallOsdSettings.NormalizeColorHex("#ffffff"));
        Assert.Equal("#FFFFFF", WallOsdSettings.NormalizeColorHex("FFFFFF"));
        Assert.Equal("#0AF", WallOsdSettings.NormalizeColorHex("#0af"));
        Assert.Null(WallOsdSettings.NormalizeColorHex(null));
        Assert.Null(WallOsdSettings.NormalizeColorHex(""));
        Assert.Null(WallOsdSettings.NormalizeColorHex("white"));
        Assert.Null(WallOsdSettings.NormalizeColorHex("#GGHHII"));
        Assert.Null(WallOsdSettings.NormalizeColorHex("#12345"));
    }
}
