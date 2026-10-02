using System.Net;
using System.Net.Sockets;

namespace HeliVMS.Rtc;

/// <summary>
/// 接收 ffmpeg 送來的 RTP 封包。
/// <para>
/// <b>只綁 loopback。</b>這不是偷懶而是安全邊界：這條 socket 傳的是<b>未加密</b>的
/// H.264，綁到 0.0.0.0 等於把攝影機畫面開給同網段任何主機，甚至讓外部流量繞進來。
/// 瀏覽器那一段由 SIPSorcery 以 DTLS-SRTP 加密，兩段的安全性刻意不同。
/// </para>
/// <para>
/// 這一層刻意<b>不</b>用 SIPSorcery 的 <c>RTPSession</c>：那需要為它偽造一份 SDP
/// 與 ICE 環境，而我們只要一個 socket。直接用 <see cref="UdpClient"/> + 自己的標頭
/// 解析更容易測、也更少機會被 SDP 邏輯綁住。
/// </para>
/// </summary>
public sealed class RtpIngest : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stopping = new();
    private readonly IPEndPoint _target;
    private Task? _pump;

    /// <summary>綁定 loopback 的隨機可用埠。</summary>
    public RtpIngest()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bound = (IPEndPoint)_udp.Client.LocalEndPoint!;
        _target = new IPEndPoint(IPAddress.Loopback, bound.Port);
    }

    /// <summary>ffmpeg 應送 RTP 到的主機與埠（永遠是 loopback）。</summary>
    public IPEndPoint Target => _target;

    /// <summary>實際 listen 埠。</summary>
    public int Port => _target.Port;

    /// <summary>已收到的 RTP 封包數；用於就緒判定與診斷。</summary>
    public long PacketCount => Interlocked.Read(ref _packets);

    private long _packets;

    /// <summary>收到封包時的攔截點。標頭與 payload 皆指向同一個緩衝。</summary>
    public Action<RtpHeader, ReadOnlyMemory<byte>>? OnPacket { get; set; }

    /// <summary>開始收包。</summary>
    public void Start()
    {
        _pump ??= Task.Run(() => PumpAsync(_stopping.Token));
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // socket 被中止或逾時；已取消時是預期路徑，其他情況就讓迴圈再試一次。
                if (token.IsCancellationRequested) return;
                continue;
            }

            var buffer = received.Buffer;
            if (!RtpHeader.TryParse(buffer, out var header)) continue;
            if (header.PayloadLength <= 0) continue;

            Interlocked.Increment(ref _packets);

            // 單一訂閱者丟例外不該讓整條 ingest 停擺（一位觀看者的連線剛好死掉
            // 是常態，不是要記錄的系統性錯誤）。
            try
            {
                OnPacket?.Invoke(header, buffer.AsMemory(header.PayloadOffset, header.PayloadLength));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 轉送失敗：交給呼叫端記錄，這裡吞掉以維持串流。
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _udp.Dispose();
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stopping.Dispose();
    }
}