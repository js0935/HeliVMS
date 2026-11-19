using HeliVMS.Qctek.Nemo;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using HeliVMS.WebApi;

namespace HeliVMS.App.Services;

/// <summary>
/// 桌面端的 <see cref="IQctekPtzDevice"/>：把 QCTek 的 PTZ 命令換成對攝影機的 ONVIF 呼叫。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是同步阻塞</b>：QCTek 的 PTZ 連線是搖桿式的連續命令（<see cref="QctekPtzSession"/>
/// 的說明），真機在同一條連線的執行緒上直接呼叫裝置。這裡沿用同一個模型，但每一通
/// ONVIF 呼叫都套 5 秒逾時——沒有逾時的話，一台失聯的攝影機會讓 QCC 的 PTZ 面板整個
/// 卡住（面板看起來「沒有反應」，其實是我們在等一條永遠不會回應的 TCP）。
/// 連線執行緒沒有 <see cref="SynchronizationContext"/>，所以
/// <c>GetAwaiter().GetResult()</c> 不會死鎖。
/// </para>
/// <para>
/// <b>快取</b>：能力探測（<c>GetCapabilities</c>）與 profile token 是十分鐘，預設點清單是
/// 兩分鐘。QCC 的 PTZ 面板在連線時就會問一次「有沒有 PTZ／幾個預設點」，而搖桿命令
/// 每秒可能十幾條；不快取的話每一條命令都會多一次 ONVIF 往返，手感直接變成「按了
/// 一秒才動」。逾時／失敗的結果<b>不</b>快取，讓面板可以重試。
/// </para>
/// </remarks>
internal sealed class QctekPtzDevice : IQctekPtzDevice
{
    /// <summary>單一 ONVIF 呼叫的逾時（見型別說明）。</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>能力與 profile token 的快取時間。</summary>
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(10);

    /// <summary>預設點清單的快取時間（比能力短：操作員會新增／刪除預設點）。</summary>
    private static readonly TimeSpan PresetTtl = TimeSpan.FromMinutes(2);

    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly IOnvifPtzGateway _gateway;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, ProfileEntry> _profiles = [];
    private readonly Dictionary<int, PresetEntry> _presets = [];
    private readonly TimeProvider _clock;

    public QctekPtzDevice(SqliteStore store, Action<string> log)
        : this(
            new ChannelRepository(store),
            new DeviceRepository(store, new AuditLogRepository(store)),
            new OnvifPtzGateway(),
            log,
            TimeProvider.System)
    {
    }

    /// <summary>測試用建構式：注入儲存庫、gateway 與時鐘。</summary>
    internal QctekPtzDevice(
        ChannelRepository channels,
        DeviceRepository devices,
        IOnvifPtzGateway gateway,
        Action<string> log,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _channels = channels;
        _devices = devices;
        _gateway = gateway;
        _log = log;
        _clock = clock;
    }

    /// <inheritdoc />
    public bool IsSupported(int channelIndex)
    {
        var entry = ResolveProfile(channelIndex, force: false);
        return entry is not null;
    }

    /// <inheritdoc />
    public int GetPresetCount(int channelIndex)
        => GetPresets(channelIndex).Count;

    /// <inheritdoc />
    public bool Execute(int channelIndex, QctekPtzCommand command, double speedScale)
    {
        switch (command.Action)
        {
            case QctekPtzAction.Stop:
            case QctekPtzAction.StopPan:
                return Invoke(
                    channelIndex,
                    "Stop",
                    (device, token, cancellation) => _gateway.StopAsync(device, token, cancellation));

            case QctekPtzAction.Preset:
                return GotoPreset(channelIndex, command.Argument);

            case QctekPtzAction.AutoPan:
            case QctekPtzAction.FocusIn:
            case QctekPtzAction.FocusOut:
                // ONVIF 沒有自動巡航與對焦的連續移動對應（ContinuousMove 只有
                // pan/tilt/zoom），硬送會是「面板顯示在動、畫面不動」。這裡明確回
                // false——仍是已知命令，呼叫端不會把它記成未知命令。
                return false;

            default:
                if (!QctekPtzVectors.IsMove(command.Action))
                {
                    return false;
                }

                var (pan, tilt, zoom) = QctekPtzVectors.Vector(command.Action);
                var scale = Math.Clamp(speedScale, 0d, 1d);
                return Invoke(
                    channelIndex,
                    command.Action.ToString(),
                    (device, token, cancellation) => _gateway.ContinuousMoveAsync(
                        device,
                        token,
                        pan * scale,
                        tilt * scale,
                        zoom * scale,
                        cancellation));
        }
    }

