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
    private readonly IModbusTcpClient _client;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private IReadOnlyList<IoChannel> _diChannels = [];
    private readonly Dictionary<int, bool> _lastStable = [];
    private readonly Dictionary<int, bool> _candidate = [];
    private readonly Dictionary<int, long> _candidateSinceMs = [];
    private long _openEventId;
    private bool _busy;
    private bool _disposed;

    public IoDeviceMonitor(IoDevice device, IoRepository io, AlarmEventRepository events, IModbusTcpClient? client = null)
    {
        _device = device;
        _io = io;
        _events = events;
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
            if (_openEventId != 0)
            {
                _events.UpdateEnd(_openEventId, DateTime.UtcNow, null);
                _openEventId = 0;
            }
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
            return;
        }

        PollCount++;
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
        }
    }

    private void OnStableChangeLocked(IoChannel ch, bool active)
    {
        var now = DateTime.UtcNow;
        if (active)
        {
            if (_openEventId == 0)
            {
                // DI 未綁定相機（camera_id 為 NULL）時不寫入 alarm_events（避免 FK 違反）；
                // 仍會發 StateChanged 供介面顯示。
                if (ch.CameraId is not int cameraId)
                {
                    StateChanged?.Invoke(this, new IoChannelState(ch.Id, ch.Name, ch.IoIndex, active, ch.AlarmPriority));
                    return;
                }

                var detail = $"device={_device.Name};io={ch.Name};index={ch.IoIndex};priority={ch.AlarmPriority};state=on";
                var id = _events.Insert(cameraId, "io_input", now, null, detail);
                _openEventId = id;
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
        else if (_openEventId != 0)
        {
            _events.UpdateEnd(_openEventId, now, null);
            _openEventId = 0;
        }

        StateChanged?.Invoke(this, new IoChannelState(ch.Id, ch.Name, ch.IoIndex, active, ch.AlarmPriority));
    }
}