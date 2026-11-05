using System.Collections.Concurrent;
using System.Diagnostics;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording;

/// <summary>回放結束事件承載。</summary>
public sealed class PlaybackEndedEventArgs : EventArgs
{
    public PlaybackEndedEventArgs(SegmentRecord segment, bool completed)
    {
        Segment = segment;
        Completed = completed;
    }

    public SegmentRecord Segment { get; }

    /// <summary>是否正常播放到尾。</summary>
    public bool Completed { get; }
}

/// <summary>
/// 錄影回放（M3）：以 ffmpeg 自 fMP4 區段解碼 to BGR24 原始影格。
/// 支援從段內偏移起始、倍速（0.5×–8×，依目標速率跳/複用幀）與段結束通知。
/// </summary>
public sealed class PlaybackSession : IAsyncDisposable
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly IProcessFactory _processFactory;
    private readonly Func<string, StreamProbeInfo> _probe;
    private readonly Func<string, IReadOnlyList<double>> _frameTimes;
    private CancellationTokenSource? _cts;
    private IProcess? _process;
    private bool _disposed;

    public PlaybackSession(
        SegmentRecord segment,
        string? ffmpegPath = null,
        string? ffprobePath = null,
        IProcessFactory? processFactory = null,
        Func<string, StreamProbeInfo>? probe = null,
        Func<string, IReadOnlyList<double>>? frameTimes = null)
    {
        Segment = segment;
        _ffmpeg = ffmpegPath ?? FfmpegLocator.Ffmpeg;
        _ffprobe = ffprobePath ?? FfmpegLocator.Ffprobe;
        _processFactory = processFactory ?? new DefaultProcessFactory();
        _probe = probe ?? (path => StreamProbe.Probe(path, _ffprobe));
        _frameTimes = frameTimes ?? (path => FrameTimestamps.Read(path, _ffprobe));
    }

    // 探測結果快取：每次 PlayAsync 都跑一次 ffprobe 會讓每次跳轉多背一個行程啟動（數十至上百毫秒），
    // 直接吃掉「拖拽跳轉 < 300ms 出畫」的預算（§8.5 驗收）。以「檔案路徑 + 長度 + 最後寫入時間」為簽章，
    // 區段定稿後檔案不再變動即命中；仍在寫入的檔案簽章會變，自動重探。
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Mtime, StreamProbeInfo Info)> ProbeCache = new();

    public SegmentRecord Segment { get; }

    /// <summary>播放速率：0.5 / 1 / 2 / 4 / 8。</summary>
    public double Speed { get; set; } = 1.0;

    public bool IsPlaying { get; private set; }

    /// <summary>已播出幀數（供時間標記）。</summary>
    public long FrameIndex { get; private set; }

    /// <summary>已自管道讀入之總幀數（未節流，診斷用）。</summary>
    public int FramesRead { get; private set; }

    /// <summary>解碼出一幀（已依速率節奏）。</summary>
    public event EventHandler<VideoFrame>? FrameDecoded;

    /// <summary>區段播放結束（正常 or 異常）。</summary>
    public event EventHandler<PlaybackEndedEventArgs>? Ended;

    /// <summary>解碼錯誤（可忽略，僅供提示）。</summary>
    public event EventHandler<Exception>? PlaybackError;

    /// <summary>
    /// 回放解碼參數（純函式，只依檔案、段內起點與要播的長度組出引數）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 抽成純函式是為了讓選項相容性測試（<c>PlaybackDecodeArgumentsFfmpegTests</c>）能逐項問
    /// 實際安裝的 ffmpeg「這個選項你認得嗎」。ffmpeg 9.0 移除了 <c>-vsync</c>（9.0.2 會直接以
    /// <c>Unrecognized option 'vsync'</c> 結束且不輸出任何影格），本機 8.0 仍接受，所以這種回歸
    /// 在本機全綠、只有 CI 或現場才會炸。
    /// </para>
    /// <para>
    /// <paramref name="durationSeconds"/> ≤ 0 表示索引沒寫這段有多長：此時不給 <c>-t</c>，
    /// 讓 ffmpeg 播到檔尾。若照舊送 <c>-t 0</c>，ffmpeg 一幀都不輸出、行程正常結束，
    /// 播放器收到「播放完成」卻全黑——與解碼失敗在畫面上長得一模一樣。
    /// </para>
    /// <para>
    /// 每次都新建一份引數：<see cref="ProcessStartInfo.ArgumentList"/> 由呼叫端逐項填入，
    /// 純函式回新清單才不會有共享狀態。
    /// </para>
    /// </remarks>
    /// <param name="filePath">錄影檔路徑。</param>
    /// <param name="seekSeconds">段內起始偏移（<c>-ss</c>；輸入端快速跳轉）。</param>
    /// <param name="durationSeconds">播放長度上限；0＝播到檔尾（索引沒寫段長時）。</param>
    /// <param name="audioPort">
    /// 大於 0 時多掛一個 PCM 輸出到這個本機 UDP 埠（與即時監看共用同一份參數，見
    /// <see cref="AudioOutputArguments"/>）。只有檔案確實有音訊軌時才會傳入。
    /// </param>
    /// <param name="audioSampleRate">音訊輸出取樣率；須等於播放裝置協商出的值。</param>
    public static IReadOnlyList<string> BuildDecodeArguments(
        string filePath,
        double seekSeconds,
        double durationSeconds,
        int audioPort = 0,
        int audioSampleRate = AudioStreamReceiver.DefaultSampleRate)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-ss", seekSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", filePath,
        };

        if (durationSeconds > 0)
        {
            args.Add("-t");
            args.Add(durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "-f", "rawvideo",
            "-pix_fmt", "bgr24",
            // 不因時間戳重複或跳動而複製／丟棄影格（等同舊的 -vsync 0）。
            // 不可用 -vsync：ffmpeg 9.0 已移除該選項，9.0.2 會直接以 "Unrecognized option 'vsync'"
            // 結束且完全不輸出影格——CI（choco 安裝的 ffmpeg 9）上就是這樣「0 幀、日誌乾淨」，
            // 本機 8.0 仍接受，所以只有 CI 會炸。-fps_mode 自 5.1 起可用。
            "-fps_mode", "passthrough",
            "pipe:1",
        ]);

        // 回放的聲音與即時監看同一份參數：不佔新連線、不與 stdout 的 rawvideo 混流。
        if (audioPort > 0)
        {
            args.AddRange(AudioOutputArguments.Build(audioPort, audioSampleRate));
        }

        return args;
    }

    /// <summary>
    /// 開始播放；<paramref name="seekSeconds"/> 為段內起始偏移。
    /// 播放為背景工作；結束以 <see cref="Ended"/> 事件通知。
    /// </summary>
    public async Task PlayAsync(double seekSeconds = 0, CancellationToken cancellationToken = default)
    {
        if (IsPlaying)
        {
            return;
        }

        if (!File.Exists(Segment.FilePath))
        {
            Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, false));
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FrameIndex = 0;

        try
        {
            var info = await Task.Run(() => ProbeCached(Segment.FilePath), cancellationToken);
            SourceHasAudio = info.AudioCodec is not null;
            await StreamFramesAsync(info, seekSeconds, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, false));
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, ex);
            Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, false));
        }
        finally
        {
            IsPlaying = false;
        }
    }

    /// <summary>停止目前播放。</summary>
    public void Stop()
    {
        _cts?.Cancel();
        KillProcess();
    }

    /// <summary>
    /// 現場收音要用的本機 UDP 埠（0＝不掛音訊輸出）。
    /// 埠在 <see cref="PlayAsync"/> 之前就要設好：參數是啟動 ffmpeg 時寫定的。
    /// </summary>
    public int AudioPort { get; set; }

    /// <summary>音訊輸出取樣率（須與裝置一致）。</summary>
    public int AudioSampleRate { get; set; } = AudioStreamReceiver.DefaultSampleRate;

    /// <summary>這一段檔案是否量到音訊軌（null＝還沒探測到）。</summary>
    public bool? SourceHasAudio { get; private set; }

    /// <summary>
    /// 每一格的呈現時刻（秒）的快取，簽章與 <see cref="ProbeCache"/> 相同。
    /// </summary>
    /// <remarks>
    /// 多路回放時四格同時開播，可能同一個檔案被四個 session 同時要求；用 <see cref="Lazy{T}"/>
    /// 讓同一份（路徑＋長度＋最後寫入時間）只跑一次 ffprobe，否則每格各噴一個行程。
    /// </remarks>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Mtime, Lazy<IReadOnlyList<double>> Times)> TimesCache = new();

    /// <summary>
    /// 檔案的封包時間戳（秒），與解碼路徑共用同一份快取。
    /// </summary>
    /// <remarks>
    /// 逐幀要踩真的相鄰兩格（見 <see cref="PlaybackFrameTiming.IndexAtOrBefore"/>）：名目 15fps
    /// 實送 12.5fps 的檔案上固定 1/15 秒會落在同一格，按了像沒反應。讀不到時回空清單，
    /// 呼叫端退回固定步長。
    /// </remarks>
    public static IReadOnlyList<double> FrameTimes(string path)
        => TimesCached(path, path2 => FrameTimestamps.Read(path2));

    /// <summary>
    /// 讀檔案的封包時間戳（快取）；探測委派可在測試注入。
    /// 讀不到（ffprobe 失敗、檔案不存在）回空清單，呼叫端退回固定幀率節奏。
    /// </summary>
    private static IReadOnlyList<double> TimesCached(string path, Func<string, IReadOnlyList<double>> read)
    {
        long length;
        DateTime mtime;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                // 檔案不在＝沒有任何時間戳可讀，不必白跑一個 ffprobe（PlayAsync 會立刻回報未完成）。
                return [];
            }

            length = file.Length;
            mtime = file.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return read(path);
        }

        if (TimesCache.TryGetValue(path, out var hit) && hit.Length == length && hit.Mtime == mtime)
        {
            return hit.Times.Value;
        }

        var times = new Lazy<IReadOnlyList<double>>(() => read(path));
        if (TimesCache.Count > 4096)
        {
            // 對齊 ProbeCache 的上限策略：檔案不會無限多，但長跑的機器會換代。
            TimesCache.Clear();
        }

        TimesCache[path] = (length, mtime, times);
        return times.Value;
    }

    /// <summary>解碼路徑用的時間戳（走這個 session 的探測委派）。</summary>
    private IReadOnlyList<double> TimesCached(string path) => TimesCached(path, _frameTimes);

    /// <summary>
    /// 以檔案簽章快取 ffprobe 結果（見 <see cref="ProbeCache"/> 說明）。
    /// 檔案不存在或無法取得資訊時退回直接探測——快取永不改變「探不到就是探不到」的結果語意。
    /// </summary>
    private StreamProbeInfo ProbeCached(string path)
    {
        long length;
        DateTime mtime;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return _probe(path);
            }

            length = file.Length;
            mtime = file.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return _probe(path);
        }

        if (ProbeCache.TryGetValue(path, out var hit) && hit.Length == length && hit.Mtime == mtime)
        {
            return hit.Info;
        }

        var info = _probe(path);
        if (ProbeCache.Count > 4096)
        {
            ProbeCache.Clear();
        }

        ProbeCache[path] = (length, mtime, info);
        return info;
    }

    private async Task StreamFramesAsync(StreamProbeInfo info, double seekSeconds, CancellationToken token)
    {
        // 索引沒寫段長（DurationSec 為 null／0）時不能當成「剩 0 秒」：舊寫法會直接回報
        // 播放完成，播放器全黑、日誌乾淨，操作員只看到「這段壞了」。改成不給 -t 播到檔尾。
        var duration = Segment.DurationSec ?? 0;
        var remaining = duration - seekSeconds;
        if (duration > 0 && remaining <= 0.1)
        {
            Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, true));
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // 有音訊軌才掛：舊錄影（或當初沒開音訊的攝影機）的檔案沒有音訊串流，
        // 對它下 `-map 0:a:0` 會讓 ffmpeg 直接失敗，連畫面都沒有。
        var audioPort = AudioOutputArguments.ResolvePort(AudioPort, info.AudioCodec is not null);
        foreach (var arg in BuildDecodeArguments(Segment.FilePath, seekSeconds, remaining, audioPort, AudioSampleRate))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = _processFactory.Start(psi);
        _process = proc;
        IsPlaying = true;

        var stream = proc.StandardOutput.BaseStream;

        // 同時抽乾 stderr：管線填滿會讓 ffmpeg 阻塞，而且失敗原因（例如「Unrecognized option」）
        // 只在這裡，是診斷「0 幀、日誌乾淨」的唯一線索。
        var stderr = proc.StandardError.ReadToEndAsync();

        var frameSize = info.Width * info.Height * 3;
        var buffer = new byte[frameSize];
        var fps = info.Fps > 0 ? info.Fps : 25;
        var sw = Stopwatch.StartNew();

        // 這一輪的時間基準：檔案自己的封包時間戳（見 FrameTimestamps）。讀不到時退回
        // 「影格序號 ÷ 名目幀率」的舊節奏，回放照播不誤。
        var times = TimesCached(Segment.FilePath);
        var startIndex = PlaybackFrameTiming.StartIndex(times, seekSeconds);
        var fallbackStepMs = 1000.0 / fps;
        double scheduledMs = 0;
        long emitted = 0;
        long lastContentMs = 0;

        // 落後上限（毫秒）：排程落後超過這個量就放棄追趕，避免長時間停頓（例如磁碟卡住）
        // 之後以爆量補送吃光 CPU／畫格塞爆 UI 執行緒。
        const long MaxCatchUpLagMs = 250;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var offset = 0;
                while (offset < frameSize)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset, frameSize - offset), token);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset < frameSize)
                {
                    break;
                }

                FramesRead++;

                var currentSpeed = Speed;
                if (currentSpeed <= 0)
                {
                    currentSpeed = 1;
                }

                // 這一格的內容時刻＝檔案自己的時間戳（毫秒，相對起播點）。固定幀率的舊算法
                // （影格序號 ÷ 名目幀率）在送格速率會變的相機上會讓每一格都比實際早到，
                // 多路回放的各格因此以不同速率前進、OSD 時刻也跟著失真——見 FrameTimestamps。
                var contentMs = PlaybackFrameTiming.ContentMs(
                    times,
                    startIndex,
                    emitted,
                    lastContentMs,
                    fallbackStepMs);

                var nowMs = sw.Elapsed.TotalMilliseconds;
                if (emitted == 0)
                {
                    scheduledMs = nowMs;
                }
                else
                {
                    // 依「排定時刻」累加（＝上一格到這一格的內容時間差 ÷ 倍速），落後時靠後續
                    // 影格連續補送（catch-up），而不是把錨點重設到當下。舊寫法 Math.Max(nextEmitAt, now)
                    // 一旦 Delay 粒度（Windows 上約 15.6ms）超過影格間隔，每格都會被量化並重新落後，
                    // 永遠追不上：16× 實測只有約 2.5×（10 秒素材花 4 秒）。排定時刻用 double 累加，
                    // 不把量化誤差帶進下一格。
                    scheduledMs += (contentMs - lastContentMs) / currentSpeed;
                    if (nowMs < scheduledMs)
                    {
                        await Task.Delay((int)Math.Ceiling(scheduledMs - nowMs), token);
                    }
                }

                var afterEmitMs = sw.Elapsed.TotalMilliseconds;
                if (afterEmitMs - scheduledMs > MaxCatchUpLagMs)
                {
                    scheduledMs = afterEmitMs - MaxCatchUpLagMs;
                }

                FrameIndex++;
                emitted++;
                lastContentMs = contentMs;
                FrameDecoded?.Invoke(this, new VideoFrame
                {
                    Width = info.Width,
                    Height = info.Height,
                    Pixels = (byte[])buffer.Clone(),
                    TimestampUtc = Segment.StartUtc.AddSeconds(seekSeconds + (contentMs / 1000.0)),
                    PtsMs = contentMs,
                });
            }
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // 進程已結束
            }

            IsPlaying = false;
        }

        // ffmpeg 非零結束＝解碼失敗（參數不支援、檔案毀損、解碼器缺失）。舊寫法把「一個影格都
        // 沒解出」當成播放完成：播放器收到 Ended(completed: true)、畫面全黑、日誌乾淨，正是
        // ffmpeg 9 移除 -vsync 後在 CI 上的樣子（3 條驗收全 0 幀卻沒有半點錯誤訊息）。
        // 取消（Stop／換段）不走這裡，是由上面的 OperationCanceledException 直接結束。
        if (!token.IsCancellationRequested && proc.HasExited && proc.ExitCode != 0)
        {
            var detail = TryRead(stderr);
            PlaybackError?.Invoke(
                this,
                new InvalidOperationException($"ffmpeg 解碼失敗（結束碼 {proc.ExitCode}）：{detail}"));
            Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, false));
            return;
        }

        // 取消也可能從「讀取迴圈的上緣」離開（讀取還沒開始就被停止）：那時它不是播完，
        // 回報 completed: true 會讓 PlaybackWindow 把「停止」記成「已播畢」，按播放變成從段首重播。
        Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, !token.IsCancellationRequested));
    }

    /// <summary>取出已完成之 stderr 文字（未結束或已釋放時回空字串）。</summary>
    private static string TryRead(Task<string> stderr)
    {
        try
        {
            if (!stderr.IsCompleted)
            {
                _ = stderr.Wait(TimeSpan.FromSeconds(1));
            }

            return stderr.IsCompletedSuccessfully ? stderr.Result.Trim() : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void KillProcess()
    {
        lock (_gate)
        {
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                    // 已結束
                }

                _process = null;
            }
        }
    }

    private readonly object _gate = new();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _cts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
        await Task.CompletedTask;
    }
}