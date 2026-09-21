namespace HeliVMS.Storage.Tests;

public class StreamSwitcherTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private static StreamSwitcher Make(int minHold = 30, int window = 30)
        => new(minHold, window);

    private static StreamSwitcher MakeBandwidth(long limit)
        => new(mainBandwidthLimitBytesPerSec: limit);

    private static (string EventType, DateTime AtUtc) Ev(string type, DateTime at) => (type, at);

    [Fact]
    public void Honeymoon_WithinHold_KeepsCurrentDespiteBurst()
    {
        var sw = Make();
        sw.ApplySwitch(1, StreamKind.Main, T0);

        var decision = sw.Evaluate(1, 0, new[] { Ev("ai_intrusion", T0.AddSeconds(1)) }, 0, T0.AddSeconds(10));
        Assert.True(decision.NoChange);
        Assert.Equal(StreamKind.Main, decision.Target);
    }

    [Fact]
    public void EventBurst_PromotesSubToMain_AfterHoneymoon()
    {
        var sw = Make();
        sw.ApplySwitch(1, StreamKind.Sub, T0);

        var decision = sw.Evaluate(1, 0, new[] { Ev("tamper", T0.AddSeconds(40)) }, 0, T0.AddSeconds(41));
        Assert.Equal(StreamKind.Main, decision.Target);
        Assert.StartsWith("event-burst", decision.Reason);
    }

    [Fact]
    public void BandwidthPeak_DemotesMainToSub()
    {
        var sw = MakeBandwidth(60_000_000);
        sw.ApplySwitch(1, StreamKind.Main, T0);

        var decision = sw.Evaluate(1, 60_000_000, Array.Empty<(string, DateTime)>(), 2, T0.AddSeconds(31));
        Assert.Equal(StreamKind.Sub, decision.Target);
        Assert.Equal("bandwidth-peak", decision.Reason);
    }

    [Fact]
    public void NoViewers_DemotesMainToSub()
    {
        var sw = Make();
        sw.ApplySwitch(1, StreamKind.Main, T0);

        var decision = sw.Evaluate(1, 10_000, Array.Empty<(string, DateTime)>(), 0, T0.AddSeconds(31));
        Assert.Equal(StreamKind.Sub, decision.Target);
        Assert.Equal("no-viewers", decision.Reason);
    }

    [Fact]
    public void SubWithoutTriggers_KeepsSub()
    {
        var sw = Make();
        sw.ApplySwitch(1, StreamKind.Sub, T0);

        var decision = sw.Evaluate(1, 100, Array.Empty<(string, DateTime)>(), 0, T0.AddSeconds(31));
        Assert.True(decision.NoChange);
        Assert.Equal(StreamKind.Sub, decision.Target);
    }

    [Fact]
    public void EventBurst_AlreadyOnMain_KeepsMain()
    {
        var sw = Make();
        sw.ApplySwitch(1, StreamKind.Main, T0);

        var decision = sw.Evaluate(1, 1_000, new[] { Ev("ai_intrusion", T0.AddSeconds(31)) }, 1, T0.AddSeconds(31));
        Assert.True(decision.NoChange);
        Assert.Equal(StreamKind.Main, decision.Target);
    }

    [Fact]
    public void ApplySwitch_UpdatesCurrentAndTimestamp()
    {
        var sw = Make();
        sw.ApplySwitch(1, StreamKind.Sub, T0);

        Assert.Equal(StreamKind.Sub, sw.GetCurrent(1));
        Assert.Equal(T0, sw.GetLastSwitch(1));

        sw.ApplySwitch(1, StreamKind.Main, T0.AddSeconds(5));
        Assert.Equal(StreamKind.Main, sw.GetCurrent(1));
        Assert.Equal(T0.AddSeconds(5), sw.GetLastSwitch(1));
    }

    [Fact]
    public void OlderEvents_OutsideWindow_Ignored()
    {
        var sw = Make(window: 30);
        sw.ApplySwitch(1, StreamKind.Sub, T0);

        var decision = sw.Evaluate(1, 0, new[] { Ev("ai_intrusion", T0.AddSeconds(-100)) }, 0, T0.AddSeconds(31));
        Assert.True(decision.NoChange);
        Assert.Equal(StreamKind.Sub, decision.Target);
    }

    [Fact]
    public void EventWeights_AreConfigurable()
    {
        var sw = new StreamSwitcher(eventWeights: new Dictionary<string, int> { ["custom_critical"] = 500 });
        sw.ApplySwitch(1, StreamKind.Sub, T0);

        var promoted = sw.Evaluate(1, 0, new[] { Ev("motion", T0.AddSeconds(31)) }, 0, T0.AddSeconds(31));
        Assert.True(promoted.NoChange);

        var custom = sw.Evaluate(1, 0, new[] { Ev("custom_critical", T0.AddSeconds(32)) }, 0, T0.AddSeconds(32));
        Assert.Equal(StreamKind.Main, custom.Target);
    }

    [Fact]
    public void UnknownChannel_DefaultsToMain()
    {
        var sw = Make();
        Assert.Equal(StreamKind.Main, sw.GetCurrent(7));
        Assert.Null(sw.GetLastSwitch(7));
    }

    [Fact]
    public void Constructor_Validates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamSwitcher(minHoldSec: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamSwitcher(eventWindowSec: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamSwitcher(mainBandwidthLimitBytesPerSec: 0));
    }
}