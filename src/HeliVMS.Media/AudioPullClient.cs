using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using HeliVMS.Shared;

namespace HeliVMS.Media;

/// <summary>
/// 從 RTSP 或錄影檔拉出「單聲道 PCM」並以固定大小片段往外送。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端的畫面由 <see cref="RtspClient"/> 自己解碼，但它的參數帶了 <c>-an</c>——攝影機的
/// 麥克風從來沒有被聽過，而 ONVIF 的對講／現場收音是 NVR 最基本的期待。聲音不能跟影格共用
/// 同一個 ffmpeg：一個子行程只有一個 stdout，影格是固定大小的 rawvideo、音訊是連續位元組流，
/// 混在同一條管線兩邊都會錯位。
/// </para>
/// <para>
/// 這裡刻意只做「拉音訊 + 切片」，播放裝置留在 App（<c>WaveOutPlayer</c>），Media 專案才不會
/// 為了聽聲音背上 Windows 專屬 API，測試也能在沒有音效卡的地方跑。
/// </para>
/// </remarks>
public sealed class AudioPullClient : IAsyncDisposable
{
    /// <summary>預設取樣率：語音用途夠清楚，資料量只有 44.1k 立體聲的十分之一。</summary>
    public const int DefaultSampleRate = 16000;

    /// <summary>預設片段大小：16 kHz × 16 bit × 100 ms ＝ 3200 bytes。</summary>
    public const int DefaultChunkBytes = DefaultSampleRate * 2 / 10;

    /// <summary>每個片段代表 100 ms 的聲音（播放端一次寫進裝置的單位）。</summary>
    private const int ChunkMilliseconds = 100;

    private readonly string _ffmpeg;
    private readonly FfmpegProcessRegistry _registry;
    private readonly bool _live;
    private readonly double _seekSeconds;
    private readonly double _durationSeconds;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _process;
    private bool _disposed;

    /// <param name="source">RTSP 位址或錄影檔路徑。</param>
    /// <param name="live">true＝RTSP（帶 <c>-rtsp_transport tcp</c> 且會自動重連）；false＝檔案。</param>
    /// <param name="sampleRate">輸出取樣率；須與播放裝置協商出的值一致，否則聲音會忽快忽慢。</param>
    /// <param name="seekSeconds">檔案來源的起始偏移（段內秒數）；RTSP 忽略。</param>
    /// <param name="durationSeconds">檔案來源的長度上限；0＝播到檔尾。</param>
    /// <param name="ffmpegPath">ffmpeg 執行檔路徑；null＝依 <see cref="FfmpegLocator"/> 解析。</param>
    /// <param name="registry">行程登記簿；null＝全域共用（診斷頁才看得到這條音訊行程）。</param>
    public AudioPullClient(
        string source,
        bool live = true,
        int sampleRate = DefaultSampleRate,
        double seekSeconds = 0,
        double durationSeconds = 0,
        string? ffmpegPath = null,
        FfmpegProcessRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        Source = source;
        _live = live;
        SampleRate = sampleRate;
        _seekSeconds = Math.Max(0, seekSeconds);
        _durationSeconds = Math.Max(0, durationSeconds);
        _ffmpeg = ffmpegPath ?? FfmpegLocator.Ffmpeg;
        _registry = registry ?? FfmpegProcessRegistry.Shared;
    }

    /// <summary>來源（RTSP 位址或檔案路徑）。</summary>
    public string Source { get; }

    /// <summary>輸出取樣率（單聲道、16 bit）。</summary>
    public int SampleRate { get; }

    /// <summary>
    /// 一個片段的位元組數（單聲道 × 16 bit × 100 ms）。
    /// </summary>
    /// <remarks>
    /// 跟著取樣率換算而不是固定 3200 bytes：播放端用 48 kHz 開裝置時，固定 3200 bytes 只代表
    /// 33 ms，等於每秒三十次寫入，佇列與執行緒的排程抖動更容易造成斷音。
    /// </remarks>
    public int ChunkBytes => SampleRate * 2 * ChunkMilliseconds / 1000;

    /// <summary>是否正在拉取。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 最近一次 ffmpeg 的錯誤輸出尾端（帳密已遮蔽），供設定／診斷顯示。
    /// 沒有這一行，「按了沒聲音」就只能靠猜。
    /// </summary>
    public string LastDiagnostic { get; private set; } = string.Empty;

    /// <summary>
    /// 這個來源根本沒有音訊軌（首次嘗試一個片段都沒收到就結束）。
    /// 攝影機只送影像時是常態，不該讓它變成每 5 秒重試一次的空轉行程。
    /// </summary>
    public bool HasNoAudio { get; private set; }

    /// <summary>收到一個 PCM 片段（長度通常等於 100 ms，最後一段可能較短）。</summary>
    public event EventHandler<byte[]>? ChunkReady;

    /// <summary>拉取中止且不會自動重試（來源沒有音訊軌，或發生非預期錯誤）。</summary>
    public event EventHandler<string>? Faulted;

