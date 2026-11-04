using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 匯出精靈的時刻輸入。現場症狀是「設定 5 秒卻匯出 50 分鐘」：操作員把 11:04:07.25 打成
/// 11040725，而舊的 TimeSpan.TryParse 讀不懂，於是程式默默把起訖當成一整天。
/// 所以這裡除了「讀得懂現場寫法」，也釘住「讀不懂就說讀不懂」。
/// </summary>
public class ClockTimeTests
{
    [Theory]
    [InlineData("11:04", 11, 4, 0, 0)]
    [InlineData("11:04:07", 11, 4, 7, 0)]
    [InlineData("11:04:07.25", 11, 4, 7, 250)]
    [InlineData("11:04:07,25", 11, 4, 7, 250)]
    [InlineData("11:04:07.250", 11, 4, 7, 250)]
    [InlineData("00:00", 0, 0, 0, 0)]
    [InlineData("23:59:59", 23, 59, 59, 0)]
    public void 標準寫法可解析(string text, int hours, int minutes, int seconds, int milliseconds)
    {
        Assert.True(ClockTime.TryParse(text, out var time), text);
        Assert.Equal(new TimeSpan(0, hours, minutes, seconds).Add(TimeSpan.FromMilliseconds(milliseconds)), time);
    }

    [Theory]
    [InlineData("1104", 11, 4, 0, 0)]
    [InlineData("110407", 11, 4, 7, 0)]
    [InlineData("11040725", 11, 4, 7, 250)]      // 8 位＝百分秒
    [InlineData("110407250", 11, 4, 7, 250)]     // 9 位＝毫秒（與 seg-HHmmssfff.mp4 一致）
    public void 緊湊寫法可解析(string text, int hours, int minutes, int seconds, int milliseconds)
    {
        // 操作員看著分段檔名 seg-110407250.mp4 就會打成這樣；讀不懂它是這次現場事件的起因。
        Assert.True(ClockTime.TryParse(text, out var time), text);
        Assert.Equal(new TimeSpan(0, hours, minutes, seconds).Add(TimeSpan.FromMilliseconds(milliseconds)), time);
    }

    [Fact]
    public void 一位數的時分秒視為補零()
    {
        // 「8:5」就是 08:05：寬鬆地讀得懂比嚴格挑格式重要，真正要擋的是「讀不懂就當成一整天」。
        Assert.True(ClockTime.TryParse("8:5:3", out var time));
        Assert.Equal(new TimeSpan(0, 8, 5, 3), time);
    }

    [Fact]
    public void 前後空白不影響解析()
    {
        Assert.True(ClockTime.TryParse("  11:04:07.25  ", out var time));
        Assert.Equal(new TimeSpan(0, 11, 4, 7, 250), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("25:00")]          // 小時超出範圍
    [InlineData("11:60")]          // 分鐘超出範圍
    [InlineData("11:04:60")]       // 秒超出範圍
    [InlineData("11")]             // 位數不完整
    [InlineData("110")]            // 同上
    [InlineData("11040")]          // 同上
    [InlineData("1104072")]        // 7 位：不屬於任何一種寫法
    [InlineData("1104072512")]     // 10 位：超過毫秒精度
    [InlineData("11:04:07.25000000")] // 分數超過 7 位
    [InlineData("aa:bb")]
    [InlineData("11;04")]
    [InlineData("11:04:07.2a")]
    public void 讀不懂的字串一律拒絕(string? text)
    {
        // 拒絕比猜一個範圍重要：猜錯就是「設定 5 秒、拿到 50 分鐘」，成品還是有紀錄價值的檔案。
        Assert.False(ClockTime.TryParse(text, out _), text);
    }
}
