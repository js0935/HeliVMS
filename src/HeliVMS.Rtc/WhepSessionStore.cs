namespace HeliVMS.Rtc;

/// <summary>一個觀看者的一次即時監看會話。</summary>
public sealed class WhepSession : IDisposable
{
    private long _lastActivityTicks;

    public WhepSession(string id, int channelId, IWhepPeer peer, long nowTicks)
    {
        Id = id;
        ChannelId = channelId;
        Peer = peer;
        CreatedAtTicks = nowTicks;
        _lastActivityTicks = nowTicks;
    }

    /// <summary>會話 ID（<c>Location</c> 標頭與 DELETE 端點都用它）。</summary>
    public string Id { get; }

    /// <summary>被觀看的通道 ID。</summary>
    public int ChannelId { get; }

    /// <summary>該觀看者的 WebRTC 連線。</summary>
    public IWhepPeer Peer { get; }

    /// <summary>建立時間（ticks）。</summary>
    public long CreatedAtTicks { get; }

    /// <summary>最後一次有封包轉送出去的時間（ticks）。</summary>
    public long LastActivityTicks => Interlocked.Read(ref _lastActivityTicks);

    /// <summary>記得剛剛有活動，避免把正在收畫面的會話當成逾時回收。</summary>
    public void Touch(long nowTicks) => Interlocked.Exchange(ref _lastActivityTicks, nowTicks);

    public void Dispose() => Peer.Dispose();
}

/// <summary>同時觀看人數超過上限。</summary>
public sealed class WhepViewerLimitException : Exception
{
    public WhepViewerLimitException(int channelId, int limit)
        : base($"通道 {channelId} 的同時觀看人數已達上限 {limit}。")
    {
        ChannelId = channelId;
        Limit = limit;
    }

    /// <summary>被拒絕的通道 ID。</summary>
    public int ChannelId { get; }

    /// <summary>該通道的上限。</summary>
    public int Limit { get; }
}

/// <summary>
/// WHEP 會話帳本。
/// <para>
/// 這不是單純的字典——它同時是<b>資源配額的控制點</b>。上限檢查與加入必須在同一把
/// 鎖內完成：若先查再加入，操作員同時開 20 個分頁就能讓實際觀看數超過上限，
/// 而每個觀看者都代表一份持續的轉送與加密成本。缺口在 §21.5 的併發測試裡被盯著。
/// </para>
/// </summary>
public sealed class WhepSessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, HashSet<WhepSession>> _byChannel = [];
    private readonly Dictionary<string, WhepSession> _byId = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;

    /// <summary>
    /// 建立帳本。
    /// </summary>
    /// <param name="clock">
    /// 取「現在」的方式；注入以便測試逾時回收不必真的等待。
    /// </param>
    public WhepSessionStore(Func<long>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>目前所有會話數。</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byId.Count;
        }
    }

    /// <summary>某通道目前的觀看人數。</summary>
    public int CountFor(int channelId)
    {
        lock (_gate) return _byChannel.TryGetValue(channelId, out var set) ? set.Count : 0;
    }

    /// <summary>
    /// 依配額加入會話；超過 <paramref name="maxViewers"/> 時丟出
    /// <see cref="WhepViewerLimitException"/>，呼叫端據此回 429。
    /// </summary>
    public WhepSession Add(WhepSession session, int maxViewers)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxViewers, 1);

        lock (_gate)
        {
            if (_byId.ContainsKey(session.Id))
            {
                throw new InvalidOperationException($"會話 ID {session.Id} 已存在。");
            }

            if (!_byChannel.TryGetValue(session.ChannelId, out var set))
            {
                set = [];
                _byChannel[session.ChannelId] = set;
            }

            // 配額檢查與寫入刻意在同一個臨界區：見型別說明。
            if (set.Count >= maxViewers)
            {
                if (set.Count == 0) _byChannel.Remove(session.ChannelId);
                throw new WhepViewerLimitException(session.ChannelId, maxViewers);
            }

            set.Add(session);
            _byId[session.Id] = session;
            return session;
        }
    }

    /// <summary>依 ID 取得會話。</summary>
    public WhepSession? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    /// <summary>移除會話；找到時回傳 <c>true</c>。</summary>
    public bool Remove(string? id, out WhepSession? session)
    {
        session = null;
        if (string.IsNullOrEmpty(id)) return false;

        lock (_gate)
        {
            if (!_byId.Remove(id, out var found)) return false;
            session = found;
            if (_byChannel.TryGetValue(found.ChannelId, out var set))
            {
                set.Remove(found);
                if (set.Count == 0) _byChannel.Remove(found.ChannelId);
            }
            return true;
        }
    }

    /// <summary>列出某通道的會話（供狀態查詢使用）。</summary>
    public IReadOnlyList<WhepSession> ListFor(int channelId)
    {
        lock (_gate)
        {
            return _byChannel.TryGetValue(channelId, out var set)
                ? [.. set]
                : [];
        }
    }

    /// <summary>
    /// 移除閒置逾時的會話。
    /// </summary>
    /// <remarks>
    /// 逾時回收是必要的，因為瀏覽器關分頁時不一定送得到 DELETE（網路被拔掉、
    /// 分頁被回收、連線中斷）；沒有這一條，被遺忘的會話會一直佔著上限配額，
    /// 直到沒有人能再看這一路攝影機。
    /// </remarks>
    public IReadOnlyList<WhepSession> SweepIdle(TimeSpan idle)
    {
        var cutoff = _clock() - idle.Ticks;
        List<WhepSession> expired = [];

        lock (_gate)
        {
            foreach (var session in _byId.Values.Where(s => s.LastActivityTicks < cutoff).ToList())
            {
                expired.Add(session);
                _byId.Remove(session.Id);
                if (_byChannel.TryGetValue(session.ChannelId, out var set))
                {
                    set.Remove(session);
                    if (set.Count == 0) _byChannel.Remove(session.ChannelId);
                }
            }
        }

        return expired;
    }

    /// <summary>取出並清空所有會話（應用關閉時用）。</summary>
    public IReadOnlyList<WhepSession> DrainAll()
    {
        lock (_gate)
        {
            var all = _byId.Values.ToList();
            _byId.Clear();
            _byChannel.Clear();
            return all;
        }
    }
}
