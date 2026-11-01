using HeliVMS.Media;

namespace HeliVMS.App.Services;

/// <summary>
/// 把「拉音訊（<see cref="AudioPullClient"/>）」與「播出來（<see cref="WaveOutPlayer"/>）」
/// 接成一條管線；桌面上聽現場收音與回放聲音都走這裡。
/// </summary>
/// <remarks>
/// <para>
/// 取樣率由裝置決定（<see cref="WaveOutPlayer.ProbeSampleRate"/>），再要 ffmpeg 直接輸出那個
/// 取樣率：若兩邊不一致，聲音會忽快忽慢或音調偏移，而畫面完全正常——這種問題在現場
/// 只會被描述成「聲音怪怪的」，很難追。
/// </para>
/// <para>
/// 沒有音訊裝置（遠端桌面、音效卡被獨佔）不算錯誤，<see cref="PlayAsync"/> 直接回 false；
/// 呼叫端把開關切回去即可，不必跳錯誤對話框。
/// </para>
/// </remarks>
public sealed class AudioPlayer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioPullClient? _pull;
    private WaveOutPlayer? _output;
    private int _generation;
    private bool _disposed;

    /// <summary>實際協商出的取樣率；尚未播放過時為 null。</summary>
    public int? SampleRate { get; private set; }

    /// <summary>是否正在播放（有來源且有裝置）。</summary>
    public bool IsPlaying => _output is not null;

    /// <summary>來源沒有音訊軌（攝影機只送影像）；呼叫端不必再重試。</summary>
    public bool HasNoAudio { get; private set; }

    /// <summary>最近一次失敗的原因（裝置不存在、ffmpeg 錯誤）；成功時為 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>拉取端中止（沒有音訊軌或非預期錯誤）；已在 UI 執行緒上。</summary>
    public event EventHandler<string>? Faulted;

    /// <summary>
    /// 開始播放一個來源（先停掉上一條管線）。
    /// </summary>
    /// <param name="source">RTSP 位址（主碼流）或錄影檔路徑。</param>
    /// <param name="live">true＝即時串流（會自動重連）；false＝檔案（播完即止）。</param>
    /// <param name="seekSeconds">檔案來源的起始偏移；即時來源忽略。</param>
    /// <param name="durationSeconds">檔案來源的長度上限；0＝播到檔尾。</param>
    /// <param name="cancellationToken">在建立管線前取消（例如操作員已切走）。</param>
    /// <returns>true＝已開始播放；false＝沒有音訊裝置或來源無法使用。</returns>
    public async Task<bool> PlayAsync(
        string source,
        bool live,
        double seekSeconds = 0,
        double durationSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await StopAsync().ConfigureAwait(true);

        if (WaveOutPlayer.ProbeSampleRate() is not { } rate)
        {
            LastError = "no-audio-device";
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var output = new WaveOutPlayer(rate);
        output.Start();
        if (!output.IsAvailable)
        {
            LastError = output.LastError ?? "no-audio-device";
            output.Dispose();
            return false;
        }

        var pull = new AudioPullClient(source, live, rate, seekSeconds, durationSeconds);
        var generation = Interlocked.Increment(ref _generation);
        pull.ChunkReady += (_, chunk) =>
        {
            // 換來源／停止後，舊管線可能還有一兩個片段在路上：丟掉，否則會播出上一個頻道的聲音。
            if (Volatile.Read(ref _generation) == generation)
            {
                output.Queue.Enqueue(chunk);
            }
        };
        pull.Faulted += (_, reason) =>
        {
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            HasNoAudio = pull.HasNoAudio;
            LastError = reason;
            Faulted?.Invoke(this, reason);
        };

        SampleRate = rate;
        _output = output;
        _pull = pull;

        await pull.StartAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>停止播放並釋放子行程與裝置；返回後可以再次 <see cref="PlayAsync"/>。</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);

        var pull = _pull;
        var output = _output;
        _pull = null;
        _output = null;

        if (pull is null && output is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (pull is not null)
            {
                await pull.StopAsync().ConfigureAwait(false);
                await pull.DisposeAsync().ConfigureAwait(false);
            }

            if (output is not null)
            {
                // 停止會 Join 播放執行緒（上限 2 秒）；丟到執行緒池，UI 執行緒才不會被卡住。
                await Task.Run(output.Dispose).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
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
        _gate.Dispose();
    }
}
