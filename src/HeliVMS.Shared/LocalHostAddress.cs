using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HeliVMS.Shared;

/// <summary>
/// 本機對外的 IPv4 位址——給「我要從辦公室連進來」的那句話用。
/// </summary>
/// <remarks>
/// <para>
/// 狀態列原本寫「Web 服務執行中（連接埠 80）」，而那句話裡唯一缺的就是主機：操作員要打的是
/// <c>http://&lt;NVR 的 IP&gt;:80</c>。這台機器常有多張網卡（現場是 Wi-Fi 加上幾張幽靈介面），
/// 隨便挑一張給出 169.254.x.x 會讓人以為服務壞了。
/// </para>
/// <para>
/// 取不到就回 <c>null</c>（呼叫端退回 localhost）：寧可寫一個一定連得上的位址，也不要猜一個。
/// 這個函式只讀介面狀態、不做任何 I/O 或探測，所以設定頁每一次套用都可以安全呼叫。
/// </para>
/// </remarks>
public static class LocalHostAddress
{
    /// <summary>第一個可用的非迴圈 IPv4；沒有時回 <c>null</c>。</summary>
    public static string? Primary()
    {
        try
        {
            var candidates = new List<(int Rank, string Address)>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                // 有線／無線優先，其餘（VPN、橋接、虛擬網卡）排後面：前者才是辦公室連得到的那一張。
                var rank = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 0,
                    NetworkInterfaceType.Wireless80211 => 1,
                    _ => 2,
                };

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    // 169.254.x.x＝DHCP 失敗的連結本地位址：連不到任何東西，給出來只會誤導。
                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    candidates.Add((rank, unicast.Address.ToString()));
                }
            }

            return candidates.Count == 0
                ? null
                : candidates.OrderBy(c => c.Rank).First().Address;
        }
        catch (NetworkInformationException)
        {
            // 介面列舉失敗（極少數的服務帳戶情境）不是錯誤：呼叫端有 localhost 可用。
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
