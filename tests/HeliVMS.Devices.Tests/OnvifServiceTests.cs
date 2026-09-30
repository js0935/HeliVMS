using System.Xml.Linq;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證對設備發出的 SOAP 封包格式與回應解析（無實體網路）。</summary>
public class OnvifServiceTests
{
    private const string CapsResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:GetCapabilitiesResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:Capabilities>
                <td:Media><td:XAddr>http://192.168.1.5/onvif/Media</td:XAddr></td:Media>
              </td:Capabilities>
            </td:GetCapabilitiesResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string ProfilesResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:GetProfilesResponse xmlns:td="http://www.onvif.org/ver10/media/wsdl">
              <td:Profiles token="MainProfile"><td:Name>MainProfile</td:Name></td:Profiles>
              <td:Profiles token="SubProfile"><td:Name>SubProfile</td:Name></td:Profiles>
            </td:GetProfilesResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string StreamUriHostnameResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:GetStreamUriResponse xmlns:td="http://www.onvif.org/ver10/media/wsdl">
              <td:MediaUri><td:Uri>rtsp://cam-name.local/Streaming/Channels/101</td:Uri></td:MediaUri>
            </td:GetStreamUriResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string DateTimeResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:GetSystemDateAndTimeResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:SystemDateAndTime><td:UTCDateTime>
                <td:Date><td:Year>2026</td:Year><td:Month>9</td:Month><td:Day>13</td:Day></td:Date>
                <td:Time><td:Hour>5</td:Hour><td:Minute>30</td:Minute><td:Second>0</td:Second></td:Time>
              </td:UTCDateTime></td:SystemDateAndTime>
            </td:GetSystemDateAndTimeResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string CapsAllWithPtzResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:GetCapabilitiesResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:Capabilities>
                <td:Media><td:XAddr>http://192.168.1.5/onvif/Media</td:XAddr></td:Media>
                <td:PTZ><td:XAddr>http://192.168.1.5/onvif/ptz_service</td:XAddr></td:PTZ>
              </td:Capabilities>
            </td:GetCapabilitiesResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string EmptyBodyResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body />
        </s:Envelope>
        """;

    private const string PresetsResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <tptz:GetPresetsResponse xmlns:tptz="http://www.onvif.org/ver10/ptz/wsdl">
              <tptz:Preset token="p_main_preset"><tptz:Name>入口監看</tptz:Name></tptz:Preset>
              <tptz:Preset token="p_park"><tptz:Name>停車場</tptz:Name></tptz:Preset>
            </tptz:GetPresetsResponse>
          </s:Body>
        </s:Envelope>
        """;

    /// <summary>GetProfiles 請求之信封應含 Media Action 與 Body；並解析出 2 個 Profile。</summary>
    [Fact]
    public async Task GetProfiles_KeepsMediaAction_AndParsesProfiles()
    {
        var handler = new RecordingHandler((url, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsResponse : ProfilesResponse);

        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("MainProfile", profiles[0].Name);

        var profileRequest = handler.Requests
            .Single(r => r.Body.Contains("GetProfiles", StringComparison.Ordinal))
            .Body;
        Assert.Contains("http://www.onvif.org/ver10/media/wsdl/GetProfiles", profileRequest);
        Assert.Contains("http://www.onvif.org/ver10/media/wsdl", profileRequest);
        Assert.Contains("http://www.w3.org/2003/05/soap-envelope", profileRequest);
        Assert.Equal("http://192.168.1.5/onvif/Media", handler.Requests
            .Single(r => r.Body.Contains("GetProfiles", StringComparison.Ordinal)).Url);
    }

    /// <summary>提供帳密時，Header 應附 WS-Security UsernameToken（PasswordDigest）。</summary>
    [Fact]
    public async Task Envelope_WithCredentials_AttachesUsernameTokenDigest()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsResponse : ProfilesResponse);

        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", "admin", "secret", handler);
        await service.GetProfilesAsync();

        var request = handler.Requests.Single(r => r.Body.Contains("GetProfiles", StringComparison.Ordinal)).Body;
        var security = XDocument.Parse(request).Descendants().Single(e => e.Name.LocalName == "Security");
        Assert.NotNull(security);
        Assert.Equal("admin", security.Descendants().Single(e => e.Name.LocalName == "Username").Value);

