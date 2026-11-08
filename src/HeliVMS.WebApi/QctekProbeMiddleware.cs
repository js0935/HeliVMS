using System.IO;
using System.Text;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Microsoft.Extensions.Configuration;

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
        var authorization = request.Headers.Authorization.ToString();
        var isProbe = QctekHttpResponder.IsProbeRequest(request.Method, path, authorization);

        // 診斷：80 埠沒有 NemoServer 的日誌——QCC 實測「它到底送了什麼」必須看得到。
        // 只記「可疑」請求（瀏覽器 UA 開頭是 Mozilla；其餘工具與帶認證的一律留痕），
        // 避免把 SPA 的每個資源請求都寫進日誌。
        var userAgent = request.Headers.UserAgent.ToString();
        if (isProbe || authorization.Length > 0 ||
            !userAgent.StartsWith("Mozilla", StringComparison.OrdinalIgnoreCase))
        {
            Diag($"[WEB] {request.Method} {request.Path}{request.QueryString} " +
                 $"probe={isProbe} auth={authorization.Length > 0} " +
                 $"ua={(userAgent.Length <= 80 ? userAgent : userAgent[..80])}");
        }

        if (!isProbe)
        {
            await next(context);
            return;
        }

        var channels = context.RequestServices.GetService(typeof(ChannelRepository)) as ChannelRepository;

        // 有前置多工器時 Kestrel 綁的是內部臨時埠；RemoteConsolePort 必須報對外的
        // 公開埠，否則 QCC 記下的是一個只有本行程內部才存在的號碼。
        var configuration = context.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;
        var body = QctekHttpResponder.BuildBody(
            path,
            new QctekHttpResponder.Context(
                RemoteConsolePort: QctekHttpResponder.ResolveConsolePort(
                    context.Connection.LocalPort,
                    configuration?[QctekHttpResponder.PublicPortConfigKey]),
                TotalChannels: channels?.List().Count ?? 0));

        if (body is not { } content)
        {
            await next(context); // 不認識的探測路徑：交給後續（SPA／404）
            return;
        }

        Diag($"[WEB] -> 200 {content.ContentType} {Encoding.UTF8.GetByteCount(content.Body)} bytes（QCTek 探測回應）");
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = content.ContentType;

        // 逐位元組對齊真機的回應標頭（真機被 QCC 成功加入的實測值）：
        // 「Server: QCV8001」是型號識別的一部分；Connection: close 與真機相同
        // （Kestrel 依此在回應後關閉連線——QCC 對真機也是這種 close 語意）。
        context.Response.Headers["Server"] = QctekHttpResponder.ProductName;
        context.Response.Headers["Connection"] = "close";
        await context.Response.WriteAsync(content.Body, context.RequestAborted);
    }

    /// <summary>與 NemoServer 共用同一份診斷日誌（順序對照才有意義）。</summary>
    private static void Diag(string message)
        => BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), "helivms-qctek.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
            maxBytes: 512 * 1024);
}
