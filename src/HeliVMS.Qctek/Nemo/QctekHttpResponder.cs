using System.Globalization;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// QCTek 裝置的「HTTP 前景」：QCV8001 把 NEMO 與 HTTP 多工在同一個埠
/// （<c>HttpServer::PreParseRequest</c> 分辨 NEMO／GET），QCC 的裝置探測就先送 HTTP。
/// 實測（QCC 加裝置）捕獲的第一包：<c>GET / HTTP/1.1</c>＋Basic <c>admin:admin</c>——
/// 沒有這層應答，QCC 只能報「無法確認監控系統的型號與協定」。
/// </summary>
/// <remarks>
/// <para>
/// 端點與標籤由 QCV8001 <c>QC_Server</c> 的請求處理器（0x180005550）還原：
/// <c>/DVR_Info.htm</c> 回一份帶 <c>&lt;ProductName&gt;</c>／<c>&lt;ProductVersion&gt;</c>
/// 等標籤的資訊頁（QCC 從中認型號）；<c>/getparam.cgi?RemoteConsolePort…</c> 回
/// <c>RemoteConsolePort=&lt;埠&gt;</c>；<c>/emap/EmapInfo.txt</c> 回
/// <c>[Server_Number]</c>＋<c>TotalChannel=</c>。標籤本身是相容性所需的事實
/// （解析端靠它們），頁面其餘內容是 HeliVMS 自己的版本。
/// </para>
/// <para>
/// 只處理「探測所需的最小集合」：目標是讓 QCC 認出裝置並走 NEMO；未知路徑一律
/// 404，不假裝自己是完整的網頁服務——那會讓「QCC 以為這裡有網頁管理介面」
/// 而在後續流程撞牆。
/// </para>
/// </remarks>
public static class QctekHttpResponder
{
    /// <summary>裝置宣告的產品名（與 NEMO 的 <c>CHECK_SERVER_VERSION</c> 回應一致）。</summary>
    public const string ProductName = "QCV8001";

    /// <summary>產品版本行顯示的值（QCC 只做記錄；用產品名＋相容標註）。</summary>
    public const string ProductVersion = "QCV8001";

    /// <summary>
    /// 「對外公開的 Web 埠」設定鍵（由桌面端以命令列設定傳給內嵌 Kestrel）。
    /// <para>
    /// 80 埠由 <see cref="QctekPortMux"/> 佔用、HTTP 被轉發到內部的臨時埠時，
    /// Kestrel 看到的 <c>Connection.LocalPort</c> 是那個臨時埠（例如 51234）。
    /// <c>RemoteConsolePort</c> 若照實回報臨時埠，QCC 之後就會去連一個只有本行程
    /// 內部才存在的號碼——裝置看起來「加入成功、連線失敗」。
    /// </para>
    /// </summary>
    public const string PublicPortConfigKey = "helivms.public_port";

    /// <summary>
    /// 對外回報的 <c>RemoteConsolePort</c>：有設定公開埠就用它，否則用連線實際的
    /// 本機埠（沒有多工器的情況）。
    /// </summary>
    public static string ResolveConsolePort(int localPort, string? configuredPublicPort)
    {
        if (int.TryParse(
                configuredPublicPort,
                System.Globalization.NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var port) && port is > 0 and <= 65535)
        {
            return port.ToString(CultureInfo.InvariantCulture);
        }

        return localPort.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>HTTP 前景的上下文。</summary>
    public sealed record Context(string RemoteConsolePort, int TotalChannels);

    /// <summary>
    /// 由請求建完整 HTTP 回應字串；不認識的路徑或非 GET 回 <c>null</c>
    /// （呼叫端回 404；<c>null</c> 與「空回應」在探測端的意義不同）。
    /// </summary>
    public static string? BuildResponse(string? request, Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = RequestPath(request);
        return path is null ? null : BuildResponseForPath(path, context);
    }

    /// <summary>由路徑（已解析）取二進位回應——QCC 的裝置模組狀態端點（.bin）。</summary>
    public static (string ContentType, byte[] Body)? BuildBinaryBody(string path, Context context)
    {
        if (!QctekSupportBin.IsSupportBinPath(path))
        {
            return null;
        }

        return path.Equals("/QCSystemStatus.bin", StringComparison.OrdinalIgnoreCase)
            ? ("application/octet-stream", QctekSupportBin.SystemStatus())
            : path.Equals("/QCCameraStatus.bin", StringComparison.OrdinalIgnoreCase)
                ? ("application/octet-stream", QctekSupportBin.CameraStatus())
                : ("application/octet-stream", QctekSupportBin.BackupSystem(
                    context.RemoteConsolePort, context.TotalChannels));
    }

    /// <summary>
    /// 由路徑（已解析）取回應的內容——ASP.NET 端的中介層需要自行組狀態/標頭，
    /// 與 <see cref="BuildResponseForPath"/> 共用同一份路由。
    /// </summary>
    public static (string ContentType, string Body)? BuildBody(string path, Context context)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(context);

        // 路徑比對不分大小寫（QCV8001 用 _stricmp；探測端的大小寫不該決定成敗）。
        //
        // `/` 與 `/DVR_Info.htm` 回「不同的頁」——實查 QCV8001 的 media 根：
        //   GET /          → root.html：<title>QCV8001</title>（「型號識別」的來源！）
        //   GET /DVR_Info.htm → 「DVR Information」資訊頁（CPU/RAM/ProductVersion…）
        // 兩者都用 title/內容宣告產品名；QCC 認的是「QCV8001」——用 DVR Information
        // 當首頁時它拿不到型號，正是「無法確認監控系統的型號與協定」。
        if (path == "/")
        {
            return ("text/html", RootPage());
        }

        if (path.Equals("/DVR_Info.htm", StringComparison.OrdinalIgnoreCase))
        {
            return ("text/html", InfoPage(context));
        }

        if (path.StartsWith("/getparam.cgi", StringComparison.OrdinalIgnoreCase))
        {
            return ("text/plain", $"RemoteConsolePort={context.RemoteConsolePort}\r\n");
        }

        if (path.Equals("/emap/EmapInfo.txt", StringComparison.OrdinalIgnoreCase))
        {
            return ("text/plain", $"[Server_Number]\r\nTotalChannel={context.TotalChannels}\r\n");
        }

        return null;
    }

