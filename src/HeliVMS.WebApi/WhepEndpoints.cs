using System.Net;
using HeliVMS.Rtc;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// M244 WebRTC 即時監看的 WHEP 端點（§14.7 #1）。
/// <para>
/// 走 WHEP 而非自訂協商的理由是<b>瀏覽器端零相依</b>：原生
/// <c>RTCPeerConnection</c> 加兩次 HTTP 就能完成，與 M239 SPA「不引入外部 JS」的
/// 既有原則一致。
/// </para>
/// <para>
/// 授權由 <see cref="LicenseGateMiddleware"/> 以 <c>/api/stream</c> 前綴統一保護
/// （<see cref="LicenseFeatures.Remote"/>），金鑰由 <see cref="ApiKeyAuthMiddleware"/>
/// 以標頭保護。刻意<b>不</b>放寬 <c>?key=</c>：WHEP 是普通 POST，不是 WebSocket 升級，
/// 秘密放進 query 會進 browser history 與伺服器 access log。
/// </para>
/// </summary>
public static class WhepEndpoints
{
    private const string SdpContentType = "application/sdp";

    /// <summary>把 WHEP 端點掛到 <c>/api</c> 群組下。</summary>
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/stream/{channelId:int}/whep", HandleCreate);
        api.MapDelete("/stream/whep/{sessionId}", HandleDelete);
        api.MapGet("/stream/{channelId:int}/whep", HandleStatus);
    }

    /// <summary>
    /// WHEP 建立會話：瀏覽器的 offer 進來，answer 出去。
    /// </summary>
    private static async Task HandleCreate(
        HttpContext context,
        int channelId,
        LiveStreamService live,
        ChannelRepository channels,
        DeviceRepository devices,
        AuditLogRepository audit,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("HeliVMS.WebApi.Whep");

        if (!MediaTypeIsSdp(context.Request.ContentType))
        {
            await Error(context, StatusCodes.Status415UnsupportedMediaType,
                $"WHEP 需要 Content-Type: {SdpContentType}。");
            return;
        }

        var channel = channels.Get(channelId);
        if (channel is null)
        {
            await Error(context, StatusCodes.Status404NotFound, "找不到這一路攝影機。");
            return;
        }

        // 帳密一律在拉流的當下才組合，不落進資料庫、日誌或稽核（與 RtspStreamResolver 的
        // 既有約定一致）。channel.MainStreamUrl 保持裸位址。
        var rtspUrl = RtspStreamResolver.Resolve(
            channel.MainStreamUrl,
            channel.DeviceId,
            host => devices.FindByIp(host)?.Id,
            devices.GetRtspCredentials);

        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            await Error(context, StatusCodes.Status409Conflict, "這一路攝影機尚未設定 RTSP 位址。");
            return;
        }

        string offer;
        try
        {
            offer = await ReadOfferAsync(context.Request, context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            await Error(context, StatusCodes.Status413PayloadTooLarge,
                $"offer 過大（上限 {WhepLimits.MaxOfferBytes} bytes）。");
            return;
        }

        var source = new ChannelSource(channelId, rtspUrl);

        try
        {
            var (session, answer) = await live.OpenAsync(source, offer, () => new WhepPeer(live.Options), context.RequestAborted);

            audit.Record(
                Actor(context),
                "stream.live.start",
                "stream",
                "channel",
                channelId,
                $"viewer={session.Id}");

            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = SdpContentType;
            context.Response.Headers.Location = $"/api/stream/whep/{session.Id}";

            // 串流內容不可被任何中介快取；這是現場畫面，不是可重播的資源。
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            await context.Response.WriteAsync(answer.Answer, context.RequestAborted);
        }
        catch (WhepViewerLimitException ex)
        {
            // 429：這是「請稍後再試」而不是「你被拒絕」，讓前端可以自動重試。
            logger.LogInformation("通道 {ChannelId} 的即時觀看已達上限 {Limit}。", ex.ChannelId, ex.Limit);
            context.Response.Headers.RetryAfter = "5";
            await Error(context, StatusCodes.Status429TooManyRequests, ex.Message);
        }
        catch (WhepNegotiationException ex)
        {
            logger.LogInformation("通道 {ChannelId} 的 WHEP 協商失敗：{Reason}", channelId, ex.Message);
            await Error(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (PublisherStartException ex)
        {
            // 502：上游（攝影機／ffmpeg）出問題，不是呼叫方的問題。
            // ex.Message 已經過 RtspUri.RedactText 遮蔽，不含 RTSP 帳密。
            logger.LogWarning("通道 {ChannelId} 的 publisher 啟動失敗：{Reason}", channelId, ex.Message);
            await Error(context, StatusCodes.Status502BadGateway, $"無法從攝影機取得即時畫面：{ex.Message}");
        }
    }

    /// <summary>WHEP 關閉會話。</summary>
    private static async Task HandleDelete(
        HttpContext context,
        string sessionId,
        LiveStreamService live,
        AuditLogRepository audit)
    {
        // Close 回傳被關掉的會話；先拿後關的寫法會讓稽核永遠拿不到東西（會話已被移除）。
        var session = live.Close(sessionId);
        if (session is not null)
        {
            audit.Record(
                Actor(context),
                "stream.live.stop",
                "stream",
                "channel",
                session.ChannelId,
                $"viewer={sessionId}");
        }

        // 冪等：重複 DELETE 或已逾時回收的會話都視為成功，讓前端可以放心重試。
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        await Task.CompletedTask;
    }

    /// <summary>查詢某通道的即時狀態；供 UI 顯示「有幾人在看」與是否在發佈。</summary>
    private static async Task HandleStatus(HttpContext context, int channelId, LiveStreamService live)
    {
        var view = live.Describe(channelId);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            channelId = view.ChannelId,
            active = view.Active,
            viewers = view.Viewers,
            limit = view.Limit,
            codec = "H264",
            transport = "WHEP",
        });
    }

    private static bool MediaTypeIsSdp(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;

        // 允許 application/sdp 與帶參數的寫法（charset）。
        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim();

        return mediaType.Equals(SdpContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadOfferAsync(HttpRequest request, CancellationToken token)
    {
        // 帶上限讀取：內容長度未知或超過上限時直接擲 BadHttpRequestException，
        // 交由呼叫端回 413，而不是先把整個 body 讀進記憶體。
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > WhepLimits.MaxOfferBytes)
            {
                throw new BadHttpRequestException("offer exceeds the accepted size");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Actor(HttpContext context)
    {
        // 本 API 以單一 API 金鑰授權，沒有個別使用者身分；因此稽核主體記來源位址。
        // 記 "unknown" 而不是讓稽核項目空白，未來接上 SSO 時才不會出現無法歸屬的舊資料。
        return context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
    }

    private static async Task Error(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
