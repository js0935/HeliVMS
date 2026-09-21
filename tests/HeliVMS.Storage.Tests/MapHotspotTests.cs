using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class MapHotspotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private static MapDeviceRecord Pin(int id, int channelId, string type = "camera", bool enabled = true, double x = 0.5, double y = 0.5)
        => new(id, 1, type, channelId, x, y, 0, 90, 3, enabled);

    private static MapEventSample Evt(int channelId, string kind, DateTimeOffset utc)
        => new(channelId, kind, utc);

    [Fact]
    public void Compute_EmptyInputs_ReturnsEmpty()
    {
        var result = MapEventAggregator.Compute(Array.Empty<MapDeviceRecord>(), Array.Empty<MapEventSample>(), Now, Window);

        Assert.Empty(result);
    }

    [Fact]
    public void Compute_EmptySamples_ReturnsZeroCountPin()
    {
        var result = MapEventAggregator.Compute(new[] { Pin(1, 7) }, Array.Empty<MapEventSample>(), Now, Window);

        var hotspot = Assert.Single(result);
        Assert.Equal(1, hotspot.DeviceId);
        Assert.Equal(7, hotspot.ChannelId);
        Assert.Equal(0, hotspot.Count);
        Assert.False(hotspot.Hot);
        Assert.Equal(0, hotspot.Urgency);
        Assert.Empty(hotspot.DistinctKinds);
    }

    [Fact]
    public void Compute_AggregatesByPinAndCounts()
    {
        var pins = new[] { Pin(1, 7), Pin(2, 8) };
        var samples = new[]
        {
            Evt(7, "motion", Now.AddMinutes(-5)),
            Evt(7, "motion", Now.AddMinutes(-4)),
            Evt(8, "tamper", Now.AddMinutes(-3)),
        };

        var result = MapEventAggregator.Compute(pins, samples, Now, Window);

        Assert.Equal(2, result.Count);
        var hot7 = result.Single(h => h.ChannelId == 7);
        Assert.Equal(2, hot7.Count);
        Assert.Equal(new[] { "motion" }, hot7.DistinctKinds);
        Assert.False(hot7.Hot);
        var hot8 = result.Single(h => h.ChannelId == 8);
        Assert.Equal(1, hot8.Count);
    }

    [Fact]
    public void Compute_HotThreshold_RaisesHotAndUrgency()
    {
        var samples = new[]
        {
            Evt(7, "motion", Now.AddMinutes(-10)),
            Evt(7, "motion", Now.AddMinutes(-9)),
            Evt(7, "ai", Now.AddMinutes(-8)),
        };

        var result = MapEventAggregator.Compute(new[] { Pin(1, 7) }, samples, Now, Window);

        var hotspot = Assert.Single(result);
        Assert.True(hotspot.Hot);
        Assert.Equal(60, hotspot.Urgency);
    }

    [Fact]
    public void Compute_RecentEvent_GrantsRecencyBonus()
    {
        var samples = new[]
        {
            Evt(7, "motion", Now.AddSeconds(-10)),
            Evt(7, "motion", Now.AddSeconds(-9)),
            Evt(7, "tamper", Now.AddSeconds(-8)),
        };

        var result = MapEventAggregator.Compute(new[] { Pin(1, 7) }, samples, Now, Window);

        var hotspot = Assert.Single(result);
        Assert.True(hotspot.Hot);
        Assert.Equal(75, hotspot.Urgency);
    }

    [Fact]
    public void Compute_Urgency_CapsAt100()
    {
        var samples = new[]
        {
            Evt(7, "motion", Now.AddSeconds(-5)),
            Evt(7, "motion", Now.AddSeconds(-4)),
            Evt(7, "tamper", Now.AddSeconds(-3)),
            Evt(7, "ai", Now.AddSeconds(-2)),
            Evt(7, "offline", Now.AddSeconds(-1)),
            Evt(7, "motion", Now.AddSeconds(-20)),
        };

        var result = MapEventAggregator.Compute(new[] { Pin(1, 7) }, samples, Now, Window);

        Assert.Equal(100, Assert.Single(result).Urgency);
    }

    [Fact]
    public void Compute_IgnoresFutureAndBeforeWindowSamples()
    {
        var pins = new[] { Pin(1, 7) };
        var samples = new[]
        {
            Evt(7, "future", Now.AddMinutes(1)),
            Evt(7, "old", Now.Subtract(Window).AddMinutes(-1)),
            Evt(7, "exactEdge", Now.Subtract(Window)),
            Evt(7, "nowEdge", Now),
        };

        var result = MapEventAggregator.Compute(pins, samples, Now, Window);

        var hotspot = Assert.Single(result);
        Assert.Equal(2, hotspot.Count);
        Assert.Equal(new[] { "exactEdge", "nowEdge" }, hotspot.DistinctKinds);
    }

    [Fact]
    public void Compute_DisabledPins_AreSkipped()
    {
        var pins = new[] { Pin(1, 7, enabled: false), Pin(2, 8) };
        var samples = new[]
        {
            Evt(7, "motion", Now.AddMinutes(-5)),
            Evt(7, "motion", Now.AddMinutes(-4)),
            Evt(8, "tamper", Now.AddMinutes(-3)),
        };

        var result = MapEventAggregator.Compute(pins, samples, Now, Window);

        Assert.Single(result);
        Assert.Equal(8, result[0].ChannelId);
    }

    [Fact]
    public void Compute_UnpinnedChannelEvents_AreDropped()
    {
        var samples = new[]
        {
            Evt(999, "motion", Now.AddMinutes(-5)),
            Evt(7, "tamper", Now.AddMinutes(-3)),
        };

        var result = MapEventAggregator.Compute(new[] { Pin(1, 7) }, samples, Now, Window);

        Assert.Single(result);
        Assert.Equal(7, result[0].ChannelId);
        Assert.Equal(1, result[0].Count);
    }

    [Fact]
    public void Compute_CameraPin_PreferredOverIoPin_SameChannel()
    {
        var pins = new[] { Pin(1, 7, type: "io"), Pin(2, 7, type: "camera") };
        var samples = new[]
        {
            Evt(7, "motion", Now.AddMinutes(-5)),
            Evt(7, "tamper", Now.AddMinutes(-3)),
        };

        var result = MapEventAggregator.Compute(pins, samples, Now, Window);

        var hotspot = Assert.Single(result);
        Assert.Equal(2, hotspot.DeviceId);
    }

    [Fact]
    public void Compute_SortByCountDescThenLastUtcDesc()
    {
        var pins = new[] { Pin(1, 7), Pin(2, 8), Pin(3, 9) };
        var samples = new[]
        {
            Evt(7, "motion", Now.AddMinutes(-20)),
            Evt(7, "motion", Now.AddMinutes(-19)),
            Evt(8, "tamper", Now.AddMinutes(-3)),
            Evt(9, "motion", Now.AddMinutes(-2)),
        };

        var result = MapEventAggregator.Compute(pins, samples, Now, Window);

        Assert.Equal(7, result[0].ChannelId);
        Assert.Equal(9, result[1].ChannelId);
        Assert.Equal(8, result[2].ChannelId);
    }

    [Fact]
    public void Compute_ThresholdBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MapEventAggregator.Compute(new[] { Pin(1, 7) }, Array.Empty<MapEventSample>(), Now, Window, minHotThreshold: 0));
    }

    [Fact]
    public void Compute_LastUtc_TracksMostRecent()
    {
        var samples = new[]
        {
            Evt(7, "motion", Now.AddMinutes(-10)),
            Evt(7, "ai", Now.AddMinutes(-2)),
        };

        var result = MapEventAggregator.Compute(new[] { Pin(1, 7) }, samples, Now, Window);

        Assert.Equal(Now.AddMinutes(-2), Assert.Single(result).LastUtc);
    }
}