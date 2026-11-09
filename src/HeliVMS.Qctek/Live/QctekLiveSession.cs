using System.Buffers.Binary;
using System.Net.Sockets;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// <c>GET_LIVE_START</c> 之後的即時影像通道：讀 0x90-byte keepalive（<c>QCTEK_RM</c>）、
/// 依頻道位圖啟停每頻道來源、每 tick 每頻道寫至多一個 FRAM；無影格時每秒回狀態幀。
/// </summary>
/// <remarks>
/// <para>
/// 線路事實由真機兩端核對：伺服器 live 迴圈（<c>QC_Server</c> 的 <c>FUN_18001eb80</c>）
/// 每 tick 對每個「已要求」頻道寫至多一個 type=1 封包、逾 0x130（304）個 tick 沒有
/// keepalive 就斷線（33ms tick ≈ 10 秒）、對「已要求但沒影格」的頻道約每秒回一個
/// 0x8000 系狀態碼；客戶端（<c>dvrProxy_live::GetLiveSend</c>）每 250ms 送一個
/// 0x90-byte keepalive：<c>QCTEK_RM</c>＋<c>01 00 00 01</c>＋u32 stream type
/// （0=主碼流、1=子碼流，signed）＋0x10–0x8f 的 1024-bit 頻道位圖（bit i＝頻道 i，
/// LSB-first）。頻道選擇<b>只</b>透過這張位圖——命令本身不帶頻道。
/// </para>
/// <para>
/// 為什麼是<b>同步單執行緒輪詢</b>：真機的核心就是這個形狀（每連線一條同步迴圈，
/// 阻塞 socket＋短逾時輪詢）；而 async＋執行緒池的每個 await 延續在這台同時錄影的
/// 機器上有 20–90ms 的排隊延遲，QCC 的啟動狀態機以 33ms 為週期，回應跨過一個週期它
/// 就在授權成功後立刻關線（OK 之後 0.6ms 發 FIN、從不送 keepalive、永遠黑屏）。
/// 執行緒池的排隊延遲無法靠提高優先級消除，只能離開它。
/// </para>
/// <para>
/// 為什麼「有界佇列、滿了丟最舊」：CMS 只看「現在」，解碼端的輸出節奏由客戶端
/// （DecProc 對牆鐘調速）決定；我們積壓再久它也不會補播，只會看到愈來愈舊的畫面。
/// 與即時監看「丟棄發生在讀取之後（管線抽乾）」同一取捨：消費任務總是把 ffmpeg
/// 讀乾淨，積壓只在佇列端發生。
/// </para>
/// </remarks>
public static class QctekLiveSession
{
    /// <summary>keepalive（<c>QCTEK_RM</c>）封包長度。</summary>
    public const int KeepAliveLength = 0x90;

    /// <summary>超過這個 tick 數沒有 keepalive 就斷線（真機 live 迴圈的 break3 條件）。</summary>
    public const int KeepAliveTimeoutTicks = 0x130;

    /// <summary>tick 週期：真機 live 迴圈的輸出節奏（≈30fps 的寫出上限）。</summary>
    public const int TickMilliseconds = 33;

    /// <summary>每頻道的 AU 積壓上限（滿了丟最舊）。</summary>
    public const int QueueCapacity = 8;

    /// <summary>主迴圈的輪詢步長（毫秒）：tick 節奏的粒度，也是 keepalive 的讀取延遲上限。</summary>
    public const int PollMilliseconds = 5;

    /// <summary>來源建立失敗後的重試間隔（攝影機短暫離線後能自動恢復，又不必每 tick 撞一次 ffmpeg）。</summary>
    public static readonly TimeSpan SourceRetryInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(TickMilliseconds);

    /// <summary>無影格狀態幀的間隔（真機約每秒一個）。</summary>
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 接手一條已回覆 <c>GET_LIVE_START</c> 的連線（同步、單執行緒），直到對端關閉、
    /// 逾時，或呼叫端關閉串流。
    /// </summary>
    /// <param name="stream">同一條 TCP 串流（<c>OK </c>＋NUL 已由呼叫端寫出）。必須是
    /// 可輪詢的 <see cref="NetworkStream"/>（非阻塞讀 keepalive、33ms tick 共用一條迴圈）。</param>
    /// <param name="initial">交棒時尚未耗用的殘餘位元組（可能已是半個 keepalive）。</param>
    /// <param name="remote">診斷用的對端位址。</param>
    /// <param name="context">裝置資訊（頻道數、即時影像來源工廠）。</param>
    /// <param name="log">可選的訊息記錄。</param>
    public static void RunSync(
        Stream stream,
        byte[] initial,
        string remote,
        IQctekDeviceContext context,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(context);

        if (stream is not NetworkStream network)
        {
            throw new ArgumentException(
                "即時影像通道需要可輪詢的 NetworkStream（阻塞讀會讓 33ms tick 停擺）。", nameof(stream));
        }

        var run = new LiveRunSync(network, remote, context, log);
        run.RunSync(initial);
    }

