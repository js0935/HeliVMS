using System.Net.Sockets;

namespace HeliVMS.Qctek.Nemo;

/// <summary>連線「第一包」的讀取結果。</summary>
public enum InitialReadStatus
{
    /// <summary>讀到資料（位元組數見 <c>count</c>）。</summary>
    Data,

    /// <summary>對端在第一包之前就關線（位元組數為 0）。</summary>
    Closed,

    /// <summary>等到閒置上限仍沒有資料。</summary>
    TimedOut,
}

/// <summary>
/// 連線「第一包」的讀取：同步阻塞讀＋閒置上限＋結果分類（<see cref="InitialReadStatus"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要共用一份：Web 埠多工器（<see cref="QctekPortMux"/>）與獨立 NEMO 埠
/// （<see cref="NemoServer"/>）都在「對端還沒送任何位元組」的階段等待，而兩邊都曾各自
/// 寫過「沉默就無限等」。症狀一模一樣：連線物件永遠活著、專用執行緒被佔住、
/// 日誌<em>什麼都沒有</em>——而「QCC 根本沒連進來」與「連進來但一個位元組都沒送」
/// 在日誌上長成同一件事，兩者的處置卻完全相反。
/// </para>
/// <para>
/// 沉默不是任何一種協定的開場：真機的 live 連線以 <c>GET_LIVE_START</c> 開場、
/// HTTP 請求以請求行開場（2026-11-14 逐包實錄）。所以等待必須有上限；「上限到」
/// 與「對端關線」要能分辨，前者是對方卡住、後者是對方放棄。
/// </para>
/// <para>
/// <b>逾時只屬於第一包。</b>離開這裡時 <c>ReceiveTimeout</c> 一定還原成呼叫前的值，
/// 否則 NEMO 的長連線（警報回報器每 20 秒才發一條）與 live 通道會在閒置時被砍，
/// 而症狀只是「畫面每隔幾十秒黑一次」。
/// </para>
/// </remarks>
public static class InitialReadGate
{
    /// <summary>第一包的等待上限。QCC 與真機都在毫秒級送出，30 秒只是「不要無限等」。</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>以預設上限讀第一包。</summary>
    public static InitialReadStatus Read(
        TcpClient client,
        Stream stream,
        byte[] buffer,
        out int count)
        => Read(client, stream, buffer, DefaultIdleTimeout, out count);

    /// <summary>讀第一包；<paramref name="idleTimeout"/> 到就回 <see cref="InitialReadStatus.TimedOut"/>。</summary>
    public static InitialReadStatus Read(
        TcpClient client,
        Stream stream,
        byte[] buffer,
        TimeSpan idleTimeout,
        out int count)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(buffer);

        count = 0;
        var previous = SafeGetTimeout(client);
        try
        {
            SafeSetTimeout(
                client,
                (int)Math.Clamp(idleTimeout.TotalMilliseconds, 1, int.MaxValue));
            try
            {
                count = stream.Read(buffer, 0, buffer.Length);
                return count == 0 ? InitialReadStatus.Closed : InitialReadStatus.Data;
            }
            catch (IOException ex) when (IsTimeout(ex))
            {
                return InitialReadStatus.TimedOut;
            }
        }
        finally
        {
            SafeSetTimeout(client, previous);
        }
    }

    /// <summary>
    /// 只認真正的「閒置逾時」：連線被重設或關閉時的 <see cref="IOException"/> 也要往外丟，
    /// 否則對端一斷線就會被記成「沉默逾時」，把日誌變成另一種謊。
    /// </summary>
    private static bool IsTimeout(IOException ex)
        => ex.InnerException is SocketException
        {
            SocketErrorCode: SocketError.TimedOut or SocketError.WouldBlock,
        };

    private static int SafeGetTimeout(TcpClient client)
    {
        try
        {
            return client.ReceiveTimeout;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void SafeSetTimeout(TcpClient client, int milliseconds)
    {
        try
        {
            client.ReceiveTimeout = milliseconds;
        }
        catch (Exception)
        {
            // socket 已被關閉（StopAsync 或對端重設）：沒有東西可設，也不影響收尾。
        }
    }
}
