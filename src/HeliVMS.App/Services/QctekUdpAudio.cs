using System.Net;
using System.Net.Sockets;
using HeliVMS.Qctek.Live;

namespace HeliVMS.App.Services;

/// <summary>
/// 接收 ffmpeg 送到本機 UDP 埠的 G.711 µ-law 位元組流（QCC 即時影像的音軌）。
/// </summary>
/// <remarks>
/// <para>
/// 埠在<b>啟動 ffmpeg 之前</b>就綁好（作業系統配一個空閒埠，見 <see cref="Port"/>），
/// 因為埠號要寫進 ffmpeg 的引數；「先綁著但還沒開始讀」是刻意的——聲音還沒被要之前，
/// 送來的封包由作業系統丟掉，不會反壓 ffmpeg 影響<b>畫面</b>。
/// </para>
/// <para>
/// 為什麼不用 <c>UdpClient.ReceiveAsync</c>：即時影像的輪詢執行緒每 33ms 走一圈，
/// 非同步接收需要一條額外的迴圈與取消處理；這裡只要「現在有什麼就拿什麼」，
/// 用 <see cref="Socket.Available"/> ＋ 同步 <see cref="Socket.Receive(Span{byte}, SocketFlags, out SocketError)"/>
/// 就是最短路徑，而且不會阻塞 tick。
/// </para>
/// </remarks>
internal sealed class QctekUdpAudio : IQctekLiveAudio, IDisposable
{
    private readonly Socket _socket;
    private int _disposed;

    public QctekUdpAudio()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // 一個音訊封包最大也只有幾 KB，但突發（ffmpeg 把緩衝一次倒出）可能好幾包；
        // 緩衝留大一點，讓 tick 之間到達的封包不會被作業系統丟掉。
        try
        {
            _socket.ReceiveBufferSize = 64 * 1024;
        }
        catch (SocketException)
        {
            // 部分環境不允許調整：用預設值，最差只是突發時多掉幾包聲音。
        }

        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    /// <summary>已綁定的本機 UDP 埠（要寫進 ffmpeg 的引數）。</summary>
    public int Port { get; }

    /// <summary>目前可讀的位元組數（診斷用；0 ＝ 這一段沒有聲音進來）。</summary>
    public int Available
    {
        get
        {
            try
            {
                return _socket.Available;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>非阻塞讀取：讀不到（或已被釋放）回 0。</summary>
    public int Read(Span<byte> destination)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        try
        {
            if (_socket.Available <= 0)
            {
                return 0;
            }

            var count = _socket.Receive(destination, SocketFlags.None, out var error);
            return error == SocketError.Success ? count : 0;
        }
        catch (SocketException)
        {
            // 收尾中的競爭（ICMP port unreachable 等）：音訊可以沒有，不能讓 tick 停。
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception)
        {
            // socket 收尾失敗不影響來源關閉。
        }
    }
}
