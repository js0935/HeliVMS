using System.Net;
using System.Text;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// PTZ 能力的跨實例快取（<see cref="OnvifDeviceService.EnsurePtzCapabilityAsync"/>）。
///
/// <para>
/// Web API 的每條 PTZ 命令與巡邏的每一步都會 <c>new OnvifDeviceService</c>；解析結果原本
/// 只活在單一實例內，每條命令都重付 GetCapabilities＋GetNodes 驗證的 2–4 個 HTTP 往返——
/// 現場實測一天 2328 次 resolve（%TEMP%\helivms-onvif-ptz.log），慢的公網攝影機上每條
/// 搖桿命令因此多等 1–2 秒。這裡釘住：結果跨實例沿用、命名空間（ver20-only 設備）隨能力
/// 一起沿用、「不支援 PTZ」的判定也在快取裡、TTL 逾期後重新解析。
/// </para>
/// </summary>
[Collection(OnvifPtzCacheCollection.Name)]
public sealed class OnvifPtzCapabilityCacheTests : IDisposable
{
    private readonly CapturingHandler _handler = new();

    public OnvifPtzCapabilityCacheTests()
    {
        OnvifDeviceService.ResetPtzCapabilityCacheForTests();
        OnvifDeviceService.PtzCapabilityTtl = TimeSpan.FromMinutes(10);
    }

    public void Dispose()
    {
        OnvifDeviceService.ResetPtzCapabilityCacheForTests();
        OnvifDeviceService.PtzCapabilityTtl = TimeSpan.FromMinutes(10);
    }

    /// <summary>可程式化的偽攝影機；記下每個請求的本文供計數與命名空間斷言。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public Func<string, string> Resolve { get; set; } = static _ =>
            string.Concat(EnvOpen, EnvClose);

        /// <summary>真時回 404（探測路徑全落空）；預設回 200 ＋ Resolve 的 SOAP 本文。</summary>
        public bool RespondNotFound { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (RespondNotFound)
            {
                return OnvifTestHttp.Status(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Resolve(body), Encoding.UTF8, "application/soap+xml"),
            };
        }

        public int CountContaining(string fragment) => Bodies.Count(b => b.Contains(fragment, StringComparison.Ordinal));
    }

    private const string EnvOpen =
        @"<?xml version=""1.0""?><s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""><s:Body>";
    private const string EnvClose = "</s:Body></s:Envelope>";

    private const string PtzCapabilities =
        @"<d:GetCapabilitiesResponse xmlns:d=""http://www.onvif.org/ver10/device/wsdl"">" +
        @"<d:Capabilities><d:PTZ><d:XAddr>http://dev/ptz</d:XAddr></d:PTZ></d:Capabilities>" +
        @"</d:GetCapabilitiesResponse>";

    /// <summary>ver20-only 設備：GetNodes 以 ver10 命名空間送會被 Namespace 錯誤拒絕。</summary>
    private void RouteVer20OnlyCamera()
    {
        _handler.Resolve = body =>
        {
            if (body.Contains("GetCapabilities"))
            {
                return EnvOpen + PtzCapabilities + EnvClose;
            }

            if (body.Contains("ver10/ptz/wsdl"))
            {
                return OnvifTestHttp.Fault("ter:Namespace");
            }

            return EnvOpen + EnvClose;
        };
    }

    [Fact]
    public async Task 第二個實例沿用快取的PTZ能力而不是重新解析()
    {
        RouteVer20OnlyCamera();
        var first = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await first.EnsurePtzCapabilityAsync();
        var resolvedRequests = _handler.Bodies.Count;

        Assert.True(first.HasPtz);
        Assert.True(resolvedRequests >= 2, "解析至少要送 GetCapabilities 與 GetNodes 驗證");

        // 第二個實例（Web API 每條命令的形狀）：不再送任何請求。
        var second = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await second.EnsurePtzCapabilityAsync();

        Assert.True(second.HasPtz);
        Assert.Equal(resolvedRequests, _handler.Bodies.Count);
    }

    [Fact]
    public async Task ver20設備的命名空間隨能力一起快取()
    {
        RouteVer20OnlyCamera();
        var first = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await first.EnsurePtzCapabilityAsync();

        // 新實例的 ContinuousMove 必須直接用 ver20 命名空間送出：只快取 XAddr 不快取
        // 命名空間的話，ver20-only 設備的每條命令都會以 ver10 開場而整批失敗。
        var second = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await second.EnsurePtzCapabilityAsync();
        var requestsAfterCacheHit = _handler.Bodies.Count;
        await second.ContinuousMoveAsync("prof1", 0.1, 0, 0);

        var moves = _handler.Bodies.Where(b => b.Contains("ContinuousMove")).ToList();
        var move = Assert.Single(moves);
        Assert.Contains("http://www.onvif.org/ver20/ptz/wsdl", move);
        // 快取命中之後不得再有任何解析往返（GetNodes 是驗證用的探測）。
        Assert.Equal(requestsAfterCacheHit, _handler.Bodies.Count - 1);
    }

    [Fact]
    public async Task 不支援PTZ的判定也會被快取()
    {
        // GetCapabilities 沒有 PTZ 區段、其餘請求一律 404：探測路徑全部落空＝不支援。
        _handler.Resolve = _ => string.Empty;
        _handler.RespondNotFound = true;
        var first = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await first.EnsurePtzCapabilityAsync();

        Assert.False(first.HasPtz);
        var probed = _handler.Bodies.Count;
        Assert.True(probed > 0, "第一次解析必須真的探測過");

        var second = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await second.EnsurePtzCapabilityAsync();

        Assert.False(second.HasPtz);
        Assert.Equal(probed, _handler.Bodies.Count);
    }

    [Fact]
    public async Task 快取逾期後會重新解析()
    {
        OnvifDeviceService.PtzCapabilityTtl = TimeSpan.Zero;
        RouteVer20OnlyCamera();
        var first = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await first.EnsurePtzCapabilityAsync();
        var resolvedRequests = _handler.Bodies.Count;

        var second = new OnvifDeviceService("http://dev/devicemgmt", null, null, _handler);
        await second.EnsurePtzCapabilityAsync();

        Assert.True(_handler.Bodies.Count > resolvedRequests, "TTL 歸零＝快取逾期，必須重新解析");
        Assert.True(second.HasPtz);
    }
}
