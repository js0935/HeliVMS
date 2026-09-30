using System.Net;
using System.Text;
using System.Xml.Linq;

namespace HeliVMS.Devices.Tests;

/// <summary>ONVIF 測試共用之 HTTP 偽裝回應與快照記錄器。</summary>
internal static class OnvifTestHttp
{
    internal const string DeviceInfoXml = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:GetDeviceInformationResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:Manufacturer>HIKVISION</td:Manufacturer>
              <td:Model>DS-2CD2T47G2</td:Model>
              <td:FirmwareVersion>V5.5.800</td:FirmwareVersion>
              <td:SerialNumber>DS2CT47G20001</td:SerialNumber>
              <td:HardwareId>ABCDEF0123456789</td:HardwareId>
            </td:GetDeviceInformationResponse>
          </s:Body>
        </s:Envelope>
        """;

    internal static HttpResponseMessage Soap(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml"),
    };

    internal static HttpResponseMessage Status(
        HttpStatusCode statusCode,
        string? body = null,
        string? wwwAuthenticate = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                body ?? string.Empty,
                Encoding.UTF8,
                "application/soap+xml"),
        };

        if (wwwAuthenticate is not null)
        {
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuthenticate);
        }

        return response;
    }

    /// <summary>SOAP 1.2 Fault（ter:xxx 為 ONVIF 標準錯誤碼）。</summary>
    internal static string Fault(string code, string text = "操作不被支援") => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <s:Fault>
              <s:Code>
                <s:Value>s:Sender</s:Value>
                <s:Subcode><s:Value>{code}</s:Value></s:Subcode>
              </s:Code>
              <s:Reason><s:Text xml:lang="zh-TW">{text}</s:Text></s:Reason>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    /// <summary>SOAP 1.1 Fault（部分舊固件回傳，Fault 直接位於 Envelope 之下）。</summary>
    internal static string Soap11Fault(string code, string text = "認證失敗") => $"""
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Fault>
            <faultcode>soapenv:Client</faultcode>
            <faultstring>{text}</faultstring>
            <detail>
              <ter:NotAuthorized xmlns:ter="http://www.onvif.org/ver10/error">{code}</ter:NotAuthorized>
            </detail>
          </soapenv:Fault>
        </soapenv:Envelope>
        """;

    internal static string Capabilities(params (string Category, string XAddr)[] capabilities)
    {
        var body = new XElement(
            XName.Get("GetCapabilitiesResponse", "http://www.onvif.org/ver10/device/wsdl"),
            new XElement(
                XName.Get("Capabilities", "http://www.onvif.org/ver10/device/wsdl"),
                capabilities.Select(c => new XElement(
                    XName.Get(c.Category, "http://www.onvif.org/ver10/device/wsdl"),
                    new XElement(XName.Get("XAddr", "http://www.onvif.org/ver10/device/wsdl"), c.XAddr)))));

        return Wrap(XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"), body);
    }

    internal static string Profiles(params (string Token, string Name, int Width, int Height, string Encoding, string SourceToken)[] profiles) => Wrap(
        XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"),
        new XElement(
            XName.Get("GetProfilesResponse", "http://www.onvif.org/ver10/media/wsdl"),
            profiles.Select(p => new XElement(
                XName.Get("Profiles", "http://www.onvif.org/ver10/media/wsdl"),
                new XAttribute("token", p.Token),
                new XElement(XName.Get("Name", "http://www.onvif.org/ver10/media/wsdl"), p.Name),
                new XElement(
                    XName.Get("VideoSourceConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", p.SourceToken)),
                new XElement(
                    XName.Get("VideoEncoderConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XElement(XName.Get("Encoding", "http://www.onvif.org/ver10/schema"), p.Encoding),
                    new XElement(XName.Get("Width", "http://www.onvif.org/ver10/schema"), p.Width),
                    new XElement(XName.Get("Height", "http://www.onvif.org/ver10/schema"), p.Height))))));

    /// <summary>
    /// 依 ONVIF 規範，編碼器解析度置於 VideoEncoderConfiguration/Resolution 之下
    /// （實機 VIVOTEK SD9368-EHL / IP916x 回應即為此形狀）。
    /// </summary>
    internal static string ProfilesWithResolutionElement(
        params (string Token, string Name, int Width, int Height, string Encoding, string SourceToken)[] profiles) => Wrap(
        XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"),
        new XElement(
            XName.Get("GetProfilesResponse", "http://www.onvif.org/ver10/media/wsdl"),
            profiles.Select(p => new XElement(
                XName.Get("Profiles", "http://www.onvif.org/ver10/media/wsdl"),
                new XAttribute("token", p.Token),
                new XElement(XName.Get("Name", "http://www.onvif.org/ver10/media/wsdl"), p.Name),
                new XElement(
                    XName.Get("VideoSourceConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", p.SourceToken)),
                new XElement(
                    XName.Get("VideoEncoderConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", $"{p.Token}_enc"),
                    new XElement(XName.Get("Encoding", "http://www.onvif.org/ver10/schema"), p.Encoding),
                    new XElement(
                        XName.Get("Resolution", "http://www.onvif.org/ver10/schema"),
                        new XElement(XName.Get("Width", "http://www.onvif.org/ver10/schema"), p.Width),
                        new XElement(XName.Get("Height", "http://www.onvif.org/ver10/schema"), p.Height)))))));

    /// <summary>
    /// VideoSourceConfiguration/Bounds 只有感測器尺寸，編碼器未提供解析度時不得回落使用。
    /// </summary>
    internal static string ProfilesWithSourceBoundsOnly(
        params (string Token, string Name, int SensorWidth, int SensorHeight, string Encoding)[] profiles) => Wrap(
        XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"),
        new XElement(
            XName.Get("GetProfilesResponse", "http://www.onvif.org/ver10/media/wsdl"),
            profiles.Select(p => new XElement(
                XName.Get("Profiles", "http://www.onvif.org/ver10/media/wsdl"),
                new XAttribute("token", p.Token),
                new XElement(XName.Get("Name", "http://www.onvif.org/ver10/media/wsdl"), p.Name),
                new XElement(
                    XName.Get("VideoSourceConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", "VideoSource1"),
                    new XElement(
                        XName.Get("Bounds", "http://www.onvif.org/ver10/schema"),
                        new XAttribute("width", p.SensorWidth),
                        new XAttribute("height", p.SensorHeight))),
                new XElement(
                    XName.Get("VideoEncoderConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", $"{p.Token}_enc"),
                    new XElement(XName.Get("Encoding", "http://www.onvif.org/ver10/schema"), p.Encoding))))));

    /// <summary>含 PTZConfiguration/NodeToken 的 profile（實機 VIVOTEK 回應形狀，node token = camctrl_c1）。</summary>
    internal static string ProfilesWithPtzNode(string token, string nodeToken) => Wrap(
        XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"),
        new XElement(
            XName.Get("GetProfilesResponse", "http://www.onvif.org/ver10/media/wsdl"),
            new XElement(
                XName.Get("Profiles", "http://www.onvif.org/ver10/media/wsdl"),
                new XAttribute("token", token),
                new XElement(XName.Get("Name", "http://www.onvif.org/ver10/media/wsdl"), token),
                new XElement(
                    XName.Get("VideoSourceConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", "VideoSource1")),
                new XElement(
                    XName.Get("VideoEncoderConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", token + "_enc"),
                    new XElement(XName.Get("Encoding", "http://www.onvif.org/ver10/schema"), "H264"),
                    new XElement(
                        XName.Get("Resolution", "http://www.onvif.org/ver10/schema"),
                        new XElement(XName.Get("Width", "http://www.onvif.org/ver10/schema"), 1920),
                        new XElement(XName.Get("Height", "http://www.onvif.org/ver10/schema"), 1080))),
                new XElement(
                    XName.Get("PTZConfiguration", "http://www.onvif.org/ver10/media/wsdl"),
                    new XAttribute("token", "ptzconfiguration"),
                    new XElement(XName.Get("NodeToken", "http://www.onvif.org/ver10/schema"), nodeToken)))));

    internal static string StreamUri(string uri) => Wrap(
        XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"),
        new XElement(
            XName.Get("GetStreamUriResponse", "http://www.onvif.org/ver10/media/wsdl"),
            new XElement(
                XName.Get("MediaUri", "http://www.onvif.org/ver10/schema"),
                new XElement(XName.Get("Uri", "http://www.onvif.org/ver10/schema"), uri))));

    internal static string Wrap(XElement body) => Wrap(body.Name, body);

    private static string Wrap(XName bodyName, XElement body) => new XDocument(
        new XElement(
            XName.Get("Envelope", "http://www.w3.org/2003/05/soap-envelope"),
            new XElement(bodyName, body))).ToString(SaveOptions.DisableFormatting);
}

internal sealed record RequestSnapshot(string Url, string Body, string? Authorization);

/// <summary>依請求內容回應 SOAP 內容之記錄型 handler（相容既有測試）。</summary>
internal sealed class RecordingHandler : HttpMessageHandler
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
        lock (Requests)
        {
            Requests.Add(new RequestSnapshot(
                request.RequestUri!.ToString(),
                body,
                request.Headers.Authorization?.ToString()));
        }

        return OnvifTestHttp.Soap(_resolve(request.RequestUri.ToString(), body));
    }
}

/// <summary>可完整控制狀態碼與標頭之 handler（認證／錯誤情境測試）。</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _script;

    public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script) => _script = script;

    public List<RequestSnapshot> Requests { get; } = [];

    public int CallCount
    {
        get
        {
            lock (Requests)
            {
                return Requests.Count;
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(new RequestSnapshot(
                request.RequestUri!.ToString(),
                body,
                request.Headers.Authorization?.ToString()));
        }

        return _script(request, body);
    }
}