    /// <summary>
    /// 解析一個 0x90-byte keepalive。開頭不是 <c>QCTEK_RM</c>、或 stream type 為負
    /// （真機要求 signed ≥ 0）都視為串流錯位，呼叫端應關閉連線。
    /// </summary>
    /// <param name="packet">待解析的位元組（至少 <see cref="KeepAliveLength"/> 個）。</param>
    /// <param name="subStream">0＝主碼流、1＝子碼流（keepalive <c>+0x0c</c>）。</param>
    /// <param name="channels">1024-bit 頻道位圖展開成布林陣列（bit i＝頻道 i，LSB-first）。</param>
    public static bool TryParseKeepAlive(ReadOnlySpan<byte> packet, out int subStream, out bool[] channels)
    {
        subStream = 0;
        channels = [];
        if (packet.Length < KeepAliveLength || !packet.StartsWith("QCTEK_RM"u8))
        {
            return false;
        }

        var type = BinaryPrimitives.ReadInt32LittleEndian(packet[0x0c..]);
        if (type < 0)
        {
            return false;
        }

        subStream = type;
        channels = new bool[1024];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = (packet[0x10 + (i >> 3)] & (1 << (i & 7))) != 0;
        }

        return true;
    }
}

/// <summary>一次即時影像對話的執行狀態（單一輪詢執行緒＋每頻道消費執行緒）。</summary>
internal sealed class LiveRunSync
{
    private readonly NetworkStream _stream;
    private readonly string _remote;
    private readonly IQctekDeviceContext _context;
    private readonly Action<string>? _log;
    private readonly ChannelState?[] _channels;
    private int _subStream;

    public LiveRunSync(
        NetworkStream stream,
        string remote,
        IQctekDeviceContext context,
        Action<string>? log)
    {
        _stream = stream;
        _remote = remote;
        _context = context;
        _log = log;
        // 位圖以「我們宣告的頻道數」為界：超出頻道數的 bit 直接忽略——QCC 的頻道數
        // 來自我們自己的 GET_NCHANNEL，正常情況勾不出這種 bit。
        _channels = new ChannelState?[Math.Clamp(context.ChannelCount, 0, 1024)];
    }

    /// <summary>主迴圈：非阻塞抽乾 keepalive、每 33ms 一個輸出 tick、逾時斷線。</summary>
    public void RunSync(byte[] initial)
    {
        Log($"LIVE：即時影像通道開始（{_remote}）");
        var pending = new PendingKeepAlives(initial);
        var read = new byte[8192];
        var sinceKeepAlive = 0;
        var nextTickAt = Environment.TickCount64 + QctekLiveSession.TickMilliseconds;

        try
        {
            while (true)
            {
                // 1) 非阻塞抽乾已到達的 keepalive（Available 為 0 時不等待）。
                while (_stream.DataAvailable)
                {
                    var count = _stream.Read(read, 0, read.Length);
                    if (count == 0)
                    {
                        Log($"LIVE：對端關閉連線（{_remote}）");
                        return;
                    }

                    pending.Append(read.AsSpan(0, count));
                    while (pending.HasComplete)
                    {
                        if (!pending.TryParseNext(out var subStream, out var bits))
                        {
                            _log?.Invoke(
                                $"LIVE：keepalive 不合格（{_remote}），關閉連線。前 96 bytes：" +
                                QctekDiagnostics.DescribeBytes(pending.TakeBroken()));
                            return;
                        }

                        sinceKeepAlive = 0;
                        ApplyBitmap(subStream, bits);
                    }
                }

                // 2) tick 到點：逾時判定＋每頻道至多一個 FRAM／狀態幀。
                var now = Environment.TickCount64;
                if (now >= nextTickAt)
                {
                    nextTickAt = now + QctekLiveSession.TickMilliseconds;
                    sinceKeepAlive++;
                    if (sinceKeepAlive > QctekLiveSession.KeepAliveTimeoutTicks)
                    {
                        _log?.Invoke(
                            $"LIVE：{sinceKeepAlive} 個 tick 沒有 keepalive（{_remote}），關閉連線");
                        return;
                    }

                    Tick(now);
                }

                // 3) 輪詢步長：keepalive 的讀取延遲與 tick 節奏的粒度都在這裡。
                Thread.Sleep(QctekLiveSession.PollMilliseconds);
            }
        }
        catch (Exception ex)
        {
            // 對端斷線、呼叫端關閉串流（伺服器停用）：與真機的「連線斷」同一處理。
            Log($"LIVE：連線結束（{_remote}）：{ex.Message}");
        }
        finally
        {
            StopAll();
            Log($"LIVE：即時影像通道結束（{_remote}）");
        }
    }

