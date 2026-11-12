using System.Net.Sockets;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// QCC 遠端回放的通道：OPENFILE 授權成功後，從錄影檔讀幀包 FRAM（時戳＝錄影時刻），
/// 同時輪詢 SEEKFILE／CLOSEFILE 文字命令。
/// </summary>
/// <remarks>
/// <para>
/// 幀流與 live 完全相同的 FRAM 格式（QCC 端直接重用
/// <c>dvrProxy_live::readQCV8001frame</c>）；差異在時戳——回放的時戳是<b>錄影時刻</b>
/// （過去的時間），QCC 的回放進度條靠它排程，與 live 的「寫出時牆鐘」刻意不同。
/// </para>
/// <para>
/// SEEKFILE/CLOSEFILE 是文字命令（NUL 結尾），與幀流共用同一條連線——以
/// <see cref="NetworkStream.DataAvailable"/> 輪詢，與 live 的 keepalive 同一模型。
/// SEEKFILE 的秒數語意（相對/絕對）待真機抓包驗證，目前以「檔案起點的絕對秒」實作。
/// </para>
/// </remarks>
public static class QctekPlaybackSession
{
    /// <summary>主迴圈：從 <paramref name="first"/> 開始逐段輸出幀流，直到對端關閉或 CLOSEFILE。</summary>
    public static void RunSync(
        Stream stream,
        QctekPlaybackSegment first,
        IQctekDeviceContext context,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(context);

        if (stream is not NetworkStream network)
        {
            throw new ArgumentException("回放通道需要可輪詢的 NetworkStream。", nameof(stream));
        }

        var fromUtc = first.StartUtc;
        var command = new NemoSessionLoop.FrameBuffer();
        var buffer = new byte[4096];

        try
        {
            while (true)
            {
                // 1) 輪詢 SEEKFILE／CLOSEFILE（文字命令，NUL 結尾）。
                while (network.DataAvailable)
                {
                    var n = network.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                    {
                        Log(log, $"PB：對端關閉連線");
                        return;
                    }

                    command.Append(buffer.AsSpan(0, n));
                    while (TryTakeCommand(command, out var text))
                    {
                        log?.Invoke($"PB ← {text}");
                        if (text.StartsWith("CLOSEFILE", StringComparison.Ordinal))
                        {
                            WriteOk(network);
                            Log(log, "PB：CLOSEFILE");
                            return;
                        }

                        if (text.StartsWith("SEEKFILE", StringComparison.Ordinal) &&
                            TryParseSeekSeconds(text, out var seconds))
                        {
                            fromUtc = first.StartUtc.AddSeconds(seconds);
                            log?.Invoke($"PB ← SEEKFILE {seconds}s");
                        }
                    }
                }

                // 2) 從 fromUtc 的段開始輸出幀流（段尾自動接下一段）。
                var segment = context.FindSegmentCovering(first.ChannelIndex, fromUtc)
                    ?? context.FindSegmentFrom(first.ChannelIndex, fromUtc);
                if (segment is null)
                {
                    Log(log, $"PB：{fromUtc:HH:mm:ss} 之後沒有錄影，結束回放");
                    return;
                }

                StreamSegment(
                    network, context, segment, log,
                    () => network.DataAvailable,
                    () =>
                    {
                        var n = network.Read(buffer, 0, buffer.Length);
                        if (n == 0)
                        {
                            return (string?)null;
                        }

                        command.Append(buffer.AsSpan(0, n));
                        return TryTakeCommand(command, out var t) ? t : string.Empty;
                    },
                    out var nextFrom);

                if (nextFrom is null)
                {
                    return; // CLOSEFILE 或對端關閉
                }

                fromUtc = nextFrom.Value;
            }
        }
        catch (Exception ex)
        {
            Log(log, $"PB：連線結束：{ex.Message}");
        }
    }

