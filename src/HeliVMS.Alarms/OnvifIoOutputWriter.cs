using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>設備上的一顆繼電器輸出（ONVIF RelayOutput 的權杖與名稱）。</summary>
public sealed record OnvifRelayRef(string Token, string Name);

/// <summary>
/// 把「乾接點輸出埠」（<see cref="IoPortRecord"/>，DO）寫到攝影機的 ONVIF 繼電器。
///
/// <para>
/// 為什麼需要這一層：<see cref="IoRuleHost"/> 的 DO 動作原本落到 <c>NullIoOutputWriter</c>
/// ——規則觸發後只更新引擎狀態、寫了事件，實際繼電器不會動。ONVIF 的
/// <c>SetRelayOutputState</c> 早已實作，卻只有 Web API（<c>/api/io</c>）在用，桌面端的
/// 手動觸發與規則鏈是斷的：畫面看起來有反應，現場的閘門／警燈沒有動作。
/// </para>
/// <para>
/// 與設備的連線以委派注入（<see cref="RelayLister"/>／<see cref="RelaySwitcher"/>）：
/// ONVIF 的 SOAP 行為由 HeliVMS.Devices 與其測試涵蓋，這裡只負責「埠 → 頻道 → 設備 →
/// 繼電器權杖」的解析與失敗處理，因此可以在 HeliVMS.Alarms.Tests 用假設備測到底。
/// </para>
/// </summary>
public sealed class OnvifIoOutputWriter : IIoOutputWriter
{
    /// <summary>列出設備的繼電器輸出。</summary>
    public delegate Task<IReadOnlyList<OnvifRelayRef>> RelayLister(DeviceRecord device, CancellationToken cancellationToken);

    /// <summary>切換設備上的某一顆繼電器。</summary>
    public delegate Task RelaySwitcher(DeviceRecord device, string relayToken, bool active, CancellationToken cancellationToken);

    private readonly IoPortRepository _ports;
    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly RelayLister _listRelays;
    private readonly RelaySwitcher _switchRelay;
    private readonly AuditLogRepository? _audit;

    /// <param name="ports">乾接點埠設定（DO 的端子號與所屬頻道）。</param>
    /// <param name="channels">頻道 → 設備的綁定解析。</param>
    /// <param name="devices">設備（ONVIF 位址與憑證）。</param>
    /// <param name="listRelays">列出設備繼電器（實際呼叫端為 ONVIF 服務）。</param>
    /// <param name="switchRelay">切換設備繼電器（實際呼叫端為 ONVIF 服務）。</param>
    /// <param name="audit">稽核日誌；null 時不寫。</param>
    public OnvifIoOutputWriter(
        IoPortRepository ports,
        ChannelRepository channels,
        DeviceRepository devices,
        RelayLister listRelays,
        RelaySwitcher switchRelay,
        AuditLogRepository? audit = null)
    {
        _ports = ports;
        _channels = channels;
        _devices = devices;
        _listRelays = listRelays;
        _switchRelay = switchRelay;
        _audit = audit;
    }

    /// <summary>最近一次寫入失敗的原因（供介面顯示）；成功時清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>寫入失敗時的通知（介面用來提示；DO 失敗不會讓規則鏈中斷）。</summary>
    public event EventHandler<string>? Failed;

    public async Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default)
    {
        var port = _ports.Get(outputPortId);
        if (port is null || port.Kind != IoPortKind.Do)
        {
            // 非 DO（或埠已被刪除）沒有硬體可寫：直接略過，不當成失敗。
            return;
        }

        try
        {
            var channel = _channels.List().FirstOrDefault(c => c.Id == port.ChannelId);
            if (channel?.DeviceId is not int deviceId)
            {
                Fail($"port={port.Id}", "channel-not-bound");
                return;
            }

            var device = _devices.Get(deviceId);
            if (device is null)
            {
                Fail($"port={port.Id}", "device-missing");
                return;
            }

            var relays = await _listRelays(device, cancellationToken).ConfigureAwait(false);
            var token = RelayTokenFor(port, relays);
            if (token is null)
            {
                Fail($"port={port.Id} number={port.Number}", "relay-not-found");
                return;
            }

            await _switchRelay(device, token, logicalOn, cancellationToken).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 攝影機連不上、逾時、SOAP 錯誤：記下來並通知介面，但不往外丟——
            // DO 寫失敗不該讓同一批動作鏈（例如同時要寫的警報事件）半途而廢。
            Fail($"port={port.Id}", ex.Message);
        }
    }

    /// <summary>
    /// 決定這個輸出埠對應哪一顆繼電器權杖；找不到回 <c>null</c>。
    ///
    /// <para>
    /// ONVIF 的權杖是不透明的字串（<c>RelayOutputToken_1</c>、<c>relay1</c>…），但乾接點
    /// 埠是接線端子號，兩者沒有標準對應。順序固定為：
    /// <b>(1)</b> 權杖完全相同（操作員直接把權杖填進端子號）；
    /// <b>(2)</b> 權杖或名稱的尾數等於端子號（<c>Relay_2</c>／<c>Relay 2</c> 對端子 2）；
    /// <b>(3)</b> 端子號當 1 起算的序號（端子 1＝第一顆）。
    /// 前兩條處理常見命名，第三條是沒有線索時的保底；都不成立回 <c>null</c>（寧可不動，
    /// 也不要切錯繼電器——切錯等於驅動錯的閘門）。
    /// </para>
    /// </summary>
    public static string? RelayTokenFor(IoPortRecord port, IReadOnlyList<OnvifRelayRef> relays)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(relays);

        foreach (var relay in relays)
        {
            if (string.Equals(relay.Token, port.Name, StringComparison.OrdinalIgnoreCase))
            {
                return relay.Token;
            }
        }

        foreach (var relay in relays)
        {
            if (TrailingNumber(relay.Token) == port.Number || TrailingNumber(relay.Name) == port.Number)
            {
                return relay.Token;
            }
        }

        var index = port.Number - 1;
        return index >= 0 && index < relays.Count ? relays[index].Token : null;
    }

    /// <summary>字串結尾的數字（<c>Relay_2</c>→2、<c>relay</c>→null）。</summary>
    public static int? TrailingNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var i = text.Length;
        while (i > 0 && char.IsAsciiDigit(text[i - 1]))
        {
            i--;
        }

        if (i == text.Length)
        {
            return null;
        }

        return int.TryParse(text.AsSpan(i), out var value) ? value : null;
    }

    private void Fail(string target, string reason)
    {
        LastError = reason;
        try
        {
            _audit?.Record("system", "io.output.failed", AuditCategories.Recording, detail: $"{target}: {reason}");
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 稽核寫不進去不該讓 DO 失敗處理再炸一次。
        }

        Failed?.Invoke(this, reason);
    }
}
