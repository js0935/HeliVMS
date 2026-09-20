using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>M62（§5.10）：複合事件規則——match／all／any／not／within／count／timeBetween。</summary>
public class RuleEngineTests
{
    private static AiRuleRecord Rule(int id, string name, string expr, string actions = "{}", bool enabled = true)
        => new(id, name, expr, actions, enabled, "2026-01-01T00:00:00.000Z");

    private static AlarmEventRecord Evt(DateTime utc, string type, int channel = 1) => new()
    {
        StartUtc = utc,
        EventType = type,
        ChannelId = channel,
    };

    private static DateTime T(int second) =>
    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).AddSeconds(second);

    private static DateTime Local(int hour, int minute) => new(2026, 1, 2, hour, minute, 0);

    [Fact]
    public void Match_byEventType_And_Channel()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "motion on 3",
                """{"match":{"eventType":"motion","channelId":3}}"""),
        });

        Assert.Empty(eval.Feed(Evt(T(0), "motion", channel: 1)));
        Assert.Empty(eval.Feed(Evt(T(1), "ai_intrusion", channel: 3)));
        var hit = Assert.Single(eval.Feed(Evt(T(2), "motion", channel: 3)));
        Assert.Equal("motion on 3", hit.Name);
    }

    [Fact]
    public void All_RequiresEveryChild()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "both",
                """{"all":[{"match":{"eventType":"motion"}},{"match":{"channelId":5}}]}"""),
        });

        Assert.Empty(eval.Feed(Evt(T(0), "motion", channel: 1)));
        Assert.Single(eval.Feed(Evt(T(1), "motion", channel: 5)));
    }

    [Fact]
    public void Any_SatisfiedBySingleChild()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "either",
                """{"any":[{"match":{"eventType":"tamper"}},{"match":{"eventType":"ai_intrusion"}}]}"""),
        });

        Assert.Single(eval.Feed(Evt(T(0), "tamper")));
        Assert.Single(eval.Feed(Evt(T(1), "ai_intrusion")));
        Assert.Empty(eval.Feed(Evt(T(2), "motion")));
    }

    [Fact]
    public void Not_Inverts()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "no motion",
                """{"not":{"match":{"eventType":"motion"}}}"""),
        });

        Assert.Empty(eval.Feed(Evt(T(0), "motion")));
        Assert.Single(eval.Feed(Evt(T(1), "offline")));
    }

    [Fact]
    public void Within_Fires_OnlyWhenPrecededInsideWindow()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "intrusion after motion",
                """{"all":[{"match":{"eventType":"ai_intrusion"}},{"within":{"seconds":30,"match":{"eventType":"motion"}}}]}"""),
        });

        Assert.Empty(eval.Feed(Evt(T(0), "motion")));
        var hit = Assert.Single(eval.Feed(Evt(T(20), "ai_intrusion")));
        Assert.Equal("intrusion after motion", hit.Name);
    }

    [Fact]
    public void Within_Misses_WhenPrecedingEventOutsideWindow()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "intrusion after motion",
                """{"all":[{"match":{"eventType":"ai_intrusion"}},{"within":{"seconds":30,"match":{"eventType":"motion"}}}]}"""),
        });

        eval.Feed(Evt(T(0), "motion"));
        Assert.Empty(eval.Feed(Evt(T(40), "ai_intrusion")));
    }

    [Fact]
    public void Count_RequiresAtLeastNWithinWindow()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "three motions",
                """{"count":{"atLeast":3,"seconds":60,"match":{"eventType":"motion"}}}"""),
        });

        Assert.Empty(eval.Feed(Evt(T(0), "motion")));
        Assert.Empty(eval.Feed(Evt(T(1), "motion")));
        Assert.Single(eval.Feed(Evt(T(2), "motion")));

        var eval2 = new RuleEvaluator(new[]
        {
            Rule(2, "two motions in 10s",
                """{"count":{"atLeast":2,"seconds":10,"match":{"eventType":"motion"}}}"""),
        });
        Assert.Empty(eval2.Feed(Evt(T(0), "motion")));
        Assert.Single(eval2.Feed(Evt(T(5), "motion")));
    }

    [Fact]
    public void CountWindow_DropsOldEvents_ByBufferSpan()
    {
        var eval = new RuleEvaluator(
            new[]
            {
                Rule(1, "within 8h",
                    """{"within":{"seconds":100000,"match":{"eventType":"motion"}}}"""),
            },
            bufferSpan: TimeSpan.FromSeconds(30));

        eval.Feed(Evt(T(0), "motion"));
        Assert.Empty(eval.Feed(Evt(T(120), "ai_intrusion")));
        Assert.Equal(1, eval.BufferCount);
    }

    [Fact]
    public void TimeBetween_HitsInWindow_AndMissesOutside()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "business hours", """{"timeBetween":{"start":930,"end":1800}}"""),
        });

        var un = new AlarmEventRecord { StartUtc = Local(10, 0), EventType = "motion" };
        Assert.Single(eval.Feed(un));
        var outside = new AlarmEventRecord { StartUtc = Local(20, 0), EventType = "motion" };
        Assert.Empty(eval.Feed(outside));
    }

    [Fact]
    public void TimeBetween_Overnight_IncludesEndOfDay()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "night window", """{"timeBetween":{"start":2200,"end":200}}"""),
        });

        Assert.Single(eval.Feed(new AlarmEventRecord { StartUtc = Local(23, 0), EventType = "motion" }));
        Assert.Single(eval.Feed(new AlarmEventRecord { StartUtc = Local(1, 0), EventType = "motion" }));
        Assert.Empty(eval.Feed(new AlarmEventRecord { StartUtc = Local(5, 0), EventType = "motion" }));
    }

    [Fact]
    public void Actions_SurfacedOnMatch()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "escalate", """{"match":{"eventType":"ai_tailgating"}}""",
                """{"severity":"critical","tag":"escalated","notify":true}"""),
        });

        var hit = Assert.Single(eval.Feed(Evt(T(0), "ai_tailgating")));
        Assert.Equal("critical", hit.Actions.Severity);
        Assert.Equal("escalated", hit.Actions.Tag);
        Assert.True(hit.Actions.Notify);
    }

    [Fact]
    public void DisabledAndInvalidRules_AreIgnored()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "disabled", """{"match":{"eventType":"motion"}}""", enabled: false),
            Rule(2, "broken", "not-json{{{{"),
        });

        Assert.Empty(eval.Feed(Evt(T(0), "motion")));
    }

    [Fact]
    public void Reset_ClearsWindow()
    {
        var eval = new RuleEvaluator(new[]
        {
            Rule(1, "intrusion after motion",
                """{"all":[{"match":{"eventType":"ai_intrusion"}},{"within":{"seconds":60,"match":{"eventType":"motion"}}}]}"""),
        });

        eval.Feed(Evt(T(0), "motion"));
        Assert.Equal(1, eval.BufferCount);
        eval.Reset();
        Assert.Equal(0, eval.BufferCount);
        Assert.Empty(eval.Feed(Evt(T(10), "ai_intrusion")));
    }

    [Fact]
    public void Expression_RoundTrips_ThroughJson()
    {
        const string json =
            """{"all":[{"match":{"eventType":"ai_intrusion"}},{"within":{"seconds":60,"match":{"eventType":"motion"}}},{"timeBetween":{"start":900,"end":2200}}]}""";
        var node = RuleExpressionNode.Parse(json);

        var reparsed = RuleExpressionNode.Parse(node!.ToJson());
        Assert.NotNull(reparsed);
        Assert.NotNull(reparsed!.All);
        Assert.Equal(3, reparsed.All!.Count);
        Assert.NotNull(reparsed.All[1].Within);
        Assert.Equal(60, reparsed.All[1].Within!.Seconds);
        Assert.NotNull(reparsed.All[1].Within!.Match);
        Assert.Equal("motion", reparsed.All[1].Within!.Match!.EventType);
    }
}