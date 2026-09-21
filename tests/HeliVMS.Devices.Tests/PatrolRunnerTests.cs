using HeliVMS.Devices.Ptz;

namespace HeliVMS.Devices.Tests;

public class PatrolRunnerTests
{
    private static readonly DateTime D00 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private sealed class Clock
    {
        public DateTime Now = D00.AddHours(8);
        public DateTime Utc() => Now;
    }

    private sealed class RecordingExecutor : IPtzExecutor
    {
        public List<string> Presets = new();
        public Exception? Throw { get; set; }

        public Task GotoPresetAsync(string presetName, CancellationToken ct = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Presets.Add(presetName);
            return Task.CompletedTask;
        }
    }

    private static PatrolPlan Plan(
        IReadOnlyList<PatrolStep> steps,
        bool enabled = true,
        TimeSpan start = default,
        TimeSpan end = default)
        => new("p", enabled, start, end, steps);

    private static (PatrolRunner Runner, RecordingExecutor Exec, Clock Clock) Setup(
        IReadOnlyList<PatrolStep> steps,
        bool enabled = true,
        TimeSpan? start = null,
        TimeSpan? end = null)
    {
        var clock = new Clock();
        var controller = new PatrolController(
            Plan(steps, enabled, start ?? new TimeSpan(8, 0, 0), end ?? new TimeSpan(18, 0, 0)),
            clock.Utc);
        var exec = new RecordingExecutor();
        return (new PatrolRunner(controller, exec), exec, clock);
    }

    [Fact]
    public async Task TickOnce_FirstAction_CallsExecutorWithFirstPreset()
    {
        var (r, e, _) = Setup(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) });

        var action = await r.TickOnceAsync();

        Assert.Equal(PatrolActionKind.MoveTo, action.Kind);
        Assert.Equal(new[] { "A" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_ClockAdvance_CallsPresetsInOrder()
    {
        var (r, e, clock) = Setup(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min), new PatrolStep("C", Min) });

        await r.TickOnceAsync();
        clock.Now += Min;
        await r.TickOnceAsync();
        clock.Now += Min;
        await r.TickOnceAsync();

        Assert.Equal(new[] { "A", "B", "C" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_DuringDwell_DoesNotCallExecutor()
    {
        var (r, e, clock) = Setup(new[] { new PatrolStep("A", Min) });

        await r.TickOnceAsync();
        clock.Now += TimeSpan.FromSeconds(10);
        var action = await r.TickOnceAsync();

        Assert.Equal(PatrolActionKind.Wait, action.Kind);
        Assert.Equal(new[] { "A" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_Disabled_DoesNotCallExecutor()
    {
        var (r, e, _) = Setup(new[] { new PatrolStep("A", Min) }, enabled: false);

        var action = await r.TickOnceAsync();

        Assert.Equal(PatrolActionKind.Idle, action.Kind);
        Assert.Empty(e.Presets);
    }

    [Fact]
    public async Task TickOnce_HoldBlocksThenResumes()
    {
        var (r, e, clock) = Setup(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) });

        await r.TickOnceAsync(); // A
        r.Controller.HoldFor(TimeSpan.FromMinutes(5));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(PatrolActionKind.Idle, (await r.TickOnceAsync()).Kind); // hold 中
        Assert.Equal(new[] { "A" }, e.Presets);

        r.Controller.ReleaseHold();
        var wait = await r.TickOnceAsync(); // 8:02，anchor 平移至 8:02（hold 2m 凍結）→ A 剩 1m
        Assert.Equal(PatrolActionKind.Wait, wait.Kind);
        Assert.Equal(TimeSpan.FromMinutes(1), wait.Remaining);
        Assert.Equal(new[] { "A" }, e.Presets);

        clock.Now += TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)); // 8:03:01，A 期滿
        var a2 = await r.TickOnceAsync();
        Assert.Equal(PatrolActionKind.MoveTo, a2.Kind);
        Assert.Equal(new[] { "A", "B" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_WrapsLastToFirst_CallsFirstAgain()
    {
        var (r, e, clock) = Setup(new[] { new PatrolStep("A", Min), new PatrolStep("B", Min) });

        await r.TickOnceAsync(); // A
        clock.Now += Min;
        await r.TickOnceAsync(); // B
        clock.Now += Min;
        var wrap = await r.TickOnceAsync(); // 回 A

        Assert.Equal("A", wrap.PresetName);
        Assert.Equal(new[] { "A", "B", "A" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_ExecutorThrows_Propagates()
    {
        var (r, e, _) = Setup(new[] { new PatrolStep("A", Min) });
        e.Throw = new InvalidOperationException("offline");

        await Assert.ThrowsAsync<InvalidOperationException>(() => r.TickOnceAsync());
    }

    [Fact]
    public async Task TickOnce_RepeatedCallsSameStep_CallsExecutorOnce()
    {
        var (r, e, clock) = Setup(new[] { new PatrolStep("A", Min) });

        await r.TickOnceAsync();
        await r.TickOnceAsync();
        clock.Now += TimeSpan.FromSeconds(30);
        await r.TickOnceAsync();

        Assert.Equal(new[] { "A" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_ZeroDwell_CallsConsecutivePresets()
    {
        var (r, e, clock) = Setup(new[]
        {
            new PatrolStep("A", TimeSpan.Zero),
            new PatrolStep("B", TimeSpan.Zero),
        });

        Assert.Equal("A", (await r.TickOnceAsync()).PresetName);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.Equal("B", (await r.TickOnceAsync()).PresetName);

        Assert.Equal(new[] { "A", "B" }, e.Presets);
    }

    [Fact]
    public async Task TickOnce_AfterWindowEndThenReenter_StopsThenRestarts()
    {
        var (r, e, clock) = Setup(new[] { new PatrolStep("A", Min) },
            start: new TimeSpan(8, 0, 0), end: new TimeSpan(9, 0, 0));

        await r.TickOnceAsync(); // A（8:00）
        clock.Now = D00.AddHours(9).AddMinutes(1);
        Assert.Equal(PatrolActionKind.Idle, (await r.TickOnceAsync()).Kind); // 時段過
        Assert.Equal(new[] { "A" }, e.Presets);

        clock.Now = D00.AddDays(1).AddHours(8);
        Assert.Equal(PatrolActionKind.MoveTo, (await r.TickOnceAsync()).Kind); // 隔日重入
        Assert.Equal(new[] { "A", "A" }, e.Presets);
    }
}