using System.Diagnostics;
using HeliVMS.Shared.Models;

namespace HeliVMS.Media;

/// <summary>拉流連線狀態（供 UI 角標，§3.3）。</summary>
public enum RtspState
{
    /// <summary>已停止。</summary>
    Stopped,

    /// <summary>連線中（初次或重試）。</summary>
    Connecting,

    /// <summary>已解幀（即時串流中）。</summary>
    Streaming,

    /// <summary>斷線重連等待中。</summary>
    Reconnecting,
}

/// <summary>
/// 即時 RTSP 拉流（§3.3：監看管道；§21.2 #1：監看解碼、錄影不經此路）。
/// 以外部 ffmpeg 進程解碼為 BGR24 原始影格，經管道送至呼叫端。
/// 斷線後 5 秒自動重連；支援最大幀率抽樣以保護 UI 執行緒。
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    private const int RetryDelayMs = 5_000;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Process? _process;
    private bool _disposed;

    public RtspClient(string rtspUrl, string? ffmpegPath = null, string? ffprobePath = null)
    {
        RtspUrl = rtspUrl;
        _ffmpeg = ffmpegPath ?? "ffmpeg";
        _ffprobe = ffprobePath ?? "ffprobe";
    }

    public string RtspUrl { get; }

    public bool IsRunning { get; private set; }

    /// <summary>監看最大幀率上限（§3.3：防止 32 路塞爆 UI）。</summary>
    public double MaxFramesPerSecond { get; set; } = 15;

    /// <summary>目前連線狀態。</summary>
    public RtspState State { get; private set; } = RtspState.Stopped;

    /// <summary>解碼出一幀。</summary>
    public event EventHandler<VideoFrame>? FrameDecoded;

    /// <summary>連線狀態變更（供角標）。</summary>
    public event EventHandler<RtspState>? StateChanged;

    /// <summary>斷線重連時通知。</summary>
    public event EventHandler<Exception>? Reconnecting;

    /// <summary>開始拉流（背景重連迴圈）。</summary>
    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_cts is not null)
            {
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
        }

        var token = _cts.Token;
        _ = Task.Run(() => RunLoopAsync(token), token);
        return Task.CompletedTask;
    }

    /// <summary>停止拉流。</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }

        KillProcess();
        IsRunning = false;
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SetState(RtspState.Connecting);
                var (width, height) = await ProbeResolutionAsync(token);
                await StreamFramesAsync(width, height, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                SetState(RtspState.Reconnecting);
                Reconnecting?.Invoke(this, ex);
                try
                {
                    await Task.Delay(RetryDelayMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetState(RtspState.Stopped);
    }

    private void SetState(RtspState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        var handler = StateChanged;
        if (handler is not null)
        {
            try
            {
                handler(this, state);
            }
            catch
            {
                // 訂閱者例外不得影響拉流迴圈
            }
        }
    }

    private async Task StreamFramesAsync(int width, int height, CancellationToken token)
    {
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
        psi.ArgumentList.Add("-rtsp_transport");
        psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(RtspUrl);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("-vsync");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("pipe:1");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        _process = proc;
        IsRunning = true;

        var stream = proc.StandardOutput.BaseStream;
        var frameSize = width * height * 3;
        var buffer = new byte[frameSize];
        var sw = Stopwatch.StartNew();
        var emitIntervalMs = MaxFramesPerSecond > 0 ? 1000.0 / MaxFramesPerSecond : 0;
        long lastEmitMs = 0;

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
                        throw new IOException("ffmpeg 管線已結束");
                    }

                    offset += read;
                }

                var nowMs = sw.ElapsedMilliseconds;
                if (nowMs - lastEmitMs < emitIntervalMs)
                {
                    continue;
                }

                lastEmitMs = nowMs;
                if (State != RtspState.Streaming)
                {
                    SetState(RtspState.Streaming);
                }

                var frame = new VideoFrame
                {
                    Width = width,
                    Height = height,
                    Pixels = (byte[])buffer.Clone(),
                    TimestampUtc = DateTime.UtcNow,
                    PtsMs = nowMs,
                };
                FrameDecoded?.Invoke(this, frame);
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

            IsRunning = false;
        }
    }

    /// <summary>以 ffprobe 取得影格解析度。</summary>
    private async Task<(int Width, int Height)> ProbeResolutionAsync(CancellationToken token)
    {
        var info = await Task.Run(() => StreamProbe.Probe(RtspUrl, _ffprobe), token);
        return (info.Width, info.Height);
    }

    private void KillProcess()
    {
        Process? p;
        lock (_gate)
        {
            p = _process;
        }

        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 已結束
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _cts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}