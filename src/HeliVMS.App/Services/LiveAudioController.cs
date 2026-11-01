namespace HeliVMS.App.Services;

/// <summary>
/// 監看牆的現場收音：同一時間只有一台在播，點別台就把這台靜音。
/// </summary>
/// <remarks>
/// <para>
/// 「獨佔收音」是刻意的：多台一起播，操作員分不出哪一格在講話，音效卡也只有一條。
/// 主流的做法一樣——Blue Iris／Agent DVR 是逐台的喇叭按鈕、一次一台；Frigate 的即時畫面
/// 也因為瀏覽器限制實質上一次只能出聲一台。
/// </para>
/// <para>
/// 聲音的來源是「這台正在顯示的解碼行程」（<see cref="ChannelSession.AudioReceiver"/>）：
/// 埠在工作階段建立時就綁好、ffmpeg 參數也寫好，所以按喇叭<b>不用重連</b>——白變藍是瞬間的，
/// 畫面不會閃。換台、換分割、停止監看、關窗都要把聲音收回，否則會聽到上一台在說話。
/// </para>
/// </remarks>
public sealed class LiveAudioController : IAsyncDisposable
{
    private readonly Func<int, ChannelSession?> _sessionFor;
    private readonly AudioOutput _output = new();
    private int? _channelId;
    private bool _disposed;

    /// <param name="sessionFor">頻道 → 工作階段；沒有對應時回 null。</param>
    public LiveAudioController(Func<int, ChannelSession?> sessionFor)
        => _sessionFor = sessionFor ?? throw new ArgumentNullException(nameof(sessionFor));

    /// <summary>目前正在播音的頻道；沒有在播時為 null。</summary>
    public int? ChannelId => _channelId;

    /// <summary>是否正在播音。</summary>
    public bool IsPlaying => _channelId is not null;

    /// <summary>這個頻道是否顯示喇叭圖示（有開收音）。</summary>
    public bool CanListen(int channelId) => _sessionFor(channelId)?.AudioReceiver is not null;

    /// <summary>這個頻道是否確定量到音訊軌（null＝還沒探測到，先當成可能有）。</summary>
    public bool? HasAudioTrack(int channelId) => _sessionFor(channelId)?.SourceHasAudio;

    /// <summary>
    /// 點喇叭：在播就靜音；沒播就開始（並把別台靜音）。
    /// </summary>
    /// <returns>呼叫後是否正在播這個頻道。</returns>
    public async Task<bool> ToggleAsync(int channelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_channelId == channelId)
        {
            await StopAsync().ConfigureAwait(true);
            return false;
        }

        await StopAsync().ConfigureAwait(true);

        var session = _sessionFor(channelId);
        if (session?.AudioReceiver is not { } receiver)
        {
            LastError = "listen-not-enabled";
            return false;
        }

        if (session.SourceHasAudio == false)
        {
            // 這台沒有麥克風：不掛音訊輸出（RtspClient.ResolveAudioPort 已擋），
            // 這裡給操作員一個看得到的說明，而不是安靜地沒聲音。
            LastError = "no-audio-track";
            return false;
        }

        try
        {
            var started = await _output.StartAsync(receiver).ConfigureAwait(true);
            _channelId = started ? channelId : null;
            LastError = started ? null : _output.LastError;
            return started;
        }
        catch (ObjectDisposedException)
        {
            // 工作階段在按下與開始之間被釋放（換台、重連、關窗）；當作沒有這次請求。
            _channelId = null;
            LastError = "session-closed";
            return false;
        }
    }

    /// <summary>停止播音；正在播的頻道圖示會退回白色。</summary>
    public Task StopAsync()
    {
        _channelId = null;
        return _output.StopAsync();
    }

    /// <summary>最近一次失敗的原因（供提示列顯示）。</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channelId = null;
        await _output.DisposeAsync().ConfigureAwait(false);
    }
}
