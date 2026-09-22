namespace HeliVMS.Storage.Tests;

public class NlEventQueryParserTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Parse_ChannelAfterKeyword()
    {
        var result = NlEventQueryParser.Parse("頻道5 移動", Now);
        Assert.Equal(5, result.ChannelId);
    }

    [Fact]
    public void Parse_ChannelBeforeKeyword()
    {
        var result = NlEventQueryParser.Parse("5路 入侵", Now);
        Assert.Equal(5, result.ChannelId);
    }

    [Fact]
    public void Parse_ChannelFullWidthDigits()
    {
        var result = NlEventQueryParser.Parse("頻道３ 移動", Now);
        Assert.Equal(3, result.ChannelId);
    }

    [Fact]
    public void Parse_Today_Bounds()
    {
        var result = NlEventQueryParser.Parse("今天 移動", Now);

        Assert.Equal(Now.Date, result.FromUtc);
        Assert.Equal(Now, result.ToUtc);
    }

    [Fact]
    public void Parse_Yesterday_Bounds()
    {
        var result = NlEventQueryParser.Parse("昨天 移動", Now);

        Assert.Equal(Now.Date.AddDays(-1), result.FromUtc);
        Assert.Equal(Now.Date, result.ToUtc);
    }

    [Theory]
    [InlineData("最近 30 分鐘", 30, "分鐘")]
    [InlineData("最近 2 小時", 2, "小時")]
    public void Parse_RecentWindow(string query, int amount, string unit)
    {
        var result = NlEventQueryParser.Parse(query, Now);

        var expected = unit == "小時"
            ? Now.AddHours(-amount)
            : Now.AddMinutes(-amount);
        Assert.Equal(expected, result.FromUtc);
        Assert.Null(result.ToUtc);
    }

    [Fact]
    public void Parse_PeriodWords_AnchorSameDay()
    {
        var morning = NlEventQueryParser.Parse("上午 移動", Now);
        Assert.Equal(Now.Date + TimeSpan.FromHours(6), morning.FromUtc);
        Assert.Equal(Now.Date + TimeSpan.FromHours(12), morning.ToUtc);

        var night = NlEventQueryParser.Parse("晚上 移動", Now);
        Assert.Equal(Now.Date + TimeSpan.FromHours(18), night.FromUtc);
        Assert.Equal(Now.Date + TimeSpan.FromHours(24), night.ToUtc);
    }

    [Fact]
    public void Parse_ClockTime_Window()
    {
        var result = NlEventQueryParser.Parse("14:20 移動", Now);

        var at = Now.Date + TimeSpan.FromHours(14) + TimeSpan.FromMinutes(20);
        Assert.Equal(at, result.FromUtc);
        Assert.Equal(at.AddMinutes(10), result.ToUtc);
    }

    [Fact]
    public void Parse_FullWidthColon()
    {
        var result = NlEventQueryParser.Parse("14：20 移動", Now);
        Assert.Equal(Now.Date + TimeSpan.FromHours(14) + TimeSpan.FromMinutes(20), result.FromUtc);
    }

    [Theory]
    [InlineData("移動", "motion")]
    [InlineData("入侵", "ai_intrusion")]
    [InlineData("越線", "line_cross")]
    [InlineData("徘徊", "dwell")]
    [InlineData("逗留", "loitering")]
    [InlineData("群聚", "crowd")]
    [InlineData("遺留", "left_object")]
    [InlineData("移除", "removed_object")]
    [InlineData("人臉", "face")]
    [InlineData("車牌", "lpr")]
    [InlineData("離線", "offline")]
    public void Parse_EventTypeKeywords(string keyword, string eventType)
    {
        var result = NlEventQueryParser.Parse(keyword, Now);
        Assert.Contains(eventType, result.EventTypes);
    }

    [Fact]
    public void Parse_MultipleTypes_AllCollected()
    {
        var result = NlEventQueryParser.Parse("移動 且 入侵", Now);

        Assert.Equal(2, result.EventTypes.Count);
        Assert.Contains("motion", result.EventTypes);
        Assert.Contains("ai_intrusion", result.EventTypes);
    }

    [Fact]
    public void Parse_Limit()
    {
        var result = NlEventQueryParser.Parse("最新 20 筆 移動", Now);
        Assert.Equal(20, result.Limit);
    }

    [Fact]
    public void Parse_ComprehensiveQuery()
    {
        var result = NlEventQueryParser.Parse("頻道3 昨天 下午 入侵 最新 10 筆", Now);

        Assert.Equal(3, result.ChannelId);
        Assert.Equal(Now.Date.AddDays(-1) + TimeSpan.FromHours(12), result.FromUtc);
        Assert.Equal(Now.Date.AddDays(-1) + TimeSpan.FromHours(18), result.ToUtc);
        Assert.Equal(10, result.Limit);
        Assert.Contains("ai_intrusion", result.EventTypes);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Parse_UnmatchedTokens_Collected()
    {
        var result = NlEventQueryParser.Parse("頻道3 移動 想看可疑錄影", Now);

        Assert.Single(result.Unmatched);
        Assert.Equal("想看可疑錄影", result.Unmatched[0]);
    }

    [Fact]
    public void Parse_EmptyInput_EmptyResult()
    {
        var result = NlEventQueryParser.Parse("", Now);
        Assert.Null(result.ChannelId);
        Assert.Empty(result.EventTypes);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Parse_WhitespaceOnly_EmptyResult()
    {
        var result = NlEventQueryParser.Parse("  　", Now);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void ToQueryArgs_MapsAllFields()
    {
        var parsed = NlEventQueryParser.Parse("頻道2 今天 車牌 最新 50 筆", Now);

        var args = parsed.ToQueryArgs();

        Assert.Equal(2, args.ChannelId);
        Assert.Equal("lpr", args.EventType);
        Assert.Equal(Now.Date, args.FromUtc);
        Assert.Equal(Now, args.ToUtc);
        Assert.Equal(50, args.Limit);
    }
}