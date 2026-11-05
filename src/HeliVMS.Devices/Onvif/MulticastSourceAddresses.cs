using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 多播探測要用哪些來源位址（每個可多播的網卡各一份）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼一定要逐網卡送：不綁定來源位址的 UdpClient 只會從「預設路由」那個介面送出多播，
/// 於是多網卡的機器（NVR 常見：監控網段一張、辦公網段一張）永遠只找得到其中一邊的攝影機，
/// 而畫面上沒有任何錯誤——操作員的結論會是「這台搜尋壞了」。ONVIF Device Manager 與
/// wsdiscovery 一類實作都是逐介面探測後合併結果。
/// </para>
/// <para>
/// 抽成純函式是為了把選擇規則講清楚並可測試：只挑 Up 且支援多播的介面、只取 IPv4 單播位址、
/// 去重；環回只在「什麼都沒有的時候」用（它到不了攝影機，但測試與單機環境仍需要一個來源），
/// 一個都沒有時退回 <see cref="IPAddress.Any"/>（＝不綁定，與舊行為相同）。
/// </para>
/// </remarks>
public static class MulticastSourceAddresses
{
    /// <summary>一個網路的精簡描述（測試不必真的建網卡）。</summary>
    public readonly record struct Adapter(
        string Name,
        bool IsUp,
        bool SupportsMulticast,
        IReadOnlyList<IPAddress> UnicastAddresses);

    /// <summary>挑出要用的來源位址；沒有可用介面時回 <c>[IPAddress.Any]</c>。</summary>
    public static IReadOnlyList<IPAddress> Select(IEnumerable<Adapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var usable = new List<IPAddress>();
        var loopback = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var adapter in adapters)
        {
            var candidates = adapter.IsUp && adapter.SupportsMulticast
                ? adapter.UnicastAddresses
                : [];

            foreach (var address in candidates)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || !seen.Add(address.ToString()))
                {
                    continue;
                }

                if (IPAddress.IsLoopback(address))
                {
                    loopback.Add(address);
                }
                else
                {
                    usable.Add(address);
                }
            }
        }

        if (usable.Count > 0)
        {
            return usable;
        }

        return loopback.Count > 0 ? loopback : [IPAddress.Any];
    }

    /// <summary>列舉本機實際的網卡並挑出來源位址。</summary>
    public static IReadOnlyList<IPAddress> Enumerate()
    {
        try
        {
            var adapters = new List<Adapter>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Select(ua => ua.Address)
                    .ToList();
                adapters.Add(new Adapter(
                    nic.Name,
                    nic.OperationalStatus == OperationalStatus.Up,
                    nic.SupportsMulticast,
                    addresses));
            }

            return Select(adapters);
        }
        catch (Exception)
        {
            // 列舉失敗（權限、驅動）不是致命的：退回不綁定來源位址的舊行為。
            return [IPAddress.Any];
        }
    }
}
