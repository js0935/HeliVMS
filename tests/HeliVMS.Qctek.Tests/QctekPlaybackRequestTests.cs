using HeliVMS.Qctek.Live;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 回放查詢的參數解析（QCC 命令的數字序列 → 頻道/年/月/日）。
/// 解析規則是<b>寬鬆的暫定版</b>（QCC 端 sprintf varargs 顯示不全，真機抓包後修正）：
/// 「≥1970 的是年、1–12 可為月、1–31 可為日、第一個非年數為頻道」；
/// 這組測試把現行規則釘住——真機抓包若有出入，改解析器時測試會跟著改，
/// 但「解析不出年月回 null（呼叫端回 FAIL 語意）」與「日缺省為 1」是契約，不該變。
/// </summary>
public sealed class QctekPlaybackRequestTests
{
    [Fact]
    public void 年在前時解析出頻道與日期()
    {
        // CATALOG 的參數順序（文件註記：CATALOG 的年在前）。
        var q = QctekPlaybackRequest.Parse([2026, 11, 10, 5]);

        Assert.NotNull(q);
        Assert.Equal(5, q!.ChannelIndex);
        Assert.Equal(2026, q.Year);
        Assert.Equal(11, q.Month);
        Assert.Equal(10, q.Day);
    }

    [Fact]
    public void 頻道為零代表全部頻道()
    {
        var q = QctekPlaybackRequest.Parse([0, 2026, 11, 10]);

        Assert.NotNull(q);
        Assert.Equal(0, q!.ChannelIndex);
        Assert.Equal(2026, q.Year);
        Assert.Equal(11, q.Month);
        Assert.Equal(10, q.Day);
    }

    [Fact]
    public void 缺年或缺月回null由呼叫端回FAIL語意()
    {
        Assert.Null(QctekPlaybackRequest.Parse([11, 10]));
        Assert.Null(QctekPlaybackRequest.Parse([2026]));
        Assert.Null(QctekPlaybackRequest.Parse([]));
    }

    [Fact]
    public void 只有年與一個一至十二的數字視為月查詢而不是缺月()
    {
        // 10 在 1–12 內：先歸月（月曆查詢），不是歸日——[2026, 10] 是「2026 年 10 月」。
        var q = QctekPlaybackRequest.Parse([2026, 10]);

        Assert.NotNull(q);
        Assert.Equal(2026, q!.Year);
        Assert.Equal(10, q.Month);
        Assert.Equal(1, q.Day);
    }

    [Fact]
    public void 缺日預設為1號()
    {
        // 只問「某月」時（月曆查詢），日沒有意義：補 1 讓 DateTime 建構不炸。
        var q = QctekPlaybackRequest.Parse([2026, 11]);

        Assert.NotNull(q);
        Assert.Equal(1, q!.Day);
        Assert.Equal(2026, q.Year);
        Assert.Equal(11, q.Month);
    }

    [Fact]
    public void 月日同值時各歸各位()
    {
        // 12 月 12 日：第一個 12 歸月、第二個歸日，不會互吞。
        var q = QctekPlaybackRequest.Parse([2026, 12, 12, 3]);

        Assert.NotNull(q);
        Assert.Equal(12, q!.Month);
        Assert.Equal(12, q.Day);
        Assert.Equal(3, q.ChannelIndex);
    }

    [Fact]
    public void 超出範圍的數字不會被誤認成頻道()
    {
        // 65535 之類的雜訊不該被當成頻道（0–1024 之外全忽略）。
        var q = QctekPlaybackRequest.Parse([2026, 11, 10, 65535]);

        Assert.NotNull(q);
        Assert.Equal(0, q!.ChannelIndex);
    }
}
