using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>PTZ 連線的 raw 分幀結果。</summary>
public enum QctekPtzFrameStatus
{
    /// <summary>還沒收到結尾 NUL：等下一包。</summary>
    Incomplete,

    /// <summary>只有 NUL（前導殘尾）：吃掉落數，沒有命令。</summary>
    Empty,

    /// <summary>一條完整命令（<c>Consumed</c> 已含結尾 NUL）。</summary>
    Command,

    /// <summary>出現不可列印位元組：不是這個協議，關線。</summary>
    Invalid,

    /// <summary>超過長度上限：對端不是講這個協議（或串流不同步），關線。</summary>
    TooLong,
}

/// <summary>一條 PTZ raw 命令的分幀結果。</summary>
/// <param name="Status">分幀狀態。</param>
/// <param name="Command">命令文字（僅 <see cref="QctekPtzFrameStatus.Command"/> 有值）。</param>
/// <param name="Consumed">應從緩衝消耗的位元組數（<see cref="QctekPtzFrameStatus.Incomplete"/> 為 0）。</param>
public readonly record struct QctekPtzFrame(
    QctekPtzFrameStatus Status,
    string Command,
    int Consumed);

/// <summary>
/// PTZ 連線的 raw 分幀（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// PTZ 走<b>另一條</b> TCP 連線，命令是「ASCII 文字＋結尾 NUL」，<b>沒有</b> NEMO 標頭
/// （<c>QC_Server</c> 的 <c>FUN_180008c00</c> 一次 <c>read</c> 後在尾端補 NUL）。
/// 所以這個分幀器刻意不碰 <see cref="NemoMessage"/>：把 PTZ 當 NEMO 框解析的話，
/// 第一條命令就找不到 <c>NEMO </c> 前綴，連線直接關掉。
/// </para>
/// <para>
/// 客戶端最後送出的 <c>PTZ_DisConnect</c> <b>只寫 14 bytes、不含 NUL</b>，然後立刻
/// close；所以呼叫端在 EOF 時必須把殘留緩衝當成最後一條命令處理（見
/// <see cref="QctekPtzSession"/>）。
/// </para>
/// </remarks>
public static class QctekPtzWire
{
    /// <summary>單條命令的長度上限；超過視為串流不同步。</summary>
    public const int MaxRawCommandLength = 4096;

    /// <summary>從累積緩衝取出下一條命令。</summary>
    public static QctekPtzFrame Next(ReadOnlySpan<byte> data)
    {
        // 前導 NUL：可能是上一條命令的殘尾，也可能是客戶端在連線開頭多送的一個 NUL。
        // 吃掉而不是回 Invalid——真機的讀取器同樣把它當成空命令。
        var i = 0;
        while (i < data.Length && data[i] == 0)
        {
            i++;
        }

        if (i >= data.Length)
        {
            return new QctekPtzFrame(QctekPtzFrameStatus.Empty, string.Empty, i);
        }

        var start = i;
        while (i < data.Length && data[i] != 0)
        {
            if (data[i] is < 0x20 or > 0x7E)
            {
                return new QctekPtzFrame(QctekPtzFrameStatus.Invalid, string.Empty, i);
            }

            i++;
            if (i - start > MaxRawCommandLength)
            {
                return new QctekPtzFrame(QctekPtzFrameStatus.TooLong, string.Empty, i);
            }
        }

        if (i >= data.Length)
        {
            return new QctekPtzFrame(QctekPtzFrameStatus.Incomplete, string.Empty, 0);
        }

        return new QctekPtzFrame(
            QctekPtzFrameStatus.Command,
            Encoding.ASCII.GetString(data[start..i]),
            i + 1);
    }

    /// <summary>
    /// EOF 時的殘留緩衝：客戶端的 <c>PTZ_DisConnect</c> 不帶 NUL，所以「連線關閉前
    /// 已收到的文字」本身就是最後一條命令。不可列印或不完整時回 <c>null</c>（不猜）。
    /// </summary>
    public static QctekPtzCommand? ParseTail(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            if (b == 0)
            {
                continue;
            }

            if (b is < 0x20 or > 0x7E || text.Length >= MaxRawCommandLength)
            {
                return null;
            }

            text.Append((char)b);
        }

        return text.ToString().Trim(' ', '\0').Length == 0
            ? null
            : QctekPtzCommand.Parse(text.ToString());
    }
}

