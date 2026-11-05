using System.Net.Sockets;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 掃描用的 TCP 連線預檢：先看哪些埠真的有人在聽，再對那些埠做 ONVIF 探測。
/// </summary>
/// <remarks>
/// <para>
/// 兩個好處：①**快**——沒有回應的位址只要等一次連線逾時（數百毫秒），而不是每個埠各等一次
/// ONVIF 的兩秒逾時；②**找得到非標準埠**——海康／大華／TVT 的 ONVIF 可能在 8899／34567／5000，
/// 只試固定幾個埠的清單一定會漏掉，而先掃埠再探測的成本比逐埠探測低。
/// </para>
/// <para>
/// TCP 連得上不代表是 ONVIF（可能只是登入頁或 RTSP），所以預檢只是**過濾**：
/// 過了還是要跑一次 ONVIF 呼叫才算找到設備。
/// </para>
/// </remarks>
public static class OnvifPortScan
{
    /// <summary>
    /// 單一埠的連線逾時（掃描要的是「有沒有人在聽」，不是完整握手）。整個位址的預檢成本＝
    /// 埠數 × 這個值，而掃描是逐位址循序跑這個清單，所以它同時決定了「沒設備的位址要等多久」。
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 逐一檢查哪些埠接受連線；回傳的順序與 <paramref name="ports"/> 相同（清單順序即優先序）。
    /// </summary>
    public static async Task<IReadOnlyList<int>> OpenPortsAsync(
        string host,
        IReadOnlyList<int> ports,
        TimeSpan? connectTimeout = null,
        Func<string, int, TimeSpan, CancellationToken, Task<bool>>? probe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(ports);

        var timeout = connectTimeout ?? DefaultConnectTimeout;
        probe ??= TryConnectAsync;

        var open = new List<int>();
        foreach (var port in ports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await probe(host, port, timeout, cancellationToken).ConfigureAwait(false))
            {
                open.Add(port);
            }
        }

        return open;
    }

    /// <summary>單一 TCP 連線測試（逾時或拒絕都算「沒有人在聽」）。</summary>
    private static async Task<bool> TryConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 逾時＝沒有回應；呼叫端仍可繼續下一個埠。
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
