using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// Web 端攝影機 IO（乾接點）：列出設備的數位輸入 (DI)、繼電器輸出 (DO)，並切換 DO 狀態。
///
/// 與 <see cref="PtzEndpoints"/> 同一個理由抽成 <see cref="IOnvifIoGateway"/>：端點層
/// （頻道→設備解析、輸入驗證、HTTP 狀態碼）要能與 ONVIF I/O 分離測試，不需在 API 測試裡
/// 再養一份 ONVIF SOAP 假伺服器。真正的 gateway 只是 <see cref="OnvifDeviceService"/> 的薄配接，
/// 其 SOAP 行為已由 HeliVMS.Devices.Tests 涵蓋（見 OnvifIoServiceTests）。
///
/// ONVIF 沒有「輪詢 DI 目前狀態」的操作——輸入狀態只在事件中回報，所以這裡的 inputs 只描述
/// 有哪些端子，不含即時狀態；DO 則可直接以 SetRelayOutputState 切換。
/// </summary>
public static class IoEndpoints
{
    public static void Map(WebApplication app)
    {
        var io = app.MapGroup("/api/io");

        io.MapGet("/{channelId:int}/inputs", async (
            int channelId,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifIoGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
                Results.Ok(new IoInputsResponse(
                    await gateway.GetDigitalInputsAsync(device, cancellation).ConfigureAwait(false))));
        });

        io.MapGet("/{channelId:int}/outputs", async (
            int channelId,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifIoGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
                Results.Ok(new IoOutputsResponse(
                    await gateway.GetRelayOutputsAsync(device, cancellation).ConfigureAwait(false))));
        });

        io.MapPost("/{channelId:int}/outputs/{relayToken}/state", async (
            int channelId,
            string relayToken,
            IoRelayStateRequest body,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifIoGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            if (string.IsNullOrWhiteSpace(relayToken))
            {
                return Results.BadRequest(new { error = "缺少繼電器權杖" });
            }

            return await Run(async () =>
            {
                await gateway.SetRelayOutputAsync(device, relayToken, body.Active, cancellation).ConfigureAwait(false);
                return Results.Ok();
            });
        });
    }

    /// <summary>
    /// 解析頻道所綁定的設備。未綁定以 409 回應（請求本身沒錯，是資源狀態不支援），
    /// 缺頻道／缺設備以 404 回應。
    /// </summary>
    private static (DeviceRecord Device, IResult? Error) Resolve(
        ChannelRepository channels,
        DeviceRepository devices,
        int channelId)
    {
        var channel = channels.List().FirstOrDefault(c => c.Id == channelId);
        if (channel is null)
        {
            return (null!, Results.NotFound(new { error = $"找不到頻道 {channelId}" }));
        }

        if (channel.DeviceId is not int deviceId)
        {
            return (null!, Results.Conflict(new { error = "此頻道未綁定 ONVIF 設備，無法使用 IO" }));
        }

        var device = devices.Get(deviceId);
        if (device is null)
        {
            return (null!, Results.NotFound(new { error = "頻道綁定的設備已不存在" }));
        }

        return (device, null);
    }

    /// <summary>把 ONVIF 例外轉成 HTTP：不支援 IO 是 409，其餘裝置端失敗是 502。</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OnvifException ex)
        {
            return ex.Code == OnvifErrorCode.NotSupported
                ? Results.Conflict(new { error = ex.Message, code = ex.Code.ToString() })
                : Results.Json(
                    new { error = ex.Message, code = ex.Code.ToString() },
                    statusCode: StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }
}

public sealed record IoInputsResponse(IReadOnlyList<IoInputInfo> Inputs);
public sealed record IoOutputsResponse(IReadOnlyList<IoOutputInfo> Outputs);
public sealed record IoInputInfo(string Token, int Id, string Name, string Label);
public sealed record IoOutputInfo(string Token, string Mode, bool Bistable, string Name, string Label);
public sealed record IoRelayStateRequest(bool Active);

/// <summary>攝影機 IO 操作；抽離是為了在端點測試注入假物件（見 <see cref="IoEndpoints"/> 說明）。</summary>
public interface IOnvifIoGateway
{
    Task<IReadOnlyList<IoInputInfo>> GetDigitalInputsAsync(DeviceRecord device, CancellationToken cancellation);

    Task<IReadOnlyList<IoOutputInfo>> GetRelayOutputsAsync(DeviceRecord device, CancellationToken cancellation);

    Task SetRelayOutputAsync(DeviceRecord device, string relayToken, bool active, CancellationToken cancellation);
}

/// <summary>預設實作：以 DPAPI 解出的憑證連上設備的 ONVIF 端點（與桌機同一條解析路徑）。</summary>
public sealed class OnvifIoGateway : IOnvifIoGateway
{
    public async Task<IReadOnlyList<IoInputInfo>> GetDigitalInputsAsync(DeviceRecord device, CancellationToken cancellation)
    {
        using var service = Create(device);
        var inputs = await service.GetDigitalInputsAsync(cancellation).ConfigureAwait(false);
        return inputs
            .Select(i => new IoInputInfo(i.Token, i.Id, i.Name, i.DisplayLabel))
            .ToList();
    }

    public async Task<IReadOnlyList<IoOutputInfo>> GetRelayOutputsAsync(DeviceRecord device, CancellationToken cancellation)
    {
        using var service = Create(device);
        var outputs = await service.GetRelayOutputsAsync(cancellation).ConfigureAwait(false);
        return outputs
            .Select(o => new IoOutputInfo(o.Token, o.Mode, o.IsBistable, o.Name, o.DisplayLabel))
            .ToList();
    }

    public async Task SetRelayOutputAsync(DeviceRecord device, string relayToken, bool active, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.SetRelayOutputStateAsync(relayToken, active, cancellation).ConfigureAwait(false);
    }

    private static OnvifDeviceService Create(DeviceRecord device)
    {
        var host = $"http://{device.Ip}:{device.Port}/onvif/device_service";
        // 以 SafeUnprotect 而非 Unprotect：舊資料可能為明文或由其他機器／帳戶加密，
        // 直接解密會擲出例外而讓整個請求變成 500（與 PTZ gateway 的處理一致）。
        var password = string.IsNullOrWhiteSpace(device.PasswordEncrypted)
            ? null
            : DeviceRepository.SafeUnprotect(device.PasswordEncrypted);
        return new OnvifDeviceService(host, device.Username, password);
    }
}
