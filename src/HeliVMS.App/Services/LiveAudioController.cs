namespace HeliVMS.App.Services;

/// <summary>
/// 監看牆「聽現場聲音」的控制器：記住目前是哪一台在發聲，並負責換台與停止。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要一個控制器、而不是在視窗裡直接開管線：聲音是「有狀態的資源」——同一時間
/// 只能有一台在播，換台／換分割／停止監看／關窗都要收回。散在事件處理常式裡最容易漏掉的
/// 就是「切到別的頻道但舊的聲音還在」，聽起來像另一台攝影機在說話，而且沒有任何錯誤。
/// </para>
/// <para>
/// 來源固定是主碼流（<c>ChannelSession.RecordingUrl</c>）：次碼流是為了省頻寬給多分割用的，
/// 通常沒有音訊軌或有損壓縮，聽起來會有雜音。
/// </para>
/// </remarks>
public sealed class LiveAudioController : IAsyncDisposable
{
    private readonly Func<int, string?> _mainStreamFor;
    private readonly AudioPlayer _player = new();
    private int? _channelId;
    private bool _disposed;

    /// <param name="mainStreamFor">頻道 → 主碼流 RTSP 位址；沒有對應時回 null。</param>
    public LiveAudioController(Func<int, string?> mainStreamFor)
        => _mainStreamFor = mainStreamFor ?? throw new ArgumentNullException(nameof(mainStreamFor));

    /// <summary>目前正在播音的頻道；沒有在播時為 null。</summary>
    public int? ChannelId => _channelId;

    /// <summary>是否正在播音。</summary>
    public bool IsPlaying => _channelId is not null;

    /// <summary>這台攝影機的碼流沒有音訊軌（或連不上）；呼叫端應停止重試。</summary>
    public bool HasNoAudio => _player.HasNoAudio;

    /// <summary>最近一次失敗的原因。</summary>
    public string? LastError => _player.LastError;

    /// <summary>拉取中止（沒有音訊軌或非預期錯誤）。</summary>
    public event EventHandler<string>? Faulted
    {
        add => _player.Faulted += value;
        remove => _player.Faulted -= value;
    }

    /// <summary>
    /// 讓聲音跟著 <paramref name="channelId"/> 走：同一台不動、換台就切、null 就停。
    /// </summary>
    /// <returns>切換後是否正在播音。</returns>
    public async Task<bool> SyncAsync(int? channelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (channelId == _channelId)
        {
            return IsPlaying;
        }

        if (channelId is not { } id)
        {
            await StopAsync().ConfigureAwait(true);
            return false;
        }

        return await StartAsync(id).ConfigureAwait(true);
    }

    /// <summary>開始播 <paramref name="channelId"/> 的主碼流聲音；回 false＝沒有來源／沒有裝置。</summary>
    public async Task<bool> StartAsync(int channelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var source = _mainStreamFor(channelId);
        if (string.IsNullOrWhiteSpace(source))
        {
            await StopAsync().ConfigureAwait(true);
            return false;
        }

        if (_channelId == channelId && _player.IsPlaying)
        {
            return true;
        }

        await StopAsync().ConfigureAwait(true);

        var started = await _player.PlayAsync(source, live: true).ConfigureAwait(true);
        _channelId = started ? channelId : null;
        return started;
    }

    /// <summary>停止播音（換台、換分割、停止監看、關窗都要呼叫）。</summary>
    public async Task StopAsync()
    {
        _channelId = null;
        await _player.StopAsync().ConfigureAwait(true);
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
        await _player.DisposeAsync().ConfigureAwait(false);
    }
}
