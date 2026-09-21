namespace HeliVMS.Storage.Tests;

public class EdgeRecoveryPlannerTests
{
    private static DateTime T(int minute) =>
        new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minute);

    private static EdgeSegment Seg(int startMin, int endMin, string source = "cam1")
        => new(source, T(startMin), T(endMin));

    private readonly EdgeRecoveryPlanner _planner = new();

    [Fact]
    public void Plan_EmptyManifest_ReturnsEmpty()
    {
        var plan = _planner.Plan(Array.Empty<EdgeSegment>(), Array.Empty<EdgeSegment>(), T(100));

        Assert.Empty(plan.Tasks);
        Assert.Equal(0, plan.SkippedStaleSegments);
    }

    [Fact]
    public void Plan_NoLocalCoverage_SingleFullFetch()
    {
        var plan = _planner.Plan(new[] { Seg(0, 10) }, Array.Empty<EdgeSegment>(), T(100));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal(T(0), task.FetchStartUtc);
        Assert.Equal(T(10), task.FetchEndUtc);
    }

    [Fact]
    public void Plan_LocalCoversMiddle_TwoFetches()
    {
        var device = new[] { Seg(0, 30) };
        var local = new[] { Seg(10, 20) };

        var plan = _planner.Plan(device, local, T(100));

        Assert.Equal(2, plan.Tasks.Count);
        Assert.Equal((T(0), T(10)), (plan.Tasks[0].FetchStartUtc, plan.Tasks[0].FetchEndUtc));
        Assert.Equal((T(20), T(30)), (plan.Tasks[1].FetchStartUtc, plan.Tasks[1].FetchEndUtc));
    }

    [Fact]
    public void Plan_LocalFullyCovers_Empty()
    {
        var plan = _planner.Plan(new[] { Seg(0, 30) }, new[] { Seg(0, 30) }, T(100));

        Assert.Empty(plan.Tasks);
    }

    [Fact]
    public void Plan_GapBeyondThreshold_SplitsIntoTwo()
    {
        var planner = new EdgeRecoveryPlanner(mergeThreshold: TimeSpan.FromSeconds(60));
        // 間隙 60 分鐘 > 60s
        var plan = planner.Plan(new[] { Seg(0, 10), Seg(70, 80) }, Array.Empty<EdgeSegment>(), T(100));

        Assert.Equal(2, plan.Tasks.Count);
        Assert.Equal(0, plan.MergedGapCount);
    }

    [Fact]
    public void Plan_GapWithinThreshold_MergesSingleSpanning()
    {
        var planner = new EdgeRecoveryPlanner(mergeThreshold: TimeSpan.FromHours(1));
        var plan = planner.Plan(new[] { Seg(0, 10), Seg(40, 50) }, Array.Empty<EdgeSegment>(), T(100));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal((T(0), T(50)), (task.FetchStartUtc, task.FetchEndUtc));
        Assert.Equal(1, plan.MergedGapCount);
    }

    [Fact]
    public void Plan_OverlappingDeviceSegments_NormalizedAsOne()
    {
        var plan = _planner.Plan(new[] { Seg(0, 20), Seg(10, 30) }, Array.Empty<EdgeSegment>(), T(100));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal((T(0), T(30)), (task.FetchStartUtc, task.FetchEndUtc));
    }

    [Fact]
    public void Plan_AllStaleSkipped_ReturnsEmptyWithCount()
    {
        var planner = new EdgeRecoveryPlanner(staleTtl: TimeSpan.FromHours(24));
        var plan = planner.Plan(new[] { Seg(0, 10), Seg(20, 30) }, Array.Empty<EdgeSegment>(), T(2000));

        Assert.Empty(plan.Tasks);
        Assert.Equal(2, plan.SkippedStaleSegments);
    }

    [Fact]
    public void Plan_PartiallyStale_OnlyFreshFetched()
    {
        var planner = new EdgeRecoveryPlanner(staleTtl: TimeSpan.FromHours(24));
        // T(0) 過期；T(600) 新鮮（now=T(2000)，staleFloor=T(560)）
        var plan = planner.Plan(new[] { Seg(0, 10), Seg(600, 610) }, Array.Empty<EdgeSegment>(), T(2000));

        Assert.Equal(1, plan.SkippedStaleSegments);
        var task = Assert.Single(plan.Tasks);
        Assert.Equal((T(600), T(610)), (task.FetchStartUtc, task.FetchEndUtc));
    }

    [Fact]
    public void Plan_MaxFetchWindow_ChunksLongSpan()
    {
        var planner = new EdgeRecoveryPlanner(maxFetchWindow: TimeSpan.FromMinutes(90));
        var plan = planner.Plan(new[] { Seg(0, 200) }, Array.Empty<EdgeSegment>(), T(100));

        Assert.Equal(3, plan.Tasks.Count); // 90+90+20
        Assert.Equal((T(0), T(90)), (plan.Tasks[0].FetchStartUtc, plan.Tasks[0].FetchEndUtc));
        Assert.Equal((T(90), T(180)), (plan.Tasks[1].FetchStartUtc, plan.Tasks[1].FetchEndUtc));
        Assert.Equal((T(180), T(200)), (plan.Tasks[2].FetchStartUtc, plan.Tasks[2].FetchEndUtc));
    }

    [Fact]
    public void Plan_InvalidSegment_Throws()
    {
        var bad = new EdgeSegment("cam1", T(10), T(10));
        Assert.Throws<ArgumentException>(() => _planner.Plan(new[] { bad }, Array.Empty<EdgeSegment>(), T(100)));
        Assert.Throws<ArgumentException>(() =>
            _planner.Plan(Array.Empty<EdgeSegment>(), new[] { bad }, T(100)));
    }

    [Fact]
    public void Plan_LocalTouchingStart_NotCovered_Fetched()
    {
        // 本機涵蓋至 [0,10)，設備 [10,20) 起點與其相接 → 設備窗未被涵蓋 → 需補
        var plan = _planner.Plan(new[] { Seg(10, 20) }, new[] { Seg(0, 10) }, T(100));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal((T(10), T(20)), (task.FetchStartUtc, task.FetchEndUtc));
    }

    [Fact]
    public void Plan_CtorNegativeThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EdgeRecoveryPlanner(mergeThreshold: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EdgeRecoveryPlanner(staleTtl: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EdgeRecoveryPlanner(maxFetchWindow: TimeSpan.Zero));
    }
}