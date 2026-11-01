using HeliVMS.Alarms;
using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 攝影機 DI 監視主機：為「已綁定 ONVIF 設備且設定了 DI 埠」的頻道建立
/// <see cref="CameraDiMonitor"/>，把攝影機乾接點輸入的事件餵進規則鏈。
///
/// <para>
/// 為什麼要有主機：監視器本身只認事件來源（可測），把「哪台設備要監視」與 ONVIF 連線
/// 放在這裡，才不會讓 Alarms 相依裝置層；同時集中管理訂閱的生命週期（增減設備、停用埠、
/// 關閉程式）。
/// </para>
/// <para>
/// 與 Modbus 的 <see cref="IoMonitorHost"/> 並存：兩者是不同的實體路徑（IO 模組 vs 攝影機
/// 內建乾接點），站台可能同時有兩者。沒有綁定設備或沒有 DI 埠的頻道不會產生任何流量——
/// 訂閱是持續連線，對沒接線的設備開著只是浪費。
/// </para>
/// </summary>
public sealed class CameraDiHost : IDisposable
{
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(5);

    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly AuditLogRepository _audit;
    private readonly IoRuleHost _ruleHost;
    private readonly Dictionary<int, CameraDiMonitor> _monitors = [];
    private readonly object _gate = new();
    private bool _disposed;

    public CameraDiHost(SqliteStore store)
    {
        _channels = new ChannelRepository(store);
        _audit = new AuditLogRepository(store);
        _devices = new DeviceRepository(store, _audit);
        _ports = new IoPortRepository(store);
        _rules = new IoRuleRepository(store);
        _events = new AlarmEventRepository(store);

        // 規則鏈與手動模擬（IoWindow）共用同一份實作：事件落地與 DO 輸出只有一套行為。
        _ruleHost = new IoRuleHost(
            _ports,
            _rules,
            _events,
            new OnvifIoOutputWriter(_ports, _channels, _devices, ListRelaysAsync, SwitchRelayAsync, _audit));
    }

    /// <summary>依資料庫現況同步監視器集合（新增有 DI 埠的設備、移除不再需要的）。</summary>
    /// <summary>警報動作（DI 觸發 → 帶畫面／截圖／播音／地圖等）由畫面層執行；宿主只轉發。</summary>
    public event EventHandler<IReadOnlyList<IoAction>>? LinkTriggered;

    public void RefreshAndStart()
    {
        if (_disposed)
        {
            return;
        }

        var diPorts = _ports.List().Where(p => p.Kind == IoPortKind.Di && p.Enabled).ToList();
        var channelById = _channels.List().ToDictionary(c => c.Id);

        var wanted = new Dictionary<int, (DeviceRecord Device, List<int> ChannelIds)>();
        foreach (var port in diPorts)
        {
            if (!channelById.TryGetValue(port.ChannelId, out var channel)
                || channel.DeviceId is not int deviceId)
            {
                continue;
            }

            if (!wanted.TryGetValue(deviceId, out var entry))
            {
                var device = _devices.Get(deviceId);
                if (device is null)
                {
                    continue;
                }

                entry = (device, []);
                wanted[deviceId] = entry;
            }

            entry.ChannelIds.Add(port.ChannelId);
        }

        lock (_gate)
        {
            foreach (var stale in _monitors.Keys.Except(wanted.Keys).ToList())
            {
                _monitors[stale].Dispose();
                _monitors.Remove(stale);
            }

            foreach (var (deviceId, entry) in wanted)
            {
                if (_monitors.ContainsKey(deviceId))
                {
                    continue;
                }

                var monitor = CreateMonitor(entry.Device);
                monitor.LinkTriggered += (_, actions) => LinkTriggered?.Invoke(this, actions);
                monitor.Start();
                _monitors[deviceId] = monitor;
            }
        }
    }

    /// <summary>
    /// 讓警報中心剛改完的 IO 連動規則立即生效（重建引擎；等同重新上電）。
    /// </summary>
    /// <remarks>
    /// 規則在警報中心編輯，執行中的監視器拿的是建立當下的引擎副本；
    /// 沒有這一步，改完規則要重啟軟體才會生效，操作員會以為設定沒存到。
    /// </remarks>
    public void ReloadRules()
    {
        _ruleHost.Reload();
    }

    /// <summary>目前監視中的設備數（供診斷／契約測試）。</summary>
    public int MonitorCount
    {
        get
        {
            lock (_gate)
            {
                return _monitors.Count;
            }
        }
    }

    private CameraDiMonitor CreateMonitor(DeviceRecord device)
        => new(
            device.Name,
            _ports,
            _ruleHost,
            async ct =>
            {
                using var service = CreateService(device);
                var inputs = await service.GetDigitalInputsAsync(ct).ConfigureAwait(false);
                return inputs.Select(i => new OnvifInputRef(i.Token, i.Id, i.Name)).ToList();
            },
            async ct =>
            {
                using var service = CreateService(device);
                return await service.CreatePullPointSubscriptionAsync(cancellationToken: ct).ConfigureAwait(false);
            },
            async (subscription, ct) =>
            {
                using var service = CreateService(device);
                var messages = await service.PullMessagesAsync(subscription, PullTimeout, ct).ConfigureAwait(false);
                var events = new List<CameraDiInput>();
                foreach (var message in messages)
                {
                    if (OnvifDeviceService.TryParseDigitalInput(message, out var token, out var active))
                    {
                        events.Add(new CameraDiInput(token, active));
                    }
                }

                return events;
            },
            _audit);

    private static async Task<IReadOnlyList<OnvifRelayRef>> ListRelaysAsync(
        DeviceRecord device, CancellationToken cancellationToken)
    {
        using var service = CreateService(device);
        var relays = await service.GetRelayOutputsAsync(cancellationToken).ConfigureAwait(false);
        return relays.Select(r => new OnvifRelayRef(r.Token, r.Name)).ToList();
    }

    private static async Task SwitchRelayAsync(
        DeviceRecord device, string relayToken, bool active, CancellationToken cancellationToken)
    {
        using var service = CreateService(device);
        await service.SetRelayOutputStateAsync(relayToken, active, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以設備資料建立 ONVIF 服務；位址與憑證解析集中在 <see cref="OnvifServiceFactory"/>
    /// （與 Web API 的 IO gateway、設定中心、IoWindow 的 DO 寫入器同一條路徑）。
    /// </summary>
    private static OnvifDeviceService CreateService(DeviceRecord device)
        => OnvifServiceFactory.Create(device);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            foreach (var monitor in _monitors.Values)
            {
                monitor.Dispose();
            }

            _monitors.Clear();
        }
    }
}
