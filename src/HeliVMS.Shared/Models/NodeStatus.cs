namespace HeliVMS.Shared.Models;

/// <summary>
/// 節點身分與狀態（§14.1 #10 中央/多機集群管理，M295）。
///
/// 由子節點的 <c>GET /api/node</c> 回傳，中央站據此顯示「這台是誰、跑哪個版本、有幾路」。
/// <c>node_id</c> 取 <c>MachineIdProvider.GetDeviceCode()</c>：主機板＋CPU 的穩定指紋，
/// 比 IP 更能代表「同一台機器」（換網段、換網卡都不變）；若指紋與登記時不同，代表換機或被複製。
/// </summary>
public sealed record NodeStatus(
    string NodeId,
    string Name,
    string Version,
    int Channels);
