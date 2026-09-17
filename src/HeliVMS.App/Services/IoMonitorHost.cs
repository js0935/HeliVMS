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
    private readonly Dictionary<int, IoDeviceMonitor> _monitors = [];
    private readonly object _gate = new();
    private bool _disposed;

    public IoMonitorHost(SqliteStore store)
    {
        _io = new IoRepository(store);
        _events = new AlarmEventRepository(store);
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
                    var monitor = new IoDeviceMonitor(dev, _io, _events);
                    monitor.EventInserted += OnEventInserted;
                    monitor.Start();
                    _monitors[dev.Id] = monitor;
                }
            }
        }
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