using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// Web 端 PTZ 控制（M25 桌機 PTZ 的遠端對應）：移動／停止／Home／預設點。
///
/// 抽成 <see cref="IOnvifPtzGateway"/> 是為了讓端點層（頻道→設備解析、輸入驗證、HTTP 狀態碼）
/// 能與 ONVIF I/O 分離測試：端點測試注入假 gateway，不必真的連上攝影機，
/// 也不需要在測試裡複製一份 ONVIF SOAP 假伺服器。真正的 gateway 只是
/// <see cref="OnvifDeviceService"/> 的薄配接，其 SOAP 行為已由 HeliVMS.Devices.Tests 涵蓋。
///
/// 授權：PTZ 沒有專屬功能旗標（§19.3 的 remote／ai／schedule／adsso 皆非此意），
/// 故與其他核心端點一樣交由 <see cref="LicenseGateMiddleware"/> 的前綴規則決定，
/// 這裡刻意不新增規則，避免把基本版客戶的本機網頁主控台 PTZ 一併擋掉。
/// </summary>
public static class PtzEndpoints
{
    /// <summary>方向鍵送出的速度向量；各軸限制在 ONVIF 規範的 -1..1。</summary>
    private const double MaxVelocity = 1.0;

    public static void Map(WebApplication app)
    {
        var ptz = app.MapGroup("/api/ptz");

        ptz.MapGet("/{channelId:int}/profiles", async (
            int channelId,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
                Results.Ok(new PtzProfilesResponse(
                    await gateway.GetProfilesAsync(device, cancellation).ConfigureAwait(false))));
        });

        ptz.MapPost("/{channelId:int}/move", async (
            int channelId,
            PtzMoveRequest body,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            // 非有限數（NaN／Infinity）會在序列化成 SOAP 屬性時產生非數值，裝置端直接 Fault。
            if (!double.IsFinite(body.Pan) || !double.IsFinite(body.Tilt) || !double.IsFinite(body.Zoom))
            {
                return Results.BadRequest(new { error = "移動速度必須是有限數值" });
            }

            return await Run(async () =>
            {
                await gateway.ContinuousMoveAsync(
                    device,
                    body.ProfileToken,
                    Clamp(body.Pan),
                    Clamp(body.Tilt),
                    Clamp(body.Zoom),
                    cancellation).ConfigureAwait(false);
                return Results.Ok();
            });
        });

        ptz.MapPost("/{channelId:int}/stop", async (
            int channelId,
            PtzProfileRequest body,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
            {
                await gateway.StopAsync(device, body.ProfileToken, cancellation).ConfigureAwait(false);
                return Results.Ok();
            });
        });

        ptz.MapPost("/{channelId:int}/home", async (
            int channelId,
            PtzProfileRequest body,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
            {
                await gateway.HomeAsync(device, body.ProfileToken, cancellation).ConfigureAwait(false);
                return Results.Ok();
            });
        });

        ptz.MapGet("/{channelId:int}/presets", async (
            int channelId,
            string? profileToken,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
                Results.Ok(await gateway.GetPresetsAsync(device, profileToken, cancellation).ConfigureAwait(false)));
        });

        ptz.MapPost("/{channelId:int}/presets", async (
            int channelId,
            PtzPresetSaveRequest body,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            var name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim();
            return await Run(async () =>
                Results.Ok(new PtzPresetInfo(
                    await gateway.SetPresetAsync(device, body.ProfileToken, name, cancellation).ConfigureAwait(false),
                    name ?? string.Empty)));
        });

        ptz.MapPost("/{channelId:int}/presets/{presetToken}/goto", async (
            int channelId,
            string presetToken,
            PtzProfileRequest body,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
            {
                await gateway.GotoPresetAsync(device, body.ProfileToken, presetToken, cancellation).ConfigureAwait(false);
                return Results.Ok();
            });
        });

        ptz.MapDelete("/{channelId:int}/presets/{presetToken}", async (
            int channelId,
            string presetToken,
            string? profileToken,
            ChannelRepository channels,
            DeviceRepository devices,
            IOnvifPtzGateway gateway,
            CancellationToken cancellation) =>
        {
            var (device, error) = Resolve(channels, devices, channelId);
            if (error is not null)
            {
                return error;
            }

            return await Run(async () =>
            {
                await gateway.RemovePresetAsync(device, profileToken, presetToken, cancellation).ConfigureAwait(false);
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
            return (null!, Results.Conflict(new { error = "此頻道未綁定 ONVIF 設備，無法 PTZ" }));
        }

        var device = devices.Get(deviceId);
        if (device is null)
        {
            return (null!, Results.NotFound(new { error = "頻道綁定的設備已不存在" }));
        }

        return (device, null);
    }

    /// <summary>把 ONVIF 例外轉成 HTTP：不支援 PTZ 是 409，其餘裝置端失敗是 502。</summary>
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

    private static double Clamp(double value) => Math.Clamp(value, -MaxVelocity, MaxVelocity);
}

public sealed record PtzProfilesResponse(IReadOnlyList<PtzProfileInfo> Profiles);
public sealed record PtzProfileInfo(string Token, string Name, string Label);
public sealed record PtzPresetInfo(string Token, string Name);
public sealed record PtzMoveRequest(string? ProfileToken, double Pan, double Tilt, double Zoom);
public sealed record PtzProfileRequest(string? ProfileToken);
public sealed record PtzPresetSaveRequest(string? ProfileToken, string? Name);

/// <summary>PTZ 裝置操作；抽離是為了在端點測試注入假物件（見 <see cref="PtzEndpoints"/> 說明）。</summary>
public interface IOnvifPtzGateway
{
    Task<IReadOnlyList<PtzProfileInfo>> GetProfilesAsync(DeviceRecord device, CancellationToken cancellation);

    Task ContinuousMoveAsync(DeviceRecord device, string? profileToken, double pan, double tilt, double zoom, CancellationToken cancellation);

    Task StopAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation);

    Task HomeAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation);

    Task<IReadOnlyList<PtzPresetInfo>> GetPresetsAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation);

    Task<string> SetPresetAsync(DeviceRecord device, string? profileToken, string? name, CancellationToken cancellation);

    Task GotoPresetAsync(DeviceRecord device, string? profileToken, string presetToken, CancellationToken cancellation);

    Task RemovePresetAsync(DeviceRecord device, string? profileToken, string presetToken, CancellationToken cancellation);
}

