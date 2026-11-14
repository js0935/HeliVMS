using System.Buffers.Binary;
using System.Net.Sockets;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// <c>GET_LIVE_START</c> 之後的即時影像通道：讀 0x90-byte keepalive（<c>QCTEK_RM</c>）、
/// 依頻道位圖啟停每頻道來源、每 tick 每頻道寫至多一個 FRAM；無影格時每秒回狀態幀。
/// 第一個 keepalive（＝客戶端的訂閱）到達前<b>不寫出任何位元組</b>——真機的因果方向
/// 是 QCC 先訂閱、伺服器才推流（見 <see cref="LiveRunSync.RunSync"/> 的逐包證據）。
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
/// 讀乾淨，積壓只在佇列端發生。但丟棄<b>不能丟掉解碼起點</b>：容量必須大於啟動
/// 突發（見 <see cref="QueueCapacity"/>），而且新的關鍵影格到達時寧可整段作廢
/// （見 <see cref="LiveFrameQueue"/>）——否則 QCC 收到的是一串解不開的 P-frame，
/// 表現成「連線正常、畫面全黑、日誌乾淨」。
/// </para>
/// </remarks>
public static class QctekLiveSession
{
    /// <summary>keepalive（<c>QCTEK_RM</c>）封包長度。</summary>
    public const int KeepAliveLength = 0x90;

    /// <summary>超過這個 tick 數沒有 keepalive 就斷線（真機 live 迴圈的 break3 條件）。</summary>
    public const int KeepAliveTimeoutTicks = 0x130;

    /// <summary>啟動寬限：交棒後等第一個 keepalive（＝訂閱）的上限（三倍嚴格值）。
    /// 真機的第一個 keepalive 在 "OK " 後約 15ms 就到；寬限只是防呆，讓
    /// 「永遠不訂閱的連線」不會無限佔用一條執行緒，不是預期路徑。</summary>
    public const int StartupKeepAliveGraceTicks = KeepAliveTimeoutTicks * 3;

    /// <summary>tick 週期：真機 live 迴圈的輸出節奏（≈30fps 的寫出上限）。</summary>
    public const int TickMilliseconds = 33;

