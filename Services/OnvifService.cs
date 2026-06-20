// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Globalization;
using Serilog;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HeliVMS.Models;

using HeliVMS.Helpers;

namespace HeliVMS.Services;

public class OnvifService : IOnvifService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly QCTekService? _qctek;
    private readonly RtspUrlResolver _urlResolver;

    public OnvifService() : this(null, null) { }

    public OnvifService(QCTekService? qctek = null, RtspUrlResolver? urlResolver = null)
    {
        _qctek = qctek;
        _urlResolver = urlResolver ?? new RtspUrlResolver();
    }
    // ─────────────────────────────────────────────────────  Subnet Scan  ─────────────────────────────────────────────────────
    public async Task<List<OnvifDiscoveryResult>> ScanSubnetAsync(
        string subnet, int onvifPort, string username, string password,
        IProgress<(int current, int total, string ip)>? progress = null)
    {
        var parts = subnet.Split('.');
        if (parts.Length < 3)
            throw new ArgumentException("Subnet format must be x.x.x (e.g. 192.168.1)");

        var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
        var total = 254;
        var completed = 0;
        var results = new List<OnvifDiscoveryResult>(total);
        var semaphore = new SemaphoreSlim(32);
            var tasks = new List<Task<(string Ip, OnvifDiscoveryResult? Result)>>(254);

        for (int i = 1; i <= 254; i++)
        {
            var ip = $"{prefix}.{i}";
            await semaphore.WaitAsync().ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var r = await TryDiscoverAsync(ip, onvifPort, username, password).ConfigureAwait(false);
                    return (ip, r.Result);
                }
                finally
                {
                    semaphore.Release();
                    var c = Interlocked.Increment(ref completed);
                    progress?.Report((c, total, ip));
                }
            }));
        }

        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var t in allResults)
            if (t.Result is not null)
                results.Add(t.Result);

        return results;
    }

    private async Task<(string Ip, OnvifDiscoveryResult? Result)> TryDiscoverAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            Log.Debug("[HeliVMS] ONVIF TryDiscoverAsync: {Ip}:{Port}", ip, port);
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(ip, port);
            if (await Task.WhenAny(connectTask, Task.Delay(2000)).ConfigureAwait(false) != connectTask)
                return (ip, null);
            if (!tcp.Connected)
                return (ip, null);

            var result = await DiscoverCameraAsync(ip, port, username, password).ConfigureAwait(false);
            return (ip, result);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or SocketException)
        {
            Log.Debug("[HeliVMS] ONVIF TryDiscoverAsync: {Ip}:{Port} exception: {Msg}", ip, port, ex.Message);
            return (ip, null);
        }
    }

    // ─────────────────────────────────────────────────────  Single Camera Discovery  ─────────────────────────────────────────────────────
    public async Task<OnvifDiscoveryResult?> DiscoverCameraAsync(
        string ip, int port, string username, string password)
    {
        Log.Debug("[HeliVMS] ONVIF DiscoverCameraAsync: {Ip}:{Port}", ip, port);
        var deviceInfo = await GetDeviceInformationAsync(ip, port, username, password).ConfigureAwait(false);
        if (deviceInfo is null)
        {
            Log.Debug("[HeliVMS] ONVIF DiscoverCameraAsync: {Ip}:{Port} GetDeviceInformation failed", ip, port);
            return null;
        }

        var profiles = await GetProfilesAsync(ip, port, username, password).ConfigureAwait(false) ?? new List<OnvifProfile>();
        foreach (var p in profiles)
        {
            p.RtspUrl = await GetStreamUriAsync(ip, port, username, password, p.Token).ConfigureAwait(false);
        }

        return new OnvifDiscoveryResult
        {
            IpAddress = ip,
            Manufacturer = deviceInfo.Manufacturer,
            Model = deviceInfo.Model,
            SerialNumber = deviceInfo.SerialNumber,
            FirmwareVersion = deviceInfo.FirmwareVersion,
            Profiles = profiles,
            ProbedPort = port
        };
    }

    private static readonly int[] DefaultOnvifPorts = { 80, 8080, 8899, 5000, 7070 };

    public async Task<OnvifDiscoveryResult?> DiscoverCameraWithPortFallbackAsync(
        string ip, string username, string password, int? preferredPort = null)
    {
        int[] ports;
        if (preferredPort.HasValue)
        {
            var pref = preferredPort.Value;
            int count = 1;
            for (int pi = 0; pi < DefaultOnvifPorts.Length; pi++)
                if (DefaultOnvifPorts[pi] != pref) count++;
            ports = new int[count];
            ports[0] = pref;
            int idx = 1;
            for (int pi = 0; pi < DefaultOnvifPorts.Length; pi++)
                if (DefaultOnvifPorts[pi] != pref)
                    ports[idx++] = DefaultOnvifPorts[pi];
        }
        else
        {
            ports = DefaultOnvifPorts;
        }

        foreach (var port in ports)
        {
            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(ip, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(1500)).ConfigureAwait(false) != connectTask || !tcp.Connected)
                    {
                        continue;
                    }

                var result = await DiscoverCameraAsync(ip, port, username, password).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }

    public async Task<(string manufacturer, string model, string name)> ProbeDeviceInfoAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            var info = await GetDeviceInformationAsync(ip, port, username, password).ConfigureAwait(false);
            if (info is null) { return ("", "", ""); }
            // Combine manufacturer + model for display name
            var name = $"{info.Manufacturer} {info.Model}".Trim();
            return (info.Manufacturer, info.Model, name);
        }
        catch
        {
            return ("", "", "");
        }
    }

    public async Task<(string MainUrl, string SubUrl)> TryResolveStreamUrlsAsync(
        string ip, int onvifPort, string username, string password,
        string fallbackMainUrl, string? fallbackSubUrl)
    {
        string? onvifMainUrl = null;
        string? onvifSubUrl = null;
        string? manufacturer = null;

        try
        {
            var result = await DiscoverCameraWithPortFallbackAsync(ip, username, password, onvifPort).ConfigureAwait(false);
            manufacturer = result?.Manufacturer;

            if (result is not null)
            {
                if (result.Profiles.Count > 0)
                {
                    var profilesWithRtsp = new List<OnvifProfile>(result.Profiles.Count);
                    for (int pi = 0; pi < result.Profiles.Count; pi++)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Profiles[pi].RtspUrl))
                    {
                        profilesWithRtsp.Add(result.Profiles[pi]);
                    }
                    }

                    if (profilesWithRtsp.Count > 0)
                    {
                        OnvifProfile? mainProfile = null, subProfile = null;
                        for (int pi = 0; pi < profilesWithRtsp.Count; pi++)
                        {
                            var pn = profilesWithRtsp[pi].Name;
                            if (pn.Contains("main", StringComparison.OrdinalIgnoreCase) || pn.Contains("1"))
                            {
                                mainProfile ??= profilesWithRtsp[pi];
                            }
                            if (pn.Contains("sub", StringComparison.OrdinalIgnoreCase) || pn.Contains("2"))
                            {
                                subProfile ??= profilesWithRtsp[pi];
                            }
                            if (mainProfile is not null && subProfile is not null) { break; }
                        }

                        if (mainProfile is not null)
                        {
                            onvifMainUrl = SubstituteRtspHost(mainProfile.RtspUrl, ip);
                        }
                        else
                        {
                            onvifMainUrl = SubstituteRtspHost(profilesWithRtsp[0].RtspUrl, ip);
                        }

                        if (subProfile is not null)
                        {
                            onvifSubUrl = SubstituteRtspHost(subProfile.RtspUrl, ip);
                        }
                        else if (profilesWithRtsp.Count > 1)
                        {
                            onvifSubUrl = SubstituteRtspHost(profilesWithRtsp[1].RtspUrl, ip);
                        }
                    }
                }
            }
            else
            {
                LogTrace("DiscoverCameraWithPortFallbackAsync returned null, will retry manufacturer via GetDeviceInformationAsync");
            }
        }
        catch (Exception ex)
        {
            LogTrace("ONVIF WS error: {ExMsg}", ex.Message);
        }

        // ── If manufacturer is null, retry GetDeviceInformationAsync ──
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            try
            {
                var devInfo = await GetDeviceInformationAsync(ip, onvifPort, username, password).ConfigureAwait(false);
                if (devInfo is not null)
                {
                    manufacturer = devInfo.Manufacturer;
                    LogTrace("Retried GetDeviceInformationAsync: manufacturer=|{Manufacturer}|", manufacturer);
                }
            }
            catch (Exception ex)
            {
                LogTrace("GetDeviceInformationAsync retry failed: {ExMsg}", ex.Message);
            }
        }

        // ── Brand override for known manufacturers (e.g. Aver) ──
        LogTrace("Brand override check: manufacturer=|{Manufacturer}|", manufacturer);
        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            var brandKey = NormalizeBrand(manufacturer);
            LogTrace("NormalizeBrand=|{BrandKey}|, ShouldOverride={ShouldOverride}", brandKey, ShouldOverrideBrandUrl(brandKey));
            if (ShouldOverrideBrandUrl(brandKey))
            {
                LogTrace("BEFORE main={MainUrl}, sub={SubUrl}", onvifMainUrl, onvifSubUrl);
                var brandUrl = RtspUrlBuilder.BuildRtspUrlWithBrand(
                    ip, 554, username, password, brandKey);
                if (!string.IsNullOrWhiteSpace(brandUrl))
                {
                    LogTrace("Brand override main: {Manufacturer} -> {BrandUrl}", manufacturer, brandUrl);
                    onvifMainUrl = brandUrl;
                }
                var brandSubUrl = RtspUrlBuilder.BuildRtspUrlSubWithBrand(
                    ip, 554, username, password, brandKey);
                if (!string.IsNullOrWhiteSpace(brandSubUrl))
                {
                    LogTrace("Brand override sub: {Manufacturer} -> {BrandSubUrl}", manufacturer, brandSubUrl);
                    onvifSubUrl = brandSubUrl;
                }
                LogTrace("AFTER main={MainUrl}, sub={SubUrl}", onvifMainUrl, onvifSubUrl);
            }
        }
        else
        {
            LogTrace("SKIP brand override: manufacturer is null/empty");
        }

        // ── QCTek fallback: use QC_Onvif.dll if ONVIF WS failed ──
        string? qctekMainUrl = null;
        if (string.IsNullOrWhiteSpace(onvifMainUrl) && _qctek is not null)
        {
            try
            {
                var qctekUrl = _qctek.OnvifQueryRtspUrl(ip, username, password);
                if (!string.IsNullOrWhiteSpace(qctekUrl))
                {
                    qctekMainUrl = SubstituteRtspHost(qctekUrl, ip);
                    LogTrace("QCTek fallback resolved main URL: {QctekMainUrl}", qctekMainUrl);
                }
            }
            catch (Exception ex)
            {
                LogTrace("QCTek fallback error: {ExMsg}", ex.Message);
            }
        }

        // ── Priority: ONVIF WS > Brand Override > QCTek > fallback ──
        LogTrace("ResolvePair input: main=|{MainUrl}|, sub=|{SubUrl}|, fallbackMain=|{FallbackMain}|, fallbackSub=|{FallbackSub}|", onvifMainUrl, onvifSubUrl, fallbackMainUrl, fallbackSubUrl);
        var resolved = _urlResolver.ResolvePair(
            onvifMainUrl ?? "",
            onvifSubUrl,
            qctekMainUrl ?? fallbackMainUrl,
            fallbackSubUrl,
            manufacturer);
        LogTrace("ResolvePair output: main=|{MainUrl}|, sub=|{SubUrl}|", resolved.MainUrl, resolved.SubUrl);

        return resolved;
    }

    // ─────────────────────────────────────────────────────  GetDeviceInformation  ─────────────────────────────────────────────────────
    private async Task<DeviceInfo?> GetDeviceInformationAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ip)) { return null; }

            string capabilitiesUrl = $"http://{ip}:{port}/onvif/device_service";
            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<GetDeviceInformation xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(capabilitiesUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return null; }

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);

            return new DeviceInfo
            {
                Manufacturer = GetElementValue(doc, "Manufacturer"),
                Model = GetElementValue(doc, "Model"),
                FirmwareVersion = GetElementValue(doc, "FirmwareVersion"),
                SerialNumber = GetElementValue(doc, "SerialNumber")
            };
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            Log.Debug("[HeliVMS] ONVIF GetDeviceInformation error for {Ip}: {Msg}", ip, ex.Message);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────  GetProfiles  ─────────────────────────────────────────────────────
    private async Task<List<OnvifProfile>> GetProfilesAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            var mediaUrl = await GetMediaServiceUrlAsync(ip, port, username, password).ConfigureAwait(false)
                           ?? $"http://{ip}:{port}/onvif/media_service";
            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<GetProfiles xmlns=\"http://www.onvif.org/ver10/media/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(mediaUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return new List<OnvifProfile>(); }

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);

            var profiles = new List<OnvifProfile>();
            foreach (var prof in doc.Descendants().Where(e => e.Name.LocalName == "Profiles"))
            {
                var token = prof.Attribute("token")?.Value ?? string.Empty;
                var name = GetSubElementValue(prof, "Name");
                var width = GetSubElementIntValue(prof, "Width");
                var height = GetSubElementIntValue(prof, "Height");
                var frameRate = GetSubElementIntValue(prof, "FrameRateLimit");
                var encoding = GetSubElementValue(prof, "Encoding");

                profiles.Add(new OnvifProfile
                {
                    Token = token,
                    Name = name,
                    Width = width,
                    Height = height,
                    FrameRate = frameRate,
                    Encoding = encoding
                });
            }
            return profiles;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            Log.Debug("[HeliVMS] ONVIF GetProfiles error for {Ip}: {Msg}", ip, ex.Message);
            return new List<OnvifProfile>();
        }
    }

    // ─────────────────────────────────────────────────────  GetStreamUri ─────────────────────────────────────────────────────
    private async Task<string> GetStreamUriAsync(
        string ip, int port, string username, string password, string profileToken)
    {
        try
        {
            var mediaUrl = await GetMediaServiceUrlAsync(ip, port, username, password).ConfigureAwait(false)
                           ?? $"http://{ip}:{port}/onvif/media_service";
            var header = BuildAuthHeader(username, password);

            var body = new StringBuilder();
            body.Append("<GetStreamUri xmlns=\"http://www.onvif.org/ver10/media/wsdl\">");
            body.Append("<StreamSetup>");
            body.Append("<Stream>RTP-Unicast</Stream>");
            body.Append("<Transport xmlns=\"http://www.onvif.org/ver10/schema\"><Protocol>RTSP</Protocol></Transport>");
            body.Append("</StreamSetup>");
            body.Append($"<ProfileToken>{EscapeXml(profileToken)}</ProfileToken>");
            body.Append("</GetStreamUri>");

            var soap = BuildSoapEnvelope(header, body.ToString());
            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(mediaUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return string.Empty; }

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);
            foreach (var e in doc.Descendants())
            {
                if (e.Name.LocalName == "Uri") return e.Value;
            }
            return string.Empty;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            Log.Debug("[HeliVMS] ONVIF GetStreamUri error for {Ip}: {Msg}", ip, ex.Message);
            return string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────  GetCapabilities / GetMediaServiceUrl  ─────────────────────────────────────────────────────
    private async Task<string?> GetMediaServiceUrlAsync(
        string ip, int port, string username, string password)
    {
        var url = $"http://{ip}:{port}/onvif/device_service";
        var header = BuildAuthHeader(username, password);
        var soap = BuildSoapEnvelope(header,
            "<GetCapabilities xmlns=\"http://www.onvif.org/ver10/device/wsdl\"><Category>All</Category></GetCapabilities>");
        var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        try
        {
            var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { return null; }
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var idx = body.IndexOf("<MediaService", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var xaddrIdx = body.IndexOf("<XAddr>", idx, StringComparison.Ordinal);
                if (xaddrIdx >= 0)
                {
                    var end = body.IndexOf("</XAddr>", xaddrIdx, StringComparison.Ordinal);
                    if (end > xaddrIdx)
                    {
                        return body.Substring(xaddrIdx + 7, end - (xaddrIdx + 7)).Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            Log.Debug("[HeliVMS] ONVIF GetCapabilities error for {Ip}: {Msg}", ip, ex.Message);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────  WS-Security Auth Header (PasswordDigest)  ─────────────────────────────────────────────────────
    private static string BuildAuthHeader(string username, string password)
    {
        var nonce = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);

        var nonceBase64 = Convert.ToBase64String(nonce);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var raw = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonce, 0, raw, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, raw, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, raw, nonce.Length + createdBytes.Length, passwordBytes.Length);

        string digest;
        using (var sha1 = SHA1.Create())
            digest = Convert.ToBase64String(sha1.ComputeHash(raw));

        var sb = new StringBuilder();
        sb.Append("<wsse:Security xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">");
        sb.Append("<wsse:UsernameToken>");
        sb.Append($"<wsse:Username>{EscapeXml(username)}</wsse:Username>");
        sb.Append($"<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{digest}</wsse:Password>");
        sb.Append($"<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">{nonceBase64}</wsse:Nonce>");
        sb.Append($"<wsu:Created>{created}</wsu:Created>");
        sb.Append("</wsse:UsernameToken>");
        sb.Append("</wsse:Security>");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────  PTZ Control  ─────────────────────────────────────────────────────
    public async Task<bool> PTZ_ContinuousMoveAsync(
        string ip, int port, string username, string password, float x, float y, float zoom)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                $"<tptz:ContinuousMove xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                $"<Velocity x=\"{x.ToString(CultureInfo.InvariantCulture)}\" y=\"{y.ToString(CultureInfo.InvariantCulture)}\" zoom=\"{zoom.ToString(CultureInfo.InvariantCulture)}\"/>" +
                $"</tptz:ContinuousMove>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> PTZ_AbsoluteMoveAsync(
        string ip, int port, string username, string password, float x, float y, float zoom)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                $"<tptz:AbsoluteMove xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                $"<Position x=\"{x.ToString(CultureInfo.InvariantCulture)}\" y=\"{y.ToString(CultureInfo.InvariantCulture)}\" zoom=\"{zoom.ToString(CultureInfo.InvariantCulture)}\"/>" +
                $"</tptz:AbsoluteMove>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> PTZ_RelativeMoveAsync(
        string ip, int port, string username, string password, float x, float y, float zoom)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                $"<tptz:RelativeMove xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                $"<Translation x=\"{x.ToString(CultureInfo.InvariantCulture)}\" y=\"{y.ToString(CultureInfo.InvariantCulture)}\" zoom=\"{zoom.ToString(CultureInfo.InvariantCulture)}\"/>" +
                $"</tptz:RelativeMove>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> PTZ_StopAsync(string ip, int port, string username, string password)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<tptz:Stop xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> PTZ_GotoPresetAsync(
        string ip, int port, string username, string password, string presetToken)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                $"<tptz:GotoPreset xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                $"<PresetToken>{EscapeXml(presetToken)}</PresetToken></tptz:GotoPreset>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────  PTZ Presets  ─────────────────────────────────────────────────────
    public async Task<List<OnvifPreset>> PTZ_GetPresetsAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return new List<OnvifPreset>(); }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<tptz:GetPresets xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new List<OnvifPreset>();

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);

            var presetNodes = doc.Descendants(XName.Get("{http://www.onvif.org/ver20/ptz/wsdl}Preset")).ToArray();
            var presets = new List<OnvifPreset>(presetNodes.Length);
            foreach (var presetNode in presetNodes)
            {
                var token = GetElementValue(presetNode, "Token");
                var name = GetElementValue(presetNode, "Name");
                presets.Add(new OnvifPreset { Token = token, Name = name });
            }
            return presets;
        }
        catch { return new List<OnvifPreset>(); }
    }

    public async Task<bool> PTZ_SetPresetAsync(
        string ip, int port, string username, string password, string presetName)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                $"<tptz:SetPreset xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                $"<PresetName>{EscapeXml(presetName)}</PresetName></tptz:SetPreset>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> PTZ_RemovePresetAsync(
        string ip, int port, string username, string password, string presetToken)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return false; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                $"<tptz:RemovePreset xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                $"<PresetToken>{EscapeXml(presetToken)}</PresetToken></tptz:RemovePreset>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────  PTZ Status & Configuration  ─────────────────────────────────────────────────────
    public async Task<OnvifPTZStatus?> PTZ_GetStatusAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return null; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<tptz:GetStatus xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);

            XElement? positionNode = null;
            foreach (var e in doc.Descendants())
            {
                if (e.Name == XName.Get("{http://www.onvif.org/ver20/ptz/wsdl}PTZStatus"))
                { positionNode = e; break; }
            }
            if (positionNode is null) { return null; }

            float TryParseFloat(string? val) =>
                float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;

            var panVal = GetElementValue(positionNode, "Position/PanTilt/x");
            var tiltVal = GetElementValue(positionNode, "Position/PanTilt/y");
            var zoomVal = GetElementValue(positionNode, "Position/Zoom/x");

            return new OnvifPTZStatus
            {
                Pan = TryParseFloat(panVal),
                Tilt = TryParseFloat(tiltVal),
                Zoom = TryParseFloat(zoomVal)
            };
        }
        catch { return null; }
    }

    public async Task<OnvifPTZConfiguration?> PTZ_GetConfigurationAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            var ptzUrl = await GetPTZServiceUrlAsync(ip, port, username, password).ConfigureAwait(false);
            if (ptzUrl is null) { return null; }

            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<tptz:GetConfiguration xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(ptzUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);

            XElement? configNode = null;
            foreach (var e in doc.Descendants())
            {
                if (e.Name == XName.Get("{http://www.onvif.org/ver20/ptz/wsdl}PTZConfiguration"))
                { configNode = e; break; }
            }
            if (configNode is null) return null;

            return new OnvifPTZConfiguration
            {
                Name = GetElementValue(configNode, "Name"),
                Token = GetElementValue(configNode, "Token")
            };
        }
        catch { return null; }
    }

    private async Task<string?> GetPTZServiceUrlAsync(
        string ip, int port, string username, string password)
    {
        try
        {
            var capabilitiesUrl = $"http://{ip}:{port}/onvif/device_service";
            var header = BuildAuthHeader(username, password);
            var soap = BuildSoapEnvelope(header,
                "<GetCapabilities xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>");

            var content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            var response = await _http.PostAsync(capabilitiesUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return null; }

            var resp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = SafeParseXml(resp);

            XElement? ptzNode = null;
            foreach (var e in doc.Descendants())
            {
                if (e.Name == XName.Get("{http://www.onvif.org/ver10/device/wsdl}PTZ"))
                { ptzNode = e; break; }
            }
            return ptzNode is null ? null : GetElementValue(ptzNode, "XAddr");
        }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────  SOAP Helpers  ─────────────────────────────────────────────────────
    /// <summary>Safely parse XML, return empty doc on failure</summary>
    private static XDocument SafeParseXml(string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return new XDocument();
        }

        // Detect HTML response (device returned web page instead of SOAP XML)
        var trimmed = xml.AsSpan().TrimStart();
        if (trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("[HeliVMS] ONVIF SafeParseXml: received HTML instead of XML");
            return new XDocument();
        }

        // Sanitize bare & in XML (e.g. in Manufacturer/Model/SerialNumber values)
        var sanitized = Regex.Replace(xml,
            "&(?!(amp|lt|gt|quot|apos|#[0-9]+|#x[0-9a-fA-F]+);)",
            "&amp;",
            RegexOptions.IgnoreCase);
        return XDocument.Parse(sanitized);
    }

    private static string BuildSoapEnvelope(string authHeader, string bodyContent)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">");
        sb.Append("<s:Header>").Append(authHeader).Append("</s:Header>");
        sb.Append("<s:Body>").Append(bodyContent).Append("</s:Body>");
        sb.Append("</s:Envelope>");
        return sb.ToString();
    }

    private static string GetElementValue(XDocument doc, string localName)
    {
        foreach (var e in doc.Descendants())
        {
            if (e.Name.LocalName == localName) return e.Value;
        }
        return string.Empty;
    }

    private static string GetElementValue(XElement element, string localName)
    {
        foreach (var e in element.Descendants())
        {
            if (e.Name.LocalName == localName) return e.Value;
        }
        return string.Empty;
    }

    private static string GetSubElementValue(XElement parent, string localName)
    {
        foreach (var e in parent.Descendants())
        {
            if (e.Name.LocalName == localName) return e.Value;
        }
        return string.Empty;
    }

    private static int GetSubElementIntValue(XElement parent, string localName)
    {
        var val = GetSubElementValue(parent, localName);
        return int.TryParse(val, out var result) ? result : 0;
    }

    private static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value)) { return value; }
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    /// <summary>Normalize ONVIF manufacturer name to brand key for RTSP URL lookup</summary>
    private static string NormalizeBrand(string? manufacturer)
    {
        if (string.IsNullOrEmpty(manufacturer)) { return ""; }
        var m = manufacturer.ToLowerInvariant();

        // Use brand config to normalize manufacturer names
        var config = RtspUrlBuilder.BrandConfig;
        if (config is not null)
        {
            foreach (var entry in config.Brands)
            {
                var aliases = entry.Aliases;
                for (int i = 0; i < aliases.Count; i++)
                {
                    if (m.Contains(aliases[i]))
                    {
                        return entry.Key;
                    }
                }
            }
        }

        // Fallback: hardcoded brand aliases
        if (m.Contains("hikvision")) { return "hikvision"; }
        if (m.Contains("dahua") || m.Contains("大華")) { return "dahua"; }
        if (m.Contains("axis")) { return "axis"; }
        if (m.Contains("foscam")) { return "foscam"; }
        if (m.Contains("vivotek")) { return "vivotek"; }
        if (m.Contains("aver") || m.Contains("avermedia") || m.Contains("aver information")) { return "aver"; }
        if (m.Contains("uniview") || m.Contains("宇視")) { return "uniview"; }
        if (m.Contains("acti")) { return "acti"; }
        if (m.Contains("geovision") || m.Contains("geo vision")) { return "geovision"; }
        if (m.Contains("tiandy") || m.Contains("天視")) { return "tiandy"; }
        if (m.Contains("honeywell")) { return "honeywell"; }
        if (m.Contains("idis")) { return "idis"; }
        return "";
    }

    /// <summary>Check if brand's GetStreamUri URL should be overridden</summary>
    private static bool ShouldOverrideBrandUrl(string? brandKey)
    {
        return brandKey switch
        {
            "aver" => true,
            _ => false,
        };
    }

    internal static string SubstituteRtspHost(string rtspUrl, string userIp)
    {
        try
        {
            var uri = new Uri(rtspUrl);
            return $"rtsp://{userIp}:{uri.Port}{uri.PathAndQuery}{uri.Fragment}";
        }
        catch
        {
            return rtspUrl;
        }
    }

    // ─────────────────────────────────────────────────────  Trace logging helper ─────────────────────────────────────────────────────
    private static void LogTrace(string template, params object?[] args)
    {
        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            Log.Debug("[HeliVMS] ONVIF trace: " + template, args);
    }

    // ─────────────────────────────────────────────────────  Internal DeviceInfo helper  ─────────────────────────────────────────────────────
    private class DeviceInfo
    {
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string FirmwareVersion { get; set; } = string.Empty;
    }
}
