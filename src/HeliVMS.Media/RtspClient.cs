using System.Diagnostics;
using HeliVMS.Shared;
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
    private Task? _loop;
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

    /// <summary>
    /// ffprobe 探測解析度的逾時上限。攝影機 RTSP 連線無回應時 ffprobe 可能停滯，
    /// 逾時後必須讓重連迴圈接手，否則頻道會永遠停在 Connecting。
    /// 實測最慢的攝影機（220.130.205.226）ffprobe 需 11.6～55.3 秒才回傳解析度，
    /// 故上限需留顯著餘量，否則該類攝影機永遠無法開台。
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 首幀逾時上限（需容納攝影機送出 SPS/PPS 與 IDR）。實測部分攝影機從連線到首幀需 17 秒，
    /// 狀況最差的攝影機（220.130.205.226）原生 ffmpeg 也需 19.3 秒、且耗時波動大，
    /// 故給較寬裕的時間；逾時會轉為重連，不會永久卡在 Connecting。
    /// </summary>
    public TimeSpan FirstFrameTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>已出過畫面後，串流停滯的容忍上限。</summary>
    public TimeSpan FrameTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>目前連線狀態。</summary>
    public RtspState State { get; private set; } = RtspState.Stopped;

    /// <summary>
    /// 最近一次 ffmpeg 的錯誤輸出尾端（最多 20 行，帳密已遮蔽），供 UI 診斷顯示。
    /// 用於回答「為何這個頻道連不上」而毋須洩漏憑證。
    /// </summary>
    public string LastDiagnostic { get; private set; } = string.Empty;

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
        _loop = Task.Run(() => RunLoopAsync(token), token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止拉流，並等待重連迴圈完全收尾。返回後 <see cref="State"/>、<see cref="IsRunning"/> 與
    /// <see cref="LastDiagnostic"/> 即為確定值，且可再次呼叫 <see cref="StartAsync"/> 重新拉流
    /// （健康重啟會在同一實例上 Stop 後再 Start；若此處不清掉 _cts，重啟會被靜默忽略，
    /// 造成頻道永久無法恢復）。
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
        }

        KillProcess();

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 迴圈未在時限內收尾（進程卡住）；KillProcess 已先中止 ffmpeg。
            }
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }

        IsRunning = false;
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

        // 必須持續排乾 stderr：ffmpeg 遇連線異常會反覆輸出錯誤，若無人讀取，管線填滿
        // （Windows 約 64KB）後 ffmpeg 會阻塞在寫入而不再產出影格，導致正常的串流
        // 被誤判為停滯並陷入重連迴圈。此處只保留尾端供診斷，並遮蔽可能回顯的帳密。
        var stderrTail = new Queue<string>();
        var stderrGate = new object();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                using var reader = proc.StandardError;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (stderrGate)
                    {
                        stderrTail.Enqueue(line);
                        while (stderrTail.Count > 20)
                        {
                            stderrTail.Dequeue();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 進程結束或管線關閉；診斷內容非必要。
            }
        });

        var stream = proc.StandardOutput.BaseStream;
        var frameSize = width * height * 3;
        var buffer = new byte[frameSize];
        var sw = Stopwatch.StartNew();
        var emitIntervalMs = MaxFramesPerSecond > 0 ? 1000.0 / MaxFramesPerSecond : 0;
        long lastEmitMs = 0;
        var firstFrame = true;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // 部分攝影機可完成 RTSP 連線卻永遠不送影格（或送出與 ffprobe 不同的解析度，
                // 使讀取永遠無法湊滿一幀）。若不設限，頻道會永久停在 Connecting 且不觸發重連。
                var limit = firstFrame ? FirstFrameTimeout : FrameTimeout;
                using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                frameCts.CancelAfter(limit);

                var offset = 0;
                while (offset < frameSize)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(offset, frameSize - offset), frameCts.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // 影格逾時（非使用者取消）：視為串流停滯，交由重連迴圈處理。
                        throw new TimeoutException(
                            firstFrame
                                ? $"串流未在 {FirstFrameTimeout.TotalSeconds:0} 秒內送出首幀（預期 {width}×{height}）"
                                : $"串流停滯超過 {FrameTimeout.TotalSeconds:0} 秒未送出下一幀（預期 {width}×{height}）");
                    }

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
                firstFrame = false;
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

            // 等排乾任務收尾（短暫上限），避免釋放進程時仍有讀取中的管線。
            try
            {
                await stderrTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 排乾未在時限內完成（進程已死）亦可忽略，仍取用已收集到的內容。
            }

            string[] tail;
            lock (stderrGate)
            {
                tail = [.. stderrTail];
            }

            // ffmpeg 會回顯輸入網址（含帳密），對外顯示前必須遮蔽。
            LastDiagnostic = RtspUri.RedactText(string.Join(Environment.NewLine, tail));

            IsRunning = false;
        }
    }

    /// <summary>以 ffprobe 取得影格解析度（逾時或取消會擲出，讓重連迴圈接手）。</summary>
    private async Task<(int Width, int Height)> ProbeResolutionAsync(CancellationToken token)
    {
// 明確傳入逾時與取消權杖，讓 StreamProbe 得以終止 ffprobe 進程；外層 WaitAsync 僅作保險。
        var info = await Task
            .Run(() => StreamProbe.Probe(RtspUrl, _ffprobe, ProbeTimeout, token), token)
            .WaitAsync(ProbeTimeout + TimeSpan.FromSeconds(2), token);
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
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