/// <summary>
/// PTZ 連線的同步讀寫迴圈：<c>PTZ_Connect</c> 成功回覆之後，同一條連線改由這裡接手。
/// </summary>
/// <remarks>
/// <para>
/// 真機的 PTZ 迴圈（<c>QC_Server</c> <c>FUN_180008030</c>）只有一個規則：<b>讀到 NUL
/// 為止就是一條命令，動作命令不回任何位元組</b>。例外是 <c>PTZ_DisConnect</c>（客戶端
/// 送 14 bytes、無 NUL 就 close）與未知命令（不回覆、連線繼續）。這裡照抄——
/// 回一個「OK」給 <c>PTZ_Up</c> 會被客戶端當成下一條命令的回應而錯位。
/// </para>
/// <para>
/// 同步阻塞是刻意的：QCC 的 PTZ 是搖桿式的連續命令，每個 <c>await</c> 的喚醒延遲都
/// 直接變成搖桿的手感（見 <see cref="NemoSessionLoop"/> 的說明）。
/// </para>
/// </remarks>
public static class QctekPtzSession
{
    /// <summary>
    /// 把 <paramref name="stream"/> 當 PTZ 連線處理，直到 <c>PTZ_DisConnect</c>、
    /// 對端關線或資料不同步。
    /// </summary>
    /// <param name="stream">已連上的串流。</param>
    /// <param name="initial">回覆 <c>OK …</c> 之後緩衝裡尚未處理的位元組（可能已是命令或半條）。</param>
    /// <param name="remote">診斷用的對端位址。</param>
    /// <param name="context">裝置資訊（提供 <see cref="IQctekDeviceContext.Ptz"/>）。</param>
    /// <param name="channelIndex"><c>PTZ_Connect</c> 指定的頻道（0-based）。</param>
    /// <param name="log">可選的訊息記錄。</param>
    public static void RunSync(
        Stream stream,
        byte[] initial,
        string remote,
        IQctekDeviceContext context,
        int channelIndex,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(context);

        var controller = context.Ptz;
        if (controller is null)
        {
            log?.Invoke($"PTZ：沒有 PTZ 控制器（{remote}），關閉連線");
            return;
        }

        var pending = new NemoSessionLoop.FrameBuffer();
        pending.Append(initial);
        var read = new byte[4096];

        try
        {
            while (true)
            {
                // 先把緩衝裡完整的命令處理完（交棒時帶過來的殘餘位元組也在裡面）。
                while (pending.Length > 0)
                {
                    var frame = QctekPtzWire.Next(pending.Span);

                    if (frame.Status is QctekPtzFrameStatus.Incomplete)
                    {
                        break; // 半條命令：繼續讀
                    }

                    if (frame.Status is QctekPtzFrameStatus.Invalid or QctekPtzFrameStatus.TooLong)
                    {
                        log?.Invoke(
                            $"PTZ：非 PTZ 資料（{remote}，{frame.Status}），關閉連線。前 96 bytes：" +
                            QctekDiagnostics.DescribeBytes(pending.Span));
                        return;
                    }

                    pending.Consume(frame.Consumed);

                    if (frame.Status == QctekPtzFrameStatus.Command)
                    {
                        var command = QctekPtzCommand.Parse(frame.Command);
                        if (command.Action == QctekPtzAction.Disconnect)
                        {
                            log?.Invoke($"PTZ：客戶端結束（{remote}）");
                            return;
                        }

                        controller.Apply(channelIndex, command);
                    }
                }

                var count = stream.Read(read, 0, read.Length);
                if (count == 0)
                {
                    // EOF：殘留緩衝就是最後一條命令（PTZ_DisConnect 不帶 NUL）。
                    if (QctekPtzWire.ParseTail(pending.Span) is { } tail &&
                        tail.Action != QctekPtzAction.Disconnect)
                    {
                        controller.Apply(channelIndex, tail);
                    }

                    log?.Invoke($"PTZ：連線關閉（{remote}）");
                    return;
                }

                pending.Append(read.AsSpan(0, count));
            }
        }
        catch (Exception ex)
        {
            // 單一 PTZ 連線的例外（含寫入失敗、呼叫端關閉串流的取消）不影響伺服器本體。
            log?.Invoke($"PTZ 連線結束（{remote}）：{ex.Message}");
        }
    }
}
