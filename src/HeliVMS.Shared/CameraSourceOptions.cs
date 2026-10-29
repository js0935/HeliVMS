using HeliVMS.Shared.Models;

namespace HeliVMS.Shared;

/// <summary>
/// 頻道管理「來源攝影機」下拉的候選清單。
/// </summary>
/// <remarks>
/// <para>
/// 抽成純函式是為了能行為測試：桌面端沒有 WPF 執行期測試（見 <c>UiWiringContractTests</c>），
/// 而「已加入頻道的攝影機仍出現在清單裡」正是那種看畫面看不出來、要真的建過資料才會發現的缺陷
/// （同一台攝影機會被綁到兩條頻道，兩邊搶同一組 RTSP 串流）。
/// </para>
/// <para>
/// 顯示 IP 而不是名稱：同型號攝影機的預設名稱常常一模一樣（IPC、Camera01…），
/// 裝機現場是靠 IP 認機器；名稱留白時才退回顯示名稱，總比一個空白項目好。
/// </para>
/// </remarks>
public static class CameraSourceOptions
{
    /// <summary>一個下拉選項：綁定的設備 Id 與顯示文字（IP，沒有 IP 時為名稱）。</summary>
    public sealed record Option(int DeviceId, string Label);

    /// <summary>
    /// 依設備清單與「已被頻道綁定」的設備 Id 產生候選項目，順序沿用設備 Id。
    /// </summary>
    /// <param name="devices">所有攝影機（<c>DeviceRepository.List()</c> 的結果）。</param>
    /// <param name="boundDeviceIds">
    /// 已綁定頻道的設備 Id（<c>ChannelRepository.List()</c> 的 <c>DeviceId</c>，未綁定者為 null）。
    /// </param>
    public static IReadOnlyList<Option> Build(
        IEnumerable<DeviceRecord> devices,
        IEnumerable<int?> boundDeviceIds)
    {
        var bound = boundDeviceIds
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        return devices
            .Where(device => !bound.Contains(device.Id))
            .OrderBy(device => device.Id)
            .Select(device => new Option(device.Id, LabelFor(device)))
            .ToList();
    }

    /// <summary>顯示文字：IP 優先，未填 IP 的舊資料退回名稱。</summary>
    private static string LabelFor(DeviceRecord device) =>
        string.IsNullOrWhiteSpace(device.Ip) ? device.Name : device.Ip.Trim();
}
