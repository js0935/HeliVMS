using System.Diagnostics;
using System.Globalization;
using HeliVMS.Shared;

namespace HeliVMS.Media;

/// <summary>抽樣幀：32×32 灰階（1024 bytes）＋它在來源裡的時間偏移。</summary>
public readonly record struct GraySample(TimeSpan Offset, byte[] Gray32x32);

/// <summary>
/// 以 ffmpeg 從媒體檔抽樣灰階幀（32×32、管線輸出、不落磁碟），供「以圖搜圖」比對。
/// </summary>
/// <remarks>
/// <para>
/// <b>一次行程抽一批、不落磁碟</b>：逐幀各開一顆 ffmpeg 會讓「搜尋一天」變成
/// 上千次行程啟動（每次都要重讀檔頭與 seek），在錄影磁碟上是不可接受的負載；
/// 這裡用 <c>fps</c> 濾鏡讓一顆行程吐出整段的等間隔樣本，並以 rawvideo 管線
/// 直接送回記憶體。樣本一律縮成 32×32 灰階：比對端的
/// <see cref="HeliVMS.Shared.PerceptualHash"/> 只需要 9×8，多出來的解析度是
/// 給「保留長寬比＋補黑邊」用的（見下）。
/// </para>
/// <para>
/// <b>查詢圖與掃描樣本必須走同一條縮放管線</b>：<c>scale=32:32:force_original_aspect_ratio=decrease</c>
/// 再 <c>pad</c> 黑邊。若查詢圖直接拉伸、掃描樣本保持比例（或反之），同一個場景
/// 兩邊的幾何就不同，指紋對不上——症狀是「明明有錄到卻搜不到」，
/// 所以兩個入口共用同一份濾鏡字串。
/// </para>
/// <para>
/// 逾時或取消一律直接砍行程樹（與 <see cref="SnapshotGrabber"/> 同模型）：
/// 讀取卡死時唯一能讓它回來的方法就是把 ffmpeg 砍掉；「砍」之後讀取端會拿到
/// EOF 或 I/O 錯誤，由看守旗標決定要拋逾時還是取消。
/// </para>
/// </remarks>
public static class FrameSampleExtractor
{
    /// <summary>樣本邊長（像素）。</summary>
    public const int SampleSize = 32;

    /// <summary>樣本位元組數（灰階、列優先）。</summary>
    public const int SampleBytes = SampleSize * SampleSize;

    /// <summary>逾時上限（單一來源檔案）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 共用的縮放濾鏡：保持長寬比縮進 32×32 後補黑邊——查詢圖與掃描樣本必須一字不差。
    /// </summary>
    public const string ScaleFilter =
        "scale=32:32:force_original_aspect_ratio=decrease,pad=32:32:(ow-iw)/2:(oh-ih)/2";

    /// <summary>
    /// 抽樣參數（純函式，供測試逐項比對）。
    ///
    /// <para>
    /// <c>keyFramesOnly</c>（預設開）＝只解關鍵幀（<c>-skip_frame nokey</c>，decoder
    /// 選項放 <c>-i</c> 前）。實測（同一台機器、5 路錄影進行中）：一個 5 分鐘段
    /// 全解碼 3.7–5.3 秒、只解關鍵幀 0.4 秒——約 9 倍差；代價是樣本時刻的精度受
    /// GOP 限制（±1–4 秒，fps 濾鏡仍輸出等間隔時間戳、取最接近的關鍵幀）。
    /// 「找出在哪裡出現過」不需要毫秒精度——之後用回放微調即可。
    /// </para>
    /// </summary>
    public static List<string> BuildSampleArguments(
        string inputPath, TimeSpan from, TimeSpan span, double intervalSeconds, int maxSamples,
        bool keyFramesOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (from < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(from));
        }