    private bool GotoPreset(int channelIndex, int index)
    {
        var presets = GetPresets(channelIndex);
        if (index < 0 || index >= presets.Count)
        {
            // QCC 的 preset 是清單索引，不是 ONVIF token。索引超出範圍（面板與攝影機
            // 的清單不同步）時什麼都不做，不要拿別人的 token 亂跳。
            _log($"PTZ：預設點索引 {index} 超出範圍（頻道 {channelIndex + 1}，共 {presets.Count} 個）");
            return false;
        }

        var token = presets[index].Token;
        return Invoke(
            channelIndex,
            $"Preset {index}",
            (device, profileToken, cancellation) =>
                _gateway.GotoPresetAsync(device, profileToken, token, cancellation));
    }

    /// <summary>
    /// 取（或建立）頻道的 profile 快取；取不到就回 <c>null</c>＝這個頻道沒有 PTZ。
    /// </summary>
    private ProfileEntry? ResolveProfile(int channelIndex, bool force)
    {
        lock (_gate)
        {
            if (!force &&
                _profiles.TryGetValue(channelIndex, out var cached) &&
                _clock.GetUtcNow() - cached.ResolvedUtc < ProfileTtl)
            {
                return cached;
            }
        }

        var device = ResolveDevice(channelIndex);
        if (device is null)
        {
            return null;
        }

        IReadOnlyList<PtzProfileInfo> profiles;
        try
        {
            using var cancellation = new CancellationTokenSource(CallTimeout);
            profiles = _gateway.GetProfilesAsync(device, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 能力探測失敗（離線、逾時、不支援 ONVIF）不快取：面板下次連線可以重試。
            _log($"PTZ：能力探測失敗（頻道 {channelIndex + 1}）：{ex.GetType().Name} {ex.Message}");
            return null;
        }

        if (profiles.Count == 0)
        {
            return null;
        }

        var entry = new ProfileEntry(device, profiles[0].Token, _clock.GetUtcNow());
        lock (_gate)
        {
            _profiles[channelIndex] = entry;
        }

        return entry;
    }

    private IReadOnlyList<PtzPresetInfo> GetPresets(int channelIndex)
    {
        lock (_gate)
        {
            if (_presets.TryGetValue(channelIndex, out var cached) &&
                _clock.GetUtcNow() - cached.ResolvedUtc < PresetTtl)
            {
                return cached.Items;
            }
        }

        if (ResolveProfile(channelIndex, force: false) is not { } profile)
        {
            return [];
        }

        try
        {
            using var cancellation = new CancellationTokenSource(CallTimeout);
            var items = _gateway
                .GetPresetsAsync(profile.Device, profile.ProfileToken, cancellation.Token)
                .GetAwaiter()
                .GetResult();

            lock (_gate)
            {
                _presets[channelIndex] = new PresetEntry(items, _clock.GetUtcNow());
            }

            return items;
        }
        catch (Exception ex)
        {
            _log($"PTZ：取預設點失敗（頻道 {channelIndex + 1}）：{ex.GetType().Name} {ex.Message}");
            return [];
        }
    }

    private bool Invoke(
        int channelIndex,
        string what,
        Func<DeviceRecord, string?, CancellationToken, Task> action)
    {
        if (ResolveProfile(channelIndex, force: false) is not { } profile)
        {
            return false;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(CallTimeout);
            action(profile.Device, profile.ProfileToken, cancellation.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            // 裝置端失敗（逾時、攝影機拒收）不中斷 PTZ 迴圈：真機對失敗的動作同樣
            // 只回 0 個位元組，客戶端繼續送下一條命令。
            _log($"PTZ：{what} 失敗（頻道 {channelIndex + 1}）：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>QCC 的頻道序號 → 綁定的攝影機；沒有綁定（或裝置已刪）時回 <c>null</c>。</summary>
    private DeviceRecord? ResolveDevice(int channelIndex)
    {
        try
        {
            var channels = _channels.List();
            if (channelIndex < 0 || channelIndex >= channels.Count)
            {
                return null;
            }

            var deviceId = channels[channelIndex].DeviceId;
            if (deviceId is null or <= 0)
            {
                return null;
            }

            return _devices.Get(deviceId.Value);
        }
        catch (Exception ex)
        {
            _log($"PTZ：頻道 {channelIndex + 1} 的裝置查詢失敗：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private sealed record ProfileEntry(DeviceRecord Device, string ProfileToken, DateTimeOffset ResolvedUtc);

    private sealed record PresetEntry(IReadOnlyList<PtzPresetInfo> Items, DateTimeOffset ResolvedUtc);
}
