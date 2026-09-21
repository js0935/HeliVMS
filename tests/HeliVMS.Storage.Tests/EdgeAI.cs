namespace HeliVMS.Storage.Tests;

public class EdgeAIClassifierTests
{
    private static readonly DateTime Zero = new(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

    private static EdgeMetadataEvent Ev(string track, string behavior, double x, double y, double dt,
        double w = 20, double h = 20)
        => new("cam1", Zero.AddSeconds(dt), "person", track, behavior, x, y, w, h);

    private readonly EdgeDirectionClassifier _classifier = new();

    private TrajectoryDirection Dir(params EdgeMetadataEvent[] samples) => _classifier.Classify(samples);

    [Fact]
    public void Classify_LeftToRight()
    {
        var d = Dir(
            Ev("t", "Appear", 0, 50, 0),
            Ev("t", "Move", 6, 50, 1),
            Ev("t", "Move", 12, 50, 2),
            Ev("t", "Move", 18, 50, 3),
            Ev("t", "Move", 24, 50, 4));

        Assert.Equal(TrajectoryDirection.LeftToRight, d);
    }

    [Fact]
    public void Classify_RightToLeft()
    {
        var d = Dir(
            Ev("t", "Appear", 24, 50, 0),
            Ev("t", "Move", 18, 50, 1),
            Ev("t", "Move", 12, 50, 2),
            Ev("t", "Move", 6, 50, 3),
            Ev("t", "Disappear", 0, 50, 4));

        Assert.Equal(TrajectoryDirection.RightToLeft, d);
    }

    [Fact]
    public void Classify_TopToBottom()
    {
        var d = Dir(
            Ev("t", "Appear", 50, 0, 0),
            Ev("t", "Move", 50, 6, 1),
            Ev("t", "Move", 50, 12, 2),
            Ev("t", "Move", 50, 18, 3),
            Ev("t", "Move", 50, 24, 4));

        Assert.Equal(TrajectoryDirection.TopToBottom, d);
    }

    [Fact]
    public void Classify_BottomToTop()
    {
        var d = Dir(
            Ev("t", "Appear", 50, 24, 0),
            Ev("t", "Move", 50, 18, 1),
            Ev("t", "Move", 50, 12, 2),
            Ev("t", "Move", 50, 6, 3),
            Ev("t", "Disappear", 50, 0, 4));

        Assert.Equal(TrajectoryDirection.BottomToTop, d);
    }

    [Fact]
    public void Classify_StationaryUnderThreshold()
    {
        var d = Dir(
            Ev("t", "Appear", 100, 100, 0),
            Ev("t", "Move", 100.2, 100.1, 1),
            Ev("t", "Move", 99.8, 100, 2),
            Ev("t", "Move", 100.1, 99.9, 3),
            Ev("t", "Disappear", 100, 100, 4));

        Assert.Equal(TrajectoryDirection.Stationary, d);
    }

    [Fact]
    public void Classify_ZigzagNetSmall_Stationary()
    {
        var d = Dir(
            Ev("t", "Appear", 0, 50, 0),
            Ev("t", "Move", 5, 50, 1),
            Ev("t", "Move", 0, 50, 2),
            Ev("t", "Move", 5, 50, 3),
            Ev("t", "Disappear", 0, 50, 4));

        Assert.Equal(TrajectoryDirection.Stationary, d);
    }

    [Fact]
    public void Classify_TooFewSamples_Stationary()
    {
        Assert.Equal(TrajectoryDirection.Stationary, Dir(Ev("t", "Appear", 0, 0, 0)));
        Assert.Equal(TrajectoryDirection.Stationary,
            Dir(Ev("t", "Appear", 0, 0, 0), Ev("t", "Disappear", 100, 0, 1)));
    }
}

public class EdgeAITrackerTests
{
    private static readonly DateTime Zero = new(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

    private static EdgeMetadataEvent Ev(string track, string behavior, double x, double dt,
        double w = 20, double h = 20)
        => new("cam1", Zero.AddSeconds(dt), "person", track, behavior, x, 50, w, h);

    private static EdgeAITracker NewTracker(double timeoutSec = 5, int maxTracks = 256)
        => new(TimeSpan.FromSeconds(timeoutSec), maxTracks);

    // 近即時串流語意：utcNow 貼近最新樣本時間（gap≪timeout 則不觸發超時）。
    private static DateTime NowAt(double dt) => Zero.AddSeconds(dt);

    [Fact]
    public void Tracker_DisappearCompletesTrack()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Push(Ev("t", "Appear", 0, 0), NowAt(0)));
        Assert.Empty(tracker.Push(Ev("t", "Move", 10, 1), NowAt(1)));
        var done = tracker.Push(Ev("t", "Disappear", 20, 2), NowAt(2));