    /// <summary>
    /// 輸出一個段的幀流：ffmpeg 讀錄影檔 → AU → FRAM（時戳＝段起始＋幀偏移）；
    /// 段讀完回下一個起點；途中偵測 SEEK/CLOSE 回控制訊號。
    /// </summary>
    private static DateTime? StreamSegment(
        NetworkStream network,
        IQctekDeviceContext context,
        QctekPlaybackSegment segment,
        Action<string>? log,
        Func<bool> dataAvailable,
        Func<string?> readCommand,
        out DateTime? nextFrom)
    {
        nextFrom = null;
        var source = context.CreatePlaybackSource(segment.FilePath);
        if (source is null)
        {
            log?.Invoke($"PB：錄影檔來源建立失敗（{segment.FilePath}）");
            return segment.StartUtc;
        }

        try
        {
            var output = source.Output;
            var grouper = new QctekH264AccessUnitGrouper();
            var buffer = new byte[64 * 1024];
            var auIndex = 0L;

            while (true)
            {
                // 命令優先：幀流中每個 AU 之間檢查一次 SEEK/CLOSE。
                if (dataAvailable())
                {
                    var text = readCommand();
                    if (text is null)
                    {
                        return null; // 對端關閉
                    }

                    if (text.Length > 0)
                    {
                        log?.Invoke($"PB ← {text}");
                        if (text.StartsWith("CLOSEFILE", StringComparison.Ordinal))
                        {
                            WriteOk(network);
                            Log(log, "PB：CLOSEFILE（幀流中）");
                            return null;
                        }

                        if (text.StartsWith("SEEKFILE", StringComparison.Ordinal) &&
                            TryParseSeekSeconds(text, out var seconds))
                        {
                            nextFrom = segment.StartUtc.AddSeconds(seconds);
                            return nextFrom; // 重定位：回呼端以新時刻重找段
                        }
                    }
                }

                var count = output.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break; // 段讀完
                }

                foreach (var unit in grouper.Append(buffer.AsSpan(0, count)))
                {
                    // 時戳＝錄影時刻：段起始＋幀偏移（33ms/幀的近似；精確時戳下輪從檔案讀）。
                    var frameUtc = segment.StartUtc.AddMilliseconds(auIndex++ * 33);
                    var packet = QctekLivePackets.Data(
                        segment.ChannelIndex,
                        unit.Block1,
                        unit.Block2,
                        unit.Video,
                        new DateTimeOffset(frameUtc).ToUnixTimeSeconds(),
                        frameUtc.Microsecond,
                        frameUtc.ToFileTime());
                    network.Write(packet, 0, packet.Length);
                }
            }

            // 段讀完：下一段從段尾開始（5 分鐘段；實際段長由下輪 FindSegmentFrom 銜接）。
            nextFrom = segment.StartUtc.AddSeconds(300);
            return nextFrom;
        }
        finally
        {
            DisposeQuietly(source);
        }
    }

    /// <summary>從累積緩衝取一條 NUL 結尾的文字命令；不足時回 false。</summary>
    private static bool TryTakeCommand(NemoSessionLoop.FrameBuffer buffer, out string text)
    {
        var span = buffer.Span;
        var nul = span.IndexOf((byte)0);
        if (nul < 0)
        {
            text = string.Empty;
            return false;
        }

        text = System.Text.Encoding.ASCII.GetString(span[..nul]).TrimEnd('\r', '\n');
        buffer.Consume(nul + 1);
        return true;
    }

    private static bool TryParseSeekSeconds(string text, out long seconds)
    {
        var number = System.Text.RegularExpressions.Regex.Match(
            text, @"-?\d+", System.Text.RegularExpressions.RegexOptions.Compiled);
        seconds = 0;
        return number.Success && long.TryParse(number.Value, out seconds);
    }

    private static void WriteOk(NetworkStream network)
    {
        var ok = "OK \0"u8.ToArray();
        network.Write(ok, 0, ok.Length);
    }

    private static void DisposeQuietly(IQctekLiveSource source)
    {
        try
        {
            source.DisposeAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }

    private static void Log(Action<string>? log, string message) => log?.Invoke(message);
}
