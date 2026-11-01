using System.Net;
using System.Net.Sockets;

namespace HeliVMS.Media;

/// <summary>
/// 接收解碼 ffmpeg 送到本機 UDP 埠的 PCM，並切成固定大小的片段。
/// </summary>
/// <remarks>
/// <para>
/// 埠在建立時就綁好（作業系統配一個空閒埠，見 <see cref="Port"/>），因為 ffmpeg 的參數要在
/// 行程啟動前就寫定；「先綁著但還沒開始讀」是刻意的——聲音通道還沒打開時，送來的封包由
/// 作業系統丟掉，沒有錯誤、也不會反壓影響畫面。
/// </para>
/// <para>
/// <see cref="Start"/> 會先把積在佇列裡的舊資料排掉：從「按下喇叭」到「開始讀」之間可能積了
/// 一兩秒的聲音，先播那段只會讓人以為聲音對不上畫面。
/// </para>
/// <para>
/// 只用 <c>System.Net.Sockets</c>，不碰播放裝置，所以沒有音效卡的 CI 也測得起來。
/// </para>
/// </remarks>
public sealed class AudioStreamReceiver : IDisposable
{
    /// <summary>預設取樣率：語音用途夠清楚，資料量只有 44.1 kHz 立體聲的十分之一。</summary>
    public const int DefaultSampleRate = 16000;

    /// <summary>每個片段代表 100 ms 的聲音（播放端一次寫進裝置的單位）。</summary>
    public const int ChunkMilliseconds = 100;

    private readonly object _gate = new();
    private readonly UdpClient _client;
    private readonly byte[] _drainBuffer;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="sampleRate">來源取樣率；須與 ffmpeg 的 <c>-ar</c> 一致。</param>
    /// <param name="chunkMilliseconds">片段長度（毫秒）。</param>
    public AudioStreamReceiver(int sampleRate = DefaultSampleRate, int chunkMilliseconds = ChunkMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkMilliseconds, 10);

        SampleRate = sampleRate;
        ChunkBytes = sampleRate * 2 * chunkMilliseconds / 1000;

        // 先綁定才拿得到埠號；綁 0 讓作業系統挑，絕不用「先探測再放掉」的猜測法
        // （那會在別的行程搶到同一個埠時，讓聲音悄悄消失）。
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;
        _drainBuffer = new byte[Math.Max(ChunkBytes * 4, 8192)];
    }

    /// <summary>已綁定的本機 UDP 埠（要寫進 ffmpeg 的參數）。</summary>
    public int Port { get; }

    /// <summary>來源取樣率。</summary>
    public int SampleRate { get; }

    /// <summary>一個片段的位元組數。</summary>
    public int ChunkBytes { get; }

    /// <summary>是否正在接收。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>收到的位元組總數（診斷：能分辨「沒有音訊」與「有音訊但播不出來」）。</summary>
    public long BytesReceived { get; private set; }

    /// <summary>送出過的片段數。</summary>
    public long ChunksReceived { get; private set; }

    /// <summary>最近一次收到資料的時間；從未收到時為 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime LastDataUtc { get; private set; } = DateTime.MinValue;

    /// <summary>最近一次接收失敗的原因（診斷用；正常時為空字串）。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>收到一個片段（100 ms 一個，最後一個可能較短）。</summary>
    public event EventHandler<byte[]>? ChunkReady;

    /// <summary>開始接收；會先丟掉開始前積在佇列裡的舊資料。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            DrainStale();

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsRunning = true;
            _loop = Task.Run(() => RunAsync(cts.Token));
        }
    }

    /// <summary>停止接收（埠仍綁著，下次 <see cref="Start"/> 可以繼續用）。</summary>
    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 收尾失敗不影響呼叫端（socket 會在 Dispose 關掉）。
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            IsRunning = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _client.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var buffer = new byte[ChunkBytes];
        var filled = 0;

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // Windows 對「先前送來的 ICMP port unreachable」會回報連線重設。
                // 這裡記下來繼續聽：聲音通道短暫中斷不該把整個接收器收掉。
                LastError = ex.Message;
                continue;
            }

            if (datagram.Buffer.Length == 0)
            {
                continue;
            }

            BytesReceived += datagram.Buffer.Length;
            LastDataUtc = DateTime.UtcNow;

            var offset = 0;
            while (offset < datagram.Buffer.Length)
            {
                var take = Math.Min(ChunkBytes - filled, datagram.Buffer.Length - offset);
                Array.Copy(datagram.Buffer, offset, buffer, filled, take);
                filled += take;
                offset += take;

                if (filled < ChunkBytes)
                {
                    continue;
                }

                Emit(buffer, ChunkBytes);
                filled = 0;
            }
        }

        // 最後一個不完整片段仍送出（少播 100 ms 內的聲音，比卡住好）。
        if (filled > 0)
        {
            Emit(buffer, filled);
        }

        IsRunning = false;
    }

    private void Emit(byte[] buffer, int length)
    {
        var chunk = new byte[length];
        Array.Copy(buffer, chunk, length);
        ChunksReceived++;

        var handler = ChunkReady;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, chunk);
        }
        catch (Exception)
        {
            // 播放端例外不得讓接收迴圈停擺。
        }
    }

    /// <summary>丟掉開始前已經積在 socket 佇列裡的資料（避免一開聲就播幾秒前的聲音）。</summary>
    private void DrainStale()
    {
        try
        {
            while (_client.Available > 0)
            {
                if (_client.Client.Receive(_drainBuffer) <= 0)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 佇列本來就是空的，或 socket 已關閉；不影響後續接收。
        }
    }
}