        var track = Assert.Single(done);
        Assert.Equal("t", track.TrackId);
        Assert.Equal(TrajectoryDirection.LeftToRight, track.Direction);
        Assert.Equal(Zero.AddSeconds(0), track.FirstUtc);
        Assert.Equal(Zero.AddSeconds(2), track.LastUtc);
    }

    [Fact]
    public void Tracker_TimeoutSplitsIntoTracks()
    {
        var tracker = NewTracker(timeoutSec: 5);

        tracker.Push(Ev("t", "Appear", 0, 0), NowAt(0));
        tracker.Push(Ev("t", "Move", 10, 1), NowAt(1));
        var first = tracker.Push(Ev("t", "Appear", 30, 10), NowAt(10)); // 逾 5s → 收尾第一條

        Assert.Single(first);
        tracker.Push(Ev("t", "Move", 35, 11), NowAt(11));
        var second = tracker.Push(Ev("t", "Disappear", 40, 12), NowAt(12));

        var secondTrack = Assert.Single(second);
        Assert.Equal(Zero.AddSeconds(10), secondTrack.FirstUtc);
        Assert.Equal(TrajectoryDirection.LeftToRight, secondTrack.Direction);
    }

    [Fact]
    public void Tracker_InterleavedTracksStayIsolated()
    {
        var tracker = NewTracker();

        tracker.Push(Ev("a", "Appear", 0, 0), NowAt(0));
        tracker.Push(Ev("b", "Appear", 50, 0), NowAt(0));
        tracker.Push(Ev("a", "Move", 10, 1), NowAt(1));
        tracker.Push(Ev("b", "Move", 40, 1), NowAt(1));
        var doneA = tracker.Push(Ev("a", "Disappear", 20, 2), NowAt(2));
        var doneB = tracker.Push(Ev("b", "Disappear", 30, 2), NowAt(2));

        var a = Assert.Single(doneA);
        var b = Assert.Single(doneB);
        Assert.Equal(TrajectoryDirection.LeftToRight, a.Direction);
        Assert.Equal(TrajectoryDirection.RightToLeft, b.Direction);
    }

    [Fact]
    public void Tracker_OutOfOrderTimestamps_SortedAtFinalize()
    {
        var tracker = NewTracker();

        tracker.Push(Ev("t", "Appear", 0, 3), NowAt(3));  // 送達順逆向（時間戳最晚先來）
        tracker.Push(Ev("t", "Move", 10, 2), NowAt(2));
        var done = tracker.Push(Ev("t", "Disappear", 20, 1), NowAt(1));

        var track = Assert.Single(done);
        Assert.Equal(Zero.AddSeconds(1), track.FirstUtc);
        Assert.Equal(Zero.AddSeconds(3), track.LastUtc);
        Assert.Equal(TrajectoryDirection.RightToLeft, track.Direction);
    }

    [Fact]
    public void Tracker_EvictsOldestBeyondMax()
    {
        var tracker = NewTracker(maxTracks: 2);

        tracker.Push(Ev("a", "Appear", 0, 0), NowAt(0));
        tracker.Push(Ev("b", "Appear", 0, 0), NowAt(0));
        tracker.Push(Ev("c", "Appear", 0, 0), NowAt(0)); // 逾上限 → 逐 "a"

        var flushed = tracker.Flush();
        Assert.Equal(2, flushed.Count);
        Assert.DoesNotContain(flushed, t => t.TrackId == "a");
    }
}