using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 音訊事件感測 L1 協調器（M78，§5.8）：以單一服務層管理多頻道的
/// <see cref="AudioTriggerEngine"/> 生命週期與啟用狀態（讀取 `channels.audio_enabled`），
/// 支援每頻道門檻覆寫；`Feed` 自動路由至對應引擎，停用頻道不建立引擎、不寫事件。
/// </summary>
public sealed class AudioSensorCoordinator : IDisposable
{
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;
    private readonly IReadOnlyDictionary<int, AudioTriggerConfig> _configOverrides;
    private readonly Dictionary<int, AudioTriggerEngine> _engines = new();
    private readonly object _gate = new();
    private HashSet<int> _enabled = [];
    private bool _disposed;

    public AudioSensorCoordinator(
        SqliteStore store,
        AlarmEventRepository repo,
        IReadOnlyDictionary<int, AudioTriggerConfig>? configOverrides = null)
    {
        _store = store;
        _repo = repo;
        _configOverrides = configOverrides ?? new Dictionary<int, AudioTriggerConfig>();
        Refresh();
    }

    public AudioSensorCoordinator(SqliteStore store, IReadOnlyDictionary<int, AudioTriggerConfig>? configOverrides = null)
        : this(store, new AlarmEventRepository(store), configOverrides)
    {
    }

    /// <summary>目前啟用的頻道 id（升冪）。</summary>
    public IReadOnlyList<int> Channels
    {
        get
        {
            lock (_gate)
            {
                return _enabled.OrderBy(x => x).ToList();
            }
        }
    }

    public bool IsEnabled(int channelId)
    {
        lock (_gate)
        {
            return _enabled.Contains(channelId);
        }
    }

    /// <summary>事件寫入 alarm_events 後引發（自各引擎轉傳；呼叫端勿在 handler 內做重工作）。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>重新讀取 `channels.audio_enabled`，套用新增停用之頻道啟用狀態。</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _enabled = new HashSet<int>(
                new ChannelRepository(_store).List().Where(c => c.AudioEnabled).Select(c => c.Id));
        }
    }

    /// <summary>送入 PCM 樣本路由至對應引擎；停用頻道為無作業並回傳空清單。</summary>
    public IReadOnlyList<AudioBlockResult> Feed(int channelId, IReadOnlyList<short> pcm, DateTime utc)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_enabled.Contains(channelId))
            {
                return Array.Empty<AudioBlockResult>();
            }

            var engine = GetOrCreate(channelId);
            var results = engine.Feed(pcm, utc);
            return results;
        }
    }

    /// <summary>捨棄特定頻道未完成開窗（斷線重連／頻道切換）。</summary>
    public void Reset(int channelId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_engines.TryGetValue(channelId, out var engine))
            {
                engine.Reset();
            }
        }
    }

    /// <summary>捨棄所有頻道之未完成開窗。</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var engine in _engines.Values)
            {
                engine.Reset();
            }
        }
    }

    /// <summary>停止前結算所有頻道之未完成事件（Flush 各引擎）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var engine in _engines.Values)
            {
                engine.Flush();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var engine in _engines.Values)
            {
                engine.Dispose();
            }

            _engines.Clear();
            _disposed = true;
        }
    }

    private AudioTriggerEngine GetOrCreate(int channelId)
    {
        if (_engines.TryGetValue(channelId, out var existing))
        {
            return existing;
        }

        var engine = new AudioTriggerEngine(
            channelId,
            _repo,
            _configOverrides.TryGetValue(channelId, out var cfg) ? cfg : null);
        engine.EventInserted += OnEngineEventInserted;
        _engines.Add(channelId, engine);
        return engine;
    }

    private void OnEngineEventInserted(object? sender, AlarmEventRecord e) => EventInserted?.Invoke(this, e);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioSensorCoordinator));
        }
    }
}