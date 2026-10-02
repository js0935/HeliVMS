namespace HeliVMS.Rtc;

/// <summary>
/// 啟動 publisher 的委派：拿到已備好的 publisher 與 RTP 目標埠後啟動 ffmpeg。
/// </summary>
/// <remarks>
/// 抽成委派的理由只有一個——測試時不能真的去拉攝影機與 ffmpeg。
/// 回傳非 <c>null</c> 表示啟動失敗，端點層據此回 502 並附上遮蔽過的原因。
/// </remarks>
public delegate Task<PublisherStartException?> PublisherStarter(
    LivePublisher publisher,
    System.Net.IPEndPoint rtpTarget,
    ChannelSource source,
    CancellationToken token);

/// <summary>
/// 一條通道的執行期狀態：一個 ingest socket + 一個 ffmpeg publisher + 觀看者集合。
/// <para>
/// 刻意做成<b>每通道一個</b>的獨立物件而非散在字典裡的欄位：publisher 的啟動是
/// 非同步且需要等待就緒的，這種狀態機放在共用的類別裡必然出現「A 通道等 B 通道
/// 的鎖」這種難查的問題。
/// </para>
/// </summary>
internal sealed class ChannelRuntime : IAsyncDisposable
{
    private readonly int _channelId;
    private readonly WhepOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly HashSet<string> _attached = [];
    private readonly Action<int, RtpHeader, ReadOnlyMemory<byte>> _sink;
    private readonly Func<long> _clock;
    private RtpIngest? _ingest;
    private LivePublisher? _publisher;
    private TaskCompletionSource<bool>? _firstPacket;
    private long _lastViewerTicks;
    private bool _disposed;

