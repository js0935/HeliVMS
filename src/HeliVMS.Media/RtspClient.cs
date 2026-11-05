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
    /// <param name="rtspUrl">RTSP 位址。</param>
    /// <param name="hwAccel">硬體解碼方式；null＝軟解。</param>
    /// <param name="audioPort">
    /// 大於 0 時多掛一個 PCM 輸出到這個本機 UDP 埠（見 <see cref="AudioOutputArguments"/>）。
    /// 只有確定串流有音訊軌時才會傳入（沒有麥克風的攝影機什麼都不掛）。
    /// </param>
    /// <param name="audioSampleRate">音訊輸出取樣率；須等於播放裝置協商出的值。</param>
    public static IReadOnlyList<string> BuildDecodeArguments(
        string rtspUrl,
        string? hwAccel = null,
        int audioPort = 0,
        int audioSampleRate = AudioStreamReceiver.DefaultSampleRate)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-rtsp_transport", "tcp",

            // 每一格的封包時間戳改成「本機收到它的牆鐘時刻」：即時監看唯一需要的是
            // 「這條串流現在離現場多遠」，而攝影機的 RTP 時間戳是相對時鐘，對不到牆鐘。
            // 只影響時間戳，不影響畫面（解碼器輸出順序由 H.264 的畫面序決定）。
            "-use_wallclock_as_timestamps", "1",

            // 每秒把「最新解出的那一格是哪一刻」寫到 stderr（見 ParseProgress）。
            // 這是操作員唯一能分辨「延遲在攝影機／網路」還是「在本機管線」的數字。
            "-progress", "pipe:2", "-stats_period", "1",
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

        // 音訊走「同一條連線的另一個輸出」：不佔攝影機額外的 RTSP session，
        // 也不會和 stdout 上的 rawvideo 混在一起（一個子行程只有一條 stdout）。
        if (audioPort > 0)
        {
            args.AddRange(AudioOutputArguments.Build(audioPort, audioSampleRate));
        }

        return args;
    }

    public string RtspUrl { get; }

    /// <summary>
    /// 現場收音要用的本機 UDP 埠（0＝不掛音訊輸出）。
    /// </summary>
    /// <remarks>
    /// 由呼叫端（App）先綁好 <see cref="AudioStreamReceiver"/> 再把埠設進來；執行中才改只會
    /// 在下一輪重連生效，因為參數是啟動 ffmpeg 時寫定的。
    /// </remarks>
    public int AudioPort { get; set; }

    /// <summary>音訊輸出取樣率（須與裝置一致）。</summary>
    public int AudioSampleRate { get; set; } = AudioStreamReceiver.DefaultSampleRate;

    /// <summary>
    /// 這一輪探測到的結果：true＝串流有音訊軌、false＝沒有、null＝還沒探測到。
    /// 「有設定收音但攝影機根本沒有麥克風」要靠它才顯示得出來，而不是安靜地沒聲音。
    /// </summary>
    public bool? SourceHasAudio { get; private set; }

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

    /// <summary>已從解碼管線讀到的影格數（含因顯示上限而丟棄的）。</summary>
    /// <remarks>診斷用；由讀取迴圈單執行緒寫入，讀取端容許舊值。</remarks>
    public long FramesRead { get; private set; }

    /// <summary>已對外送出的影格數。</summary>
    public long FramesEmitted { get; private set; }

    /// <summary>因 <see cref="MaxFramesPerSecond"/> 而丟棄的影格數。</summary>
    /// <remarks>
    /// 丟棄是「先讀進來再丟」：ffmpeg 的管線永遠被抽乾，延遲才不會隨時間累積。
    /// 這個數字若長期是 0，代表上限比來源還高（白白多解碼）；若佔比很高，代表上限太低。
    /// </remarks>
    public long FramesDropped { get; private set; }

    /// <summary>最近一次解出的影格尺寸（0×0＝還沒有影格）。</summary>
    public (int Width, int Height) FrameSize { get; private set; }

    /// <summary>
    /// 本機解碼管線積了多少秒的影格（＝輸出進度自第一筆回報以來落後牆鐘多少）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由 <c>-progress</c> 的 <c>out_time_us</c> 與牆鐘的差推得。為什麼不是「絕對延遲」：
    /// ffmpeg 會把輸出時間歸零，所以影像離現場多遠（攝影機 ＋ 網路 ＋ 解碼緩衝）**在本機量不到**；
    /// 硬要報一個數字只能是編的。這裡報的是本機唯一該負責的那一段——管線有沒有積影格；
    /// 它若持續變大，畫面就會愈拖愈舊（現場回報的「延遲」），而且是這台機器可以修的。
    /// </para>
    /// <para>
    /// 沒有進度回報時為 <c>null</c>：欄位顯示「—」，不可寫 0——0 讀起來像「完全沒有落後」。
    /// </para>
    /// </remarks>
    public TimeSpan? StreamBacklog { get; private set; }

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

    /// <summary>
    /// 重新拉流：ffmpeg 的參數（含要不要掛音訊輸出）只在行程啟動時寫定，
    /// 改變參數的唯一途徑就是把行程殺掉重開。
    /// </summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SetState(RtspState.Connecting);
                var probe = await ProbeAsync(token);
                var (width, height) = (probe.Width, probe.Height);

                // 有音訊軌才掛音訊輸出：沒有麥克風的攝影機完全不碰這件事，
                // 也就不可能因為音訊而影響畫面（`-map 0:a:0` 對沒有音訊的來源會讓行程失敗）。
                SourceHasAudio = probe.AudioCodec is not null;
                var audioPort = AudioOutputArguments.ResolvePort(AudioPort, SourceHasAudio);

                // 只在「設定了硬體加速、本機支援、且不在失敗冷卻期內」時才帶 -hwaccel。
                // 一幀都沒出的失敗會進入冷卻期，下一輪即退回軟解（見 catch）。
                var hwAccel = EffectiveHwAccel();
                _lastAttemptUsedHwAccel = hwAccel is not null;
                _lastAttemptProducedFrame = false;

                await StreamFramesAsync(width, height, hwAccel, audioPort, token);
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

    private async Task StreamFramesAsync(int width, int height, string? hwAccel, int audioPort, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildDecodeArguments(RtspUrl, hwAccel, audioPort, AudioSampleRate))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        _process = proc;
        IsRunning = true;

        // 上一條連線的數字不能留著：重連後的操作員要看的是「現在」多遠，不是上一條的殘值。
        StreamBacklog = null;
        _progressAtUtc = DateTime.MinValue;
        _progressAnchorUtc = DateTime.MinValue;

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
                    // -progress 每秒寫一行 key=value。那些行不是診斷訊息，混進尾端會把真正的
                    // 錯誤（帳密錯、串流不合）擠出 20 行之外——操作員看到的「原因」就變成
                    // 「out_time_us=…」這種天書。
                    if (IsProgressLine(line))
                    {
                        ParseProgress(line);
                        continue;
                    }

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

        // 顯示上限用令牌桶而不是「距上一張至少 1/上限 秒」：攝影機送出來的影格是一陣一陣的，
        // 嚴格等間距會把每一批的第二張丟掉（實測 read=15/s 卻只 shown=5–10/s，比來源還頓）。
        var gate = new FrameRateGate();
        long lastDecisionMs = 0;
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

                FramesRead++;

