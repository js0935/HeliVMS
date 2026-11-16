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

    /// <summary>
    /// 這條 socket 的接收緩衝要多大。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>這是實測出來的第一個「WHEP 沒畫面」主因。</b>Windows 的 UDP 預設接收緩衝約 64 KB，
    /// 而一條 1080p／2 Mbps 的 H.264 IDR 影格本身就有約 70–80 個封包、約 110 KB：
    /// ffmpeg 把整張 IDR 一次寫出來，緩衝瞬間就滿，超出的一個影格內就補不回來。
    /// </para>
    /// <para>
    /// 而本專案沒有接 RTX／NACK（SIPSorcery 不重傳），掉一個封包就毀一張影格——
    /// 掉在 IDR 上更慘：瀏覽器要等下一個關鍵影格，而每個關鍵影格都會再掉一次。
    /// 實際量到的結果就是「ICE／DTLS 都 established、Chrome 每秒送 PLI、
    /// 畫面十幾秒才出來一張」，而兩邊日誌都是乾淨的。
    /// </para>
    /// <para>
    /// 1 MB 留的是「好幾張 IDR 加上一整秒的 P-frame」的餘裕，而不是無上限：
    /// 真正的無上限只是在延後症狀，掉包仍會發生，只是掉在比較晚的地方；
    /// 而且 Windows 的 socket 緩衝算在非分頁集區，每條通道配太大的緩衝
    /// 在 32 路以上會變成另一個問題（這台機器實測預設值只有 64 KB，
    /// 1 MB 已是 16 倍，足以容納一整張 1080p IDR 的十幾倍）。
    /// </para>
    /// </remarks>
    internal const int ReceiveBufferBytes = 1024 * 1024;

    /// <summary>綁定 loopback 的隨機可用埠。</summary>
    public RtpIngest()
    {
        // 刻意自己建 socket 再 bind：ReceiveBufferSize 必須在資料進來之前設定，
        // 而 UdpClient(IPEndPoint) 的建構子會先把 socket 綁好。
        _udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            _udp.Client.ReceiveBufferSize = ReceiveBufferBytes;
        }
        catch (SocketException)
        {
            // 系統不給這麼大的緩衝不是致命錯誤：沿用系統值，行為退回舊的樣子。
            // 這種情況會反映在 PacketsMissed 上，而不是讓整條串流起不來。
        }

        _udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var bound = (IPEndPoint)_udp.Client.LocalEndPoint!;
        _target = new IPEndPoint(IPAddress.Loopback, bound.Port);
    }

    /// <summary>這條 socket 實際生效的接收緩衝大小（見 <see cref="ReceiveBufferBytes"/>）。</summary>
    internal int ReceiveBufferSize => _udp.Client.ReceiveBufferSize;

    /// <summary>ffmpeg 應送 RTP 到的主機與埠（永遠是 loopback）。</summary>
    public IPEndPoint Target => _target;

    /// <summary>實際 listen 埠。</summary>
    public int Port => _target.Port;

    /// <summary>已收到的 RTP 封包數；用於就緒判定與診斷。</summary>
    public long PacketCount => Interlocked.Read(ref _packets);

    /// <summary>
    /// 依序號推算「ffmpeg 送了但這裡沒收到」的封包數。
    /// <para>
    /// 這個數字回答「WHEP 沒畫面」最容易分錯的一件事：封包掉了是掉在
    /// <b>攝影機→ffmpeg→這裡</b>（本機收包太慢、socket 緩衝太小），還是掉在
    /// <b>這裡→瀏覽器</b>（轉送或 SRTP 送不出去）。兩邊的症狀完全一樣——ICE／DTLS
    /// established、Chrome 每秒送 PLI、畫面十幾秒才出來一張——但處置相反。
    /// </para>
    /// <para>
    /// 這裡之所以值得留一個計數器：本專案<b>沒有</b>接 RTX／NACK（SIPSorcery 不重傳），
    /// 所以掉一個封包就毀一張影格；那張若是 IDR，瀏覽器就得等下一個關鍵影格，
    /// 而關鍵影格本身又是幾十個封包組起來的——掉包率稍高就足以讓畫面永遠起不來。
    /// </para>
    /// </summary>
    public long PacketsMissed => Interlocked.Read(ref _missed);

    private long _packets;
    private long _missed;

    /// <summary>上一個收到的序號；<c>-1</c> 表示還沒收到任何封包。</summary>
    private int _lastSequence = -1;

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
            TrackSequence(header.SequenceNumber);

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

    /// <summary>以序號空隙累計遺失封包數（見 <see cref="PacketsMissed"/>）。</summary>
    private void TrackSequence(ushort sequenceNumber)
    {
        var previous = _lastSequence;
        if (previous < 0)
        {
            _lastSequence = sequenceNumber;
            return;
        }

        // 序號是 16 位元、會回繞，所以差值一定要用 ushort 相減：
        // 直接用 int 相減會在回繞那一刻得到 -65535，被當成六萬多個遺失封包。
        var gap = unchecked((ushort)(sequenceNumber - (ushort)previous));

        // 只有「往前走」才推進基準。遲到的舊封包（gap >= 0x8000）若也更新基準，
        // 下一次正常封包就會被算成一次巨大的空隙——實測 100,101,100,99,102
        // 在「無條件更新基準」的寫法下報 2 個遺失，而其實一個都沒漏。
        // gap == 0 是重複封包，同樣不動基準。
        if (gap is 0 or >= 0x8000) return;

        _lastSequence = sequenceNumber;
        if (gap > 1) Interlocked.Add(ref _missed, gap - 1);
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