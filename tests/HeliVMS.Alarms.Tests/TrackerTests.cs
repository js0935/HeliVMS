namespace HeliVMS.Alarms.Tests;

/// <summary>M61（§5.7）：單鏡目標追蹤原語——匈牙利 IoU 指派、track_id 穩定、生命周期。</summary>
public class TrackerTests
{
    private static Detection At(float x, float y, float w = 0.1f, float h = 0.1f, string cls = "person") =>
        new(cls, 0.9f, x, y, w, h);

    private static TrackState? Find(IEnumerable<TrackState> tracks, int id) =>
        tracks.FirstOrDefault(t => t.TrackId == id);

    [Fact]
    public void SameObjectAcrossFrames_KeepsStableTrackId()
    {
        var tracker = new Tracker();
        var f1 = tracker.Update(new[] { At(0.5f, 0.5f) });
        int id1 = Assert.Single(f1).TrackId;

        var f2 = tracker.Update(new[] { At(0.52f, 0.5f) });
        Assert.Equal(id1, Assert.Single(f2).TrackId);

        var f3 = tracker.Update(new[] { At(0.55f, 0.5f) });
        Assert.Equal(id1, Assert.Single(f3).TrackId);
    }

    [Fact]
    public void MultipleObjects_EachRetainsOwnId()
    {
        var tracker = new Tracker();
        var f1 = tracker.Update(new[] { At(0.3f, 0.3f), At(0.7f, 0.7f) });
        int left = f1.Single(t => t.X < 0.5).TrackId;
        int right = f1.Single(t => t.X > 0.5).TrackId;

        var f2 = tracker.Update(new[] { At(0.31f, 0.3f), At(0.69f, 0.7f) });
        Assert.Equal(left, f2.Single(t => t.X < 0.5).TrackId);
        Assert.Equal(right, f2.Single(t => t.X > 0.5).TrackId);
        Assert.True(f2.All(t => t.Misses == 0));
    }

    [Fact]
    public void NewObjectBeyondIoUThreshold_GetsNewId_AndOldKeepsId()
    {
        var tracker = new Tracker();
        var f1 = tracker.Update(new[] { At(0.5f, 0.5f) });
        int first = Assert.Single(f1).TrackId;

        var f2 = tracker.Update(new[] { At(0.5f, 0.5f), At(0.25f, 0.25f) });
        Assert.Equal(first, f2.Single(t => t.X > 0.4).TrackId);
        int second = f2.Single(t => t.X < 0.4).TrackId;
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MissingObject_ExceedsMaxMisses_IsPruned_AndIdNotReused()
    {
        var tracker = new Tracker(maxMisses: 2);
        var f1 = tracker.Update(new[] { At(0.5f, 0.5f) });
        int id = Assert.Single(f1).TrackId;

        var f2 = tracker.Update(Array.Empty<Detection>());
        Assert.Equal(1, Assert.Single(f2).Misses);

        f2 = tracker.Update(Array.Empty<Detection>());
        Assert.Equal(2, Assert.Single(f2).Misses);

        f2 = tracker.Update(Array.Empty<Detection>());
        Assert.Empty(f2);

        var f3 = tracker.Update(new[] { At(0.5f, 0.5f) });
        Assert.NotEqual(id, Assert.Single(f3).TrackId);
    }

    [Fact]
    public void EmulatedPosition_BlendsTowardMeasurement()
    {
        var tracker = new Tracker(smoothAlpha: 0.3);
        var f1 = tracker.Update(new[] { At(0.2f, 0.5f) });
        Assert.Equal(0.2f, Assert.Single(f1).X, 3);

        var f2 = tracker.Update(new[] { At(0.24f, 0.5f) });
        var t = Assert.Single(f2);
        Assert.Equal(0.2f + 0.3f * (0.24f - 0.2f), t.X, 3);
    }

    [Fact]
    public void ClassMismatch_DoesNotMatch()
    {
        var tracker = new Tracker();
        var f1 = tracker.Update(new[] { At(0.5f, 0.5f, cls: "person") });
        Assert.Equal(1, Assert.Single(f1).TrackId);

        var f2 = tracker.Update(new[] { At(0.5f, 0.5f, cls: "car") });
        Assert.Equal(2, f2.Count);
        var car = f2.Single(t => t.Class == "car");
        var person = f2.Single(t => t.Class == "person");
        Assert.Equal(2, car.TrackId);
        Assert.Equal(1, person.TrackId);
        Assert.Equal(1, person.Misses);
    }

    [Fact]
    public void Teleport_FarAway_CreatesNewId_OldTrackMisses()
    {
        var tracker = new Tracker();
        var f1 = tracker.Update(new[] { At(0.1f, 0.1f) });
        int id = Assert.Single(f1).TrackId;

        var f2 = tracker.Update(new[] { At(0.9f, 0.9f) });
        Assert.Equal(2, f2.Count);
        var moved = f2.Single(t => t.X > 0.8);
        Assert.Equal(0.9f, moved.X, 3);
        Assert.NotEqual(id, moved.TrackId);
        var old = f2.Single(t => t.X < 0.5);
        Assert.Equal(id, old.TrackId);
        Assert.Equal(1, old.Misses);
    }

    [Fact]
    public void Hungarian_AssignmentIgnoresDetectionOrder()
    {
        var tracker = new Tracker();
        var f1 = tracker.Update(new[] { At(0.3f, 0.3f), At(0.7f, 0.7f) });
        int left = f1.Single(t => t.X < 0.5).TrackId;
        int right = f1.Single(t => t.X > 0.5).TrackId;

        var f2 = tracker.Update(new[] { At(0.69f, 0.7f), At(0.31f, 0.3f) });
        var left2 = f2.Single(t => t.X < 0.5);
        var right2 = f2.Single(t => t.X > 0.5);
        Assert.Equal(left, left2.TrackId);
        Assert.Equal(right, right2.TrackId);
        Assert.Equal(0.3f + 0.3f * (0.31f - 0.3f), left2.X, 3);
        Assert.Equal(0.7f + 0.3f * (0.69f - 0.7f), right2.X, 3);
    }

    [Fact]
    public void Reset_RestartsIdsFromOne()
    {
        var tracker = new Tracker();
        tracker.Update(new[] { At(0.5f, 0.5f) });
        tracker.Reset();
        Assert.Empty(tracker.Update(Array.Empty<Detection>()));

        var f = tracker.Update(new[] { At(0.5f, 0.5f) });
        Assert.Equal(1, Assert.Single(f).TrackId);
    }

    [Fact]
    public void MissingIncrementsMisses_MatchClearsThem()
    {
        var tracker = new Tracker(maxMisses: 5);
        tracker.Update(new[] { At(0.5f, 0.5f) });

        int first = tracker.Update(Array.Empty<Detection>()).Single(t => t.X > 0.4).Misses;
        Assert.Equal(1, first);

        var f2 = tracker.Update(new[] { At(0.51f, 0.5f) });
        Assert.Equal(0, Assert.Single(f2).Misses);
    }

    [Fact]
    public void DetectionOutput_CarriesTrackId()
    {
        var tracker = new Tracker();
        tracker.Update(new[] { At(0.5f, 0.5f) });
        var state = Assert.Single(tracker.Update(new[] { At(0.5f, 0.5f) }));
        Assert.True(state.TrackId >= 1);
    }
}