using System.Collections.Concurrent;

namespace HeliVMS.Rtc;

/// <summary>一條通道的即時串流現況（供 UI 顯示）。</summary>
/// <param name="ChannelId">通道 ID。</param>
/// <param name="Publisher">publisher 狀態。</param>
/// <param name="Viewers">目前觀看人數。</param>
/// <param name="PacketsReceived">此 publisher 開啟以來收到的 RTP 封包數。</param>
/// <param name="Problem">publisher 的錯誤描述（已遮蔽憑證）；正常時為 <c>null</c>。</param>
public readonly record struct ChannelLiveStatus(
    int ChannelId,
    PublisherState Publisher,
    int Viewers,
    long PacketsReceived,
    string? Problem);

/// <summary>某通道目前的即時狀態查詢結果。</summary>
/// <param name="ChannelId">通道 ID。</param>
/// <param name="Active">該通道是否正在發佈。</param>
/// <param name="Viewers">觀看人數。</param>
/// <param name="Limit">該通道的觀看上限。</param>
public readonly record struct ChannelLiveView(
    int ChannelId,
    bool Active,
    int Viewers,
    int Limit);

/// <summary>publisher 啟動所需的資訊，由呼叫端（WebApi）提供。</summary>
/// <param name="ChannelId">通道 ID。</param>
/// <param name="RtspUrl">
/// 已含帳密的實際 RTSP 位址（務必經 <c>RtspStreamResolver.Resolve</c> 組合）。
/// </param>
public readonly record struct ChannelSource(int ChannelId, string RtspUrl);

