using System.Globalization;
using System.Net;

namespace HeliVMS.Shared;

/// <summary>
/// 攝影機「網頁介面」（Web UI）的位址——單一來源。
/// </summary>
/// <remarks>
/// <para>
/// 攝影機管理要讓操作員一鍵用瀏覽器開啟那一台攝影機的設定頁。看似只要把 IP 加上
/// <c>http://</c>，但有兩個地方會讓按鈕「按了卻開到錯的地方」：
/// </para>
/// <para>
/// ① <b>devices.port 是 ONVIF 服務埠，不是網頁埠。</b> 攝影機的網頁介面幾乎都在 80
/// （少數在 8080），而 ONVIF 服務埠常常被設成 2020／8000／8899／34567／5000。
/// 直接拿它組 <c>http://ip:2020/</c>，瀏覽器會停在一個 SOAP 錯誤頁或空白頁，
/// 操作員只會得到「這個按鈕壞了」的結論。所以只有設定值本身是網頁埠時才採用，
/// 其餘一律回到 80。
/// </para>
/// <para>
/// ② <b>位址欄可能已經帶了 scheme 或埠。</b> 舊資料、手動輸入、由探索結果帶進來的
/// 值都可能是 <c>192.168.1.10:8080</c> 或 <c>http://cam.example</c>；無條件再接一次
/// <c>http://</c> 會變成無法解析的字串。
/// </para>
/// <para>
/// 這裡刻意<b>不</b>把帳號密碼放進網址：攝影機管理保存的是 RTSP／ONVIF 憑證，
/// 在瀏覽器位址列留下密碼會同時出現在歷史紀錄與截圖裡。
/// </para>
/// </remarks>
public static class CameraWebUrl
{
    /// <summary>直接視為「網頁埠」的設定值；其餘（ONVIF 服務埠）一律回到 80。</summary>
    public static IReadOnlyList<int> WebPorts { get; } = [80, 8080, 443];

    /// <summary>攝影機網頁介面在設定值不是網頁埠時使用的埠。</summary>
    public const int DefaultPort = 80;

    /// <summary>
    /// 由裝置的主機與設定埠組出網頁介面位址；位址無法解析時回 <c>null</c>
    /// （呼叫端要說明原因，不能靜默不動作）。
    /// </summary>
    /// <param name="host">devices.ip：純 IP、主機名，或已帶 scheme／埠的位址。</param>
    /// <param name="port">devices.port：ONVIF 服務埠（見類別說明）。</param>
    public static string? Build(string? host, int port)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        // 已帶 scheme 的值原樣沿用（只驗證是 http/https 且真的解析得出主機）。
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Host.Length == 0
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            return uri.AbsolutePath is "" or "/"
                ? uri.GetLeftPart(UriPartial.Authority) + "/"
                : uri.ToString();
        }

        // 其餘值只能是 host[:port]；空白、路徑或反斜線都不是攝影機位址。
        if (text.Any(char.IsWhiteSpace) || text.Contains('/') || text.Contains('\\'))
        {
            return null;
        }

        var webPort = WebPorts.Contains(port) ? port : DefaultPort;
        var scheme = webPort == 443 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var authority = Authority(text);
        if (authority is null)
        {
            return null;
        }

        // scheme 的預設埠省略不寫，位址列才乾淨（http://ip/ 而不是 http://ip:80/）。
        var suffix = webPort is 80 or 443
            ? string.Empty
            : ":" + webPort.ToString(CultureInfo.InvariantCulture);

        return $"{scheme}://{authority}{suffix}/";
    }

    /// <summary>
    /// 把主機寫成可放進網址的形式：IPv6 字面值必須加方括號，否則
    /// <c>http://fe80::1/</c> 會被解讀成「主機 fe80、埠 :1」。
    /// </summary>
    private static string? Authority(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return host.Length > 2 ? host : null;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "[" + address + "]"
                : address.ToString();
        }

        // 看起來像 IPv6 卻解析不出來（殘缺值）就不要猜；含單一冒號的 host:port 才放行。
        if (host.Contains(':'))
        {
            var colon = host.IndexOf(':', StringComparison.Ordinal);
            var hostPart = host[..colon];
            var portPart = host[(colon + 1)..];
            if (hostPart.Contains(':') || portPart.Length == 0 || !portPart.All(char.IsAsciiDigit))
            {
                return null;
            }
        }

        return host;
    }
}
