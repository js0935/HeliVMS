using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 依接點來源路由 DO 輸出：<c>io_ports.io_channel_id</c> 有值 → Modbus 線圈；
/// 沒有 → 攝影機繼電器（ONVIF）。
/// </summary>
/// <remarks>
/// 接點統一登記在 <c>io_ports</c>，連動規則只看埠；實際打到哪種硬體由這裡決定。
/// 沒有路由時，Modbus 接點的輸出會被誤送到攝影機繼電器（或反之）——症狀是
/// 「規則觸發了，閘門不動」，而且日誌乾淨。
/// </remarks>
public sealed class RoutingIoOutputWriter : IIoOutputWriter
{
    private readonly IoPortRepository _ports;
    private readonly IIoOutputWriter _cameraWriter;
    private readonly IIoOutputWriter _modbusWriter;

    public RoutingIoOutputWriter(IoPortRepository ports, IIoOutputWriter cameraWriter, IIoOutputWriter modbusWriter)
    {
        _ports = ports;
        _cameraWriter = cameraWriter;
        _modbusWriter = modbusWriter;
    }

    /// <inheritdoc />
    public async Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default)
    {
        var port = _ports.Get(outputPortId);
        if (port?.IoChannelId is not null)
        {
            await _modbusWriter.WriteAsync(outputPortId, logicalOn, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _cameraWriter.WriteAsync(outputPortId, logicalOn, cancellationToken).ConfigureAwait(false);
    }
}
