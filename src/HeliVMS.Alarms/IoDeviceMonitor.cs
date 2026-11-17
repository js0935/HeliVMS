using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>DI/DO 通道狀態變更（UI 即時顯示用）。</summary>
public sealed record IoChannelState(long ChannelId, string Name, int IoIndex, bool Active, string AlarmPriority);

/// <summary>
/// 網路 IO 模組監視器（§16.2）：每 poll_ms 輪詢離散輸入（FC02），對啟用 DI 通道做
/// 去抖＋polarity 反相狀態機——上升沿寫 io_input 事件（可綁定相機）、下降沿結算。
/// 每個模組一個實例；設定變更以 <see cref="RefreshChannels"/> 重載通道。
/// </summary>
public sealed class IoDeviceMonitor : IDisposable
{
    private readonly IoDevice _device;
    private readonly IoRepository _io;
    private readonly AlarmEventRepository _events;
    private readonly IoRuleHost? _ruleHost;
    private readonly IoPortRepository? _ports;
    private readonly IModbusTcpClient _client;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private IReadOnlyList<IoChannel> _diChannels = [];
    private readonly Dictionary<int, bool> _lastStable = [];
    private readonly Dictionary<int, bool> _candidate = [];
    private readonly Dictionary<int, long> _candidateSinceMs = [];

    /// <summary>
    /// 每個 DI 通道各自開啟中的 <c>io_input</c> 事件（通道 id → 事件 id）。
    /// </summary>
    /// <remarks>
    /// 這裡刻意是「每通道一份」而不是一個欄位：一顆 ADAM-6250 有 8 個 DI，而事件窗口是
    /// 「輸入 still active 期間」。共用一個 <c>_openEventId</c> 時，兩點同時 active 只有第一點
    /// 會留下事件，第二點降下時關掉的是<b>第一點</b>的窗口——事件中心的時間軸因此顯示
    /// 「A 已經結束」而 B 從未發生，兩張表都寫入成功、日誌乾淨。模組化接點普及後這是常態
    /// （一道門＋一個門磁同時觸發），不是邊角案例。
    /// </remarks>
    private readonly Dictionary<int, long> _openEventIds = [];
    private bool _busy;
    private bool _disposed;

