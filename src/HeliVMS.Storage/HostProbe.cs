using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HeliVMS.Storage;

/// <summary>可達性探測結果的種類。</summary>
public enum HostProbeOutcome
{
    /// <summary>有回應（ICMP 或 TCP 其中之一）。</summary>
    Reached,

    /// <summary>兩種探測都沒有回應。</summary>
    Timeout,

    /// <summary>位址是空的或無法解析，連探測都沒發出。</summary>
    InvalidAddress,
}

/// <summary>一次探測的結果；<see cref="ViaTcp"/> 表示是 TCP 連線成功而不是 ICMP 回覆。</summary>
public sealed record HostProbeResult(HostProbeOutcome Outcome, int LatencyMs = 0, bool ViaTcp = false)
{
    public bool Reached => Outcome == HostProbeOutcome.Reached;
}

/// <summary>
/// 「這台攝影機還在不在」的探測：先 ICMP，沒回應再試裝置的 TCP 連接埠。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要分兩段：現場不少攝影機、交換器或防火牆直接丟掉 ICMP，只回 TCP。只做 ICMP 會把
/// 一台好好在出畫的攝影機報成「逾時」，操作員就會去重開一台沒壞的機器——這比沒有 Ping
/// 更糟，因為它給了一個看起來很確定的錯誤答案。
/// </para>
/// <para>
/// 反過來只做 TCP 也不夠：RTSP／ONVIF 埠可能被設定成只在拉流時才連得上。先 ICMP 最快也最
/// 沒副作用，失敗才退回 TCP；兩者都失敗才叫不通，而且訊息要說清楚「ICMP 與 TCP 都沒回」，
/// 讓操作員知道是自己封了 ICMP 還是機器真的掛了。
/// </para>
/// <para>
/// ICMP 與 TCP 都做成可注入的委派：測試因此不需要真的連網，也就不會因為 CI 的網路環境而
/// 時紅時綠；真正的系統呼叫只剩兩支很薄的私有方法。
/// </para>
/// </remarks>
public static class HostProbe
{
    /// <summary>送出 ICMP 並回傳（是否成功, 延遲毫秒）。</summary>
    public delegate Task<(bool Success, int LatencyMs)> IcmpSender(string host, TimeSpan timeout);

    /// <summary>嘗試 TCP 連線並回傳是否連上。</summary>
    public delegate Task<bool> TcpConnector(string host, int port, TimeSpan timeout);

    /// <summary>
    /// 探測單一主機。位址可以是純 IP／主機名、<c>ip:port</c>，或整條 RTSP／HTTP 網址。
    /// </summary>
    /// <param name="hostOrAddress">裝置設定的位址字串（可能含埠或整條網址）。</param>
    /// <param name="tcpPort">位址本身沒有埠時要測的連接埠（裝置的 ONVIF／HTTP 埠）。</param>
    /// <param name="timeout">單一階段的逾時；ICMP 與 TCP 各自適用。</param>
    /// <param name="icmp">測試用的 ICMP 替身；null 表示用系統的 IcmpSendEcho。</param>
    /// <param name="tcp">測試用的 TCP 替身；null 表示真的連線。</param>
    public static async Task<HostProbeResult> ProbeAsync(
        string? hostOrAddress,
        int tcpPort,
        TimeSpan timeout,
        IcmpSender? icmp = null,
        TcpConnector? tcp = null)
    {
        var (host, port, valid) = ParseEndpoint(hostOrAddress, tcpPort);
        if (!valid)
        {
            return new HostProbeResult(HostProbeOutcome.InvalidAddress);
        }

        var started = Stopwatch.StartNew();

        try
        {
            var (success, latency) = await (icmp ?? SendIcmpAsync)(host, timeout).ConfigureAwait(false);
            if (success)
            {
                return new HostProbeResult(HostProbeOutcome.Reached, Math.Max(0, latency));
            }
        }
        catch (Exception)
        {
            // 名稱解析失敗、權限不足、網路介面變動都會擲；一律當「這一階段沒回應」，
            // 交給 TCP 再判一次，而不是把例外丟到 UI 變成一句看不懂的錯誤。
        }

        try
        {
            if (await (tcp ?? TryTcpAsync)(host, port, timeout).ConfigureAwait(false))
            {
                // TCP 沒有 ping 的往返時間可用，改用整體耗時；它代表的是「連得上要多久」，
                // 對操作員的意義與 ICMP 的 RTT 相同。
                return new HostProbeResult(HostProbeOutcome.Reached, (int)started.ElapsedMilliseconds, ViaTcp: true);
            }
        }
        catch (Exception)
        {
        }

        return new HostProbeResult(HostProbeOutcome.Timeout);
    }

