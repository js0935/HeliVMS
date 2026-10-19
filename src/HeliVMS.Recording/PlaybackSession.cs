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
    private CancellationTokenSource? _cts;
    private IProcess? _process;
    private bool _disposed;

    public PlaybackSession(
        SegmentRecord segment,
        string? ffmpegPath = null,
        string? ffprobePath = null,
        IProcessFactory? processFactory = null,
        Func<string, StreamProbeInfo>? probe = null)
    {
        Segment = segment;
        _ffmpeg = ffmpegPath ?? FfmpegLocator.Ffmpeg;
        _ffprobe = ffprobePath ?? FfmpegLocator.Ffprobe;
        _processFactory = processFactory ?? new DefaultProcessFactory();
        _probe = probe ?? (path => StreamProbe.Probe(path, _ffprobe));
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
        var duration = Segment.DurationSec ?? 0;
        var remaining = duration - seekSeconds;
        if (remaining <= 0.1)
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
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(seekSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(Segment.FilePath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("bgr24");
        // 不因時間戳重複或跳動而複製／丟棄影格（等同舊的 -vsync 0）。
        // 不可用 -vsync：ffmpeg 9.0 已移除該選項，9.0.2 會直接以 "Unrecognized option 'vsync'"
        // 結束且完全不輸出影格——CI（choco 安裝的 ffmpeg 9）上就是這樣「0 幀、日誌乾淨」，
        // 本機 8.0 仍接受，所以只有 CI 會炸。-fps_mode 自 5.1 起可用。
        psi.ArgumentList.Add("-fps_mode");
        psi.ArgumentList.Add("passthrough");
        psi.ArgumentList.Add("pipe:1");

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
        long nextEmitAt = 0;

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

                var nowMs = sw.ElapsedMilliseconds;
                if (nextEmitAt == 0)
                {
                    nextEmitAt = nowMs;
                }

                var intervalMs = 1000.0 / fps / currentSpeed;
                if (nowMs < nextEmitAt)
                {
                    await Task.Delay((int)(nextEmitAt - nowMs), token);
                }

                // 依「排定時刻」累加，落後時靠後續影格連續補送（catch-up），而不是把錨點重設到當下。
                // 舊寫法 Math.Max(nextEmitAt, now) 一旦 Delay 粒度（Windows 上約 15.6ms）超過影格間隔，
                // 每格都會被量化並重新落後，永遠追不上：16× 實測只有約 2.5×（10 秒素材花 4 秒）。
                nextEmitAt += (long)Math.Ceiling(intervalMs);
                var afterEmitMs = sw.ElapsedMilliseconds;
                if (afterEmitMs - nextEmitAt > MaxCatchUpLagMs)
                {
                    nextEmitAt = afterEmitMs - MaxCatchUpLagMs;
                }

                FrameIndex++;
                var contentSeconds = FrameIndex / fps;
                FrameDecoded?.Invoke(this, new VideoFrame
                {
                    Width = info.Width,
                    Height = info.Height,
                    Pixels = (byte[])buffer.Clone(),
                    TimestampUtc = Segment.StartUtc.AddSeconds(seekSeconds + contentSeconds),
                    PtsMs = (long)Math.Round(contentSeconds * 1000),
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

        Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, true));
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