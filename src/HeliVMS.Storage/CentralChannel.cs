namespace HeliVMS.Storage;

/// <summary>
/// 中央站聚合到的子節點頻道（§14.1 #10，M302）。
/// </summary>
/// <remarks>
/// 由子節點 <c>GET /api/channels</c> 取得後，補上來源節點（<see cref="NodeId"/>／<see cref="NodeName"/>）。
/// 跨機時「頻道編號」只有在同一台節點內才唯一，所以兩者必須一起帶著走，否則上層無法分辨
/// 「節點 A 的第 1 路」與「節點 B 的第 1 路」。
/// </remarks>
/// <param name="NodeId">來源節點在 <c>central_nodes</c> 的主鍵。</param>
/// <param name="NodeName">來源節點顯示名稱（節點被改名時以當下名稱呈現）。</param>
/// <param name="ChannelId">子節點上的頻道編號。</param>
/// <param name="ChannelName">頻道名稱。</param>
/// <param name="Enabled">該頻道是否啟用（停用仍是有效條目，只是不錄影）。</param>
public sealed record CentralChannel(
    long NodeId,
    string NodeName,
    int ChannelId,
    string ChannelName,
    bool Enabled);