var nowMs = sw.ElapsedMilliseconds;
                // 令牌桶自己處理「第一張一定出畫」（起始令牌是滿的），所以不必再為首幀開特例；
                // 丟棄一律發生在讀取之後：管線永遠被抽乾，延遲才不會隨時間累積。
                var elapsed = TimeSpan.FromMilliseconds(nowMs - lastDecisionMs);
                lastDecisionMs = nowMs;
                if (!gate.Admit(elapsed, MaxFramesPerSecond))
                {
                    FramesDropped++;
                    continue;
                }
                FramesEmitted++;
                FrameSize = (width, height);
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

    /// <summary><c>-progress</c> 寫出的 key=value 行（不是診斷訊息）。</summary>
    /// <remarks>
    /// 只認已知的鍵：誤判一般診斷訊息為進度會讓它從 <see cref="LastDiagnostic"/> 消失，
    /// 而那正是操作員唯一看得到的失敗原因。
    /// </remarks>
    private static bool IsProgressLine(string line)
    {
        foreach (var key in ProgressKeys)
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] ProgressKeys =
    [
        "frame=", "fps=", "stream_0_0_q=", "bitrate=", "total_size=", "out_time_us=", "out_time_ms=",
        "out_time=", "dup_frames=", "drop_frames=", "speed=", "progress=",
    ];

    /// <summary>
    /// 從 <c>-progress</c> 的 <c>out_time_us</c> 更新 <see cref="StreamBacklog"/>。
    /// </summary>
    /// <remarks>
    /// ffmpeg 會把輸出時間歸零（實測 <c>-copyts</c> 也一樣），所以只能比較「變化量」：
    /// 以第一筆回報為錨，之後「牆鐘走了多久」減「輸出時間走了多久」就是管線積了多少影格。
    /// 因為參數帶了 <c>-use_wallclock_as_timestamps</c>，輸出時間的推進就是影格的**到達**時間，
    /// 攝影機自己的時鐘快慢不會被誤算成落後。
    /// </remarks>
    private void ParseProgress(string line)
    {
        const string Prefix = "out_time_us=";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        var text = line[Prefix.Length..].Trim();
        if (text.Length == 0 || text == "N/A"
            || !long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var outUs)
            || outUs <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_progressAtUtc == DateTime.MinValue)
        {
            // 錨點：這一刻的落後量定義為 0（起點無法得知，也不該假裝知道）。
            _progressAnchorUtc = now;
            _progressAnchorOutUs = outUs;
            _progressAtUtc = now;
            StreamBacklog = TimeSpan.Zero;
            return;
        }

        var wallDelta = (now - _progressAnchorUtc).TotalMilliseconds;
        var outputDelta = (outUs - _progressAnchorOutUs) / 1000.0;
        var backlog = wallDelta - outputDelta;

        // 合理區間：0 到 10 分鐘。負值（回報順序或時鐘抖動）與爆量都歸零，
        // 免得畫面上出現一個操作員無法解釋的數字。
        StreamBacklog = backlog is > 0 and < 600_000
            ? TimeSpan.FromMilliseconds(backlog)
            : TimeSpan.Zero;
        _progressAtUtc = now;
    }

    /// <summary>落後量的錨點（第一筆進度回報的牆鐘與輸出時間）。</summary>
    private DateTime _progressAnchorUtc = DateTime.MinValue;
    private long _progressAnchorOutUs;

    /// <summary>
    /// 目前可用的落後值：進度回報中斷超過 10 秒就不再宣稱（畫面停住時舊數字會騙人）。
    /// </summary>
    public TimeSpan? CurrentStreamBacklog =>
        _progressAtUtc != DateTime.MinValue && DateTime.UtcNow - _progressAtUtc <= TimeSpan.FromSeconds(10)
            ? StreamBacklog
            : null;

    /// <summary>最近一次收到進度回報的時刻（用來判斷落後值是否已經過期）。</summary>
    private DateTime _progressAtUtc = DateTime.MinValue;

    /// <summary>
    /// 以 ffprobe 取得解析度與音訊編碼（逾時或取消會擲出，讓重連迴圈接手）。
    /// </summary>
    /// <remarks>
    /// 以前只回傳解析度，把 <c>AudioCodec</c> 丟掉；收音要判斷「這台有沒有麥克風」就得多探測
    /// 一次，等於每次連線多開一個 ffprobe——同一份結果一起傳回來才不用重複探。
    /// </remarks>
    private async Task<StreamProbeInfo> ProbeAsync(CancellationToken token)
    {
// 明確傳入逾時與取消權杖，讓 StreamProbe 得以終止 ffprobe 進程；外層 WaitAsync 僅作保險。
        var info = await Task
            .Run(() => StreamProbe.Probe(RtspUrl, _ffprobe, ProbeTimeout, token), token)
            .WaitAsync(ProbeTimeout + TimeSpan.FromSeconds(2), token);
        return info;
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
