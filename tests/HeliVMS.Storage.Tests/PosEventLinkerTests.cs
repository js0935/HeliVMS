namespace HeliVMS.Storage.Tests;

public class PosEventLinkerTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static PosTransaction Txn(long id, int? ch, DateTime at, long? cents = 1000)
        => new(id, ch, at, cents ?? 1000);

    private static VideoEvent Ev(long id, int ch, DateTime at, string type = "motion")
        => new(id, ch, at, type);

    [Fact]
    public void MatchesSameChannelWithinWindow()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 2, T0) },
            new[] { Ev(10, 2, T0.AddSeconds(3)) },
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, summary.MatchedTxns);
        Assert.Equal(0, summary.UnmatchedTxns);
        Assert.Equal(0, summary.OrphanEvents);
        var pair = Assert.Single(summary.Pairs);
        Assert.Equal(1, pair.TxnId);
        Assert.Equal(10, pair.EventId);
        Assert.Equal(TimeSpan.FromSeconds(3), pair.Offset);
    }

    [Fact]
    public void WindowBoundaryIsInclusive()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 1, T0) },
            new[] { Ev(10, 1, T0.AddMinutes(2)) },
            TimeSpan.FromMinutes(2));

        Assert.Equal(1, summary.MatchedTxns);
    }

    [Fact]
    public void OutsideWindowIsUnmatchedAndEventOrphans()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 1, T0) },
            new[] { Ev(10, 1, T0.AddMinutes(10)) },
            TimeSpan.FromMinutes(2));

        Assert.Equal(0, summary.MatchedTxns);
        Assert.Equal(1, summary.UnmatchedTxns);
        Assert.Equal(1, summary.OrphanEvents);
        Assert.Empty(summary.Pairs);
    }

    [Fact]
    public void DifferentChannelNeverCrossLinks()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 3, T0) },
            new[] { Ev(10, 4, T0.AddSeconds(1)) },
            TimeSpan.FromSeconds(30));

        Assert.Equal(0, summary.MatchedTxns);
        Assert.Equal(1, summary.UnmatchedTxns);
        Assert.Equal(1, summary.OrphanEvents);
    }

    [Fact]
    public void PicksClosestEventWhenTwoCandidates()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 1, T0) },
            new[] { Ev(10, 1, T0.AddSeconds(20)), Ev(11, 1, T0.AddSeconds(4)) },
            TimeSpan.FromSeconds(30));

        var pair = Assert.Single(summary.Pairs);
        Assert.Equal(11, pair.EventId);
        Assert.Equal(TimeSpan.FromSeconds(4), pair.Offset);
    }

    [Fact]
    public void EventConsumedAtMostOnce()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 1, T0), Txn(2, 1, T0.AddSeconds(8)) },
            new[] { Ev(10, 1, T0.AddSeconds(1)) },
            TimeSpan.FromSeconds(10));

        Assert.Equal(1, summary.MatchedTxns);
        Assert.Equal(1, summary.UnmatchedTxns);
        Assert.Equal(0, summary.OrphanEvents);
    }

    [Fact]
    public void TxnWithoutChannelIsUnmatched()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, null, T0) },
            new[] { Ev(10, 1, T0) },
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, summary.MatchedTxns);
        Assert.Equal(1, summary.UnmatchedTxns);
        Assert.Equal(1, summary.OrphanEvents);
    }

    [Fact]
    public void MatchesBeforeWindowAsWellAsAfter()
    {
        var summary = PosEventLinker.Link(
            new[] { Txn(1, 1, T0) },
            new[] { Ev(10, 1, T0.AddSeconds(-4)) },
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, summary.MatchedTxns);
        Assert.Equal(TimeSpan.FromSeconds(-4), Assert.Single(summary.Pairs).Offset);
    }
}