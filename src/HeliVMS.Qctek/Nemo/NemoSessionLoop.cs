using System.Net.Sockets;
using System.Text;
using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// 單一連線的 NEMO 讀寫迴圈（同步核心）：分幀交給 <see cref="QctekSession"/>，
/// 這裡只負責「讀進累積緩衝 → 消耗 → 寫回回應」。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼是<b>同步</b>I/O：QCC 的即時影像狀態機以 33ms 為週期輪詢，挑戰與 OK 的回應
/// 只要跨過一個週期（實測 >33ms 就會），QCC 便在授權成功後立刻關線（OK 之後 0.6ms 發
/// FIN），即時影像永遠黑屏。真機（QC_Live.exe，C++）的回應低於 1ms、每次成功；HeliVMS
/// 走 async＋執行緒池時回應 20–90ms、每次失敗——這台機器同時錄影與監看，執行緒池的
/// 工作佇列常年非空，每個 await 延續的喚醒都被排到幾十毫秒之外。連線處理改成
/// 「專用執行緒＋同步阻塞 socket」之後才與真機同一量級，而且這正是真機的核心模型
/// （每連線一條同步迴圈）。
/// </para>
/// <para>
/// 同一個 NEMO 對話有兩個入口——獨立埠的 <see cref="NemoServer"/>（遠端 CMS 用）與
/// Web 埠的前置多工器 <see cref="QctekPortMux"/>（QCC 用）；兩邊都必須走這一份，
/// 否則只會修好其中一份（症狀：「5170 可以、80 不行」或反過來，兩邊日誌都正常）。
/// </para>
/// <para>
/// <see cref="NemoSessionLoop.RunSync(System.IO.Stream,byte[],string,HeliVMS.Qctek.Nemo.IQctekDeviceContext,Action{string})"/>
/// 的 <c>initial</c> 是呼叫端為了分辨 NEMO／HTTP 而已經讀掉的位元組；這裡
/// 接手時必須先處理它們，否則連線的第一個命令會被吞掉。
/// </para>
/// </remarks>
public static class NemoSessionLoop
{
    /// <summary>
    /// 把 <paramref name="stream"/> 當 NEMO 對話處理（同步、阻塞），直到對端關閉、
    /// 串流不同步（直接關閉，不猜測重新同步點），或呼叫端關閉串流（取消）。
    /// </summary>
    /// <param name="stream">已連上的串流（呼叫端負責在取消時關閉它以打斷阻塞讀）。</param>
    /// <param name="initial">呼叫端已讀取、尚未處理的位元組（可為空）。</param>
    /// <param name="remote">診斷用的對端位址。</param>
    /// <param name="context">裝置資訊與憑證政策。</param>
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

        var pending = new FrameBuffer();
        pending.Append(initial);
        var read = new byte[8192];
        var session = new QctekSession(context);
        var heartbeatSeen = false;
        var sawCommand = false;

