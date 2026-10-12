using System.Globalization;
using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 巡航時段解析（<see cref="PatrolWindowTime"/>）的行為契約。
///
/// 背景：巡邏排程的 HH:mm 一旦被判為壞值，退避值是「全天」。
/// 那代表每台有排程的攝影機整天都在轉頭，而且畫面上完全看不出異常——
/// 操作員只會覺得「這台怎麼一直在動」。所以格式判斷必須嚴格而可預期。
/// </summary>
public sealed class PatrolWindowTimeTests
{
    [Theory]
    [InlineData("00:00", 0, 0)]
    [InlineData("09:30", 9, 30)]
    [InlineData("23:59", 23, 59)]
    [InlineData("8:05", 8, 5)]
    [InlineData(" 09:30 ", 9, 30)]
    public void 標準格式會解析成對應的時刻(string text, int hours, int minutes)
    {
        Assert.True(PatrolWindowTime.TryParse(text, out var value));
        Assert.Equal(new TimeSpan(hours, minutes, 0), value);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("25:00")]
    [InlineData("-1:00")]
    [InlineData("12:60")]
    [InlineData("9")]
    [InlineData("09:30:00")]
    [InlineData("0930")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ab:cd")]
    [InlineData("+9:30")]
    [InlineData("9,030")]
    [InlineData(" 9 : 30 ")]
    [InlineData("١٢:٣٠")]
    public void 不合法或越界的時段不會被誤認為合法(string? text)
    {
        // 9 點半被誤判為不合法會讓全天執行；「09:30:00」被接受則會讓
        // 排程在不同解讀下指向不同時刻，兩種都會造成無法預期的巡邏時間。
        Assert.False(PatrolWindowTime.TryParse(text, out _));
    }

    [Fact]
    public void 時段格式壞掉時整段退為全天而不是午夜()
    {
        // 逐端退為 0 會得到 00:00-00:00，只在午夜執行：比不排程更難察覺，
        // 因為排程看起來是「有開著」。
        var (start, end) = PatrolWindowTime.Parse("09:00", "壞掉");

        Assert.Equal(TimeSpan.Zero, start);
        Assert.Equal(PatrolWindowTime.AllDayEnd, end);
    }

    [Fact]
    public void 兩端都合法時原樣保留跨午夜時段()
    {
        // 22:00-06:00 是常見的夜間巡邏；把它收斂成全天會讓白天也在轉頭。
        var (start, end) = PatrolWindowTime.Parse("22:00", "06:00");

        Assert.Equal(new TimeSpan(22, 0, 0), start);
        Assert.Equal(new TimeSpan(6, 0, 0), end);
        Assert.True(start > end, "跨午夜時段必須維持 start > end，讓控制器走跨午夜分支。");
    }

    [Fact]
    public void 現場常見的非英文語系機器仍能解析同一個時段()
    {
        // 現場機器若是中文或波蘭文 culture，框架的 TimeSpan.TryParseExact 連
        // "hh\\:mm" 這種固定格式都不認，會讓所有排程退成全天。
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "zh-TW", "pl-PL", "tr-TR" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.True(PatrolWindowTime.TryParse("09:30", out var value), $"culture={name}");
                Assert.Equal(new TimeSpan(9, 30, 0), value);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}