    /// <summary>建立通道執行期。</summary>
    /// <param name="channelId">通道 ID。</param>
    /// <param name="options">設定。</param>
    /// <param name="sink">收到 RTP 封包時的回呼（轉發給觀看者）。</param>
    /// <param name="clock">
    /// 取「現在」的 UTC ticks。<b>必須與呼叫 <see cref="Attach"/>／<see cref="Detach"/> 時用的是同一個時鐘</b>：
    /// 閒置判斷是拿「現在」減去 <c>_lastViewerTicks</c>，若這裡偷偷用系統時間而那邊用注入時鐘，
    /// 測試會得到一個差了好幾十年的區間，於是閒置回收的行為變成無法驗證。
    /// </param>
    public ChannelRuntime(
        int channelId,
        WhepOptions options,
        Action<int, RtpHeader, ReadOnlyMemory<byte>> sink,
        Func<long>? clock = null)
    {
        _channelId = channelId;
        _options = options;
        _sink = sink;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>
    /// publisher 現況。
    /// <para>
    /// 刻意<b>由本類別維護</b>而不是直接看 <see cref="LivePublisher.State"/>：publisher
    /// 的狀態機是 ffmpeg 專屬的細節，而這裡要回答的是「這條通道有沒有在發佈」。
    /// 把兩者綁死會讓協調層無法在沒有 ffmpeg 的環境下測試。
    /// </para>
    /// </summary>
    public PublisherState PublisherState
    {
        get
        {
            if (_publisher is null) return PublisherState.Stopped;

            // ffmpeg 真的退出是唯一的故障來源，交由 publisher 回報。
            if (_publisher.State == PublisherState.Faulted) return PublisherState.Faulted;

            return _firstPacketSeen ? PublisherState.Streaming : PublisherState.Starting;
        }
    }

    /// <summary>是否已收到過 RTP 封包。</summary>
    private volatile bool _firstPacketSeen;

    /// <summary>已收到的 RTP 封包數。</summary>
    public long PacketsReceived => _ingest?.PacketCount ?? 0;

    /// <summary>publisher 的錯誤描述（已遮蔽）；正常時為 <c>null</c>。</summary>
    public string? Problem => PublisherState switch
    {
        PublisherState.Faulted when !string.IsNullOrWhiteSpace(_publisher?.LastError) => _publisher!.LastError,
        PublisherState.Faulted when _publisher?.ExitCode is { } code => $"ffmpeg 已結束（代碼 {code}）。",
        PublisherState.Faulted => "ffmpeg 已結束。",
        _ => null,
    };

    /// <summary>記得有人正在看這條通道。</summary>
    public void Attach(WhepSession session, long nowTicks)
    {
        lock (_attached)
        {
            _attached.Add(session.Id);
        }

        Interlocked.Exchange(ref _lastViewerTicks, nowTicks);
    }

    /// <summary>移除一位觀看者。</summary>
    /// <remarks>
    /// <b>最後一位離開時刻意不把 <c>_lastViewerTicks</c> 推回現在</b>：閒置計時要量的是
    /// 「距離上一次真的有人在看多久」，不是「距離我們處理到這筆移除多久」。
    /// 逾時回收正是反例——會話是在超過 <c>SessionIdleTimeout</c> 之後才被撿走的，
    /// 若 Detach 把時鐘設成回收當下，publisher 的閒置倒數就被重啟，得多等一個
    /// <c>PublisherIdleTimeout</c> 才關得掉。
    /// </remarks>
    public void Detach(string sessionId, long nowTicks)
    {
        lock (_attached)
        {
            _attached.Remove(sessionId);

            // 還有別人在看就重新起算；沒人看就保留「最後一次有人在看」的時間。
            if (_attached.Count > 0) Interlocked.Exchange(ref _lastViewerTicks, nowTicks);
        }
    }

    /// <summary>
    /// 確保 publisher 已啟動且就緒。
    /// <para>
    /// 第一次呼叫會真的啟動 ffmpeg 並<b>等到第一個 RTP 封包</b>才回傳：沒有這一步，
    /// API 端點會在 ffmpeg 還在連線時就回 201，瀏覽器拿到一個不會有畫面的 200。
    /// 後續呼叫則直接共用同一個 publisher——這正是「每通道一次編碼」的重點。
    /// </para>
    /// </summary>
    public async Task EnsurePublisherAsync(
        ChannelSource source,
        PublisherStarter starter,
        CancellationToken token)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChannelRuntime));

        // 快速路徑：已經在串流就不要再搶鎖。
        if (_publisher is not null && PublisherState == PublisherState.Streaming) return;

        await _startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_publisher is not null && PublisherState == PublisherState.Streaming) return;

            if (_publisher is { State: PublisherState.Faulted or PublisherState.Stopped })
            {
                await TeardownAsync().ConfigureAwait(false);
            }

            _ingest = new RtpIngest();
            _ingest.OnPacket = OnPacket;
            _ingest.Start();

            _firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var publisher = new LivePublisher(_options, LiveEncodeOptions.Default);

            // 先掛上再啟動：啟動失敗時 Teardown 才會連 publisher 一起收乾淨。
            _publisher = publisher;

            var failure = await starter(publisher, _ingest.Target, source, token).ConfigureAwait(false);
            if (failure is not null)
            {
                await TeardownAsync().ConfigureAwait(false);
                throw failure;
            }
        }
        finally
        {
            _startGate.Release();
        }

        // 等第一個封包（含逾時）。放在鎖外，避免 30 秒的等待卡住其他觀看者。
        await WaitForFirstPacketAsync(token).ConfigureAwait(false);
    }

    private void OnPacket(RtpHeader header, ReadOnlyMemory<byte> payload)
    {
        _firstPacketSeen = true;
        _publisher?.MarkStreaming();
        _firstPacket?.TrySetResult(true);
        _sink(_channelId, header, payload);
    }

    private async Task WaitForFirstPacketAsync(CancellationToken token)
    {
        var first = _firstPacket;
        if (first is null) return;

        try
        {
            // 逾時有兩種後果要分清楚：ffpeg 自己退出（該回 502 並附上遮蔽過的 stderr），
            // 或只是還沒出畫面（回 504，讓 UI 有機會重試）。
            var completed = await Task.WhenAny(first.Task, Task.Delay(_options.PublisherStartTimeout, token))
                .ConfigureAwait(false);

            if (completed == first.Task)
            {
                await first.Task.ConfigureAwait(false);
                return;
            }

            token.ThrowIfCancellationRequested();

            if (PublisherState == PublisherState.Faulted)
            {
                var reason = string.IsNullOrWhiteSpace(_publisher?.LastError)
                    ? $"ffmpeg 已結束（代碼 {_publisher?.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未知"}）。"
                    : _publisher!.LastError;

                throw new PublisherStartException($"ffmpeg 無法從此攝影機串流：{reason}");
            }

            throw new PublisherStartException(
                $"超過 {_options.PublisherStartTimeout.TotalSeconds:0} 秒仍未收到 RTP 封包。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PublisherStartException("等待第一個 RTP 封包時逾時。");
        }
    }

    /// <summary>沒有觀看者且閒置逾時時關掉 publisher，省下 32 路的無謂 CPU。</summary>
    public async Task StopPublisherIfIdleAsync(TimeSpan idle, CancellationToken token)
    {
        if (_publisher is null) return;

        lock (_attached)
        {
            if (_attached.Count > 0) return;
        }

        if (_clock() - Interlocked.Read(ref _lastViewerTicks) < idle.Ticks) return;

        await _startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_attached)
            {
                if (_attached.Count > 0) return;
            }

            if (_clock() - Interlocked.Read(ref _lastViewerTicks) < idle.Ticks) return;
            await TeardownAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task TeardownAsync()
    {
        if (_ingest is not null)
        {
            _ingest.OnPacket = null;
            await _ingest.DisposeAsync().ConfigureAwait(false);
            _ingest = null;
        }

        if (_publisher is not null)
        {
            await _publisher.DisposeAsync().ConfigureAwait(false);
            _publisher = null;
        }

        _firstPacket = null;
        _firstPacketSeen = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await TeardownAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}