    /// <summary>
    /// 建立 ffmpeg 參數。單聲道、16 bit、無容器（s16le）是刻意的：播放端只要照著
    /// 這個格式開裝置就能直接寫入，不必再解碼或重採樣。
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        string source,
        bool live = true,
        int sampleRate = DefaultSampleRate,
        double seekSeconds = 0,
        double durationSeconds = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        if (live)
        {
            // 與畫面同一種傳輸方式：UDP 在 NAT／多網卡機器上經常只掉音訊，症狀是「有畫面沒聲音」。
            args.Add("-rtsp_transport");
            args.Add("tcp");
            // 不讓 ffmpeg 為了量測而預先緩衝整段：現場收音要的是即時，不是完美。
            args.Add("-fflags");
            args.Add("nobuffer");
        }
        else if (seekSeconds > 0)
        {
            args.Add("-ss");
            args.Add(seekSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(source);

        if (!live && durationSeconds > 0)
        {
            args.Add("-t");
            args.Add(durationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "-vn",
            "-ac", "1",
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-acodec", "pcm_s16le",
            "-f", "s16le",
            "pipe:1",
        ]);

        return args;
    }

    /// <summary>
    /// 把連續位元組流切成固定大小片段。
    /// </summary>
    /// <remarks>
    /// 片段大小是「播放端一次寫進裝置的單位」：太大則停止／切換頻道時要等它播完（聽起來像卡住），
    /// 太小則每秒上百次系統呼叫。100 ms 是兩者的折衷。最後一段可能不滿，照原長度回傳即可。
    /// </remarks>
    public static async IAsyncEnumerable<byte[]> ReadChunksAsync(
        Stream stream,
        int chunkBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 2);

        var buffer = new byte[chunkBytes];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                yield break;
            }

            var filled = read;
            while (filled < chunkBytes)
            {
                var more = await stream
                    .ReadAsync(buffer.AsMemory(filled, chunkBytes - filled), cancellationToken)
                    .ConfigureAwait(false);
                if (more == 0)
                {
                    break;
                }

                filled += more;
            }

            var chunk = new byte[filled];
            Array.Copy(buffer, chunk, filled);
            yield return chunk;

            if (filled < chunkBytes)
            {
                yield break;
            }
        }
    }

    /// <summary>開始拉取（背景迴圈；RTSP 來源會自動重連）。</summary>
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
        _loop = Task.Run(() => RunAsync(token), token);
        return Task.CompletedTask;
    }

    /// <summary>停止拉取並等待迴圈收尾；返回後可以再次 <see cref="StartAsync"/>。</summary>
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
                await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 迴圈未在時限內收尾（行程卡住）；KillProcess 已先中止 ffmpeg。
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var failures = 0;

        while (!token.IsCancellationRequested)
        {
            var chunks = 0;
            try
            {
                await PullOnceAsync(() => chunks++, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastDiagnostic = ex.Message;
            }

            if (!_live)
            {
                // 檔案來源播完就結束：重試只會把剛剛播過的聲音再播一次。
                break;
            }

            if (chunks == 0)
            {
                // 一個片段都沒收到就結束——多半是這支攝影機沒有音訊軌（或連不上）。
                // 繼續每 5 秒重開一個 ffmpeg 只是製造沒有結果的行程；讓呼叫端知道原因，
                // 由使用者再按一次才重試。
                HasNoAudio = true;
                Faulted?.Invoke(this, string.IsNullOrWhiteSpace(LastDiagnostic)
                    ? "no-audio"
                    : LastDiagnostic);
                break;
            }

            failures++;
            try
            {
                await Task.Delay(FfmpegBackoff.DelayFor(failures), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        IsRunning = false;
    }

    private async Task PullOnceAsync(Action onChunk, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in BuildArguments(Source, _live, SampleRate, _seekSeconds, _durationSeconds))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        _process = proc;
        IsRunning = true;

        using var job = _registry.Register(FfmpegJobKind.Decode, "audio:" + RtspUri.Redact(Source));

        // 必須持續排乾 stderr：管線填滿（Windows 約 64 KB）後 ffmpeg 會卡在寫入而不再輸出音訊。
        // 只留尾端幾行供診斷，並遮蔽可能回顯的帳密。
        var stderrTail = new Queue<string>();
        var stderrGate = new object();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (stderrGate)
                    {
                        stderrTail.Enqueue(RtspUri.Redact(line));
                        while (stderrTail.Count > 20)
                        {
                            stderrTail.Dequeue();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 行程結束或管線關閉；診斷內容非必要。
            }
        });

        try
        {
            await foreach (var chunk in ReadChunksAsync(proc.StandardOutput.BaseStream, ChunkBytes, token)
                               .ConfigureAwait(false))
            {
                var handler = ChunkReady;
                if (handler is not null)
                {
                    try
                    {
                        handler(this, chunk);
                    }
                    catch (Exception)
                    {
                        // 播放端例外不得中斷拉取迴圈（否則一次裝置錯誤就讓整條音訊永久停擺）。
                    }
                }

                onChunk();
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
            catch (Exception)
            {
                // 已結束或無權限；不影響收尾。
            }

            await Task.WhenAny(stderrTask, Task.Delay(500)).ConfigureAwait(false);
            lock (stderrGate)
            {
                LastDiagnostic = string.Join(" | ", stderrTail);
            }

            if (ReferenceEquals(_process, proc))
            {
                _process = null;
            }

            IsRunning = false;
        }
    }

    private void KillProcess()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _process;
        }

        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 已結束或無權限。
        }
    }
}