    /// <summary>
    /// 從裝置設定的位址字串取出主機與要測的 TCP 埠。
    /// </summary>
    /// <remarks>
    /// 裝置表的 <c>ip</c> 欄位理應只有主機，但現場貼上的常常是 <c>rtsp://…</c> 或
    /// <c>192.168.1.10:8000</c>。與其讓 Ping 對這種字串直接失敗，不如在這裡拆乾淨，
    /// 而且埠以字串裡的為準（它比 devices.port 更接近實際在聽的埠）。
    /// </remarks>
    public static (string Host, int Port, bool Valid) ParseEndpoint(string? raw, int fallbackPort)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return (string.Empty, 0, false);
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            // 只取 scheme 之後、第一個路徑分隔之前的那一段（authority），再丟回下面的
            // 裸位址邏輯處理。刻意不用 Uri.IsDefaultPort 判斷「有沒有寫埠」：rtsp 的預設埠
            // 就是 554，Uri 會回報「用的是預設埠」，於是 rtsp://…:554 反而被換成 devices.port，
            // 探測就打錯埠了。直接看字串有沒有寫埠才不會騙人。
            var afterScheme = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var end = afterScheme.IndexOfAny(['/', '?', '#']);
            var authority = end < 0 ? afterScheme : afterScheme[..end];

            // rtsp://user:pass@host:554/... —— 認證資訊不屬於主機，切掉（含 @ 之後才是位址）。
            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                authority = authority[(at + 1)..];
            }

            if (authority.Length == 0)
            {
                return (string.Empty, 0, false);
            }

            return ParseEndpoint(authority, fallbackPort);
        }

        var host = text;
        var port = NormalizePort(fallbackPort);

        if (text.StartsWith('['))
        {
            // [fe80::1]:8000 —— 方括號是 IPv6 位址裡唯一能安全切埠的寫法。
            var close = text.IndexOf(']');
            if (close <= 1)
            {
                return (string.Empty, 0, false);
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.StartsWith(':'))
            {
                port = ParsePort(rest[1..], port);
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':');
            host = text[..colon];
            port = ParsePort(text[(colon + 1)..], port);
        }
        // 兩個以上冒號又沒有方括號＝裸 IPv6，整串都是主機，不能切。

        host = host.Trim();
        return host.Length == 0 ? (string.Empty, 0, false) : (host, port, true);
    }

    private static int ParsePort(string text, int fallback)
        => int.TryParse(text, out var port) && port is > 0 and <= 65535 ? port : fallback;

    private static int NormalizePort(int port) => port is > 0 and <= 65535 ? port : 80;

    /// <summary>真正的 ICMP：交由系統的 IcmpSendEcho（不需要系統管理員權限）。</summary>
    private static async Task<(bool Success, int LatencyMs)> SendIcmpAsync(string host, TimeSpan timeout)
    {
        using var ping = new Ping();
        var reply = await ping
            .SendPingAsync(host, (int)Math.Clamp(timeout.TotalMilliseconds, 1, 60_000))
            .ConfigureAwait(false);

        return reply.Status == IPStatus.Success
            ? (true, (int)Math.Max(0, reply.RoundtripTime))
            : (false, 0);
    }

    /// <summary>真正的 TCP：連上就算可達；連不上（含逾時、被拒）都只回 false。</summary>
    private static async Task<bool> TryTcpAsync(string host, int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
