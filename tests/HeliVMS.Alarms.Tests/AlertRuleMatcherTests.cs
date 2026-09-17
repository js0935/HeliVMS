using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

public class AlertRuleMatcherTests
{
    private static AlarmEventRecord Event(string type = "motion", int channelId = 7, string? detail = "duration=1200ms")
        => new()
        {
            Id = 1,
            ChannelId = channelId,
            EventType = type,
            StartUtc = DateTime.UtcNow,
            Detail = detail,
        };

    private static AlertRule Rule(
        long id = 1,
        string? type = null,
        int? channelId = null,
        string? keyword = null,
        string? channels = null,
        bool enabled = true)
        => new(id, "r" + id, type, channelId, keyword, channels, enabled);

    [Fact]
    public void Match_NoRules_ReturnsNull()
    {
        Assert.Null(AlertRuleMatcher.Match(Array.Empty<AlertRule>(), Event()));
    }

    [Fact]
    public void Match_ByEventType_HitsAndMisses()
    {
        var rules = new[] { Rule(type: "motion") };
        Assert.NotNull(AlertRuleMatcher.Match(rules, Event("motion")));
        Assert.Null(AlertRuleMatcher.Match(rules, Event("person")));
        Assert.Equal("motion", AlertRuleMatcher.Match(rules, Event("MOTION"))?.EventType);
    }

    [Fact]
    public void Match_ByChannelId()
    {
        var rules = new[] { Rule(channelId: 3) };
        Assert.NotNull(AlertRuleMatcher.Match(rules, Event(channelId: 3)));
        Assert.Null(AlertRuleMatcher.Match(rules, Event(channelId: 9)));
    }

    [Fact]
    public void Match_ByKeyword_DetailAndType()
    {
        var rules = new[] { Rule(keyword: "peak") };
        Assert.NotNull(AlertRuleMatcher.Match(rules, Event(detail: "duration=1200ms peak=42%")));
        Assert.Null(AlertRuleMatcher.Match(rules, Event(detail: "其他細節")));

        var rules2 = new[] { Rule(keyword: "motion") };
        Assert.NotNull(AlertRuleMatcher.Match(rules2, Event("motion", detail: null)));
        Assert.NotNull(AlertRuleMatcher.Match(rules2, Event("MOTION")));
    }

    [Fact]
    public void Match_AllConditionsAreAnd()
    {
        var rules = new[] { Rule(type: "motion", channelId: 7, keyword: "peak") };
        Assert.NotNull(AlertRuleMatcher.Match(rules, Event("motion", 7, "peak=42%")));
        Assert.Null(AlertRuleMatcher.Match(rules, Event("motion", 8, "peak=42%")));
        Assert.Null(AlertRuleMatcher.Match(rules, Event("person", 7, "peak=42%")));
        Assert.Null(AlertRuleMatcher.Match(rules, Event("motion", 7, "其他")));
    }

    [Fact]
    public void Match_FirstMatchWins()
    {
        var rules = new[]
        {
            Rule(1, type: "motion", channels: "webhook"),
            Rule(2, type: "motion", channels: "snmp"),
        };
        Assert.Equal("webhook", AlertRuleMatcher.Match(rules, Event("motion"))?.Channels);
    }

    [Fact]
    public void Match_DisabledRuleIgnored()
    {
        var rules = new[] { Rule(1, type: "motion", enabled: false) };
        Assert.Null(AlertRuleMatcher.Match(rules, Event("motion")));
    }

    [Fact]
    public void ParseChannels_SplitsAndIgnoresCase()
    {
        var set = AlertRuleMatcher.ParseChannels("webhook, snmp");
        Assert.Contains("WEBHOOK", set);
        Assert.Contains("snmp", set);
        Assert.Equal(2, set.Count);
        Assert.Empty(AlertRuleMatcher.ParseChannels(null));
        Assert.Empty(AlertRuleMatcher.ParseChannels("   "));
    }
}