        var password = security.Descendants().Single(e => e.Name.LocalName == "Password");
        Assert.Equal(
            "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest",
            password.Attribute("Type")?.Value);
        Assert.NotEmpty(password.Value);
        Assert.NotEmpty(security.Descendants().Single(e => e.Name.LocalName == "Nonce").Value);
        Assert.NotEmpty(security.Descendants().Single(e => e.Name.LocalName == "Created").Value);
    }

    /// <summary>GetSystemDateAndTime 回應之 UTC 時間解析。</summary>
    [Fact]
    public async Task GetSystemDateTime_ParsesUtc()
    {
        var handler = new RecordingHandler((_, _) => DateTimeResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        var utc = await service.GetSystemDateTimeUtcAsync();

        Assert.Equal(new DateTimeOffset(2026, 9, 13, 5, 30, 0, TimeSpan.Zero), utc);
    }

    /// <summary>設備回傳主機名（非 IP）時，RTSP URI 主機應替換為連線 IP。</summary>
    [Fact]
    public async Task GetProfiles_NormalizesStreamUriHost()
    {
        var handler = new RecordingHandler((_, body) =>
        {
            if (body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return CapsResponse;
            }

            if (body.Contains("GetStreamUri", StringComparison.Ordinal))
            {
                return StreamUriHostnameResponse;
            }

            return ProfilesResponse;
        });

        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.StreamUri == "rtsp://192.168.1.5/Streaming/Channels/101");
    }

    /// <summary>GetCapabilities（Category=All）回應含 Media＋PTZ XAddr 時，應解析出 PTZ 位址。</summary>
    [Fact]
    public async Task GetCapabilities_WithPtzAddress_ParsesPtz()
    {
        var handler = new RecordingHandler((_, _) => CapsAllWithPtzResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        Assert.False(service.HasPtz);
        await service.EnsurePtzCapabilityAsync();

        Assert.True(service.HasPtz);
        Assert.Equal("http://192.168.1.5/onvif/ptz_service", service.PtzXAddr);
    }

    /// <summary>ContinuousMove 應送 ptZ 端點並含 Velocity（PanTilt/Zoom 屬性值。Culture 依 Invariant）。</summary>
    [Fact]
    public async Task ContinuousMove_PostsVelocity_ToPtzEndpoint()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await service.ContinuousMoveAsync("MainProfile", 0.5, -0.25, 0.1);

        var request = handler.Requests.Single(r => r.Body.Contains("ContinuousMove", StringComparison.Ordinal));
        Assert.Equal("http://192.168.1.5/onvif/ptz_service", request.Url);
        Assert.Contains("http://www.onvif.org/ver10/ptz/wsdl/ContinuousMove", request.Body);
        Assert.Contains("MainProfile", request.Body);
        Assert.Contains("x=\"0.5\"", request.Body);
        Assert.Contains("y=\"-0.25\"", request.Body);
        Assert.Contains("x=\"0.1\"", request.Body);
    }

    /// <summary>GetProfiles 應解析 PTZConfiguration/NodeToken，且經分類後仍保留。</summary>
    [Fact]
    public async Task GetProfiles_ParsesPtzNodeToken_AndSurvivesClassification()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? CapsAllWithPtzResponse
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.ProfilesWithPtzNode("MainProfile", "camctrl_c1")
                    : OnvifTestHttp.StreamUri("rtsp://192.168.1.5/onvif/Media"));

        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);
        var profiles = await service.GetProfilesAsync();

        var profile = Assert.Single(profiles);
        Assert.Equal("camctrl_c1", profile.PtzNodeToken);

        // WithStreamUri / WithRole 都必須保留，否則分類後 node token 會被遺失。
        Assert.Equal("camctrl_c1", profile.WithRole(OnvifStreamRole.Main).PtzNodeToken);
        Assert.Equal("camctrl_c1", profile.WithStreamUri("rtsp://x/y").PtzNodeToken);
    }

    /// <summary>PTZ 動作應帶入先前由 GetProfiles 取得的 NodeToken（多節點設備必需）。</summary>
    [Fact]
    public async Task PtzActions_IncludeNodeToken_AfterProfileToken()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? CapsAllWithPtzResponse
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.ProfilesWithPtzNode("MainProfile", "camctrl_c1")
                    : body.Contains("GetPresets", StringComparison.Ordinal)
                        ? PresetsResponse
                        : EmptyBodyResponse);

        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);
        await service.GetProfilesAsync();

        await service.GetPtzStatusAsync("MainProfile");
        await service.ContinuousMoveAsync("MainProfile", 0.5, 0, 0);
        await service.StopPtzAsync("MainProfile");
        await service.HomeAsync("MainProfile");
        await service.AbsoluteMoveAsync("MainProfile", 0.1, 0.1, 0.1);
        await service.GetPtzPresetsAsync("MainProfile");
        await service.GotoPtzPresetAsync("MainProfile", "p_main_preset");
        await service.SetPtzPresetAsync("MainProfile", "入口");
        await service.RemovePtzPresetAsync("MainProfile", "p_main_preset");

        var actions = new[] { "GetStatus", "ContinuousMove", "Stop", "Home", "AbsoluteMove", "GetPresets", "GotoPreset", "SetPreset", "RemovePreset" };
        foreach (var action in actions)
        {
            var request = handler.Requests.Single(r => r.Body.Contains(action, StringComparison.Ordinal));

            // 元素順序：ProfileToken 之後才是 NodeToken（符合 ptz.wsdl）。
            var profileIndex = request.Body.IndexOf("MainProfile", StringComparison.Ordinal);
            var nodeIndex = request.Body.IndexOf("NodeToken", StringComparison.Ordinal);
            var nodeValueIndex = request.Body.IndexOf("camctrl_c1", StringComparison.Ordinal);

            Assert.True(nodeIndex > profileIndex, $"{action} 的 NodeToken 應置於 ProfileToken 之後");
            Assert.True(nodeValueIndex > nodeIndex, $"{action} 應送出 camctrl_c1");
        }
    }

    /// <summary>未取得 profile（無 NodeToken）時應省略該元素，交由設備套用預設節點。</summary>
    [Fact]
    public async Task PtzActions_WithoutKnownNodeToken_OmitNodeToken()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await service.StopPtzAsync("MainProfile");

        var request = handler.Requests.Single(r => r.Body.Contains("Stop", StringComparison.Ordinal));
        Assert.DoesNotContain("NodeToken", request.Body, StringComparison.Ordinal);
    }

    /// <summary>GetPtzPresets 應解析 Preset token 與名稱。</summary>
    [Fact]
    public async Task GetPtzPresets_ParsesPresets()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : PresetsResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        var presets = await service.GetPtzPresetsAsync("MainProfile");

        Assert.Equal(2, presets.Count);
        Assert.Equal("p_main_preset", presets[0].Token);
        Assert.Equal("入口監看", presets[0].Name);
    }

    /// <summary>Stop 應送至 ptZ 端點並含 PanTilt/Zoom=true。</summary>
    [Fact]
    public async Task Stop_PostsStop_ToPtzEndpoint()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await service.StopPtzAsync("MainProfile");

        var request = handler.Requests.Single(r => r.Body.Contains("Stop", StringComparison.Ordinal));
        Assert.Equal("http://192.168.1.5/onvif/ptz_service", request.Url);
        Assert.Contains("http://www.onvif.org/ver10/ptz/wsdl/Stop", request.Body);
        Assert.Contains("MainProfile", request.Body);
        Assert.Contains("PanTilt", request.Body);
        Assert.Contains("Zoom", request.Body);
    }

    /// <summary>AbsoluteMove 應送至 ptz 端點並含 Position 的 PanTilt/Zoom 值。</summary>
    [Fact]
    public async Task AbsoluteMove_PostsPosition_ToPtzEndpoint()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await service.AbsoluteMoveAsync("MainProfile", 0.6, -0.3, 0.5);

        var request = handler.Requests.Single(r => r.Body.Contains("AbsoluteMove", StringComparison.Ordinal));
        Assert.Equal("http://192.168.1.5/onvif/ptz_service", request.Url);
        Assert.Contains("http://www.onvif.org/ver10/ptz/wsdl/AbsoluteMove", request.Body);
        Assert.Contains("MainProfile", request.Body);
        Assert.Contains("x=\"0.6\"", request.Body);
        Assert.Contains("y=\"-0.3\"", request.Body);
        Assert.Contains("x=\"0.5\"", request.Body);
    }

    /// <summary>Home 應送至 ptz 端點（GotoHomePosition）。</summary>
    [Fact]
    public async Task Home_PostsHomeAction_ToPtzEndpoint()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await service.HomeAsync("MainProfile");

        var request = handler.Requests.Single(r => r.Body.Contains("Home", StringComparison.Ordinal));
        Assert.Equal("http://192.168.1.5/onvif/ptz_service", request.Url);
        Assert.Contains("MainProfile", request.Body);
    }

    /// <summary>RemovePtzPreset 應送至 ptz 端點並含 ProfileToken/PresetToken。</summary>
    [Fact]
    public async Task RemovePtzPreset_PostsRemoveAction_ToPtzEndpoint()
    {
        var handler = new RecordingHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal) ? CapsAllWithPtzResponse : EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await service.RemovePtzPresetAsync("MainProfile", "p_main");

        var request = handler.Requests.Single(r => r.Body.Contains("RemovePreset", StringComparison.Ordinal));
        Assert.Equal("http://192.168.1.5/onvif/ptz_service", request.Url);
        Assert.Contains("MainProfile", request.Body);
        Assert.Contains("p_main", request.Body);
    }
}