/// <summary>
/// 即時監看協調者：決定什麼時候啟動 ffmpeg、誰能加入、什麼時候回收。
/// <para>
/// 這個型別是 M244 的行為核心，刻意不含 SDP 或網路細節——那些分別在
/// <see cref="WhepPeer"/> 與端點層。它只回答三件事：<b>要不要開 publisher、
/// 這個人能不能看、什麼時候可以關掉</b>。
/// </para>
/// <para>
/// publisher 的生命週期綁在觀看人數上：第一個觀看者進來才啟動 ffmpeg，最後一位
/// 離開後保留 <see cref="WhepOptions.PublisherIdleTimeout"/> 才關。這樣 32 路
/// 不會為了沒人看的頻道白燒 CPU。
/// </para>
/// </summary>
public sealed class LiveStreamService : IAsyncDisposable
{
    private readonly WhepOptions _options;
    private readonly WhepSessionStore _sessions;
    private readonly PublisherStarter _publisherStarter;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<int, ChannelRuntime> _channels = new();
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// 建立服務。
    /// </summary>
    /// <param name="options">設定。</param>
    /// <param name="sessions">會話帳本；可注入以便測試。</param>
    /// <param name="publisherStarter">
    /// 啟動 publisher。回傳非 <c>null</c> 表示啟動失敗（呼叫端據此回 502）。
    /// 抽成委派是為了讓測試不必真的拉 ffmpeg。
    /// </param>
    /// <param name="clock">取「現在」；注入以便測試逾時回收。</param>
    public LiveStreamService(
        WhepOptions options,
        WhepSessionStore sessions,
        PublisherStarter publisherStarter,
        Func<long>? clock = null)
    {
        _options = options;
        _sessions = sessions;
        _publisherStarter = publisherStarter;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>會話帳本；端點層用它查詢與移除。</summary>
    public WhepSessionStore Sessions => _sessions;

    /// <summary>本服務的設定；端點層建立 <see cref="WhepPeer"/> 時需要。</summary>
    public WhepOptions Options => _options;

    /// <summary>
    /// 建立一個觀看會話：必要時啟動 publisher，完成 WHEP 協商並登記。
    /// </summary>
    /// <exception cref="WhepViewerLimitException">超過同時觀看人數上限（呼叫端回 429）。</exception>
    /// <exception cref="WhepNegotiationException">SDP 協商失敗（呼叫端回 400）。</exception>
    /// <exception cref="PublisherStartException">ffmpeg 啟動失敗（呼叫端回 502）。</exception>
    public async Task<(WhepSession Session, WhepAnswer Answer)> OpenAsync(
        ChannelSource source,
        string offerSdp,
        Func<IWhepPeer> peerFactory,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.RtspUrl);
        ArgumentNullException.ThrowIfNull(peerFactory);

        if (_disposed) throw new ObjectDisposedException(nameof(LiveStreamService));

        var peer = peerFactory();
        WhepSession? session = null;
        var registered = false;
        try
        {
            // 先協商再佔配額：協商失敗的觀看者不該佔掉寶貴的名額。
            var answer = await peer.NegotiateAsync(offerSdp, token).ConfigureAwait(false);

            var runtime = _channels.GetOrAdd(
                source.ChannelId,
                id => new ChannelRuntime(id, _options, Forward));

            await runtime.EnsurePublisherAsync(source, _publisherStarter, token).ConfigureAwait(false);

            session = new WhepSession(Guid.NewGuid().ToString("n"), source.ChannelId, peer, _clock());
            _sessions.Add(session, _options.MaxViewersPerChannel);
            registered = true;

            runtime.Attach(session, _clock());
            return (session, answer);
        }
        catch
        {
            // 協商成功但配額被拒時，peer 必須關掉，否則 SIPSorcery 的 socket 會洩漏。
            //
            // 注意 registered 這個旗標：配額被拒時會話「從未被加入帳本」，若照著
            // 「session 非 null 就 Remove」的寫法，Remove 會移除不到東西而 peer 永遠不被釋放，
            // 每多一位被擋下的觀看者就洩漏一條 DTLS 連線。
            if (registered && session is not null)
            {
                _sessions.Remove(session.Id, out _);
                session.Peer.Dispose();
            }
            else
            {
                peer.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// 轉送一個 RTP 封包給所有正在看這條通道的人。
    /// </summary>
    /// <remarks>
    /// 這是「不解碼」的具體體現：迴圈內只做標頭重建與 SRTP 加密，
    /// 沒有任何 H.264 解析。成本隨觀看人數線性成長，但每個觀看者都很便宜。
    /// </remarks>
    public void Forward(int channelId, RtpHeader header, ReadOnlyMemory<byte> payload)
    {
        if (_sessions.CountFor(channelId) == 0) return;

        foreach (var session in _sessions.ListFor(channelId))
        {
            session.Touch(_clock());
            try
            {
                session.Peer.Forward(header, payload.Span);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 這一位觀看者的連線已死。已在別處被移除時不需動作。
            }
        }
    }

    /// <summary>
    /// 關閉一個觀看會話。
    /// <para>
    /// 回傳被關掉的那個會話（而不是單純的 bool）：呼叫端需要它的 <c>ChannelId</c> 才能寫稽核。
    /// 若改成在關閉「之後」再查，會話已經從帳本移除，查到的永遠是 null——稽核看起來寫了，
    /// 實際上一筆都沒進去，而且不會有任何錯誤。
    /// </para>
    /// </summary>
    public WhepSession? Close(string? sessionId)
    {
        if (!_sessions.Remove(sessionId, out var removed) || removed is null) return null;

        removed.Peer.Close("viewer closed");
        removed.Dispose();

        if (_channels.TryGetValue(removed.ChannelId, out var runtime))
        {
            runtime.Detach(removed.Id, _clock());
        }

        return removed;
    }

    /// <summary>查詢某通道的即時狀態。</summary>
    public ChannelLiveView Describe(int channelId)
    {
        var active = _channels.TryGetValue(channelId, out var runtime)
            && runtime.PublisherState != PublisherState.Stopped;

        return new ChannelLiveView(channelId, active, _sessions.CountFor(channelId), _options.MaxViewersPerChannel);
    }

    /// <summary>
    /// 週期性維護：回收逾時的觀看會話，並關掉閒置過久的 publisher。
    /// </summary>
    /// <remarks>
    /// 刻意做成可重入的單一閘門（<c>SweepGate</c>）而非 timer：排程由 WebApi 的
    /// HostedService 決定，而這裡保證不會有兩個迴圈同時動同一條 publisher。
    /// </remarks>
    public async Task<int> MaintainAsync(CancellationToken token)
    {
        if (!await _sweepGate.WaitAsync(0, token).ConfigureAwait(false)) return 0;
        try
        {
            var reaped = 0;
            foreach (var session in _sessions.SweepIdle(_options.SessionIdleTimeout))
            {
                session.Peer.Close("idle timeout");
                session.Dispose();
                reaped++;

                // 一定要 Detach：runtime 是用「掛著的會話數」判斷該不該關 publisher。
                // 只清帳本不清 runtime，該通道會永遠被當成「還有人在看」，
                // ffmpeg 於是永不退出——逾時回收反而製造了永久佔用的 publisher。
                if (_channels.TryGetValue(session.ChannelId, out var runtime))
                {
                    runtime.Detach(session.Id, _clock());
                }
            }

            foreach (var (channelId, runtime) in _channels)
            {
                if (_sessions.CountFor(channelId) == 0)
                {
                    await runtime.StopPublisherIfIdleAsync(_options.PublisherIdleTimeout, token).ConfigureAwait(false);
                }
            }

            // 已完全沒有觀看者的通道把 runtime 丟掉，避免 32 路的字典永久留著。
            foreach (var channelId in _channels.Keys.ToList())
            {
                if (_sessions.CountFor(channelId) == 0
                    && (!_channels.TryGetValue(channelId, out var r) || r.PublisherState == PublisherState.Stopped))
                {
                    if (_channels.TryRemove(channelId, out var removed)) await removed.DisposeAsync().ConfigureAwait(false);
                }
            }

            return reaped;
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var session in _sessions.DrainAll())
        {
            session.Peer.Close("server shutting down");
            session.Dispose();
        }

        foreach (var runtime in _channels.Values)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }

        _channels.Clear();
        _sweepGate.Dispose();
    }
}
