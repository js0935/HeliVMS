namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// 單一連線的 NEMO 讀寫迴圈：分幀交給 <see cref="QctekSession"/>，這裡只負責
/// 「讀進累積緩衝 → 消耗 → 寫回回應」。
/// </summary>
/// <remarks>
/// <para>
/// 抽成獨立型別的原因：同一個 NEMO 對話有兩個入口——獨立埠的 <see cref="NemoServer"/>
/// （遠端 CMS 用）與 Web 埠的前置多工器 <see cref="QctekPortMux"/>（QCC 用）。
/// 兩邊各自寫一份讀寫迴圈，遲早只修好其中一份：症狀是「5170 可以連、80 不行」
/// （或反過來），而兩邊的日誌都正常。
/// </para>
/// <para>
/// <see cref="RunAsync"/> 的 <c>initial</c> 是呼叫端為了分辨 NEMO／HTTP 而已經讀掉的位元組；
/// 這裡接手時必須先處理它們，否則連線的第一個命令會被吞掉。
/// </para>
/// </remarks>
public static class NemoSessionLoop
{
    /// <summary>
    /// 把 <paramref name="stream"/> 當 NEMO 對話處理，直到對端關閉、工作被取消，
    /// 或串流不同步（此時直接關閉連線，不猜測重新同步點）。
    /// </summary>
    /// <param name="stream">已連上的串流。</param>
    /// <param name="initial">呼叫端已讀取、尚未處理的位元組（可為空）。</param>
    /// <param name="remote">診斷用的對端位址。</param>
    /// <param name="context">裝置資訊與憑證政策。</param>
    /// <param name="log">可選的訊息記錄。</param>
    /// <param name="cancellationToken">停止訊號。</param>
    public static async Task RunAsync(
        Stream stream,
        ReadOnlyMemory<byte> initial,
        string remote,
        IQctekDeviceContext context,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        var pending = new FrameBuffer();
        pending.Append(initial.Span);
        var read = new byte[8192];
        var session = new QctekSession(context);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
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

                    if (result.Reply is null)
                    {
                        if (result.Command is not null)
                        {
                            // 未知命令不回覆（維持 null 語意）——但一定要留痕：
                            // 這是「CMS 發了我們不認識的命令」唯一的證據。
                            log?.Invoke(
                                $"NEMO ← {QctekDiagnostics.Summarize(result.Command)}（未知命令，不回應）");
                        }
                    }
                    else
                    {
                        log?.Invoke($"NEMO ← {QctekDiagnostics.Summarize(result.Command)}");
                        await stream.WriteAsync(result.Reply.AsMemory(), cancellationToken).ConfigureAwait(false);
                        log?.Invoke($"NEMO → {QctekDiagnostics.DescribeReply(result.Reply)}");
                    }

                    pending.Consume(result.Consumed);
                }

                int count;
                try
                {
                    count = await stream.ReadAsync(read.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    return; // 對方斷線（RST 等）
                }

                if (count == 0)
                {
                    return; // 對方正常關閉
                }

                pending.Append(read.AsSpan(0, count));
            }
        }
        catch (Exception ex)
        {
            // 單一連線的例外（含寫入失敗）不影響伺服器本體。
            log?.Invoke($"NEMO 連線結束（{remote}）：{ex.Message}");
        }
    }

    /// <summary>連線的累積緩衝：附加、取用、消耗前綴（量小，位移即可）。</summary>
    internal sealed class FrameBuffer
    {
        private byte[] _buffer = new byte[8192];
        private int _length;

        public int Length => _length;

        public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

        /// <summary>目前緩衝內容的複本（交給 <see cref="NemoSessionLoop.RunAsync"/> 接手時用）。</summary>
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
