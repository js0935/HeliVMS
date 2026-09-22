namespace HeliVMS.Storage.Tests;

public class SmartwallAlertBoardTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
    private static SmartwallBoardEvent E(
        long channel,
        string type,
        string priority,
        DateTime occurred,
        int ruleOrder = 1)
        => new(channel, type, priority, occurred, ruleOrder);

    [Fact]
    public void Snapshot_ExcludesEventsOlderThanKeepWindow()
    {
        var old = E(1, "motion", "high", Now.Subtract(TimeSpan.FromSeconds(301)));

        Assert.Empty(SmartwallAlertBoard.Snapshot(new[] { old }, Now, 4));
    }

    [Fact]
    public void Snapshot_DedupesPerChannel_NewestWins()
    {
        var shot = E(1, "motion", "high", Now.AddSeconds(-10));
        var latest = E(1, "ai_intrusion", "critical", Now.AddSeconds(-1));

        var cells = SmartwallAlertBoard.Snapshot(new[] { shot, latest }, Now, 4);

        var cell = Assert.Single(cells);
        Assert.Equal("ai_intrusion", cell.EventType);
        Assert.Equal("critical", cell.Priority);
    }

    [Fact]
    public void Snapshot_OrdersByRule_Priority_ThenRecency()
    {
        var events = new[]
        {
            E(10, "motion", "low", Now.AddSeconds(-5), ruleOrder: 3),
            E(20, "ai_intrusion", "critical", Now.AddSeconds(-1), ruleOrder: 1),
            E(30, "motion", "high", Now.AddSeconds(-2), ruleOrder: 1),
        };

        var cells = SmartwallAlertBoard.Snapshot(events, Now, 4);

        Assert.Equal(new long[] { 20, 30, 10 }, cells.Select(c => c.ChannelId).ToArray());
        Assert.Equal(new int[] { 1, 2, 3 }, cells.Select(c => c.Rank).ToArray());
    }

    [Fact]
    public void Snapshot_Highlights_Within5Seconds()
    {
        var recent = E(1, "motion", "normal", Now.AddSeconds(-4));

        var cell = Assert.Single(SmartwallAlertBoard.Snapshot(new[] { recent }, Now, 4));

        Assert.True(cell.Highlight);
    }

    [Fact]
    public void Snapshot_NoHighlight_After5Seconds()
    {
        var stale = E(1, "motion", "normal", Now.AddSeconds(-6));

        var cell = Assert.Single(SmartwallAlertBoard.Snapshot(new[] { stale }, Now, 4));

        Assert.False(cell.Highlight);
    }

    [Fact]
    public void Snapshot_Age_ReflectsDelta()
    {
        var e = E(1, "motion", "normal", Now.AddSeconds(-12));

        var cell = Assert.Single(SmartwallAlertBoard.Snapshot(new[] { e }, Now, 4));

        Assert.Equal(TimeSpan.FromSeconds(12), cell.Age);
    }

    [Fact]
    public void Snapshot_CapsToMaxCells()
    {
        var events = Enumerable.Range(1, 5).Select(i => E(i, "motion", "low", Now.AddSeconds(-i)));

        var cells = SmartwallAlertBoard.Snapshot(events, Now, 3);

        Assert.Equal(3, cells.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, cells.Select(c => c.ChannelId).ToArray());
    }

    [Fact]
    public void Snapshot_Empty_ReturnsEmpty()
    {
        Assert.Empty(SmartwallAlertBoard.Snapshot(Array.Empty<SmartwallBoardEvent>(), Now, 4));
    }

    [Fact]
    public void Snapshot_MaxCellsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SmartwallAlertBoard.Snapshot(Array.Empty<SmartwallBoardEvent>(), Now, 0));
    }

    [Fact]
    public void LatestForChannel_ReturnsNewestInWindow()
    {
        var old = E(5, "motion", "low", Now.AddSeconds(-200));
        var newest = E(5, "ai_intrusion", "critical", Now.AddSeconds(-3));
        var other = E(6, "motion", "normal", Now.AddSeconds(-1));

        var result = SmartwallAlertBoard.LatestForChannel(new[] { old, newest, other }, 5, Now);

        Assert.Equal("ai_intrusion", result!.EventType);
    }

    [Fact]
    public void LatestForChannel_NoneAvailable_ReturnsNull()
    {
        var stale = E(5, "motion", "low", Now.AddSeconds(-400));

        Assert.Null(SmartwallAlertBoard.LatestForChannel(new[] { stale }, 5, Now));
    }

    [Fact]
    public void RankOf_PriorityMapping()
    {
        Assert.Equal(0, SmartwallAlertBoard.RankOf("low"));
        Assert.Equal(2, SmartwallAlertBoard.RankOf("high"));
        Assert.Equal(3, SmartwallAlertBoard.RankOf("critical"));
        Assert.Equal(int.MaxValue, SmartwallAlertBoard.RankOf("unknown"));
    }
}