    /// <summary>輸出 tick：每頻道至多一個 FRAM；沒影格時每秒一個狀態幀。</summary>
    private void Tick(long now)
    {
        var wallClock = DateTimeOffset.UtcNow;
        for (var i = 0; i < _channels.Length; i++)
        {
            var state = _channels[i];
            if (state is null)
            {
                continue;
            }

            if (state.Queue.TryPop(out var unit) && unit is not null)
            {
                // 時戳在寫出前以牆鐘蓋章：客戶端 DecProc 以封包時戳對牆鐘調速，
                // 寫出時蓋章即恆在容差內。
                Write(
                    QctekLivePackets.Data(
                        state.Channel,
                        unit.Block1,
                        unit.Block2,
                        unit.Video,
                        wallClock.ToUnixTimeSeconds(),
                        wallClock.UtcDateTime.Microsecond));
                state.LastStatusAt = wallClock;
                continue;
            }

            if (wallClock - state.LastStatusAt < QctekLiveSession.StatusInterval)
            {
                continue;
            }

            Write(QctekLivePackets.Status(
                state.Channel,
                state.Source is null
                    ? QctekLivePackets.StatusNoSource
                    : QctekLivePackets.StatusNoFrameYet));
            state.LastStatusAt = wallClock;
        }
    }

    private void Write(byte[] packet)
    {
        try
        {
            _stream.Write(packet, 0, packet.Length);
        }
        catch (Exception ex)
        {
            throw new IOException($"輸出失敗：{ex.Message}", ex);
        }
    }