        if (span <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(span));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSamples);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
        };

        if (keyFramesOnly)
        {
            args.Add("-skip_frame");
            args.Add("nokey");
        }

        args.AddRange(
        [
            // -ss 放在 -i 之前＝input seek：只在必要時解碼丟棄，一天的回放檔才有得等。
            "-ss", Seconds(from),
            "-i", inputPath,

            // -t 走輸出側＝限制總長；樣本數上限由 fps 間隔與 span 決定，另外還有
            // maxSamples 兜底（span/interval 被誤設成 0.1 之類時的防護）。
            "-t", Seconds(span),
            "-vf", $"fps=1/{Seconds(intervalSeconds)},{ScaleFilter}",
            "-f", "rawvideo",
            "-pix_fmt", "gray",
            "-frames:v", maxSamples.ToString(CultureInfo.InvariantCulture),
            "-",
        ]);
        return args;
    }

    /// <summary>單幀抽取參數（查詢影像或「抓一張」——影片與圖片檔共用）。</summary>
    public static List<string> BuildSingleFrameArguments(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        return
        [
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-vf", ScaleFilter,
            "-frames:v", "1",
            "-f", "rawvideo",
            "-pix_fmt", "gray",
            "-",
        ];
    }

    /// <summary>
    /// 抽樣並逐幀回呼（阻塞——呼叫端自行放背景執行緒）。回傳樣本數。
    /// 樣本 <c>i</c> 的時間偏移為 <c>from + i×interval</c>（fps 濾鏡自輸入起點
    /// 等間隔輸出，第一幀落在 <c>from</c>）。
    /// </summary>
    /// <exception cref="TimeoutException">超過 <paramref name="timeout"/>（預設 120 秒）。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
    /// <exception cref="InvalidOperationException">ffmpeg 在吐出任何樣本前就失敗（檔案不存在、毀損）。</exception>
    public static int ExtractSamples(
        string inputPath,
        TimeSpan from,
        TimeSpan span,
        double intervalSeconds,
        int maxSamples,
        Action<GraySample> onSample,
        string? ffmpegPath = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        bool keyFramesOnly = true)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        var args = BuildSampleArguments(inputPath, from, span, intervalSeconds, maxSamples, keyFramesOnly);
        return RunPipeline(
            inputPath, args, from, intervalSeconds, maxSamples, onSample, ffmpegPath, timeout, cancellationToken);
    }

    /// <summary>
    /// 抽單幀灰階（32×32）——查詢影像（JPEG/PNG）或影片的任意時刻。
    /// 檔案的尺寸不足一格時回傳長度 1024 的緩衝（缺的補 0）。
    /// </summary>
    public static byte[] ExtractSingleFrame(
        string inputPath,
        string? ffmpegPath = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildSingleFrameArguments(inputPath);
        byte[]? frame = null;
        RunPipeline(
            inputPath, args, TimeSpan.Zero, intervalSeconds: 1.0, maxSamples: 1,
            sample => frame ??= sample.Gray32x32,
            ffmpegPath, timeout, cancellationToken);

        if (frame is null)
        {
            throw new InvalidOperationException($"抽幀失敗（沒有輸出）：{RtspUri.Redact(inputPath)}");
        }

        return frame;
    }

    private static int RunPipeline(
        string inputPath,
        List<string> args,
        TimeSpan startOffset,
        double intervalSeconds,
        int maxSamples,
        Action<GraySample> onSample,
        string? ffmpegPath,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath ?? FfmpegLocator.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg");

        var limit = timeout ?? DefaultTimeout;
        var stderr = proc.StandardError.ReadToEndAsync();
        var stream = proc.StandardOutput.BaseStream;

        // 看守：讀取卡死時唯一能讓它回來的方法就是砍行程（見類別註解）。
        var timedOut = 0;
        var canceled = 0;
        using var guardStop = new CancellationTokenSource();
        var guard = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + limit;
            while (!guardStop.IsCancellationRequested && !proc.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref canceled, 1);
                    TryKill(proc);
                    return;
                }

                if (DateTime.UtcNow > deadline)
                {
                    Interlocked.Exchange(ref timedOut, 1);
                    TryKill(proc);
                    return;
                }

                try
                {
                    await Task.Delay(100, guardStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);

        var buffer = new byte[SampleBytes];
        var count = 0;
        var killedByUs = false;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, SampleBytes);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    break; // 行程被砍或管道關閉
                }

                if (read < SampleBytes)
                {
                    if (read > 0)
                    {
                        // 殘缺尾幀（ffmpeg 被砍在寫入中）：不是完整樣本，丟棄。
                    }

                    break;
                }

                onSample(new GraySample(
                    startOffset + TimeSpan.FromSeconds(count * intervalSeconds),
                    (byte[])buffer.Clone()));
                count++;

                if (count >= maxSamples)
                {
                    killedByUs = true;
                    TryKill(proc);
                    break;
                }

                if (Volatile.Read(ref timedOut) == 1 || Volatile.Read(ref canceled) == 1)
                {
                    break;
                }
            }
        }
        finally
        {
            guardStop.Cancel();
            try
            {
                guard.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }

        if (Volatile.Read(ref canceled) == 1)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (Volatile.Read(ref timedOut) == 1)
        {
            throw new TimeoutException($"抽樣逾時（{limit.TotalSeconds:0.#} 秒）：{RtspUri.Redact(inputPath)}");
        }

        if (!killedByUs && count == 0 && !proc.HasExited)
        {
            // 還在跑卻一個樣本都沒有（例如巨大檔案 seek 中）——保守等待結束。
            proc.WaitForExit(2000);
        }

        if (count == 0 && proc.HasExited && proc.ExitCode != 0)
        {
            var detail = SafeStderr(stderr);
            throw new InvalidOperationException(
                $"抽樣失敗（ffmpeg 結束碼 {proc.ExitCode}）：{RtspUri.Redact(inputPath)}{detail}");
        }

        return count;
    }

    private static string Seconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Seconds(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SafeStderr(Task<string> stderr)
    {
        try
        {
            if (!stderr.Wait(TimeSpan.FromSeconds(2)))
            {
                return string.Empty;
            }

            var text = stderr.Result;
            var tail = text.Length <= 200 ? text : text[^200..];
            return $"：{tail.Trim()}";
        }
        catch (Exception)
        {
            return string.Empty;
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
            // 行程已自己結束：不必處理。
        }
    }
}
