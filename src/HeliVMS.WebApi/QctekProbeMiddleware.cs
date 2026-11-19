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
        //
        // 帳號端點的 query 裡是 base64 的帳號與密碼（真機的 hBase64／update payload），
        // 整段寫進日誌等於把密碼落地，所以那三條路徑只記「有 query」與長度。
        var isAccountPath = QctekAccountEndpoint.IsAccountPath(path);
        var queryForLog = isAccountPath
            ? $" ?({request.QueryString.Value?.Length ?? 0} chars，已省略)"
            : request.QueryString.ToString();
        var userAgent = request.Headers.UserAgent.ToString();
        if (isProbe || isAccountPath || authorization.Length > 0 ||
            !userAgent.StartsWith("Mozilla", StringComparison.OrdinalIgnoreCase))
        {
            Diag($"[WEB] {request.Method} {request.Path}{queryForLog} " +
                 $"probe={isProbe} auth={authorization.Length > 0} " +
                 $"ua={(userAgent.Length <= 80 ? userAgent : userAgent[..80])}");
        }

        // QCC 的裝置模組狀態端點（.bin）必須在 isProbe 閘之前攔：這些路徑不帶 Basic
        // 認證（ua=QCHTMLGET），isProbe 判 false 會先 next() 掉——實測 404 後
        // QCC 以 3 秒一次無限重試，對應功能停在「未就緒」。
        //
        // 遠端設定頁的兩份清單也在這裡：/QCCamList.bin 與 /QCUserList.bin 任一壞掉，
        // QC_rsClient 的 QueryUserSetting 就整體判定失敗（整頁空白，兩邊日誌乾淨）。
        //
        // 遠端設定頁的「新增／修改／刪除／複製帳號」也走同一個 .bin 命名空間，但它是
        // 寫入，而且在真機是另一組處理器（support_bin.cpp 的 update／remove），所以
        // 先分流——QctekSupportBin 的白名單刻意不含這三條路徑（清單是唯讀資料，
        // 寫入端點不該被當成「支援的 .bin」）。
        if (QctekAccountEndpoint.IsAccountPath(path))
        {
            var accountService = context.RequestServices.GetService(typeof(QctekAccountService))
                as QctekAccountService;
            if (accountService is null)
            {
                // 沒有註冊服務時不可以安靜 next()：那會在 QCC 看起來像裝置不支援遠端設定，
                // 而不是我們自己的接線漏了。回 500 並留一行診斷。
                Diag($"[WEB] -> 500 {path}（QctekAccountService 未註冊）");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.Headers["Server"] = QctekHttpResponder.ProductName;
                context.Response.Headers["Connection"] = "close";
                return;
            }

            // 真機這三支都是 GET（QCC 用 FUN_180002cc0 組出整段 query 直接送出）。
            var account = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                ? accountService.Handle(path, request.QueryString.Value, authorization)
                : new QctekAccountResult(
                    QctekAccountStatus.BadRequest,
                    Detail: $"{request.Method} 不是帳號端點支援的方法");

            // 診斷只記「誰、做了什麼、結果」：query 裡是 base64 的帳號與密碼，
            // 整段寫進日誌等於把密碼落地（真機的 hBase64 也是明文的 base64）。
            Diag($"[WEB] -> {(int)account.Status} {path} action={QctekAccountEndpoint.Classify(path)} " +
                 $"auth={account.AuthSource} {account.Detail}");

            context.Response.StatusCode = (int)account.Status;
            context.Response.Headers["Server"] = QctekHttpResponder.ProductName;
            context.Response.Headers["Connection"] = "close";
            if (account.Body is { Length: > 0 } accountBody)
            {
                context.Response.ContentType = account.ContentType;
                await context.Response.WriteAsync(accountBody, context.RequestAborted);
            }

            return;
        }

        if (QctekSupportBin.IsSupportBinPath(path))
        {
            var channels0 = context.RequestServices.GetService(typeof(ChannelRepository)) as ChannelRepository;
            var configuration0 = context.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;
            var channelList = channels0?.List() ?? [];
            var binContext = new QctekHttpResponder.Context(
                RemoteConsolePort: QctekHttpResponder.ResolveConsolePort(
                    context.Connection.LocalPort,
                    configuration0?[QctekHttpResponder.PublicPortConfigKey]),
                TotalChannels: channelList.Count,
                // 只有真的被要求時才查資料庫：QCC 每 3 秒輪詢的是狀態端點，
                // 不該為它們多做兩次清單查詢。
                Cameras: path.Equals("/QCCamList.bin", StringComparison.OrdinalIgnoreCase)
                    ? channelList.Select(static c => new QctekRemoteCamera(c.Name)).ToList()
                    : null,
                Users: path.Equals("/QCUserList.bin", StringComparison.OrdinalIgnoreCase)
                    ? BuildUsers(context, authorization)
                    : null);
            var bin = QctekHttpResponder.BuildBinaryBody(path, binContext);
            if (bin is { } payload)
            {
                Diag($"[WEB] -> 200 {payload.ContentType} {payload.Body.Length} bytes（QCTek 支援端點）");
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = payload.ContentType;
                context.Response.Headers["Server"] = QctekHttpResponder.ProductName;
                context.Response.Headers["Connection"] = "close";
                await context.Response.Body.WriteAsync(payload.Body, context.RequestAborted);
                return;
            }
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
        var responderContext = new QctekHttpResponder.Context(
            RemoteConsolePort: QctekHttpResponder.ResolveConsolePort(
                context.Connection.LocalPort,
                configuration?[QctekHttpResponder.PublicPortConfigKey]),
            TotalChannels: channels?.List().Count ?? 0);

        var body = QctekHttpResponder.BuildBody(
            path,
            responderContext);

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

    /// <summary>
    /// 把 HeliVMS 的本機帳號換成 RMUL 的列。<c>IsCurrent</c> 只依「這次請求帶的
    /// Basic 帳號」判定：沒有認證標頭就不標記任何人，而不是猜第一個 admin——
    /// QCC 的 <c>isCurrentUser</c> 只認權限位元的 0x20，猜錯會讓它把別人當成自己。
    /// </summary>
    private static IReadOnlyList<QctekRemoteUser>? BuildUsers(HttpContext context, string authorization)
    {
        var repository = context.RequestServices.GetService(typeof(UserRepository)) as UserRepository;
        var users = repository?.ListUsers();
        if (users is null)
        {
            return null;
        }

        var current = BasicUsername(authorization);
        return users
            .Select(u => new QctekRemoteUser(
                u.Username,
                QctekRemoteList.PermissionFor(u.Enabled, u.Role == "admin"),
                // 序號就是帳號自己的 Id：QCC 之後的刪除／更新／複製都用它指名帳號，
                // 改用列的位置會在刪掉一個帳號之後全部錯位（刪到別人）。
                u.Id,
                IsCurrent: current is not null &&
                           string.Equals(u.Username, current, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>取 <c>Authorization: Basic</c> 的使用者名稱；沒有或格式不符回 null。</summary>
    private static string? BasicUsername(string authorization)
    {
        const string prefix = "Basic ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var raw = Convert.FromBase64String(authorization[prefix.Length..].Trim());
            var text = Encoding.UTF8.GetString(raw);
            var colon = text.IndexOf(':');
            return colon > 0 ? text[..colon] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>與 NemoServer 共用同一份診斷日誌（順序對照才有意義）。</summary>
    private static void Diag(string message)
        => BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), "helivms-qctek.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
            maxBytes: 512 * 1024);
}