/// <summary>預設實作：以 DPAPI 解出的憑證連上設備的 ONVIF 端點（與桌機 <c>PtzWindow</c> 同一條解析路徑）。</summary>
public sealed class OnvifPtzGateway : IOnvifPtzGateway
{
    public async Task<IReadOnlyList<PtzProfileInfo>> GetProfilesAsync(DeviceRecord device, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.EnsurePtzCapabilityAsync(cancellation).ConfigureAwait(false);
        if (!service.HasPtz)
        {
            throw new OnvifException(OnvifErrorCode.NotSupported, "設備不支援 ONVIF PTZ。");
        }

        var profiles = await service.GetProfilesAsync(cancellation).ConfigureAwait(false);
        return profiles
            .Select(p => new PtzProfileInfo(p.Token, p.Name, p.DisplayLabel))
            .ToList();
    }

    public async Task ContinuousMoveAsync(DeviceRecord device, string? profileToken, double pan, double tilt, double zoom, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.ContinuousMoveAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), pan, tilt, zoom, cancellation).ConfigureAwait(false);
    }

    public async Task StopAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.StopPtzAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
    }

    public async Task HomeAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.HomeAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PtzPresetInfo>> GetPresetsAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation)
    {
        using var service = Create(device);
        var presets = await service.GetPtzPresetsAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
        return presets.Select(p => new PtzPresetInfo(p.Token, p.Name)).ToList();
    }

    public async Task<string> SetPresetAsync(DeviceRecord device, string? profileToken, string? name, CancellationToken cancellation)
    {
        using var service = Create(device);
        var resolvedName = string.IsNullOrWhiteSpace(name)
            ? $"預設點 {DateTime.Now:HH:mm:ss}"
            : name;
        return await service.SetPtzPresetAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), resolvedName, cancellation).ConfigureAwait(false);
    }

    public async Task GotoPresetAsync(DeviceRecord device, string? profileToken, string presetToken, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.GotoPtzPresetAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), presetToken, cancellation).ConfigureAwait(false);
    }

    public async Task RemovePresetAsync(DeviceRecord device, string? profileToken, string presetToken, CancellationToken cancellation)
    {
        using var service = Create(device);
        await service.RemovePtzPresetAsync(await ResolveAsync(service, profileToken, cancellation).ConfigureAwait(false), presetToken, cancellation).ConfigureAwait(false);
    }

    private static OnvifDeviceService Create(DeviceRecord device)
    {
        var host = $"http://{device.Ip}:{device.Port}/onvif/device_service";
        // 以 SafeUnprotect 而非 Unprotect：舊資料可能為明文或由其他機器／帳戶加密，
        // 直接解密會擲出例外而讓整個請求變成 500（與桌機 PtzWindow 的處理一致）。
        var password = string.IsNullOrWhiteSpace(device.PasswordEncrypted)
            ? null
            : DeviceRepository.SafeUnprotect(device.PasswordEncrypted);
        return new OnvifDeviceService(host, device.Username, password);
    }

    /// <summary>
    /// 未指定 profileToken 時取第一個媒體 Profile；與桌機 PTZ 視窗「預設選第一個 Profile」一致。
    /// 指定了就沿用，讓後續的 PTZ 動作自己按需要解析能力，避免多一次 GetProfiles 往返。
    /// </summary>
    private static async Task<string> ResolveAsync(OnvifDeviceService service, string? profileToken, CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(profileToken))
        {
            return profileToken;
        }

        await service.EnsurePtzCapabilityAsync(cancellation).ConfigureAwait(false);
        if (!service.HasPtz)
        {
            throw new OnvifException(OnvifErrorCode.NotSupported, "設備不支援 ONVIF PTZ。");
        }

        var profiles = await service.GetProfilesAsync(cancellation).ConfigureAwait(false);
        if (profiles.Count == 0)
        {
            throw new OnvifException(OnvifErrorCode.NotSupported, "設備沒有可用的媒體 Profile。");
        }

        return profiles[0].Token;
    }
}
