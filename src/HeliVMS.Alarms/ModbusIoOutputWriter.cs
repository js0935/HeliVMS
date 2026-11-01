using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 把「接點輸出」路由到 Modbus 線圈（<c>io_ports.io_channel_id</c> 指到的通道）。
/// </summary>
/// <remarks>
/// 攝影機繼電器與 Modbus 模組的 DO 都登記在 <c>io_ports</c>，連動規則只看埠；
/// 這個寫入器負責 Modbus 那一半（攝影機那一半是 <see cref="OnvifIoOutputWriter"/>）。
/// 埠沒有對應的 Modbus 通道時回報失敗（記到 lastError 供診斷），不拋例外——
/// 輸出失敗不該把餵入規則的採樣迴圈打斷。
/// </remarks>
public sealed class ModbusIoOutputWriter : IIoOutputWriter
{
    private readonly IoPortRepository _ports;
    private readonly Func<int, bool, Task<bool>> _writeByIoChannel;
    private readonly Action<string>? _onFailure;

    public ModbusIoOutputWriter(
        IoPortRepository ports,
        Func<int, bool, Task<bool>> writeByIoChannel,
        Action<string>? onFailure = null)
    {
        _ports = ports;
        _writeByIoChannel = writeByIoChannel;
        _onFailure = onFailure;
    }

    /// <summary>最近一次輸出失敗的原因（診斷用）。</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public async Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default)
    {
        var port = _ports.Get(outputPortId);
        if (port?.IoChannelId is not int ioChannelId)
        {
            LastError = $"port {outputPortId} 不是 Modbus 通道的接點";
            _onFailure?.Invoke(LastError);
            return;
        }

        var ok = await _writeByIoChannel(ioChannelId, logicalOn).ConfigureAwait(false);
        if (!ok)
        {
            LastError = $"Modbus 輸出失敗：io_channel={ioChannelId} on={logicalOn}";
            _onFailure?.Invoke(LastError);
        }
    }
}
