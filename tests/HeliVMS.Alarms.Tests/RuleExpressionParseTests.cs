using HeliVMS.Alarms;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 簡易表單 ↔ JSON 的往返（規則編輯器的「編輯」靠它）：組裝器產生的形狀必須能無損解回表單，
/// 而進階模式的手寫形狀（any／not／within／巢狀）必須拒解——硬解回表單會把規則改壞，
/// 編輯器寧可退回進階模式讓操作員看到原始 JSON。
/// </summary>
public class RuleExpressionParseTests
{
    [Fact]
    public void 組裝的單一比對能無損解回表單值()
    {
        var json = RuleExpressionBuilder.Build("motion", channelId: 7);

        var form = RuleExpressionBuilder.TryParseExpression(json);

        Assert.NotNull(form);
        Assert.Equal("motion", form!.EventType);
        Assert.Equal(7, form.ChannelId);
        Assert.Null(form.CountAtLeast);
        Assert.Null(form.TimeStartHhmm);
    }

    [Fact]
    public void 組裝的計數窗能無損解回表單值()
    {
        var json = RuleExpressionBuilder.Build("ai_person", channelId: null, countAtLeast: 3, countSeconds: 45);

        var form = RuleExpressionBuilder.TryParseExpression(json);

        Assert.NotNull(form);
        Assert.Equal("ai_person", form!.EventType);
        Assert.Null(form.ChannelId);
        Assert.Equal(3, form.CountAtLeast);
        Assert.Equal(45, form.CountSeconds);
    }

    [Fact]
    public void 組裝的時段加條件能無損解回表單值()
    {
        var json = RuleExpressionBuilder.Build("motion", channelId: 2, timeStartHhmm: 800, timeEndHhmm: 1800);

        var form = RuleExpressionBuilder.TryParseExpression(json);

        Assert.NotNull(form);
        Assert.Equal("motion", form!.EventType);
        Assert.Equal(2, form.ChannelId);
        Assert.Equal(800, form.TimeStartHhmm);
        Assert.Equal(1800, form.TimeEndHhmm);
    }

    [Theory]
    [InlineData("{\"any\":[{\"match\":{\"eventType\":\"motion\"}}]}")]
    [InlineData("{\"not\":{\"match\":{\"eventType\":\"motion\"}}}")]
    [InlineData("{\"within\":{\"seconds\":30,\"match\":{\"eventType\":\"motion\"}}}")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData(null)]
    public void 進階形狀與壞JSON拒絕解回表單(string? json)
    {
        Assert.Null(RuleExpressionBuilder.TryParseExpression(json));
    }

    [Fact]
    public void 動作JSON解回嚴重度標籤與通知開關()
    {
        var json = RuleExpressionBuilder.BuildActions("critical", "夜班", notify: false);

        var (severity, tag, notify) = RuleExpressionBuilder.TryParseActions(json);

        Assert.Equal("critical", severity);
        Assert.Equal("夜班", tag);
        Assert.False(notify);
    }

    [Fact]
    public void 空動作與壞動作JSON回預設值不擲例外()
    {
        var (emptySeverity, emptyTag, emptyNotify) = RuleExpressionBuilder.TryParseActions(string.Empty);
        Assert.Null(emptySeverity);
        Assert.Null(emptyTag);
        Assert.True(emptyNotify);

        var (badSeverity, badTag, badNotify) = RuleExpressionBuilder.TryParseActions("{broken");
        Assert.Null(badSeverity);
        Assert.Null(badTag);
        Assert.True(badNotify);
    }
}
