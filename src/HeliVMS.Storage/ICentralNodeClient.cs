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
}
