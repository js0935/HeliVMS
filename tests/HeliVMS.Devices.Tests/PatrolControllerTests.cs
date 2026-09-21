using HeliVMS.Devices.Ptz;

namespace HeliVMS.Devices.Tests;

public class PatrolControllerTests
{
    private static readonly DateTime D00 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private sealed class Clock
    {
        public DateTime Now = D00.AddHours(8);
        public DateTime Utc() => Now;
    }

    private static PatrolPlan Plan(
        IReadOnlyList<PatrolStep> steps,
        bool enabled = true,
        TimeSpan start = default,
        TimeSpan end = default,
        string name = "p")
        => new(name, enabled, start, end, steps);

    [Fact]
    public void Tick_Disabled_Idle()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) },
            enabled: false, start: new TimeSpan(0), end: new TimeSpan(23, 59, 59)), clock.Utc);

        Assert.Equal(PatrolActionKind.Idle, c.Tick().Kind);
    }

    [Fact]
    public void Tick_BeforeWindow_Idle()
    {
        var clock = new Clock { Now = D00.AddHours(7) };
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal(PatrolActionKind.Idle, c.Tick().Kind);
    }

    [Fact]
    public void Tick_AtWindowStart_MovesToFirst()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        var a = c.Tick();
        Assert.Equal(PatrolActionKind.MoveTo, a.Kind);
        Assert.Equal("A", a.PresetName);
    }

    [Fact]
    public void Tick_AfterDwell_AdvancesToNext()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        c.Tick();
        clock.Now = D00.AddHours(8).AddMinutes(1);
        var a = c.Tick();

        Assert.Equal(PatrolActionKind.MoveTo, a.Kind);
        Assert.Equal("B", a.PresetName);
    }

    [Fact]
    public void Tick_LastWrapsToFirst()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal("A", c.Tick().PresetName);
        clock.Now += Min;
        Assert.Equal("B", c.Tick().PresetName);
        clock.Now += Min;
        var wrap = c.Tick();

        Assert.Equal(PatrolActionKind.MoveTo, wrap.Kind);
        Assert.Equal("A", wrap.PresetName);
    }

    [Fact]
    public void Tick_RepeatedDuringDwell_ReturnsWaitNotDuplicateMove()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        c.Tick();
        clock.Now += TimeSpan.FromSeconds(10);
        var a = c.Tick();

        Assert.Equal(PatrolActionKind.Wait, a.Kind);
        Assert.Null(a.PresetName);
        Assert.True(a.Remaining > TimeSpan.Zero);
    }

    [Fact]
    public void Tick_AfterWindowEnd_Idles()
    {
        var clock = new Clock { Now = D00.AddHours(9) };
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(9, 0, 0)), clock.Utc);

        c.Tick();
        clock.Now = D00.AddHours(9).AddMinutes(1);
        var a = c.Tick();

        Assert.Equal(PatrolActionKind.Idle, a.Kind);
    }

    [Fact]
    public void Tick_WindowReenterNextDay_ResetsToFirstStep()
    {
        var clock = new Clock { Now = D00.AddHours(8) };
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal("A", c.Tick().PresetName);
        clock.Now = D00.AddHours(19);
        Assert.Equal(PatrolActionKind.Idle, c.Tick().Kind);

        clock.Now = D00.AddDays(1).AddHours(8);
        var a = c.Tick();

        Assert.Equal(PatrolActionKind.MoveTo, a.Kind);
        Assert.Equal("A", a.PresetName);
    }

    [Fact]
    public void Tick_HoldFreezes_DwellNotConsumedDuringHold()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", TimeSpan.FromMinutes(1)) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal(PatrolActionKind.MoveTo, c.Tick().Kind); // A 於 8:00（anchor）
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(TimeSpan.FromSeconds(40), c.Tick().Remaining); // 基準無 hold 剩 40s

        c.HoldFor(TimeSpan.FromSeconds(30)); // hold 至 8:00:50
        clock.Now += TimeSpan.FromSeconds(30); // 8:00:50，hold 期滿
        c.ReleaseHold(); // anchor 平移 30s：8:00:00+30s=8:00:30 → 剩 60s? 否：dwellUntil=8:01:30
        var a = c.Tick(); // now 8:00:50 < 8:01:30 → Wait 剩 40s（等於 hold 前剩餘，證明凍結）
        Assert.Equal(PatrolActionKind.Wait, a.Kind);
        Assert.Equal(TimeSpan.FromSeconds(40), a.Remaining);
    }

    [Fact]
    public void Tick_EmptySteps_Idle()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(Array.Empty<PatrolStep>(),
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal(PatrolActionKind.Idle, c.Tick().Kind);
    }

    [Fact]
    public void Tick_ZeroDwell_AdvancesEveryTick()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", TimeSpan.Zero), new PatrolStep("B", TimeSpan.Zero) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal("A", c.Tick().PresetName);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.Equal("B", c.Tick().PresetName);
    }

    [Fact]
    public void Tick_LongClockJump_AccumulatesAcrossSteps()
    {
        var clock = new Clock();
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min), new PatrolStep("C", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(18, 0, 0)), clock.Utc);

        Assert.Equal("A", c.Tick().PresetName); // 8:00 A
        clock.Now += TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(30)); // 8:02:30（A 已過）
        Assert.Equal("B", c.Tick().PresetName); // 進 B，anchor 8:02:30
        Assert.Equal(TimeSpan.FromMinutes(1), c.Tick().Remaining); // B 停留中剩 1m

        clock.Now += TimeSpan.FromMinutes(2); // 8:04:30，B 已滿
        var a = c.Tick(); // 進 C，anchor 8:04:30
        Assert.Equal(PatrolActionKind.MoveTo, a.Kind);
        Assert.Equal("C", a.PresetName);
        Assert.Equal(TimeSpan.FromMinutes(1), a.Remaining);
    }

    [Fact]
    public void Tick_AfterHoursWindow_CrossMidnight()
    {
        var clock = new Clock { Now = D00.AddHours(23) };
        // 23:00~01:00 跨午夜
        var c = new PatrolController(Plan(new[] { new PatrolStep("A", Min) },
            start: new TimeSpan(23, 0, 0), end: new TimeSpan(1, 0, 0)), clock.Utc);

        var a = c.Tick();
        Assert.Equal(PatrolActionKind.MoveTo, a.Kind);
        Assert.Equal("A", a.PresetName);

        clock.Now = D00.AddHours(1).AddMinutes(30); // 01:30 > End → Idle
        Assert.Equal(PatrolActionKind.Idle, c.Tick().Kind);
    }
}