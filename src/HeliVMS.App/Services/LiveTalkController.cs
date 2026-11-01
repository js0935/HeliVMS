using HeliVMS.Recording.Talk;

namespace HeliVMS.App.Services;

/// <summary>
/// 監看牆的對講：把主機麥克風的聲音即時送到某台攝影機的喇叭（同一時間只有一台）。
/// </summary>
/// <remarks>
/// 與「聽現場聲音」（<see cref="LiveAudioController"/>）是兩個方向、兩個控制器：收音是
/// 攝影機→主機，對講是主機→攝影機。同樣採獨佔——同時對兩台喊話，操作員與現場都
/// 分不清誰在說話；而且便宜的攝影機一次只開得起一條回傳通道。
/// </remarks>
public sealed class LiveTalkController : IAsyncDisposable
{
    private readonly TalkService _talk = new();
    private readonly Func<int, TalkRequest?> _requestFor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TalkSession? _session;
    private CancellationTokenSource? _cts;
    private int? _channelId;
    private int _generation;
    private bool _disposed;

    /// <param name="requestFor">頻道 → 對講請求；null＝這台不能對講（未開啟、沒有碼流、沒選麥克風）。</param>
    public LiveTalkController(Func<int, TalkRequest?> requestFor)
        => _requestFor = requestFor ?? throw new ArgumentNullException(nameof(requestFor));

    /// <summary>目前正在對講的頻道；沒有時為 null。</summary>
    public int? ChannelId => _channelId;

    /// <summary>是否正在對講。</summary>
    public bool IsTalking => _channelId is not null;

    /// <summary>最近一次失敗的原因（供提示列顯示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>對講狀態變了（行程自己結束、或被停止）；UI 用它重畫圖示。</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 點麥克風：在講就停止；沒講就開始（並把別台停止）。
    /// </summary>
    /// <returns>呼叫後是否正在對講這個頻道。</returns>
    public async Task<bool> ToggleAsync(int channelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_channelId == channelId)
        {
            await StopAsync().ConfigureAwait(true);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        await StopAsync().ConfigureAwait(true);

        var request = _requestFor(channelId);
        if (request is null)
        {
            LastError = "talk-not-enabled";
            return false;
        }

        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var session = _talk.Start(request);
            var cts = new CancellationTokenSource();
            _session = session;
            _cts = cts;
            _channelId = channelId;
            LastError = null;

            // 行程自己結束（攝影機把回傳通道關掉、或 ffmpeg 失敗）也要把狀態收乾淨，
            // 否則圖示會停在「正在對講」而實際上早就沒聲音了。
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 正常停止（取消）或行程已被砍；狀態以下面的清理為準。
                }

                if (Volatile.Read(ref _generation) != generation)
                {
                    return;
                }

                await StopAsync().ConfigureAwait(false);
                StateChanged?.Invoke(this, EventArgs.Empty);
            });

            return true;
        }
        catch (Exception ex)
        {
            // 找不到供應商、ffmpeg 不存在、dshow 裝置被占用——都只讓這一次失敗。
            LastError = ex.Message;
            _channelId = null;
            return false;
        }
    }

    /// <summary>停止對講（換台、換頁簽、停止監看、關窗都要呼叫）。</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);

        var session = _session;
        var cts = _cts;
        _session = null;
        _cts = null;
        _channelId = null;

        if (session is null && cts is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            // 取消等待會讓 TalkSession 砍掉 ffmpeg 子行程（與對講視窗同一條路徑）。
            cts?.Cancel();
            cts?.Dispose();
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
        _channelId = null;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
