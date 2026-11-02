using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Media;

/// <summary>
/// 單發取影（§14 排程任務）：對一個媒體來源（RTSP 或檔案）抓一張 JPEG。
/// </summary>
/// <remarks>
/// <para>
/// 排程截圖走<b>獨立的短行程</b>而不是監看牆的解碼管線：截圖任務排定的當下，目標頻道
/// 未必在牆上（凌晨三點的牆可能只開著四格），而為了截圖把整台攝影機的 RTSP session
/// 開起來再拆掉，正是便宜攝影機「同時連線數有限」要避免的事。一次一張、抓完即走。
/// </para>
/// <para>
/// 行程模型與 <see cref="StreamProbe"/> 相同：逾時與取消都直接砍行程樹，錯誤訊息一律
/// 先遮蔽帳密。輸出用 <c>-q:v 2</c> 的 JPEG 而非 PNG：存證截图要的是「夠小、隨手能開」，
/// 不是無損。
/// </para>
/// </remarks>
public static class SnapshotGrabber
{
    /// <summary>逾時上限。攝影機「已連線但不吐流」時 ffmpeg 會反覆重試，必須由外層砍掉。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>抓一張圖到 <paramref name="outputPath"/>（JPEG）。逾時／取消／非零結束都丟例外。</summary>
    public static void Grab(
        string inputUri,
        string outputPath,
        string? ffmpegPath = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var safeUri = RtspUri.Redact(inputUri);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath ?? FfmpegLocator.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var limit = timeout ?? DefaultTimeout;
        var rwTimeoutUs = (long)Math.Clamp(limit.TotalMilliseconds * 1000, 1_000, int.MaxValue);

        // -rw_timeout：內部 I/O 等待有邊界，不要把「等攝影機回應」全部留給外層逾時硬砍。
        psi.ArgumentList.Add("-rw_timeout");
        psi.ArgumentList.Add(rwTimeoutUs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (inputUri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            inputUri.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            // RTSP 專用的 demuxer 選項：對檔案來源會直接「Option not found」，
            // 排程測試（lavfi 檔案來源）與生產（rtsp://）共用這個入口，所以有條件地加。
            psi.ArgumentList.Add("-rtsp_transport");
            psi.ArgumentList.Add("tcp");
        }
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputUri);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg");

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        var deadline = DateTime.UtcNow + limit;
        while (!proc.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(proc);
                throw new OperationCanceledException(cancellationToken);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                TryKill(proc);
                throw new TimeoutException($"取影逾時（{limit.TotalSeconds:0.#} 秒）：{safeUri}");
            }

            proc.WaitForExit((int)Math.Min(remaining.TotalMilliseconds, 100));
        }

        _ = stdout.GetAwaiter().GetResult();
        _ = stderr;

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"取影失敗：{safeUri}");
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 行程已自己結束：不必處理，也不往上拋。
        }
    }
}
