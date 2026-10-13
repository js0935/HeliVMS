using System.Net;
using System.Xml.Linq;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 驗證多廠牌相容行為：HTTP Digest／Basic 認證、WS-Security 明碼回退、SOAP 1.1／Fault 位置、
/// GetCapabilities 路徑精確解析與 Category 回退、XAddr 埠校正、Media 路徑探測、
/// Profile 解析度／主子碼流、暫時性錯誤重試。
/// </summary>
public class OnvifServiceCompatibilityTests
{
    private const string DeviceXAddr = "http://192.168.1.64/onvif/device_service";
    private const string DigestChallenge =
        "Digest realm=\"IPC-Camera\", nonce=\"abc123\", qop=\"auth\", algorithm=MD5, opaque=\"op\"";

    private static readonly OnvifClientOptions NoRetry =
        OnvifClientOptions.Default with { RetryCount = 0, RetryDelay = TimeSpan.Zero };

    // ---- 認證：HTTP Digest / Basic ----

    [Fact]
    public async Task GetInfo_WithHttpDigestChallenge_RetriesWithAuthorization()
    {
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, "<html>denied</html>", DigestChallenge)
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);

        // GetDeviceInformation(401) + 重試 + GetSystemDateAndTime
        Assert.Equal(3, handler.CallCount);

        var retry = handler.Requests[1];
        Assert.NotNull(retry.Authorization);
        Assert.StartsWith("Digest ", retry.Authorization, StringComparison.Ordinal);
        Assert.Contains("username=\"admin\"", retry.Authorization, StringComparison.Ordinal);
        Assert.Contains("uri=\"/onvif/device_service\"", retry.Authorization, StringComparison.Ordinal);
        Assert.Contains("qop=auth", retry.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_DigestDevice_Authorization不帶algorithm參數()
    {
        // 廠牌差異（實測 HOEM 白牌 IPC）：挑戰宣告 algorithm=MD5，但 Authorization 帶上
        // algorithm=MD5 反而一直回 401；省略該參數（RFC 7616 預設即 MD5）才通過。
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, wwwAuthenticate: DigestChallenge)
                : request.Headers.Authorization.ToString().Contains("algorithm=", StringComparison.Ordinal)
                    ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, wwwAuthenticate: DigestChallenge)
                    : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);
        Assert.NotNull(handler.Requests[1].Authorization);
        Assert.DoesNotContain("algorithm=", handler.Requests[1].Authorization!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_WithHttpBasicChallenge_SendsBasicHeader()
    {
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, wwwAuthenticate: "Basic realm=\"IPC\"")
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        await service.GetInfoAsync();

        Assert.Equal(3, handler.CallCount);
        Assert.StartsWith("Basic ", handler.Requests[1].Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_DigestChallengeReused_SubsequentRequestsCarryAuthorization()
    {
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, wwwAuthenticate: DigestChallenge)
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        await service.GetInfoAsync();
        await service.GetSystemDateTimeUtcAsync();

        // GetInfo(3：401 + 重試 + GetSystemDateAndTime) + GetSystemDateAndTime(1)
        // 挑戰僅被套用一次，後續請求直接帶認證
        Assert.Equal(4, handler.CallCount);
        Assert.All(handler.Requests.Skip(1), r => Assert.NotNull(r.Authorization));
    }

    [Fact]
    public async Task GetInfo_WrongCredentials_ThrowsUnauthorized()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Status(HttpStatusCode.Unauthorized, "<html>denied</html>", DigestChallenge));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "wrong", handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetInfo_401WithoutChallenge_ThrowsUnauthorized()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Status(HttpStatusCode.Unauthorized, "<html>denied</html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);
        Assert.Equal(1, handler.CallCount);
    }

    // ---- 認證：WS-Security 明碼回退與 SOAP 1.1 ----

    [Fact]
    public async Task GetInfo_WsSecurityDigestRejected_RetriesWithPasswordText()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("#PasswordText", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml)
                : OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:NotAuthorized", "認證失敗")));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);

        // GetDeviceInformation(Digest→Fault) + 匿名時鐘探測(→Fault) + GetDeviceInformation(Text→OK)
        // + GetSystemDateAndTime(Text→OK)
        Assert.Equal(4, handler.CallCount);
        Assert.Contains(
            "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest",
            handler.Requests[0].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText",
            handler.Requests[2].Body,
            StringComparison.Ordinal);
        Assert.Contains(">secret<", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 實測海康：設備時鐘慢主機 30 分鐘時，正確帳密的 PasswordDigest 被回「sender not authorized」。
    /// 應先匿名查詢設備時間量測偏差，校正 UsernameToken 的 Created 後以同一（Digest）模式重試成功，
    /// 而非誤判為帳密錯誤或降級為明碼。
    /// </summary>
    [Fact]
    public async Task GetInfo_設備時鐘偏差過大_匿名查詢設備時間校正時間戳後成功()
    {
        var handler = ClockSkewedDevice(TimeSpan.FromMinutes(-30));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);

        // [0] 初次以本機時間送出的 GetDeviceInformation（未校正）→ 遭拒
        var initial = ReadCreatedUtc(handler.Requests[0].Body);
        Assert.NotNull(initial);
        Assert.True((initial!.Value - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(5));

        // [1] 匿名查詢設備時間（不帶 WS-Security 時間戳）
        Assert.Null(ReadCreatedUtc(handler.Requests[1].Body));

        // [2] 重試的 GetDeviceInformation，Created 已校正至設備時鐘（慢 30 分鐘）
        Assert.Contains("GetDeviceInformation", handler.Requests[2].Body, StringComparison.Ordinal);
        var corrected = ReadCreatedUtc(handler.Requests[2].Body);
        Assert.NotNull(corrected);
        Assert.True(corrected!.Value < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));

        // 時鐘校正成功即不應再降級為明碼
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("#PasswordText", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInfo_設備時鐘偏差但關閉自動校正_仍回報未授權()
    {
        var handler = ClockSkewedDevice(TimeSpan.FromMinutes(-30));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            "admin",
            "secret",
            handler,
            NoRetry with { AutoClockSkew = false });

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);

        // 停用後不應匿名查詢設備時間：每個請求都應帶 WS-Security 時間戳
        Assert.All(handler.Requests, r => Assert.NotNull(ReadCreatedUtc(r.Body)));
    }

    [Fact]
    public async Task GetInfo_Soap11Fault_IsUnauthorized()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Soap(OnvifTestHttp.Soap11Fault("ter:NotAuthorized")));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);
    }

    [Fact]
    public async Task GetInfo_ActionNotSupportedFault_IsNotSupported()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:ActionNotSupported")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
        Assert.Equal("ter:ActionNotSupported", error.FaultCode);
    }

    [Fact]
    public async Task GetInfo_NonSoapBody_ThrowsBadResponse()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Soap("<html><body>404</body></html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.BadResponse, error.Code);
    }

    // ---- GetCapabilities 解析與回退 ----

    [Fact]
    public async Task GetProfiles_MultipleXAddrs_ResolvesMediaNotFirstElement()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(
                    ("Device", "http://192.168.1.64/onvif/device_service"),
                    ("Imaging", "http://192.168.1.64/onvif/imaging"),
                    ("Media", "http://192.168.1.64/onvif/Media"),
                    ("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/Streaming/Channels/101")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/Media", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Url == "http://192.168.1.64/onvif/Media");
    }

    [Fact]
    public async Task GetProfiles_MediaCategoryUnsupported_FallsBackToAll()
    {
        var handler = new ScriptedHandler((_, body) =>
        {
            if (!body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream"));
            }

            return body.Contains(">Media<", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:ActionNotSupported"))
                : OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")));
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/Media", service.MediaXAddr);
        var capabilityRequests = handler.Requests.Where(r => r.Body.Contains("GetCapabilities", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, capabilityRequests.Count);
        Assert.Contains(">Media<", capabilityRequests[0].Body, StringComparison.Ordinal);
        Assert.Contains(">All<", capabilityRequests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProfiles_XAddrWithoutServicePort_UsesDeviceServicePort()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/Streaming/Channels/101")));

        using var service = new OnvifDeviceService("http://192.168.1.64:8000/onvif/device_service", null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64:8000/onvif/Media", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Url == "http://192.168.1.64:8000/onvif/Media");
        Assert.Contains(profiles, p => p.StreamUri == "rtsp://192.168.1.64/Streaming/Channels/101");
    }

    [Fact]
    public async Task GetProfiles_NoCapabilitiesSupport_ProbesMediaPathCandidates()
    {
        var handler = new ScriptedHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/onvif/media_service", StringComparison.Ordinal))
            {
                return body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/live"));
            }

            return OnvifTestHttp.Status(HttpStatusCode.NotFound, "<html>404</html>");
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/media_service", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Url == "http://192.168.1.64/onvif/Media");
        Assert.Contains(profiles, p => p.StreamUri == "rtsp://192.168.1.64/live");
    }

    [Fact]
    public async Task GetProfiles_WithoutMediaService_ThrowsNotSupported()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.NotFound, "<html>404</html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetProfilesAsync());
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
    }

    [Fact]
    public async Task GetProfiles_Capabilities缺少Media_改用GetServices解析()
    {
        // 部分 ONVIF 2.x 設備的 GetCapabilities 回應不含 Media 節點，但 GetServices 完整列出
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Device", "http://192.168.1.64/onvif/device_service")))
                : body.Contains("GetServices", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.Services(
                        ("http://www.onvif.org/ver10/media/wsdl", "http://192.168.1.64/onvif/Media")))
                    : body.Contains("GetProfiles", StringComparison.Ordinal)
                        ? OnvifTestHttp.Soap(ProfilesFixture())
                        : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/Media", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Body.Contains("GetServices", StringComparison.Ordinal));

        // 已由 GetServices 取得端點，不應再退回路徑探測
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/onvif/media_service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsurePtzCapability_Capabilities缺少Ptz_改用GetServices解析()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Device", "http://192.168.1.64/onvif/device_service")))
                : body.Contains("GetServices", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.Services(
                        ("http://www.onvif.org/ver10/ptz/wsdl", "http://192.168.1.64/onvif/ptz_service")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        await service.EnsurePtzCapabilityAsync();

        Assert.True(service.HasPtz);
        Assert.Equal("http://192.168.1.64/onvif/ptz_service", service.PtzXAddr);
    }

    [Fact]
    public async Task GetPtzStatus_WithoutPtzCapability_ThrowsNotSupported()
    {
        // 不支援 PTZ 的設備：所有路徑（含 GetCapabilities 與 PTZ 探測路徑）皆 404
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Status(HttpStatusCode.NotFound, "<html>404</html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetPtzStatusAsync("MainProfile"));
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
        Assert.False(service.HasPtz);
    }

    // ---- Profile 解析與碼流角色 ----

    [Fact]
    public async Task GetProfiles_ParsesEncoderConfiguration_AndClassifiesStreams()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.Profiles(
                        ("MainProfile", "主碼流", 1920, 1080, "H264", "VideoSource0"),
                        ("SubProfile", "子碼流", 640, 360, "H264", "VideoSource0")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri(
                        body.Contains("MainProfile", StringComparison.Ordinal)
                            ? "rtsp://192.168.1.64/Streaming/Channels/101"
                            : "rtsp://192.168.1.64/Streaming/Channels/102")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        var main = profiles.Single(p => p.Token == "MainProfile");
        Assert.Equal(1920, main.Width);
        Assert.Equal(1080, main.Height);
        Assert.Equal("H264", main.Encoding);
        Assert.Equal("VideoSource0", main.VideoSourceToken);
        Assert.True(main.IsVideo);
        Assert.True(main.IsMainStream);
        Assert.Equal("1920×1080", main.ResolutionLabel);
        Assert.Equal("rtsp://192.168.1.64/Streaming/Channels/101", main.StreamUri);

        var sub = profiles.Single(p => p.Token == "SubProfile");
        Assert.True(sub.IsSubStream);
        Assert.Equal("MainProfile", profiles[0].Token);
    }

    [Fact]
    public async Task GetProfiles_EncoderResolutionNestsWidthHeight_ResolvesResolution()
    {
        // 實機 VIVOTEK 回應：Width/Height 位於 <tt:Resolution> 之下，而非編碼器直接子元素。
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/media_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.ProfilesWithResolutionElement(
                        ("Profile100", "Profile100", 1920, 1080, "H264", "VideoSource1"),
                        ("Profile102", "Profile102", 1280, 720, "H264", "VideoSource1")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/media/stream.sdp")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        var main = profiles.Single(p => p.Token == "Profile100");
        Assert.Equal(1920, main.Width);
        Assert.Equal(1080, main.Height);
        Assert.Equal("1920×1080", main.ResolutionLabel);
        Assert.Equal("H264", main.Encoding);
        Assert.True(main.IsMainStream);

        var sub = profiles.Single(p => p.Token == "Profile102");
        Assert.Equal(1280, sub.Width);
        Assert.Equal(720, sub.Height);
        Assert.Equal("1280×720", sub.ResolutionLabel);
        Assert.True(sub.IsSubStream);

        Assert.Equal("Profile100", profiles[0].Token);
    }

    [Fact]
    public async Task GetProfiles_EncoderWithoutResolution_DoesNotFallBackToSourceBounds()
    {
        // Bounds 是感測器尺寸（1920×1080），不可用來當編碼輸出解析度，否則子碼流會顯示錯誤資訊。
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/media_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.ProfilesWithSourceBoundsOnly(
                        ("Profile1", "Profile1", 1920, 1080, "H264")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/media/stream.sdp")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profile = Assert.Single(await service.GetProfilesAsync());

        Assert.Equal(0, profile.Width);
        Assert.Equal(0, profile.Height);
        Assert.Equal("H264", profile.Encoding);
        Assert.Equal(string.Empty, profile.ResolutionLabel);
    }

    [Fact]
    public async Task GetProfiles_OneStreamUriFails_KeepsOtherProfiles()
    {
        var handler = new ScriptedHandler((_, body) =>
        {
            if (body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")));
            }

            if (body.Contains("GetProfiles", StringComparison.Ordinal))
            {
                return OnvifTestHttp.Soap(OnvifTestHttp.Profiles(
                    ("MainProfile", "主碼流", 1920, 1080, "H264", "VideoSource0"),
                    ("DisabledProfile", "停用", 704, 576, "H264", "VideoSource0")));
            }

            return body.Contains("DisabledProfile", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:InvalidArg", "profile 不存在"))
                : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/Streaming/Channels/101"));
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("rtsp://192.168.1.64/Streaming/Channels/101", profiles.Single(p => p.Token == "MainProfile").StreamUri);
        Assert.Equal(string.Empty, profiles.Single(p => p.Token == "DisabledProfile").StreamUri);
    }

    [Fact]
    public async Task GetProfiles_RespectsMaxStreamUris()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.Profiles(
                        ("Profile_1", "1", 1920, 1080, "H264", "VideoSource0"),
                        ("Profile_2", "2", 704, 576, "H264", "VideoSource0"),
                        ("Profile_3", "3", 640, 360, "H264", "VideoSource0")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream")));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { MaxStreamUris = 1 });

        var profiles = await service.GetProfilesAsync();

        Assert.Equal(3, profiles.Count);
        Assert.Single(handler.Requests, r => r.Body.Contains("GetStreamUri", StringComparison.Ordinal));
        Assert.Equal(1, profiles.Count(p => p.StreamUri.Length > 0));
    }

    [Fact]
    public async Task GetProfiles_EmptyProfileList_ReturnsEmpty()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : OnvifTestHttp.Soap(
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                    "<trt:GetProfilesResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" /></s:Body></s:Envelope>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        Assert.Empty(await service.GetProfilesAsync());
    }

    // ---- 暫時性錯誤重試 ----

    [Fact]
    public async Task GetInfo_ServerError_RetriesOnce()
    {
        var attempts = 0;
        var handler = new ScriptedHandler((_, _) =>
            ++attempts == 1
                ? OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable, "<html>busy</html>")
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { RetryCount = 1 });

        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task GetInfo_PersistentServerError_ThrowsConnection()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable, "<html>busy</html>"));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { RetryCount = 2 });

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Connection, error.Code);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PtzAction_NotRetried_EvenOnServerError()
    {
        var handler = new ScriptedHandler((request, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable, "<html>busy</html>"));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { RetryCount = 3 });

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.StopPtzAsync("MainProfile"));
        Assert.Equal(OnvifErrorCode.Connection, error.Code);

        // GetCapabilities + PTZ 可用性驗證（GetNodes）+ Stop。
        Assert.Equal(3, handler.CallCount);
    }

    /// <summary>
    /// 設備宣告 PTZ XAddr 卻對所有 PTZ 動作回 ter:Namespace（實測 VIVOTEK SD9368-EHL）時，
    /// 應視為不支援 PTZ，讓 UI 不提供無法使用的功能。
    /// </summary>
    [Fact]
    public async Task EnsurePtzCapability_NamespaceFaultOnPtzActions_TreatsPtzAsUnsupported()
    {
        var handler = new ScriptedHandler((request, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : OnvifTestHttp.Soap(
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault>" +
                    "<faultcode>soap:Sender</faultcode><faultstring>Namespace Error</faultstring>" +
                    "<detail><ter:Namespace xmlns:ter=\"http://www.onvif.org/ver10/error\"/></detail>" +
                    "</s:Fault></s:Body></s:Envelope>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        await service.EnsurePtzCapabilityAsync();
        Assert.False(service.HasPtz);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.StopPtzAsync("MainProfile"));
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
    }

    /// <summary>
    /// 僅支援 ver10 PTZ 的設備（實測 VIVOTEK 快速球）只認 ver10 WSDL 定義的動作；對 ver20 才有的
    /// GetServiceCapabilities 會回 ter:Namespace。可用性驗證必須用 ver10 的 GetNodes，否則會把這類
    /// 明明可操控的雲台誤判為「不支援 PTZ」，UI 直接關閉 PTZ 而無任何線索。
    /// </summary>
    [Fact]
    public async Task EnsurePtzCapability_Ver10DeviceRejectingGetServiceCapabilities_StillPtz()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : body.Contains("GetServiceCapabilities", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(
                        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault>" +
                        "<faultcode>soap:Sender</faultcode><faultstring>Namespace Error</faultstring>" +
                        "<detail><ter:Namespace xmlns:ter=\"http://www.onvif.org/ver10/error\"/></detail>" +
                        "</s:Fault></s:Body></s:Envelope>")
                    : OnvifTestHttp.Soap(
                        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                        "<tptz:GetNodesResponse xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\" /></s:Body></s:Envelope>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        await service.EnsurePtzCapabilityAsync();

        Assert.True(service.HasPtz);
        Assert.Equal("http://192.168.1.64/onvif/ptz_service", service.PtzXAddr);

        // 驗證動作必須是 ver10 的 GetNodes；不可送出 ver20 才定義的 GetServiceCapabilities。
        Assert.Contains(handler.Requests, r => r.Body.Contains("GetNodes", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("GetServiceCapabilities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentProfileAndPtzResolution_SharesSingleCapabilityLookup()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(
                    ("Media", "http://192.168.1.64/onvif/Media"),
                    ("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : body.Contains("GetPresets", StringComparison.Ordinal)
                        ? OnvifTestHttp.Soap(
                            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                            "<tptz:GetPresetsResponse xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\" /></s:Body></s:Envelope>")
                        : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        await Task.WhenAll(
            service.GetProfilesAsync(),
            service.GetPtzPresetsAsync("MainProfile"),
            service.GetProfilesAsync());

        var mediaLookups = handler.Requests
            .Count(r => r.Url == DeviceXAddr && r.Body.Contains("GetCapabilities", StringComparison.Ordinal));
        Assert.Equal(2, mediaLookups);
    }

    [Fact]
    public void ReadCapabilityXAddr_PicksRequestedCategoryOnly()
    {
        var body = XDocument.Parse(OnvifTestHttp.Capabilities(
            ("Device", "http://192.168.1.64/onvif/device_service"),
            ("Media", "http://192.168.1.64/onvif/Media"))).Root!
            .Elements().First();

        Assert.Equal("http://192.168.1.64/onvif/Media", OnvifDeviceService.ReadCapabilityXAddr(body, "Media"));
        Assert.Null(OnvifDeviceService.ReadCapabilityXAddr(body, "PTZ"));
    }

    private static string ProfilesFixture() => OnvifTestHttp.Profiles(
        [("MainProfile", "主碼流", 1920, 1080, "H264", "VideoSource0")]);

    /// <summary>
    /// 模擬時鐘偏差的 ONVIF 設備：具 WS-Security 時間戳的請求，若 Created 未落在設備時間
    /// ±5 分鐘內即回 ter:NotAuthorized；匿名請求則回報設備目前時間。
    /// </summary>
    private static ScriptedHandler ClockSkewedDevice(TimeSpan deviceOffset) =>
        new((_, body) =>
        {
            var created = ReadCreatedUtc(body);
            if (created is not null &&
                (created.Value - (DateTimeOffset.UtcNow + deviceOffset)).Duration() > TimeSpan.FromMinutes(5))
            {
                // 實測海康以泛用 fault code 回報，真正線索在 Reason 文字；藉此覆蓋文字判斷路徑
                return OnvifTestHttp.Soap(OnvifTestHttp.Fault("soap:Sender", "sender not authorized"));
            }

            return body.Contains("GetSystemDateAndTime", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.SystemDateAndTime(DateTimeOffset.UtcNow + deviceOffset))
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml);
        });

    /// <summary>取出請求封包中 WS-Security 的 Created 時間戳（UTC）；無則回 null。</summary>
    private static DateTimeOffset? ReadCreatedUtc(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var created = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Created");
        return created is not null &&
               DateTimeOffset.TryParse(
                   created.Value,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out var value)
            ? value
            : null;
    }
}
