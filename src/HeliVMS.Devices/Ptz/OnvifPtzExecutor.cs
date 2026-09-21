using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Ptz;

/// <summary>
/// M70 <see cref="IPtzExecutor"/> 之 ONVIF 實作（M71）——把巡航的「預設點名稱」解析成 ONVIF
/// PresetToken（GET Presets，惰性快取）再 GotoPreset。名稱不存在→throw。
/// </summary>
public sealed class OnvifPtzExecutor : IPtzExecutor
{
    private readonly OnvifDeviceService _device;
    private readonly string _profileToken;
    private Dictionary<string, string>? _nameToToken;

    public OnvifPtzExecutor(OnvifDeviceService device, string profileToken)
    {
        _device = device;
        _profileToken = profileToken;
    }

    public async Task GotoPresetAsync(string presetName, CancellationToken ct = default)
    {
        await _device.EnsurePtzCapabilityAsync(ct);
        if (!_device.HasPtz)
        {
            throw new InvalidOperationException("裝置不支援 PTZ（未取得 PTZ 服務位址）");
        }

        _nameToToken ??= (await _device.GetPtzPresetsAsync(_profileToken, ct))
            .ToDictionary(p => p.Name, p => p.Token, StringComparer.Ordinal);

        if (!_nameToToken.TryGetValue(presetName, out var token))
        {
            throw new InvalidOperationException($"PTZ 預設點「{presetName}」不存在於裝置");
        }

        await _device.GotoPtzPresetAsync(_profileToken, token, ct);
    }
}