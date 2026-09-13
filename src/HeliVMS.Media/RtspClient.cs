using System.Diagnostics;
using HeliVMS.Shared.Models;

namespace HeliVMS.Media;

/// <summary>
/// 即時 RTSP 拉流（§3：監看管線）。
/// 以外部 ffmpeg 進程解碼為 BGR24 原始影格，經管道送至呼叫端。
/// 斷線後 5 秒自動重連。
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

    /// <summary>解碼出一幀。</summary>
    public event EventHandler<VideoFrame>? FrameDecoded;

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
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var (width, height) = await ProbeResolutionAsync(token);
                await StreamFramesAsync(width, height, token);
                attempt = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                attempt++;
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
    }

    /// <summary>以 ffprobe 取得影格解析度。</summary>
    private async Task<(int Width, int Height)> ProbeResolutionAsync(CancellationToken token)
    {
        var info = await Task.Run(() => StreamProbe.Probe(RtspUrl, _ffprobe), token);
        return (info.Width, info.Height);
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

                var frame = new VideoFrame
                {
                    Width = width,
                    Height = height,
                    Pixels = (byte[])buffer.Clone(),
                    TimestampUtc = DateTime.UtcNow,
                    PtsMs = sw.ElapsedMilliseconds,
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