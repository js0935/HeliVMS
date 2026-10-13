namespace HeliVMS.Storage;

/// <summary>
/// 中央站登記的一個子節點（central_nodes 表；§14.1 #10，M295）。
/// </summary>
/// <remarks>
/// 前半段（<see cref="Name"/>／<see cref="BaseUrl"/>／<see cref="ApiKey"/>／<see cref="Enabled"/>）是
/// 操作員設定值；後半段是最近一次探測 <c>GET /api/node</c> 的快取，讓 UI 不必逐台連線就能顯示狀態。
/// <see cref="ApiKey"/> 是呼叫子節點 API 的 Bearer 金鑰，呼叫端不可把它輸出到 API 回應。
/// <see cref="Online"/> 為 <c>null</c> 表示尚未探測過。
/// </remarks>
public sealed record CentralNode(
    long Id,
    string Name,
    string BaseUrl,
    string ApiKey,
    bool Enabled,
    string? NodeId,
    string? Version,
    int? Channels,
    bool? Online,
    DateTime? LastSeenUtc,
    string? LastError);
