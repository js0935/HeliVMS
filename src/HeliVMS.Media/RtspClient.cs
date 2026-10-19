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
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string? _hwAccel;
    private readonly FfmpegProcessRegistry _registry;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _process;
    private bool _disposed;
    private int _consecutiveFailures;
    private bool _hwAccelProbed;
    private bool _hwAccelSupported;
    private bool _lastAttemptUsedHwAccel;
    private bool _lastAttemptProducedFrame;
    private DateTime _hwAccelDisabledUntil = DateTime.MinValue;

    public RtspClient(
        string rtspUrl,
        string? ffmpegPath = null,
        string? ffprobePath = null,
        string? hwAccel = null,
        FfmpegProcessRegistry? registry = null)
    {
        RtspUrl = rtspUrl;
        _ffmpeg = ffmpegPath ?? FfmpegLocator.Ffmpeg;
        _ffprobe = ffprobePath ?? FfmpegLocator.Ffprobe;
        _hwAccel = NormalizeHwAccel(hwAccel);
        _registry = registry ?? FfmpegProcessRegistry.Shared;
    }

    /// <summary>
    /// 硬體加速解碼設定（§3.3 決策 D4：opt-in）：空／off／none／false／0 代表停用並回 <c>null</c>，
    /// 其餘值原樣帶入 ffmpeg 的 <c>-hwaccel</c>。預設停用，因為在沒有對應 GPU/DLL 的機器上，
    /// 硬體解碼會「連得上卻沒畫面」且日誌乾淨——比純軟解更難診斷。
    /// </summary>
    public static string? NormalizeHwAccel(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v) ||
            v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("0", StringComparison.Ordinal))
        {
            return null;
        }

        return v;
    }

    /// <summary>
    /// 即時監看 ffmpeg 解碼參數（純函式，測試釘住順序）。未啟用硬體加速時與既有行為完全相同，
    /// 啟用時僅在 <c>-i</c> 前插入一組 <c>-hwaccel &lt;value&gt;</c>；編碼參數（rawvideo/bgr24）不變，
    /// 故輸出仍是去交錯前的 BGR24 原始影格。
    /// </summary>
    public static IReadOnlyList<string> BuildDecodeArguments(string rtspUrl, string? hwAccel = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-rtsp_transport", "tcp",
        };

        var accel = NormalizeHwAccel(hwAccel);
        if (accel is not null)
        {
            args.Add("-hwaccel");
            args.Add(accel);
        }

        args.Add("-i");
        args.Add(rtspUrl);
        args.Add("-an");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("bgr24");
        // 不可用 -vsync 0：ffmpeg 9.0 移除了該選項（9.0.2 直接 "Unrecognized option" 立刻結束），
        // 即時監看會變成反覆重連、永遠沒有畫面。CI 安裝的正是 ffmpeg 9，但 Media 測試以假行程
        // 驗參數清單，不會執行真 ffmpeg，所以只有真人使用才會看到這個故障。
        // -fps_mode passthrough 語意相同（不複製、不丟棄影格），且自 5.1 起可用。
        args.Add("-fps_mode");
        args.Add("passthrough");
        args.Add("pipe:1");
        return args;
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

    /// <summary>
    /// 硬體解碼嘗試失敗（有帶 <c>-hwaccel</c> 卻一幀都沒出）後，改用軟解的冷卻時間（預設 5 分鐘）。
    /// 冷卻期過後會再試一次硬體解碼，避免「裝置忙碌／暫時不可用」被永久降級成軟解；
    /// 冷卻期本身則確保不會在一次崩潰後每一輪重連都重蹈覆轍（拖長黑畫面）。
    /// </summary>
    public TimeSpan HardwareFallbackCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 本機 ffmpeg 是否支援目前設定的硬體加速：<c>null</c>＝未設定硬體加速、
    /// <c>true/false</c>＝已探測且（不）支援。供診斷／測試觀察，探測前為 <c>null</c>。
    /// </summary>
    public bool? HwAccelSupported => _hwAccel is null ? null : (_hwAccelProbed ? _hwAccelSupported : null);

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

                // 只在「設定了硬體加速、本機支援、且不在失敗冷卻期內」時才帶 -hwaccel。
                // 一幀都沒出的失敗會進入冷卻期，下一輪即退回軟解（見 catch）。
                var hwAccel = EffectiveHwAccel();
                _lastAttemptUsedHwAccel = hwAccel is not null;
                _lastAttemptProducedFrame = false;

                await StreamFramesAsync(width, height, hwAccel, token);
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

                // 硬體解碼試過卻一幀都沒出：極可能是加速方式在本機不可用（有對應 GPU/DLL 才有畫面，
                // 否則連得上卻沒畫面、日誌乾淨）。退回軟解一段冷卻時間，讓頻道能出畫而非卡死。
                if (_lastAttemptUsedHwAccel && !_lastAttemptProducedFrame)
                {
                    _hwAccelDisabledUntil = DateTime.UtcNow + HardwareFallbackCooldown;
                }

                // 指數退避：離線的攝影機固定每 5 秒重試，只是在製造無用的交握與行程。
                // 每次成功收到影格時 _consecutiveFailures 會歸零，故正常來源的短暫斷線
                // 仍只等 5 秒。
                _consecutiveFailures++;
                try
                {
                    await Task.Delay(FfmpegBackoff.DelayFor(_consecutiveFailures), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetState(RtspState.Stopped);
    }

    /// <summary>
    /// 決定這一輪連線要用的硬體加速值（<c>null</c>＝軟解）：未設定、本機不支援，
    /// 或處於前次失敗的冷卻期內時皆為 <c>null</c>。首次呼叫會探測本機 ffmpeg 支援的加速方式。
    /// </summary>
    private string? EffectiveHwAccel()
    {
        if (_hwAccel is null)
        {
            return null;
        }

        if (DateTime.UtcNow < _hwAccelDisabledUntil)
        {
            return null;
        }

        if (!_hwAccelProbed)
        {
            _hwAccelProbed = true;
            var available = FfmpegHwAccel.ProbeCached(_ffmpeg, TimeSpan.FromSeconds(5));
            _hwAccelSupported = FfmpegHwAccel.ShouldUse(_hwAccel, available);
        }

        return _hwAccelSupported ? _hwAccel : null;
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

    private async Task StreamFramesAsync(int width, int height, string? hwAccel, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildDecodeArguments(RtspUrl, hwAccel))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        _process = proc;
        IsRunning = true;

        // 登錄到全域行程表：監看解碼 ffmpeg 是每顯示頻道一個的常駐子行程。
        using var job = _registry.Register(FfmpegJobKind.Decode, RtspUri.Redact(RtspUrl));

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
                // 首幀不做幀率節流：若 ffmpeg 啟動後 <1 幀間隔（e.g. MaxFramesPerSecond=30 時 <33ms）
                // 就完成了首幀讀取，`nowMs - lastEmitMs(0) < emitIntervalMs` 會誤把第一幀 skip，
                // 而 firstFrame 又尚未轉 false，導致對「單一瞬間輸出」的來源（及測試 stub）
                // 永遠收不到首幀、反覆重連（lastEmitMs 只在非 skip 時更新）。
                if (!firstFrame && nowMs - lastEmitMs < emitIntervalMs)
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
                _consecutiveFailures = 0;
                _lastAttemptProducedFrame = true;
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

            // 非零退出＝ffmpeg 真的失敗（帳密錯、串流不合等），計入行程失敗統計。
            try
            {
                if (proc.HasExited && proc.ExitCode != 0)
                {
                    job.Fail(LastDiagnostic);
                }
            }
            catch (InvalidOperationException)
            {
                // 進程已被釋放；不影響診斷。
            }
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
