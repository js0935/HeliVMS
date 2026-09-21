using System.Net;
using System.Text;
using HeliVMS.Devices.Onvif;
using HeliVMS.Devices.Ptz;

namespace HeliVMS.Devices.Tests;

public class OnvifPtzExecutorTests
{
    private const string Cap = @"{0}<d:GetCapabilitiesResponse xmlns:d=""http://www.onvif.org/ver10/device/wsdl""><d:Capabilities><d:PTZ><d:XAddr>http://dev/ptz</d:XAddr></d:PTZ></d:Capabilities></d:GetCapabilitiesResponse>{1}";
    private const string EnvOpen = @"<?xml version=""1.0""?><s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""><s:Body>";
    private const string EnvClose = "</s:Body></s:Envelope>";

    private sealed class Handler : HttpMessageHandler
    {
        public Func<string, string, string> Resolve { get; set; } = static (_, _) => string.Empty;
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Resolve(request.RequestUri!.ToString(), body), Encoding.UTF8, "application/soap+xml"),
            };
        }
    }

    private static string PresetsXml(string inner) => EnvOpen + inner + EnvClose;

    private static string PresetBody(params (string Token, string Name)[] presets)
    {
        var xml = "<tptz:GetPresetsResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\" " +
                  "xmlns:tt=\"http://www.onvif.org/ver10/schema\">";
        foreach (var (t, n) in presets)
        {
            xml += $"<tptz:Preset token=\"{t}\"><tt:Name>{n}</tt:Name></tptz:Preset>";
        }

        xml += "</tptz:GetPresetsResponse>";
        return xml;
    }

    private static async Task<(OnvifPtzExecutor Executor, OnvifDeviceService Device)> MakeAsync(
        Handler handler,
        params (string Token, string Name)[] presets)
    {
        handler.Resolve = (_, body) =>
        {
            if (body.Contains("GetCapabilities"))
            {
                return EnvOpen + string.Format(Cap, string.Empty, string.Empty) + EnvClose;
            }

            if (body.Contains("GetPresets"))
            {
                return PresetsXml(PresetBody(presets));
            }

            if (body.Contains("GotoPreset"))
            {
                return EnvOpen + "<tptz:GotoPresetResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\" />" + EnvClose;
            }

            return EnvOpen + EnvClose;
        };
        var device = new OnvifDeviceService("http://dev/devicemgmt", null, null, handler);
        return (new OnvifPtzExecutor(device, "prof1"), device);
    }

    [Fact]
    public async Task GotoPreset_ResolvesNameToToken_AndCallsGoto()
    {
        var handler = new Handler();
        var (executor, _) = await MakeAsync(handler, ("tok1", "Entrance"), ("tok2", "Lobby"));

        await executor.GotoPresetAsync("Lobby");

        Assert.Contains(handler.Bodies, b => b.Contains("GotoPreset") && b.Contains("tok2"));
    }

    [Fact]
    public async Task GotoPreset_NoPtzCapability_Throws()
    {
        var handler = new Handler();
        handler.Resolve = (_, body) => body.Contains("GetCapabilities")
            ? PresetsXml("<d:GetCapabilitiesResponse xmlns:d=\"http://www.onvif.org/ver10/device/wsdl\"><d:Capabilities /></d:GetCapabilitiesResponse>")
            : EnvOpen + EnvClose;
        using var device = new OnvifDeviceService("http://dev/devicemgmt", null, null, handler);
        var executor = new OnvifPtzExecutor(device, "prof1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.GotoPresetAsync("Lobby"));
    }

    [Fact]
    public async Task GotoPreset_UnknownName_Throws()
    {
        var handler = new Handler();
        var (executor, _) = await MakeAsync(handler, ("tok1", "Entrance"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.GotoPresetAsync("Nope"));
    }

    [Fact]
    public async Task GotoPreset_CachesPresets_OnlyOneGetPresets()
    {
        var handler = new Handler();
        var (executor, _) = await MakeAsync(handler, ("tok1", "Entrance"), ("tok2", "Lobby"));

        await executor.GotoPresetAsync("Entrance");
        await executor.GotoPresetAsync("Lobby");

        Assert.Equal(1, handler.Bodies.Count(b => b.Contains("GetPresets")));
        Assert.Equal(2, handler.Bodies.Count(b => b.Contains("GotoPreset")));
    }

    [Fact]
    public async Task GotoPreset_EmptyPresets_Throws()
    {
        var handler = new Handler();
        var (executor, _) = await MakeAsync(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.GotoPresetAsync("Entrance"));
    }

    [Fact]
    public async Task GotoPreset_SendsProfileToken()
    {
        var handler = new Handler();
        var (executor, _) = await MakeAsync(handler, ("tok1", "Entrance"));

        await executor.GotoPresetAsync("Entrance");

        var gotoBody = handler.Bodies.Single(b => b.Contains("GotoPreset"));
        Assert.Contains("prof1", gotoBody);
    }

    [Fact]
    public async Task GotoPreset_MultiPreset_PicksCorrectToken()
    {
        var handler = new Handler();
        var (executor, _) = await MakeAsync(handler, ("tok1", "Entrance"), ("tok2", "Lobby"), ("tok3", "Parking"));

        await executor.GotoPresetAsync("Parking");

        Assert.Contains(handler.Bodies, b => b.Contains("GotoPreset") && b.Contains("tok3"));
        Assert.DoesNotContain(handler.Bodies, b => b.Contains("GotoPreset") && b.Contains("tok1"));
    }
}