    /// <summary>依最新位圖啟停頻道；stream type 變更時重建所有來源。</summary>
    private void ApplyBitmap(int subStream, bool[] bits)
    {
        if (subStream != _subStream)
        {
            _log?.Invoke($"LIVE：碼流切換（{_remote}）：{(_subStream == 0 ? "主" : "子")}→{(subStream == 0 ? "主" : "子")}");
            for (var i = 0; i < _channels.Length; i++)
            {
                var state = _channels[i];
                if (state is not null && state.Source is not null)
                {
                    StopChannel(state);
                }
            }

            _subStream = subStream;
        }

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
            {
                if (i < _channels.Length && _channels[i] is { } removed)
                {
                    _channels[i] = null;
                    StopChannel(removed);
                }

                continue;
            }

            if (i >= _channels.Length)
            {
                continue; // 超出宣告頻道數：保持無狀態，tick 不會為它送任何東西
            }

            var channel = _channels[i] ??= new ChannelState(i);
            if (channel.Source is null && now - channel.SourceAttemptAt >= QctekLiveSession.SourceRetryInterval)
            {
                StartChannel(channel);
            }
        }
    }

    private void StartChannel(ChannelState state)
    {
        state.SourceAttemptAt = DateTimeOffset.UtcNow;

        IQctekLiveSource? source = null;
        try
        {
            source = _context.CreateLiveSource(state.Channel, _subStream);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"LIVE：頻道 {state.Channel} 來源建立失敗：{ex.GetType().Name} {ex.Message}");
        }

        if (source is null)
        {
            return; // state 留著：tick 每秒回 0x8002，重試間隔過後再試
        }

        state.SetSource(source);
        state.ReaderCts = new CancellationTokenSource();
        var cts = state.ReaderCts;
        state.Reader = new Thread(() => ReadSourceSync(state, cts.Token))
        {
            IsBackground = true,
            Name = $"qctek-live-src-{state.Channel}",
        };
        state.Reader.Start();
        Log($"LIVE：頻道 {state.Channel}（{(_subStream == 0 ? "主" : "子")}碼流）開始輸出（{_remote}）");
    }

    /// <summary>消費執行緒：把來源的 Annex-B 流切成 AU 推進佇列。來源結束時清掉登記。</summary>
    private void ReadSourceSync(ChannelState state, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var grouper = new QctekH264AccessUnitGrouper();
        try
        {
            var source = state.Source;
            if (source is null)
            {
                return;
            }

            var output = source.Output;
            while (true)
            {
                var count = output.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                foreach (var unit in grouper.Append(buffer.AsSpan(0, count)))
                {
                    state.Queue.Push(unit);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 來源錯誤（ffmpeg 退出、管道斷、被 StopChannel 關閉）：吞掉——「有來源但
            // 沒影格」的狀態幀會告訴 CMS 這個頻道現況，與攝影機短暫斷流的表現一致。
        }
        finally
        {
            foreach (var unit in grouper.Flush())
            {
                state.Queue.Push(unit);
            }

            // 來源已結束：清掉登記（與 StopChannel 用交換避免雙重釋放）。
            var ended = state.TakeSource();
            if (ended is not null)
            {
                DisposeQuietly(ended);
            }
        }
    }

    private void StopChannel(ChannelState state)
    {
        var source = state.TakeSource();
        state.LastStatusAt = DateTimeOffset.MinValue;
        state.Queue.Clear();
        state.ReaderCts?.Cancel();

        var reader = state.Reader;
        if (reader is not null)
        {
            reader.Join(TimeSpan.FromSeconds(2));
        }

        state.ReaderCts?.Dispose();
        state.ReaderCts = null;
        state.Reader = null;

        if (source is not null)
        {
            DisposeQuietly(source);
        }
    }

    private void StopAll()
    {
        for (var i = 0; i < _channels.Length; i++)
        {
            var state = _channels[i];
            if (state is null)
            {
                continue;
            }

            _channels[i] = null;
            StopChannel(state);
        }
    }

    private static void DisposeQuietly(IQctekLiveSource source)
    {
        try
        {
            source.DisposeAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 來源收尾失敗（行程已死等）不影響通道本身的關閉。
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}

/// <summary>
/// keepalive 的累積與解析緩衝：以 0x90 為界逐包解析；不合格的內容原樣交出供傾印。
/// </summary>
internal sealed class PendingKeepAlives
{
    private readonly NemoSessionLoop.FrameBuffer _buffer = new();

    public PendingKeepAlives(byte[] initial) => _buffer.Append(initial);

    public void Append(ReadOnlySpan<byte> data) => _buffer.Append(data);

    public bool HasComplete => _buffer.Length >= QctekLiveSession.KeepAliveLength;

    public bool TryParseNext(out int subStream, out bool[] channels)
        => QctekLiveSession.TryParseKeepAlive(
            _buffer.Span[..QctekLiveSession.KeepAliveLength], out subStream, out channels)
            && ConsumeOne();

    /// <summary>取走一個已解析的 keepalive；解析失敗時交出全部（呼叫端傾印後關線）。</summary>
    public byte[] TakeBroken()
    {
        var copy = _buffer.ToArray();
        _buffer.Consume(_buffer.Length);
        return copy;
    }

    private bool ConsumeOne()
    {
        _buffer.Consume(QctekLiveSession.KeepAliveLength);
        return true;
    }
}

/// <summary>單一頻道的即時影像狀態（來源、消費執行緒、積壓佇列、狀態幀節流）。</summary>
internal sealed class ChannelState
{
    private IQctekLiveSource? _source;

    public ChannelState(int channel) => Channel = channel;

    public int Channel { get; }

    /// <summary>目前來源；<c>null</c>＝未建立或已結束（tick 會回 0x8002）。</summary>
    /// <remarks>
    /// 讀取原子；<b>釋放一律走 <see cref="TakeSource"/></b>：消費執行緒的清理
    /// （來源自己讀完）與 bitmap 停用（<see cref="LiveRunSync.StopChannel"/>）兩條路
    /// 都可能先到，用交換保證只有一方釋放，不會雙重 Dispose。
    /// </remarks>
    public IQctekLiveSource? Source => _source;

    public IQctekLiveSource? TakeSource() => Interlocked.Exchange(ref _source, null);

    public void SetSource(IQctekLiveSource? source) => Interlocked.Exchange(ref _source, source);

    public Thread? Reader { get; set; }

    public CancellationTokenSource? ReaderCts { get; set; }

    public LiveFrameQueue Queue { get; } = new(QctekLiveSession.QueueCapacity);

    /// <summary>上次狀態幀（或影格）送出的時刻；狀態幀靠它節流到每秒一個。</summary>
    public DateTimeOffset LastStatusAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>上次嘗試建立來源的時刻（重試節流）。</summary>
    public DateTimeOffset SourceAttemptAt { get; set; } = DateTimeOffset.MinValue;
}

/// <summary>每頻道的 AU 佇列：有界、滿了丟最舊（單寫單讀，鎖只是為了 Clear 的安全性）。</summary>
public sealed class LiveFrameQueue
{
    private readonly object _lock = new();
    private readonly Queue<QctekAccessUnit> _items;
    private readonly int _capacity;

    public LiveFrameQueue(int capacity)
    {
        _capacity = Math.Max(capacity, 1);
        _items = new Queue<QctekAccessUnit>(_capacity);
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public void Push(QctekAccessUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        lock (_lock)
        {
            if (_items.Count >= _capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(unit);
        }
    }

    public bool TryPop(out QctekAccessUnit? unit)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                unit = null;
                return false;
            }

            unit = _items.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }
}
