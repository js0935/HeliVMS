namespace HeliVMS.Devices.Tests;

/// <summary>
/// 會碰 PTZ 能力跨實例快取（<c>OnvifDeviceService.PtzCapabilityCache</c>，行程級靜態狀態）
/// 的測試類別所屬的集合（不與其他集合並行）。
/// </summary>
/// <remarks>
/// 見 AGENTS.md 的行程 landmine：xunit 以「集合」為並行單位，靜態快取是行程級的全域狀態，
/// 兩個類別同時「重置＋解析」會互相踩掉對方的結果（症狀是 request 計數斷言隨機失敗）。
/// Recording 專案的 recording-io 是同一招；這裡是 Devices 專案自己的版本，因為 xunit 的
/// CollectionDefinition 無法跨 assembly 共用。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OnvifPtzCacheCollection
{
    /// <summary>集合名稱。</summary>
    public const string Name = "onvif-ptz-cache";
}
