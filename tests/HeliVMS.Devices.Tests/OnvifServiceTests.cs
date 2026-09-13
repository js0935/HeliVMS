using System.Net;
using System.Text;
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

    public sealed record RequestSnapshot(string Url, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, string, string> _resolve;

        public RecordingHandler(Func<string, string, string> resolve) => _resolve = resolve;

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(request.RequestUri!.ToString(), body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_resolve(request.RequestUri.ToString(), body), Encoding.UTF8, "application/soap+xml"),
            };
        }
    }
}