    /// <summary>
    /// 每頻道的 AU 積壓上限（滿了丟最舊），約等於 2 秒（33ms tick ＝ 30fps 的寫出上限）。
    /// </summary>
    /// <remarks>
    /// 這個值必須<b>大於啟動突發</b>，否則連線成功卻永遠沒有畫面。實測（VIVOTEK
    /// IP9164-HT、1080p15）：ffmpeg 建立 RTSP 工作階段後把輸入緩衝一次倒出來——
    /// 第一包 10279 bytes＝SPS/PPS/SPS/PPS/IDR，1 毫秒後又一包 2201 bytes＝27 個
    /// P-frame；兩包都遠早於第一個 33ms tick，所以佇列會先收到 28 個 AU 才開始被
    /// 消費。舊值 8 讓排在後面的 27 個 P-frame 把 IDR 擠出去，QCC 之後只收到解不開的
    /// P-frame，症狀是「連線正常、畫面全黑、日誌乾淨」，要等下一個 GOP 才有畫面
    /// （實測第 91 幀、約 7.5 秒）。
    /// </remarks>
    public const int QueueCapacity = 64;

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
        // 逐包證據（2026-11-14，docs/QCC-LIVE-DIAGNOSIS.md §8）：GET_LIVE_START 收到
        // "OK " 之後，是 QCC 先送第一個 keepalive（+14.5ms）、真機才送出第一個 FRAM
        // （再 +30ms）。反過來做（舊版「連線即推流」立即送音訊幀）的失敗實錄（同一次
        // 抓包 stream 163）：QCC 收到 1064 bytes 後 3.5ms 就 RST，連 keepalive 都沒
        // 送出——QCC 在送出它自己的 keepalive、進入讀取狀態之前，把任何先到的位元組
        // 當成協定錯誤。所以第一個 keepalive 到達前，這裡一個位元組都不寫（連狀態幀
        // 也不送）：對端眼中與真機完全一致。
        var sawKeepAlive = false;
        var nextTickAt = Environment.TickCount64 + QctekLiveSession.TickMilliseconds;
        try
        {
            while (true)
            {
                // 0) 對端 FIN 不帶任何資料位元組：DataAvailable 永遠是 false，輪詢看不到
                //    它——實測（QCC 的探針連線在 OK 後 7–12ms FIN）會讓連線空轉到寬限
                //    逾時，日誌還把原因記成「沒有第一個 keepalive」。用
                //    Poll(SelectRead)＋Available==0 才是可靠的 EOF 偵測。
                if (_stream.Socket.Poll(0, SelectMode.SelectRead) && _stream.Socket.Available == 0)
                {
                    Log($"LIVE：對端關閉連線（{_remote}）");
                    return;
                }

                // 1) 非阻塞抽乾已到達的資料（Available 為 0 時不等待），再解析所有完整
                //    的 keepalive——交棒時帶進來的殘餘（直接 keepalive 交棒時可能是
                //    完整的第一個 keepalive）也在 pending 裡，只靠 socket 有沒有新資料
                //    會把它餓死。
                while (_stream.DataAvailable)
                {
                    var count = _stream.Read(read, 0, read.Length);
                    if (count == 0)
                    {
                        Log($"LIVE：對端關閉連線（{_remote}）");
                        return;
                    }

                    pending.Append(read.AsSpan(0, count));
                }

                while (pending.HasComplete)
                {
                    if (!pending.TryParseNext(out var subStream, out var bits))
                    {
                        // 運行時實錄：QCC 把 ALARM_HELLO（警報心跳，NEMO 框）發到與
                        // 預覽共用的連線上。真機的 live 通道忽略這些資料繼續推流；
                        // 在這裡斷線會同時殺掉警報與預覽，並讓 QCC 陷入「交棒→斷線→
                        // 重連→再交棒」的循環。
                        var discarded = pending.Resync();
                        _log?.Invoke(
                            $"LIVE：非 keepalive 資料 {discarded} bytes（{_remote}），已重新同步。前 96 bytes：" +
                            QctekDiagnostics.DescribeBytes(pending.Peek));
                        if (discarded == 0)
                        {
                            // 無法前進（頭不完整）：等下一批資料再同步，避免空轉。
                            break;
                        }

                        continue;
                    }

                    sinceKeepAlive = 0;
                    sawKeepAlive = true;
                    ApplyBitmap(subStream, bits);
                }

                // 2) tick 到點：逾時判定＋（已訂閱後）每頻道至多一個 FRAM／狀態幀。
                var now = Environment.TickCount64;
                if (now >= nextTickAt)
                {
                    nextTickAt = now + QctekLiveSession.TickMilliseconds;
                    sinceKeepAlive++;
                    if (!sawKeepAlive)
                    {
                        // 等待第一個 keepalive（＝客戶端的訂閱）。真機 <15ms 就到；
                        // 寬限（約 30 秒）內沒有就當作不是即時影像客戶端，收掉連線。
                        // 全程沒有寫出任何位元組，對端看到的是一條安靜的連線。
                        if (sinceKeepAlive > QctekLiveSession.StartupKeepAliveGraceTicks)
                        {
                            _log?.Invoke(
                                $"LIVE：{sinceKeepAlive} 個 tick 沒有第一個 keepalive（{_remote}），關閉連線");
                            return;
                        }
                    }
                    else if (sinceKeepAlive > QctekLiveSession.KeepAliveTimeoutTicks)
                    {
                        _log?.Invoke(
                            $"LIVE：{sinceKeepAlive} 個 tick 沒有 keepalive（{_remote}，已同步），關閉連線");
                        return;
                    }
                    else
                    {
                        Tick(now);
                    }
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

            // 狀態碼的判定順序照真機（FUN_18001eb80）：無權限（0x8003）→ 攝影機資訊
            // 無效（0x8002）→ proxy 未建立（0x8004）→ 等幀（0x8001）。HeliVMS 的相容層
            // 不做使用者級頻道權限（GET_AUTH_USER 回全權限），0x8003 不會發生；
            // 「攝影機資訊無效」對映到「從未能建立來源」（頻道停用／URL 空／ffmpeg 不在），
            // 「proxy 未建立」對映到「來源曾建立但已結束、重試中」。
            Write(QctekLivePackets.Status(
                state.Channel,
                state.Source is not null
                    ? QctekLivePackets.StatusNoFrameYet
                    : state.HadSource
                        ? QctekLivePackets.StatusNoProxy
                        : QctekLivePackets.StatusNoSource));
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

        // 黃金抓包 stream 0 的第 0 個封包就是 type=2 音訊幀，它與影像無關（客戶端
        // GetFrameQCTekAudio 把它當音軌）——所以這裡送的是「有音軌」的先手，不是
        // 「叫醒畫面」的握手。真機此後每 tick 都送 1–2 包，我們只送這一包。
        try
        {
            var wallClock = DateTimeOffset.UtcNow;
            Write(QctekLivePackets.AudioFrame(
                state.Channel,
                wallClock.ToUnixTimeSeconds(),
                wallClock.UtcDateTime.Microsecond));
        }
        catch (Exception ex)
        {
            Log($"LIVE：頻道 {state.Channel} 音訊幀送出失敗：{ex.Message}");
        }

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
                    var dropped = state.Queue.Push(unit);
                    if (dropped > 0 && !state.OverflowLogged)
                    {
                        state.OverflowLogged = true;
                        _log?.Invoke(
                            $"LIVE：頻道 {state.Channel} 積壓超過 {QctekLiveSession.QueueCapacity} 幀，" +
                            $"開始丟棄較舊的影格（{_remote}）");
                    }

                    if (state.Queue.DroppedKeyframes > 0 && !state.KeyframeDropLogged)
                    {
                        // 「有連線、沒畫面」唯一會留下的證據：關鍵影格一旦被積壓丟掉，
                        // QCC 的解碼器就沒有起點，畫面凍住直到下一個 GOP 到達。
                        state.KeyframeDropLogged = true;
                        _log?.Invoke(
                            $"LIVE：頻道 {state.Channel} 的關鍵影格被積壓丟棄，" +
                            $"CMS 的畫面會凍住直到下一個 GOP（{_remote}）");
                    }
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
public sealed class PendingKeepAlives
{
    private readonly NemoSessionLoop.FrameBuffer _buffer = new();

    public PendingKeepAlives(byte[] initial) => _buffer.Append(initial);

    public void Append(ReadOnlySpan<byte> data) => _buffer.Append(data);

    public bool HasComplete => _buffer.Length >= QctekLiveSession.KeepAliveLength;

    /// <summary>目前緩衝的內容（診斷傾印用；不消耗）。</summary>
    public ReadOnlySpan<byte> Peek => _buffer.Span;

    public bool TryParseNext(out int subStream, out bool[] channels)
        => QctekLiveSession.TryParseKeepAlive(
            _buffer.Span[..QctekLiveSession.KeepAliveLength], out subStream, out channels)
            && ConsumeOne();

    /// <summary>
    /// 重新同步：丟掉位元組流中第一個 <c>QCTEK_RM</c> 頭之前的所有非 keepalive
    /// 資料（QCC 的警報心跳 ALARM_HELLO 是 NEMO 框，會與預覽共用同一條連線——
    /// 真機的 live 通道忽略它們繼續推流，斷線反而殺掉警報與預覽）。
    /// 回傳丟棄的位元組數；連下一個頭都不完整時丟到只剩尾端可能半個頭。
    /// </summary>
    public int Resync()
    {
        var span = _buffer.Span;
        var searchFrom = 1; // 開頭就是頭的情況在 TryParseNext 已被處理（格式錯才會到這）
        while (searchFrom <= span.Length - 8)
        {
            var at = span[searchFrom..].IndexOf("QCTEK_RM"u8);
            if (at < 0)
            {
                break;
            }

            var head = searchFrom + at;
            if (head + QctekLiveSession.KeepAliveLength <= span.Length)
            {
                // 後面夠一個完整 keepalive：丟掉頭之前的雜訊，讓下一輪 TryParseNext 解它。
                _buffer.Consume(head);
                return head;
            }

            // 頭之後不完整：丟到這個頭為止（半個頭留給下一輪 Append 補完）。
            _buffer.Consume(head);
            return head;
        }

        // 沒有任何頭：保留尾端 7 bytes（可能是被切斷的半個頭），其餘全丟。
        var keep = Math.Min(7, span.Length);
        var discard = span.Length - keep;
        if (discard > 0)
        {
            _buffer.Consume(discard);
        }

        return discard;
    }

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

    public void SetSource(IQctekLiveSource? source)
    {
        Interlocked.Exchange(ref _source, source);
        if (source is not null)
        {
            HadSource = true;
        }
    }

    /// <summary>是否曾成功建立過來源（區分「proxy 未建立」與「攝影機資訊無效」）。</summary>
    public bool HadSource { get; private set; }

    public Thread? Reader { get; set; }

    public CancellationTokenSource? ReaderCts { get; set; }

    public LiveFrameQueue Queue { get; } = new(QctekLiveSession.QueueCapacity);

    /// <summary>上次狀態幀（或影格）送出的時刻；狀態幀靠它節流到每秒一個。</summary>
    public DateTimeOffset LastStatusAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>上次嘗試建立來源的時刻（重試節流）。</summary>
    public DateTimeOffset SourceAttemptAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>是否已記錄過「積壓丟棄」（只記第一次，避免每丟一幀就洗版）。</summary>
    public bool OverflowLogged { get; set; }

    /// <summary>是否已記錄過「關鍵影格被積壓丟棄」（同上，只記第一次）。</summary>
    public bool KeyframeDropLogged { get; set; }
}

/// <summary>每頻道的 AU 佇列：有界、滿了丟最舊（單寫單讀，鎖只是為了 Clear 的安全性）。</summary>
/// <remarks>
/// 溢位的處理原則是「寧可跳段，也不要送解不開的影格」：關鍵影格是解碼器唯一的起點
/// （SPS/PPS 就在它的 block1 裡），把關鍵影格留在積壓裡卻丟掉它後面的影格，或反過來，
/// 結果都是讓 CMS 停在黑畫面直到下一個 GOP。所以新的關鍵影格到達而佇列已滿時，整段
/// 舊積壓直接作廢，從這張關鍵影格重新開始。
/// </remarks>
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

    /// <summary>累計丟棄的 AU 數（診斷用：這個數字不為 0 就代表曾經積壓到跳段）。</summary>
    public long Dropped { get; private set; }

    /// <summary>累計丟棄的關鍵影格數（不為 0 代表 CMS 曾因此等了一個 GOP）。</summary>
    public long DroppedKeyframes { get; private set; }

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

    /// <summary>推入一個 AU；回傳這次為了讓位而丟棄的 AU 數（0 ＝ 沒有丟）。</summary>
    public int Push(QctekAccessUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        lock (_lock)
        {
            var dropped = 0;
            if (_items.Count >= _capacity)
            {
                if (unit.Keyframe)
                {
                    // 新的解碼起點到了：積壓裡的每一幀都比它舊，而且解不開的機率很高。
                    dropped = EvictAll();
                }
                else
                {
                    dropped = EvictOldest();
                }
            }

            _items.Enqueue(unit);
            return dropped;
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

    private int EvictOldest()
    {
        var dropped = _items.Dequeue();
        Dropped++;
        if (dropped.Keyframe)
        {
            DroppedKeyframes++;
        }

        return 1;
    }

    private int EvictAll()
    {
        foreach (var unit in _items)
        {
            if (unit.Keyframe)
            {
                DroppedKeyframes++;
            }
        }

        var count = _items.Count;
        _items.Clear();
        Dropped += count;
        return count;
    }
}
