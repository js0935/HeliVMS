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

    private (IoRuleHost Host, long DiPort, long DoPort) Make(int outputSeconds)
    {
        var di = _ports.Add(1, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        var doPort = _ports.Add(1, IoPortKind.Do, 1, "閘門繼電器", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.ToggleDo, doPort, null, retriggerSec: 0, enabled: true, outputSeconds: outputSeconds);

        var host = new IoRuleHost(_ports, _rules, _events, new ProbeWriter(writes => _writes.Add(writes)));
        return (host, di, doPort);
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
        var (host, di, doPort) = Make(outputSeconds: 1);

        await host.FeedAsync(di, physicalOn: true);
        Assert.Equal((doPort, true), SingleWrite());

        // 1 秒後自動放開（時限抓 3 秒，避免 CI 慢機誤判）。
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_writes.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

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
