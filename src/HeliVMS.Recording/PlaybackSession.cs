using System.Diagnostics;
using HeliVMS.Media;
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
    private CancellationTokenSource? _cts;
    private Process? _process;
    private bool _disposed;

    public PlaybackSession(SegmentRecord segment, string? ffmpegPath = null, string? ffprobePath = null)
    {
        Segment = segment;
        _ffmpeg = ffmpegPath ?? "ffmpeg";
        _ffprobe = ffprobePath ?? "ffprobe";
    }

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
            var info = await Task.Run(() => StreamProbe.Probe(Segment.FilePath, _ffprobe), cancellationToken);
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
        psi.ArgumentList.Add("-vsync");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("pipe:1");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        _process = proc;
        IsPlaying = true;

        var stream = proc.StandardOutput.BaseStream;
        var frameSize = info.Width * info.Height * 3;
        var buffer = new byte[frameSize];
        var fps = info.Fps > 0 ? info.Fps : 25;
        var sw = Stopwatch.StartNew();
        long nextEmitAt = 0;

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

                nextEmitAt = Math.Max(nextEmitAt, sw.ElapsedMilliseconds) + (long)Math.Ceiling(intervalMs);
                FrameIndex++;
                FrameDecoded?.Invoke(this, new VideoFrame
                {
                    Width = info.Width,
                    Height = info.Height,
                    Pixels = (byte[])buffer.Clone(),
                    TimestampUtc = Segment.StartUtc.AddSeconds(seekSeconds + FrameIndex / fps / currentSpeed),
                    PtsMs = nextEmitAt,
                });
            }
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 進程已結束
            }

            IsPlaying = false;
        }

        Ended?.Invoke(this, new PlaybackEndedEventArgs(Segment, true));
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
                        _process.Kill(entireProcessTree: true);
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