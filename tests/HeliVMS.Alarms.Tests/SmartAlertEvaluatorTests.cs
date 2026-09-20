using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>M54（§14.7 #8）：智慧警報評估器。</summary>
public class SmartAlertEvaluatorTests
{
    private static AlertRule Rule(
        string? eventType = "ai_intrusion",
        int? channelId = 27,
        string? matchTypes = null,
        int frameMinutes = 5,
        int minEvents = 3,
        bool enabled = true)
        => new(1, "agg", eventType, channelId, null, null, enabled, matchTypes, frameMinutes, minEvents);

    private static AlarmEventRecord Event(int channelId = 27, string eventType = "ai_intrusion", DateTime? at = null)
        => new()
        {
            ChannelId = channelId,
            EventType = eventType,
            StartUtc = at ?? DateTime.UtcNow,
        };

    [Fact]
    public void ParseMatchTypes_CommaSeparated_Trimmed()
    {
        var set = SmartAlertEvaluator.ParseMatchTypes(" ai_intrusion , ai_line_cross ");
        Assert.Equal(2, set.Count);
        Assert.Contains("ai_intrusion", set);
        Assert.True(set.Contains("AI_LINE_CROSS"));
    }

    [Fact]
    public void ParseMatchTypes_NullOrEmpty_IsEmptySet()
    {
        Assert.Empty(SmartAlertEvaluator.ParseMatchTypes(null));
        Assert.Empty(SmartAlertEvaluator.ParseMatchTypes("  "));
    }

    [Fact]
    public void Matches_EventTypeMustEqual()
    {
        var rule = Rule(eventType: "ai_intrusion");
        Assert.True(SmartAlertEvaluator.Matches(rule, Event(eventType: "ai_intrusion")));
        Assert.False(SmartAlertEvaluator.Matches(rule, Event(eventType: "ai_crowd")));
    }

    [Fact]
    public void Matches_ChannelFilter()
    {
        var rule = Rule(channelId: 27);
        Assert.True(SmartAlertEvaluator.Matches(rule, Event(channelId: 27)));
        Assert.False(SmartAlertEvaluator.Matches(rule, Event(channelId: 5)));
    }

    [Fact]
    public void Matches_MatchTypesRestrictsEventType()
    {
        var rule = Rule(matchTypes: "ai_intrusion,ai_line_cross");
        Assert.True(SmartAlertEvaluator.Matches(rule, Event(eventType: "ai_intrusion")));
        Assert.False(SmartAlertEvaluator.Matches(rule, Event(eventType: "ai_crowd")));
    }

    [Fact]
    public void Matches_MatchTypesNull_MatchesAny()
    {
        var anyEventType = Rule(eventType: null, matchTypes: null);
        Assert.True(SmartAlertEvaluator.Matches(anyEventType, Event(eventType: "ai_motion_probe")));

        var restricted = Rule(eventType: null, matchTypes: "ai_crowd");
        Assert.True(SmartAlertEvaluator.Matches(restricted, Event(eventType: "ai_crowd")));
        Assert.False(SmartAlertEvaluator.Matches(restricted, Event(eventType: "ai_line_cross")));
    }

    [Fact]
    public void Matches_DisabledRule_IsFalse()
    {
        var rule = Rule(enabled: false);
        Assert.False(SmartAlertEvaluator.Matches(rule, Event()));
    }

    [Fact]
    public void Evaluate_ReachedAtThreshold()
    {
        var now = DateTime.UtcNow;
        var events = new[]
        {
            Event(at: now.AddMinutes(-4)),
            Event(at: now.AddMinutes(-3)),
            Event(at: now.AddMinutes(-2)),
            Event(at: now.AddMinutes(-1)),
        };
        var triggers = SmartAlertEvaluator.Evaluate(new[] { Rule(minEvents: 3) }, events, now, windowMinutes: 5);
        var trigger = Assert.Single(triggers);
        Assert.True(trigger.Reached);
        Assert.Equal(4, trigger.WindowCount);
        Assert.Equal(events[0].StartUtc, trigger.FirstUtc);
        Assert.Equal(events[3].StartUtc, trigger.LastUtc);
    }

    [Fact]
    public void Evaluate_BelowThreshold_NotReached()
    {
        var now = DateTime.UtcNow;
        var events = new[] { Event(at: now.AddMinutes(-4)) };
        var trigger = Assert.Single(SmartAlertEvaluator.Evaluate(new[] { Rule(minEvents: 3) }, events, now, 5));
        Assert.False(trigger.Reached);
        Assert.Equal(1, trigger.WindowCount);
    }

    [Fact]
    public void Evaluate_OutsideWindow_NotCounted()
    {
        var now = DateTime.UtcNow;
        var events = new[] { Event(at: now.AddMinutes(-20)), Event(at: now.AddMinutes(-6)) };
        var trigger = Assert.Single(SmartAlertEvaluator.Evaluate(new[] { Rule(minEvents: 1) }, events, now, 5));
        Assert.False(trigger.Reached);
        Assert.Equal(0, trigger.WindowCount);
    }

    [Fact]
    public void Evaluate_OtherChannel_NotCounted()
    {
        var now = DateTime.UtcNow;
        var events = new[]
        {
            Event(channelId: 5, at: now.AddMinutes(-1)),
            Event(channelId: 5, at: now.AddMinutes(-2)),
        };
        var trigger = Assert.Single(SmartAlertEvaluator.Evaluate(new[] { Rule(channelId: 27, minEvents: 2) }, events, now, 5));
        Assert.Equal(0, trigger.WindowCount);
        Assert.False(trigger.Reached);
    }

    [Fact]
    public void Evaluate_FrameMinutesZero_Ignored()
    {
        var now = DateTime.UtcNow;
        var events = new[] { Event(at: now) };
        Assert.Empty(SmartAlertEvaluator.Evaluate(new[] { Rule(frameMinutes: 0) }, events, now, 5));
    }

    [Fact]
    public void ShouldSuppress_BelowAndAtThreshold()
    {
        Assert.True(SmartAlertEvaluator.ShouldSuppress(Rule(minEvents: 3), windowCountIncludingCurrent: 2));
        Assert.False(SmartAlertEvaluator.ShouldSuppress(Rule(minEvents: 3), windowCountIncludingCurrent: 3));
        Assert.False(SmartAlertEvaluator.ShouldSuppress(Rule(frameMinutes: 0), windowCountIncludingCurrent: 1));
    }

    [Fact]
    public void Evaluate_MultipleRules_ReportEach()
    {
        var now = DateTime.UtcNow;
        var events = new[]
        {
            Event(eventType: "ai_intrusion", at: now.AddMinutes(-1)),
            Event(eventType: "ai_line_cross", at: now.AddMinutes(-1)),
        };
        var rules = new[]
        {
            Rule(eventType: "ai_intrusion", minEvents: 1),
            Rule(eventType: "ai_line_cross", minEvents: 2),
        };
        var triggers = SmartAlertEvaluator.Evaluate(rules, events, now, 5);
        Assert.Equal(2, triggers.Count);
        Assert.True(triggers[0].Reached);
        Assert.False(triggers[1].Reached);
    }
}