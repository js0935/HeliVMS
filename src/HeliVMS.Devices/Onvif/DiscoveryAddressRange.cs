using System.Net;
using System.Net.Sockets;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 「搜尋位址」輸入的展開：單一 IP／主機名、CIDR 網段（<c>192.168.1.0/24</c>）、
/// 末碼範圍（<c>192.168.1.1-50</c>）、完整範圍（<c>192.168.1.1-192.168.1.50</c>），
/// 以及以逗號／分號／空白分隔的混合清單。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：多播 WS-Discovery 在 VLAN／VPN／企業 Wi-Fi 常被擋掉，搜尋不到設備時
/// 現場只能一台一台輸入 IP。新裝機的實際流程是「機器接上去、一次把十幾台加進來」，
/// 所以一次掃一個網段才是符合流程的介面。
/// </para>
/// <para>
/// 上限 <see cref="MaxAddresses"/> 是保護：<c>/16</c> 有 65,534 個位址，掃完要好幾小時，
/// 而且畫面上只有一條跑不完的進度。超過上限時明確回報「已截斷」，讓使用者知道只掃了
/// 一部分，而不是以為整段都掃過了、只是都沒設備。
/// </para>
/// </remarks>
public static class DiscoveryAddressRange
{
    /// <summary>單次掃描的位址上限（約 2 個 /24，足夠單一機房的 VLAN）。</summary>
    public const int MaxAddresses = 512;

    /// <summary>展開結果。</summary>
    /// <param name="Addresses">要探測的主機（已去重、保持輸入順序）。</param>
    /// <param name="Truncated">是否因超過 <see cref="MaxAddresses"/> 而截斷。</param>
    public sealed record Expansion(IReadOnlyList<string> Addresses, bool Truncated);

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>把使用者輸入展開成待探測的主機清單。</summary>
    public static Expansion Expand(string? input)
    {
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        if (string.IsNullOrWhiteSpace(input))
        {
            return new Expansion(addresses, false);
        }

        foreach (var raw in input.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Append(raw.Trim(), addresses, seen, ref truncated))
            {
                continue;
            }

            if (addresses.Count >= MaxAddresses)
            {
                truncated = true;
                break;
            }
        }

        return new Expansion(addresses, truncated);
    }

    /// <summary>加入一個 token；回傳是否加入了任何位址。</summary>
    private static bool Append(string token, List<string> addresses, HashSet<string> seen, ref bool truncated)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (LooksLikeCidr(token))
        {
            // 看起來是網段就只認網段：解析不了就不要退回「一個叫 192.168.1.0/99 的主機名」，
            // 那會變成一次註定失敗的探測，而使用者只看到「沒有設備」。
            return TryExpandCidr(token, addresses, seen, ref truncated);
        }

        if (token.Contains('-'))
        {
            return TryExpandRange(token, addresses, seen, ref truncated);
        }

        Add(token, addresses, seen);
        return true;
    }

    /// <summary>
    /// 是不是「使用者想打的網段」。判準是斜線左邊必須是 IPv4 位址：
    /// <c>192.168.1.0/24</c> 是網段，而 <c>http://192.168.1.9/onvif/device_service</c> 只是
    /// 一個含斜線的網址，仍要當成單一位址。
    /// </summary>
    private static bool LooksLikeCidr(string token)
    {
        var slash = token.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        return IPAddress.TryParse(token[..slash], out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork;
    }

    private static bool TryExpandCidr(string token, List<string> addresses, HashSet<string> seen, ref bool truncated)
    {
        var parts = token.Split('/');
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var baseAddress) ||
            baseAddress.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) ||
            prefix is < 0 or > 32)
        {
            return false;
        }

        var network = ToUInt32(baseAddress);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var first = network & mask;
        var last = first | ~mask;

        // /31 與 /32 沒有「網路位址／廣播位址」可分：兩個（或一個）都是可用主機。
        if (prefix <= 30)
        {
            first += 1;
            last -= 1;
        }

        for (var value = first; ; value++)
        {
            Add(FromUInt32(value), addresses, seen);
            if (addresses.Count >= MaxAddresses)
            {
                truncated = value < last;
                return true;
            }

            if (value == last)
            {
                break;
            }
        }

        return true;
    }

    private static bool TryExpandRange(string token, List<string> addresses, HashSet<string> seen, ref bool truncated)
    {
        var parts = token.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var start) ||
            start.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        // 右側允許只寫末碼（192.168.1.1-50）或完整位址（192.168.1.1-192.168.1.50）。
        IPAddress? end;
        if (parts[1].Length <= 3 && parts[1].All(char.IsAsciiDigit))
        {
            var octets = start.ToString().Split('.');
            end = IPAddress.TryParse($"{octets[0]}.{octets[1]}.{octets[2]}.{parts[1]}", out var last)
                ? last
                : null;
        }
        else
        {
            end = IPAddress.TryParse(parts[1], out var last) && last.AddressFamily == AddressFamily.InterNetwork
                ? last
                : null;
        }

        if (end is null)
        {
            return false;
        }

        var first = ToUInt32(start);
        var lastValue = ToUInt32(end);
        if (lastValue < first)
        {
            // 頭尾顛倒：視為輸入錯誤而不是掃描整個反向位址空間。
            return false;
        }

        for (var value = first; ; value++)
        {
            Add(FromUInt32(value), addresses, seen);
            if (addresses.Count >= MaxAddresses)
            {
                truncated = value < lastValue;
                return true;
            }

            if (value == lastValue)
            {
                break;
            }
        }

        return true;
    }

    private static void Add(string host, List<string> addresses, HashSet<string> seen)
    {
        if (addresses.Count < MaxAddresses && seen.Add(host))
        {
            addresses.Add(host);
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string FromUInt32(uint value)
        => $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
}