    public IoDeviceMonitor(
        IoDevice device,
        IoRepository io,
        AlarmEventRepository events,
        IoRuleHost? ruleHost = null,
        IoPortRepository? ports = null,
        IModbusTcpClient? client = null)
    {
        _device = device;
        _io = io;
        _events = events;
        _ruleHost = ruleHost;
        _ports = ports;
        _client = client ?? new ModbusTcpClient(device.Host, device.Port);
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>io_input 事件已寫入 alarm_events（可接通知平面）。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>任一 DI 通道穩定狀態變更（UI 顯示用）。</summary>
    public event EventHandler<IoChannelState>? StateChanged;

    /// <summary>此模組資訊。</summary>
    public IoDevice Device => _device;

    /// <summary>成功輪詢次數（診斷）。</summary>
    public long PollCount { get; private set; }

    /// <summary>最近一次輪詢的時刻；<c>null</c>＝還沒輪詢過（剛啟動，或這個模組沒有 DI）。</summary>
    public DateTimeOffset? LastPollUtc { get; private set; }

    /// <summary>
    /// 最近一次輪詢是否成功（Modbus 讀取沒有丟出例外）。
    /// <para>
    /// UI 用它顯示模組在不在線：加了一顆 ADAM 卻填錯 IP，症狀是「接點建好了但永遠沒有事件」，
    /// 沒有這個欄位就只能在日誌裡找。
    /// </para>
    /// </summary>
    public bool LastPollSucceeded { get; private set; }

    /// <summary>
    /// 目前各 DI 通道的穩定狀態（UI 即時顯示用）。
    /// <para>
    /// 還沒輪詢到的通道<b>不會</b>出現在清單裡：對 UI 而言「還不知道」與「斷開」是兩回事，
    /// 混在一起會把剛加好的模組顯示成全部斷開。
    /// </para>
    /// </summary>
    public IReadOnlyList<IoChannelState> SnapshotStates()
    {
        lock (_gate)
        {
            return _diChannels
                .Where(ch => _lastStable.ContainsKey(ch.Id))
                .Select(ch => new IoChannelState(ch.Id, ch.Name, ch.IoIndex, _lastStable[ch.Id], ch.AlarmPriority))
                .ToList();
        }
    }

    /// <summary>啟動週期輪詢（poll_ms）。</summary>
    public void Start()
    {
        RefreshChannels();
        _ = _timer.Change(Math.Max(50, _device.PollMs), Math.Max(50, _device.PollMs));
    }

    /// <summary>重載通道清單（設定變更後呼叫）；不存在通道的狀態一併清除。</summary>
    public void RefreshChannels()
    {
        lock (_gate)
        {
            _diChannels = _io.ListChannels(deviceId: _device.Id, direction: "DI", enabledOnly: true);
            var ids = new HashSet<int>(_diChannels.Select(c => c.Id));
            foreach (var k in _lastStable.Keys.Except(ids).ToList())
            {
                _lastStable.Remove(k);
                _candidate.Remove(k);
                _candidateSinceMs.Remove(k);
            }
        }
    }

    /// <summary>依通道 ID 寫 DO（FC05）。非 DO 或不存在回 false。</summary>
    public async Task<bool> WriteOutputByChannelAsync(int channelId, bool on, CancellationToken ct = default)
    {
        var ch = _io.GetChannel(channelId);
        if (ch is null || ch.Direction != "DO")
        {
            return false;
        }

        return await WriteOutputAsync(ch, on, ct).ConfigureAwait(false);
    }

    /// <summary>直接對某 DO 通道寫出。</summary>
    public async Task<bool> WriteOutputAsync(IoChannel doChannel, bool on, CancellationToken ct = default)
    {
        try
        {
            return await _client.WriteSingleCoilAsync(
                (byte)_device.UnitId, (ushort)doChannel.IoIndex, on, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>關閉進行中的事件窗口（停止前收尾）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            foreach (var eventId in _openEventIds.Values)
            {
                _events.UpdateEnd(eventId, DateTime.UtcNow, null);
            }

            _openEventIds.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        Flush();
    }

    private async void OnTick(object? _)
    {
        if (_disposed || Interlocked.CompareExchange(ref _busy, true, false))
        {
            return;
        }

        try
        {
            await PollOnceAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 單次輪詢失敗不終止監視（下一次 tick 再試）
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>手動輪詢一次（測試與進階客製用；週期輪詢由 <see cref="Start"/> 驅動）。</summary>
    public async Task PollOnceAsync()
    {
        IReadOnlyList<IoChannel> channels;
        var maxIndex = -1;
        lock (_gate)
        {
            channels = _diChannels;
            if (channels.Count > 0)
            {
                maxIndex = channels.Max(c => c.IoIndex);
            }
        }

        if (maxIndex < 0)
        {
            return;
        }

        IReadOnlyList<bool> raw;
        try
        {
            raw = await _client.ReadDiscreteInputsAsync(
                (byte)_device.UnitId, 0, (ushort)(maxIndex + 1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 連不上也要說出來：UI 的「連線」欄靠這個旗標顯示離線，而不是永遠顯示線上。
            LastPollSucceeded = false;
            LastPollUtc = DateTimeOffset.UtcNow;
            return;
        }

        PollCount++;
        LastPollSucceeded = true;
        LastPollUtc = DateTimeOffset.UtcNow;
        var nowMs = Environment.TickCount64;
        foreach (var ch in channels)
        {
            if (raw.Count <= ch.IoIndex)
            {
                continue;
            }

            var bit = raw[ch.IoIndex];
            var active = ch.Polarity ? !bit : bit;
            ApplyDebounce(ch, active, nowMs);
        }
    }

    private void ApplyDebounce(IoChannel ch, bool rawActive, long nowMs)
    {
        var trigger = false;
        lock (_gate)
        {
            if (!_lastStable.TryGetValue(ch.Id, out var stable))
            {
                // 首次見到直接採納為基準，避免開機瞬間的雜訊假觸發
                _lastStable[ch.Id] = rawActive;
                _candidate[ch.Id] = rawActive;
                _candidateSinceMs[ch.Id] = nowMs;
                return;
            }

            if (rawActive == stable)
            {
                _candidate[ch.Id] = stable;
                _candidateSinceMs[ch.Id] = nowMs;
                return;
            }

            if (!_candidate.TryGetValue(ch.Id, out var cand) || cand != rawActive)
            {
                _candidate[ch.Id] = rawActive;
                _candidateSinceMs[ch.Id] = nowMs;
                return;
            }

            if (nowMs - _candidateSinceMs[ch.Id] < ch.DebounceMs)
            {
                return;
            }

            _lastStable[ch.Id] = rawActive;
            trigger = true;
        }

        if (trigger)
        {
            OnStableChangeLocked(ch, rawActive);
            FeedRuleEngine(ch, rawActive);
        }
    }

    /// <summary>
    /// DI 穩定變化餵入連動規則引擎（與攝影機乾接點同一套 io_rules）。
    /// </summary>
    /// <remarks>
    /// 通道沒有對應的接點（尚未同步）就不餵；餵入失敗也不影響既有的 io_input 事件——
    /// 規則是加值功能，不能因為它壞掉把感測器的既有行為打斷。
    /// </remarks>
    private void FeedRuleEngine(IoChannel ch, bool active)
    {
        if (_ruleHost is null || _ports is null)
        {
            return;
        }

        var portId = _ports.FindByIoChannel(ch.Id)?.Id;
        if (portId is not { } id)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _ruleHost.FeedAsync(id, active).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 規則引擎的失敗（事件 FK、輸出失敗）不往外丟：採樣迴圈不能被它打斷。
            }
        });
    }

    private void OnStableChangeLocked(IoChannel ch, bool active)
    {
        var now = DateTime.UtcNow;
        if (active)
        {
            if (!_openEventIds.ContainsKey(ch.Id))
            {
                // DI 未綁定相機（camera_id 為 NULL）時不寫入 alarm_events（避免 FK 違反）；
                // 仍會發 StateChanged 供介面顯示，規則由 FeedRuleEngine 另行餵入。
                if (ch.CameraId is not int cameraId)
                {
                    StateChanged?.Invoke(this, new IoChannelState(ch.Id, ch.Name, ch.IoIndex, active, ch.AlarmPriority));
                    return;
                }

                var detail = $"device={_device.Name};io={ch.Name};index={ch.IoIndex};priority={ch.AlarmPriority};state=on";
                var id = _events.Insert(cameraId, "io_input", now, null, detail);
                _openEventIds[ch.Id] = id;
                EventInserted?.Invoke(this, new AlarmEventRecord
                {
                    Id = id,
                    ChannelId = cameraId,
                    EventType = "io_input",
                    StartUtc = now,
                    EndUtc = null,
                    SnapshotPath = null,
                    Detail = detail,
                });
            }
        }
        else if (_openEventIds.Remove(ch.Id, out var openEventId))
        {
            _events.UpdateEnd(openEventId, now, null);
        }

        StateChanged?.Invoke(this, new IoChannelState(ch.Id, ch.Name, ch.IoIndex, active, ch.AlarmPriority));
    }
}