        try
        {
            while (true)
            {
                // 先把緩衝裡完整的命令處理完（呼叫端讀掉的初始位元組也在裡面）。
                while (pending.Length > 0)
                {
                    var result = session.Advance(pending.Span);
                    if (result.Close)
                    {
                        // 串流不同步：直接關閉，不猜測重新同步點。傾印原始位元組——
                        // 實測時唯一能回答「對方送了什麼」的就是這一行。
                        log?.Invoke(
                            $"NEMO：非 NEMO 資料（{remote}），關閉連線。前 96 bytes：" +
                            QctekDiagnostics.DescribeBytes(pending.Span));
                        return;
                    }

                    if (result.Consumed == 0)
                    {
                        break; // 半個訊框：繼續讀
                    }

                    if (result.Live && result.Reply is null)
                    {
                        // QCTEK_RM keepalive 直接送入的交棒（黃金抓包 §5c）：預覽長連線
                        // 沒有 GET_LIVE_START——QCC 對真機的 keepalive 走 QCC 啟動即建立
                        // 的長連線，第一個 keepalive 就是 live 通道的起點。Reply 為 null，
                        // 不經過「未知命令」分支（那會把它當成缺命令留痕後吞掉）。
                        pending.Consume(result.Consumed);
                        QctekLiveSession.RunSync(
                            stream,
                            pending.ToArray(),
                            remote,
                            context,
                            log);
                        return;
                    }

                    if (result.Reply is null)
                    {
                        if (result.Command is not null)
                        {
                            if (QctekCommandHandler.IsSilentHeartbeat(result.Command))
                            {
                                // ALARM_HELLO 每 5 秒一次，單一工作階段實測 1,341 次。這份
                                // 日誌是 512 KB 滾動檔，全部記下來會把真正的線索（連線失敗
                                // 原因、未知命令、live 診斷）整段擠掉——症狀會變成「日誌
                                // 乾淨」，那比沒有日誌更糟。只記第一次，足以證明警報通道活著。
                                if (!heartbeatSeen)
                                {
                                    heartbeatSeen = true;
                                    log?.Invoke(
                                        $"NEMO ← {QctekDiagnostics.Summarize(result.Command)}" +
                                        "（心跳；真機不回覆，同一連線後續不再記錄）");
                                }
                            }
                            else
                            {
                                // 未知命令不回覆（維持 null 語意）——但一定要留痕：
                                // 這是「CMS 發了我們不認識的命令」唯一的證據。
                                log?.Invoke(
                                    $"NEMO ← {QctekDiagnostics.Summarize(result.Command)}（未知命令，不回應）");
                            }
                        }
                    }
                    else
                    {
                        log?.Invoke($"NEMO ← {QctekDiagnostics.Summarize(result.Command)}");
                        stream.Write(result.Reply, 0, result.Reply.Length);
                        log?.Invoke($"NEMO → {QctekDiagnostics.DescribeReply(result.Reply)}");

                        if (result.Live)
                        {
                            // GET_LIVE_START 的成功回覆（"OK "＋NUL）已寫出：從現在起同一條
                            // 連線是即時影像通道——客戶端不再送文字命令，改送 0x90-byte
                            // keepalive、收 FRAM。交棒時把尚未耗用的殘餘位元組（可能已是
                            // 半個 keepalive）一起帶過去，否則第一個 keepalive 會丟頭。
                            pending.Consume(result.Consumed);
                            QctekLiveSession.RunSync(
                                stream,
                                pending.ToArray(),
                                remote,
                                context,
                                log);
                            return;
                        }
                    }

                    pending.Consume(result.Consumed);
                }

                var count = sawCommand
                    ? stream.Read(read, 0, read.Length)
                    : ReadWithPreviewTimeout(stream, read, pending.Length);
                if (count > 0)
                {
                    sawCommand = true;
                }
                if (count == 0)
                {
                    return; // 對方正常關閉
                }

                if (count < 0)
                {
                    // 黃金抓包 §5c（真機行為）：預覽長連線上 QCC 什麼都不送——它在等
                    // 伺服器先推流（TCP 握手後真機立即送 FRAM 0000 0000）。等待視窗
                    // 內沒有任何命令位元組，就把這條連線交棒即時影像通道開始推流；
                    // config/auth 的短連線會立刻送命令，不受視窗影響。
                    // 注意：處理過至少一條命令後（sawCommand）不再用視窗——QCC 的警報
                    // 心跳 ALARM_HELLO 每 15-20 秒才一發，心跳後的靜默會被視窗誤判
                    // （運行時實錄：警報連線被整條交棒掉）。
                    log?.Invoke($"NEMO：等待視窗內沒有命令（{remote}），交棒即時影像通道");
                    QctekLiveSession.RunSync(
                        stream,
                        pending.ToArray(),
                        remote,
                        context,
                        log);
                    return;
                }

                pending.Append(read.AsSpan(0, count));
            }
        }
        catch (Exception ex)
        {
            // 單一連線的例外（含寫入失敗、呼叫端關閉串流的取消）不影響伺服器本體。
            log?.Invoke($"NEMO 連線結束（{remote}）：{ex.Message}");
        }
    }

    /// <summary>預覽判定的等待視窗：連線後這麼久沒有任何命令位元組，就視為
    /// QCC 的預覽長連線（它在等伺服器先推流）並交棒即時影像通道。</summary>
    public const int PreviewWaitMilliseconds = 600;

    /// <summary>
    /// 讀一段資料；等待視窗內（僅緩衝還是空的時候）逾時回 <c>-1</c>，之後恢復
    /// 無限等待。NetworkStream 的 ReadTimeout 逾時會丟 IOException（內層
    /// SocketException TimedOut），在這裡轉成訊號而不是讓連線整個報錯。
    /// </summary>
    internal static int ReadWithPreviewTimeout(Stream stream, byte[] read, int buffered)
    {
        var network = stream as NetworkStream;
        if (network is null || buffered > 0)
        {
            return network is null ? stream.Read(read, 0, read.Length) : network.Read(read, 0, read.Length);
        }

        try
        {
            network.ReadTimeout = PreviewWaitMilliseconds;
            return network.Read(read, 0, read.Length);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            return -1;
        }
        finally
        {
            network.ReadTimeout = Timeout.Infinite;
        }
    }

    /// <summary>連線的累積緩衝：附加、取用、消耗前綴（量小，位移即可）。</summary>
    internal sealed class FrameBuffer
    {        private byte[] _buffer = new byte[8192];
        private int _length;

        public int Length => _length;

        public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

        /// <summary>目前緩衝內容的複本（交棒給即時影像通道時用）。</summary>
        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_length + data.Length > _buffer.Length)
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _length + data.Length)];
                _buffer.AsSpan(0, _length).CopyTo(grown);
                _buffer = grown;
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        public void Consume(int count)
        {
            if (count >= _length)
            {
                _length = 0;
                return;
            }

            _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
            _length -= count;
        }
    }
}
