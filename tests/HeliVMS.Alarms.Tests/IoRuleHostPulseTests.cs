using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 繼電器「輸出秒數」的行為契約：閉合 N 秒後自動放開（賦歸），再觸發時先取消上一次的計時。
/// </summary>
/// <remarks>
/// 沒有這一段，繼電器閉合之後就沒人記得去放開——現場回報「警報響了停不下來」。
/// 引擎只決定邏輯狀態；計時與放開由宿主（IoRuleHost 的 ScheduleOutputReset）負責。
/// </remarks>
public sealed class IoRuleHostPulseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly List<(long PortId, bool On)> _writes = [];

    public IoRuleHostPulseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-iopulse-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _ports = new IoPortRepository(_store);
        _rules = new IoRuleRepository(_store);
        _events = new AlarmEventRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private (IoRuleHost Host, long DiPort, long DoPort) Make(int outputSeconds, TimeProvider? clock = null)
    {
        var di = _ports.Add(1, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        var doPort = _ports.Add(1, IoPortKind.Do, 1, "閘門繼電器", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.ToggleDo, doPort, null, retriggerSec: 0, enabled: true, outputSeconds: outputSeconds);

        var host = new IoRuleHost(
            _ports,
            _rules,
            _events,
            new ProbeWriter(writes => _writes.Add(writes)),
            clock);
        return (host, di, doPort);
    }

    /// <summary>
    /// 可手動推進的時鐘：把「輸出 1 秒後賦歸」變成確定性斷言，而不是真的等。
    /// </summary>
    /// <remarks>
    /// 這一條測試在 CI 上紅過一次（Expected 2／Actual 1）：程式是對的，只是慢機上
    /// 1 秒的計時器超過 3 秒才輪到它。把它改成「推進時鐘」就不再受機器速度影響，
    /// 也讓斷言更強——不必留一個 3 秒的寬限值假裝那不是規則的一部分。
    /// 只支援一次性計時器（本體只用到這種）。
    /// </remarks>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        /// <summary>把時鐘往前推，並執行所有到期的一次性計時器。</summary>
        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += delta;
                due = _timers.Where(t => t.DueAt is { } d && d <= _now).ToList();
                foreach (var t in due)
                {
                    _timers.Remove(t);
                }
            }

            foreach (var t in due)
            {
                t.Fire();
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                }

                return true;
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    DueAt = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ProbeWriter(Action<(long PortId, bool On)> onWrite) : IIoOutputWriter
    {
        public Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default)
        {
            lock (onWrite)
            {
                onWrite((outputPortId, logicalOn));
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 輸出秒數會讓繼電器閉合後自動放開()
    {
        var clock = new ManualTimeProvider();
        var (host, di, doPort) = Make(outputSeconds: 1, clock);

        await host.FeedAsync(di, physicalOn: true);
        Assert.Equal((doPort, true), SingleWrite());

        // 時間還沒到：繼電器要保持閉合（脈衝的長度是規則的一部分，不能提早放開）。
        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Single(_writes);

        // 過了設定的秒數才賦歸。
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, _writes.Count);
        Assert.Equal((doPort, false), _writes[1]);
    }

    [Fact]
    public async Task 沒有輸出秒數時維持切換語意()
    {
        var (host, di, doPort) = Make(outputSeconds: 0);

        await host.FeedAsync(di, physicalOn: true);
        Assert.Equal((doPort, true), SingleWrite());

        // 再觸發一次（冷卻 0 秒）反相：沒有秒數就不是脈衝。
        await host.FeedAsync(di, physicalOn: false);
        await host.FeedAsync(di, physicalOn: true);
        Assert.Equal(2, _writes.Count);
        Assert.Equal((doPort, false), _writes[1]);
    }

    private (long, bool) SingleWrite()
    {
        lock (_writes)
        {
            Assert.Single(_writes);
            return _writes[0];
        }
    }
}
