using HeliVMS.Alarms;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// M40：警報 IO（§16.2）服務宿主——替每個啟用中的 io_devices 建立一個
/// <see cref="IoDeviceMonitor"/> 並承接其 io_input 事件（可接通知平面）；
/// 同時提供 DO 寫出介面（設定中心 DO 測試用）。
/// </summary>
public sealed class IoMonitorHost : IDisposable
{
    private readonly IoRepository _io;
    private readonly AlarmEventRepository _events;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _ioRules;
    private readonly IoRuleHost _ruleHost;
    private readonly Dictionary<int, IoDeviceMonitor> _monitors = [];
    private readonly object _gate = new();
    private bool _disposed;

    public IoMonitorHost(SqliteStore store, IIoOutputWriter? outputWriter = null)
    {
        _io = new IoRepository(store);
        _events = new AlarmEventRepository(store);
        _ports = new IoPortRepository(store);
        _ioRules = new IoRuleRepository(store);

        // 連動規則與攝影機乾接點共用同一套引擎；DO 依接點來源路由（Modbus 線圈 / 攝影機繼電器）。
        _ruleHost = new IoRuleHost(_ports, _ioRules, _events, outputWriter ?? new ModbusIoOutputWriter(_ports, WriteOutputByIoChannelAsync));
    }

    /// <summary>讓警報中心剛改完的 IO 連動規則立即生效（重建引擎）。</summary>
    public void ReloadRules() => _ruleHost.Reload();

    /// <summary>
    /// 換上含攝影機繼電器的路由寫入器：Modbus 接點的連動也能驅動攝影機繼電器（反之亦然）。
    /// </summary>
    /// <remarks>
    /// 兩個宿主的建立順序互為前後，這裡在兩者都就緒後補上路由；
    /// 引擎持有的是寫入器介面，換實作不需要重建規則。
    /// </remarks>
    public void SetCameraWriter(IIoOutputWriter cameraWriter)
    {
        _ruleHost.SetOutputWriter(new RoutingIoOutputWriter(_ports, cameraWriter, new ModbusIoOutputWriter(_ports, WriteOutputByIoChannelAsync)));
    }

    /// <summary>任一監視器寫入 io_input 事件（與 motion/tamper 相同接法餵給通知平面）。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>依資料庫現況同步監視器集合：新增啟用模組、移除停用/刪除的模組、重載通道。</summary>
    public void RefreshAndStart()
    {
        if (_disposed)
        {
            return;
        }

        var devices = _io.ListDevices();

        // Modbus 通道同步登記為接點（io_ports）：連動規則只認接點，
        // 通道改名／改極性／改綁定攝影機都要跟著，否則規則指向舊的硬體。
        SyncPortsForChannels();

        lock (_gate)
        {
            var keep = new HashSet<int>(devices.Where(d => d.Enabled).Select(d => d.Id));
            foreach (var stale in _monitors.Keys.Except(keep).ToList())
            {
                _monitors[stale].Dispose();
                _monitors.Remove(stale);
            }

            foreach (var dev in devices)
            {
                if (!dev.Enabled)
                {
                    continue;
                }

                if (_monitors.TryGetValue(dev.Id, out var existing))
                {
                    existing.RefreshChannels();
                }
                else
                {
                    var monitor = new IoDeviceMonitor(dev, _io, _events, _ruleHost, _ports);
                    monitor.EventInserted += OnEventInserted;
                    monitor.Start();
                    _monitors[dev.Id] = monitor;
                }
            }
        }
    }

    /// <summary>DO 寫出（依 io_ports 的接點；連動規則的輸出路由走這裡）。</summary>
    public async Task<bool> WriteOutputByIoChannelAsync(int ioChannelId, bool on)
    {
        var ch = _io.GetChannel(ioChannelId);
        if (ch is null)
        {
            return false;
        }

        return await WriteOutputAsync(ch.DeviceId, ioChannelId, on).ConfigureAwait(false);
    }

    /// <summary>DO 寫出（任一監視器；依通道 ID；失敗或不存在回 false）。</summary>
    public async Task<bool> WriteOutputAsync(int deviceId, int channelId, bool on)
    {
        IoDeviceMonitor? monitor;
        lock (_gate)
        {
            if (_disposed || !_monitors.TryGetValue(deviceId, out monitor))
            {
                return false;
            }
        }

        return await monitor.WriteOutputByChannelAsync(channelId, on).ConfigureAwait(false);
    }

    /// <summary>
    /// 把 Modbus 通道同步登記為接點（io_ports）；通道被刪除時連接點與其規則一起清掉。
    /// </summary>
    /// <remarks>
    /// 放在 RefreshAndStart（啟動與每次 IO 設定變更都會跑）：設定頁怎麼改都不會漏，
    /// 也不必在設定頁再接一次同步。
    /// </remarks>
    private void SyncPortsForChannels()
    {
        var channels = _io.ListChannels();
        var seen = new HashSet<int>();

        foreach (var ch in channels)
        {
            seen.Add(ch.Id);
            var kind = ch.Direction == "DO" ? IoPortKind.Do : IoPortKind.Di;
            var polarity = ch.Polarity ? IoPolarity.NormallyClosed : IoPolarity.NormallyOpen;
            _ports.EnsureForIoChannel(ch.CameraId ?? 0, ch.Id, kind, ch.IoIndex + 1, ch.Name, polarity, ch.DebounceMs);
        }

        foreach (var port in _ports.List().Where(p => p.IoChannelId is not null))
        {
            if (port.IoChannelId is int id && !seen.Contains(id))
            {
                _ports.DeleteByIoChannel(id);
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
        lock (_gate)
        {
            foreach (var m in _monitors.Values)
            {
                m.Dispose();
            }

            _monitors.Clear();
        }
    }

    private void OnEventInserted(object? sender, AlarmEventRecord record)
    {
        if (!_disposed)
        {
            EventInserted?.Invoke(this, record);
        }
    }
}