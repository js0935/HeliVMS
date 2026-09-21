using HeliVMS.Recording;

namespace HeliVMS.Storage.Tests;

public class BitrateGovernorTests
{
    private readonly BitrateGovernor _gov = new();

    [Fact]
    public void Adapt_ZeroBudget_AllTargetsZero()
    {
        var result = _gov.Adapt(0, new[]
        {
            new GoChannel(1, 1_000_000, 5),
            new GoChannel(2, 800_000, 1, InEvent: true),
        });

        Assert.Equal(0, result.AllocatedBps);
        Assert.All(result.Targets, t => Assert.Equal(0, t.TargetBps));
    }

    [Fact]
    public void Adapt_EqualWeights_SplitsEvenlyAndSumsToBudget()
    {
        var result = _gov.Adapt(3_000_000, new[]
        {
            new GoChannel(1, 1_000_000, 3),
            new GoChannel(2, 1_000_000, 3),
            new GoChannel(3, 1_000_000, 3),
        });

        Assert.Equal(3_000_000, result.AllocatedBps);
        Assert.Equal(3_000_000, result.Targets.Sum(t => t.TargetBps));
        Assert.All(result.Targets, t => Assert.Equal(1_000_000, t.TargetBps));
    }

    [Fact]
    public void Adapt_EventChannel_GetsFourTimesNonEvent()
    {
        var result = _gov.Adapt(1_000_000, new[]
        {
            new GoChannel(1, 500_000, 3),
            new GoChannel(2, 500_000, 3, InEvent: true),
        });

        var none = result.Targets.Single(t => t.ChannelId == 1).TargetBps;
        var ev = result.Targets.Single(t => t.ChannelId == 2).TargetBps;
        Assert.Equal(1_000_000, none + ev);
        Assert.Equal(200_000, none);
        Assert.Equal(800_000, ev);
    }

    [Fact]
    public void Adapt_HigherPriority_GetsMore()
    {
        var result = _gov.Adapt(6_000_000, new[]
        {
            new GoChannel(1, 1_000_000, 1),
            new GoChannel(2, 1_000_000, 5),
        });

        var low = result.Targets.Single(t => t.ChannelId == 1).TargetBps;
        var high = result.Targets.Single(t => t.ChannelId == 2).TargetBps;
        Assert.True(high > low);
        Assert.Equal(5, high / low);
    }

    [Fact]
    public void Adapt_ManyChannels_SumEqualsBudgetNoOvershoot()
    {
        var channels = Enumerable.Range(1, 8).Select(i => new GoChannel(
            i,
            1_000_000,
            (i % 5) + 1,
            i == 3)).ToArray();

        var result = _gov.Adapt(4_321_987, channels);

        Assert.Equal(4_321_987, result.Targets.Sum(t => t.TargetBps));
        Assert.Equal(4_321_987, result.AllocatedBps);
        Assert.DoesNotContain(result.Targets, t => t.TargetBps < 0);
    }

    [Fact]
    public void Adapt_SamePriority_EventWins()
    {
        var result = _gov.Adapt(1_000_000, new[]
        {
            new GoChannel(1, 500_000, 2),
            new GoChannel(2, 500_000, 2, InEvent: true),
        });

        var ev = result.Targets.Single(t => t.ChannelId == 2).TargetBps;
        var none = result.Targets.Single(t => t.ChannelId == 1).TargetBps;
        Assert.True(ev > none);
    }

    [Fact]
    public void Adapt_AllCurrentZero_RatioZero()
    {
        var result = _gov.Adapt(1_000_000, new[]
        {
            new GoChannel(1, 0, 3),
            new GoChannel(2, 0, 3),
        });

        Assert.Equal(0, result.ThrottleRatio);
        Assert.Equal(1_000_000, result.AllocatedBps);
    }

    [Fact]
    public void Adapt_NegativeCurrent_ClampedToZero()
    {
        var result = _gov.Adapt(1_000_000, new[] { new GoChannel(1, -50, 3) });

        Assert.Equal(0, result.ThrottleRatio);
        Assert.Equal(1_000_000, result.Targets.Single().TargetBps);
    }

    [Fact]
    public void Adapt_PriorityOutOfRange_Clamped()
    {
        var result = _gov.Adapt(1_000_000, new[]
        {
            new GoChannel(1, 100_000, 99),
            new GoChannel(2, 100_000, -3),
        });

        var a = result.Targets.Single(t => t.ChannelId == 1).TargetBps;
        var b = result.Targets.Single(t => t.ChannelId == 2).TargetBps;
        Assert.Equal(1_000_000, a + b);
        Assert.True(a > b);
    }

    [Fact]
    public void Adapt_EmptyChannels_ReturnsEmpty()
    {
        var result = _gov.Adapt(1_000_000, Array.Empty<GoChannel>());

        Assert.Empty(result.Targets);
        Assert.Equal(0, result.AllocatedBps);
    }

    [Fact]
    public void Adapt_NonDivisibleBudget_LargestRemainderSumsExactly()
    {
        var result = _gov.Adapt(10, new[]
        {
            new GoChannel(1, 5, 2),
            new GoChannel(2, 5, 2),
            new GoChannel(3, 5, 2),
        });

        Assert.Equal(10, result.Targets.Sum(t => t.TargetBps));
        Assert.All(result.Targets, t => Assert.InRange(t.TargetBps, 3, 4));
    }

    [Fact]
    public void Adapt_Ratio_BudgetHalfOfCurrent()
    {
        var result = _gov.Adapt(1_000_000, new[]
        {
            new GoChannel(1, 2_000_000, 3),
        });

        Assert.Equal(0.5, result.ThrottleRatio, 3);
        Assert.Equal(1_000_000, result.Targets.Single().TargetBps);
    }
}