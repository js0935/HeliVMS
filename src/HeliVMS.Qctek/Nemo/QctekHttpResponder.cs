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
        if (path is null)
        {
            return null;
        }

        // 路徑比對不分大小寫（QCV8001 用 _stricmp；探測端的大小寫不該決定成敗）。
        if (path == "/" || path.Equals("/DVR_Info.htm", StringComparison.OrdinalIgnoreCase))
        {
            return Response(200, "text/html", InfoPage(context));
        }

        if (path.StartsWith("/getparam.cgi", StringComparison.OrdinalIgnoreCase))
        {
            return Response(200, "text/plain", $"RemoteConsolePort={context.RemoteConsolePort}\r\n");
        }

        if (path.Equals("/emap/EmapInfo.txt", StringComparison.OrdinalIgnoreCase))
        {
            return Response(200, "text/plain", $"[Server_Number]\r\nTotalChannel={context.TotalChannels}\r\n");
        }

        return null;
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
        return $"HTTP/1.1 {status} {reason}\r\n" +
               $"Content-Type: {contentType}\r\n" +
               $"Content-Length: {bytes.ToString(CultureInfo.InvariantCulture)}\r\n" +
               "Connection: close\r\n\r\n" +
               body;
    }
}
