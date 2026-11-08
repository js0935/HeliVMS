using System.Globalization;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// QCTek 裝置探測攔截（Web 埠＝80）：QCC 的裝置精靈打的是「裝置的 HTTP 埠」——
/// QCV8001 的 NEMO 與 HTTP 同埠多工（預設 80），實測捕獲
/// <c>GET /</c>＋Basic <c>admin:admin</c>。HeliVMS 的 80 是 SPA，QCC 拿到 SPA
/// 只會報「無法確認監控系統的型號與協定」。
/// </summary>
/// <remarks>
/// <para>
/// 只攔「探測特徵」（見 <see cref="QctekHttpResponder.IsProbeRequest"/>）：
/// 裝置資訊路徑（<c>/DVR_Info.htm</c> 等——不屬於 SPA）與 <c>GET /</c>＋
/// <c>Authorization: Basic</c>（QCC 專屬；瀏覽器開首頁不會帶）。SPA、API 與
/// 金鑰驗證完全不受影響——中介層掛在管線最前面，命中即短路。
/// </para>
/// <para>
/// <c>GET /</c> 回 <c>root.html</c> 風格的頁（<c>&lt;title&gt;QCV8001&lt;/title&gt;</c>）
/// ——title 就是 QCC 讀的型號；<c>/DVR_Info.htm</c> 回 DVR Information 資訊頁。
/// </para>
/// </remarks>
public static class QctekProbeMiddleware
{
    public static async Task Handle(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        if (!QctekHttpResponder.IsProbeRequest(
                request.Method,
                path,
                request.Headers.Authorization.ToString()))
        {
            await next(context);
            return;
        }

        var channels = context.RequestServices.GetService(typeof(ChannelRepository)) as ChannelRepository;
        var body = QctekHttpResponder.BuildBody(
            path,
            new QctekHttpResponder.Context(
                RemoteConsolePort: context.Connection.LocalPort.ToString(CultureInfo.InvariantCulture),
                TotalChannels: channels?.List().Count ?? 0));

        if (body is not { } content)
        {
            await next(context); // 不認識的探測路徑：交給後續（SPA／404）
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = content.ContentType;
        await context.Response.WriteAsync(content.Body, context.RequestAborted);
    }
}
