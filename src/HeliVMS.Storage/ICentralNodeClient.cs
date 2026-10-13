using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// 對子節點 <c>GET /api/node</c> 的呼叫縫（§14.1 #10，M295）。
///
/// 抽成介面的唯一理由是可測試：中央站的聚合邏輯（成功／逾時／授權失敗／回應損毀）必須在
/// 不連任何真實機器的情況下逐項驗證；正式路徑由 <see cref="HttpCentralNodeClient"/> 以真實 HTTP 實作。
/// </summary>
public interface ICentralNodeClient
{
        /// <summary>
    /// 取得指定節點的身分與狀態。
    /// 任何連線失敗、非 2xx 或回應無法解析都應擲出例外，由 <see cref="CentralNodeService"/> 記為離線。
    /// </summary>
    /// <param name="baseUrl">子節點根網址，例如 <c>http://192.168.1.20:5080</c>；尾端斜線可有可無。</param>
    /// <param name="apiKey">子節點的 Bearer 金鑰；空字串表示不帶 Authorization 標頭。</param>
    /// <param name="cancellationToken">取消權杖；聚合端取消時應立即停止等待。</param>
    Task<NodeStatus> FetchAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 以子節點金鑰取回其上任一資源的原始回應（§14.1 #10 跨機回放代理，M301）。
    ///
    /// 與 <see cref="FetchAsync"/> 的關鍵差異：**非 2xx 不擲例外**，狀態碼原樣回傳。
    /// 代理要把子節點的 404（此時段沒錄影）與 200（有片）分辨開，若這裡就把 404 變成例外，
    /// 上層只剩「全部 502」可以回，使用者看到的錯誤就沒了意義。
    /// </summary>
    /// <param name="baseUrl">子節點根網址，例如 <c>http://192.168.1.20:5080</c>；尾端斜線可有可無。</param>
    /// <param name="apiKey">子節點的 Bearer 金鑰；空字串表示不帶 Authorization 標頭。</param>
    /// <param name="relativePath">相對於子節點根的路徑，含查詢字串，例如 <c>api/stream/1/playlist.m3u8?stream=main</c>。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    Task<CentralNodeResource> FetchResourceAsync(
        string baseUrl, string apiKey, string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>子節點一次 GET 的原始回應（跨機回放代理用）。</summary>
/// <param name="StatusCode">HTTP 狀態碼；原樣保留供上層轉譯（404→404、其他→502）。</param>
/// <param name="ContentType">回應的 MIME 型別；子節點未給時為 <c>null</c>。</param>
/// <param name="Body">回應主體位元組；播放清單是文字，片段是二進位。</param>
public sealed record CentralNodeResource(int StatusCode, string? ContentType, byte[] Body);