    /// <summary>由路徑（已解析）建完整 HTTP 回應字串（NEMO 伺服器的獨立埠用）。</summary>
    public static string? BuildResponseForPath(string path, Context context)
    {
        var body = BuildBody(path, context);
        return body is { } b ? Response(200, b.ContentType, b.Body) : null;
    }

    /// <summary>
    /// 這一條請求是不是「QCTek 裝置探測」？兩種特徵：①裝置資訊路徑（無條件——
    /// 這些路徑不屬於 HeliVMS 的 SPA）；②<c>GET /</c>＋<c>Authorization: Basic</c>
    /// （QCC 專屬——QCC 的裝置精靈帶 Basic <c>admin:admin</c> 探測，瀏覽器開首頁
    /// 不會帶）。只攔探測，SPA 與 API 完全不受影響。
    /// </summary>
    public static bool IsProbeRequest(string method, string path, string? authorizationHeader)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Equals("/DVR_Info.htm", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/getparam.cgi", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/emap/EmapInfo.txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path == "/" &&
               authorizationHeader is not null &&
               authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析請求行 <c>GET &lt;path&gt; HTTP/1.x</c> 的路徑；非 GET 或格式不符回 null。</summary>
    public static string? RequestPath(string? request)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return null;
        }

        var lineEnd = request.IndexOf('\r');
        var line = lineEnd >= 0 ? request[..lineEnd] : request;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = parts[1];

        // 只取路徑部分做比對（query 另外處理）。
        var query = target.IndexOf('?');
        return query >= 0 ? target[..query] : target;
    }

    /// <summary>
    /// `/` 的首頁——<b>逐位元組對齊真機</b>。真機（已被 QCC 成功加入的對照組）對
    /// <c>GET /</c> 的回應就是這一份 445-byte 的 <c>media/root.html</c>：
    /// UTF-8 BOM 開頭、<b>LF</b> 換行、結尾無換行。三個細節都刻意保留——
    /// 長度與位元組都與真機相同，QCC 以任何形式比對內容或長度
    /// （含 <c>Content-Length: 445</c>）都不會落空。
    /// </summary>
    private static string RootPage()
        => "\uFEFF<!DOCTYPE html>\n" +
           "<html>\n" +
           "<head>\n" +
           "    <meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\">\n" +
           $"    <title>{ProductName}</title>\n" +
           "    <link rel=\"shortcut icon\" type=\"image/x-icon\" href=\"/favicon.ico\" media=\"screen\" />\n" +
           "</head>\n" +
           "<body>\n" +
           "    <script language=\"JavaScript\" type=\"text/javascript\">\n" +
           "        if (typeof WebAssembly !== 'undefined') location.href = \"ws/root.html\";\n" +
           "        else location.href = \"login.html\";\n" +
           "    </script>\n" +
           "</body>\n" +
           "</html>";

    private static string InfoPage(Context context)
        => "<html><head><title> DVR Information </title></head>\r\n" +
           "<body><font face=\"Arial, Helvetica, sans-serif\" color=\"#FF0000\">" +
           $"<a><ProductName> {ProductName} Information </ProductName></a><br><br>" +
           "<font face=\"Arial, Helvetica, sans-serif\" color=\"#000000\">\r\n" +
           "<CPU> CPU: 0.0 GHz </CPU><br><br>\r\n" +
           "<RAM> RAM: 0 MB </RAM><br><br>\r\n" +
           "<VGA> VGA: 0 MB </VGA><br><br>\r\n" +
           "<DirectXVersion> DirectX Version: 11 </DirectXVersion><br><br>\r\n" +
           "<LoginID> Login ID: admin </LoginID><br><br>\r\n" +
           $"<ProductVersion> Product Version: {ProductVersion} </ProductVersion><br><br>\r\n" +
           $"<NumConnection> Number of Connection: {context.TotalChannels} </NumConnection><br><br>\r\n" +
           "</font></p></body></html>\r\n\r\n";

    private static string Response(int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        var reason = status == 200 ? "OK" : "Not Found";

        // 逐字對齊真機（現場實測 QCV8001 對 GET / 的回應）：
        //   HTTP 200 / Connection: close / Content-Length / Content-Type / Server: QCV8001
        // 「Server: QCV8001」是產品識別的一部分；Connection 用 close 與真機相同
        // （先前改 keep-alive 是未對照真機前的推測）。
        return $"HTTP/1.1 {status} {reason}\r\n" +
               "Server: " + ProductName + "\r\n" +
               $"Content-Type: {contentType}\r\n" +
               $"Content-Length: {bytes.ToString(CultureInfo.InvariantCulture)}\r\n" +
               "Connection: close\r\n\r\n" +
               body;
    }
}
