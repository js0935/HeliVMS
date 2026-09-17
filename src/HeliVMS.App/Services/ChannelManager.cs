using System.Collections.Concurrent;
using System.IO;
using HeliVMS.Alarms;
using HeliVMS.Media;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 頻道工作階段管理器：維護多路監看，每 1 秒健康檢查（畫面包裹逾時即自動重連），
/// 並以事件對外發布聚合狀態（footer 統計）。
/// </summary>
public sealed class ChannelManager : IDisposable
{
    private const int StaleSeconds = 10;
    private readonly ConcurrentDictionary<int, ChannelSession> _sessions = new();
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly System.Threading.Timer _health;
    private readonly OfflineEventTracker _offline;
    private IDetectionEngine? _detection;
    private bool _disposed;

    public ChannelManager(SqliteStore store, string recordingsRoot, string snapshotsRoot, string? modelPath)
    {
        _store = store;
        _channels = new ChannelRepository(store);
        _recordingsRoot = recordingsRoot;
        _snapshotsRoot = snapshotsRoot;
        _offline = new OfflineEventTracker(new AlarmEventRepository(store));
        _offline.CloseOpenAtStartup();
        if (!string.IsNullOrEmpty(modelPath))
        {
            try
            {
                _detection = new OnnxRuntimeCpuEngine(modelPath);
            }
            catch (Exception)
            {
                _detection = null;   // 模型壞檔或架構不符 → 關閉 AI 層
            }
        }

        _health = new System.Threading.Timer(OnHealthTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>已載入元件模型路徑（null 表示未部署）。</summary>
    public string? ModelPath => _detection is OnnxRuntimeCpuEngine e ? e.ModelPath : null;

    /// <summary>單一頻道畫面抵達。</summary>
    public event EventHandler<(int Cell, VideoFrame Frame)>? FrameArrived;

    /// <summary>單一頻道狀態轉換。</summary>
    public event EventHandler<(int Cell, ChannelInfo Channel, RtspState State)>? StateChanged;

    /// <summary>健康檢查要求重連。</summary>
    public event EventHandler<(int Cell, ChannelInfo Channel)>? HealthRestart;

    /// <summary>單一頻道完整 AI 偵測（cell 對應監看格）。</summary>
    public event EventHandler<(int Cell, DetectionsFrame Frame)>? AiDetections;

    /// <summary>單一頻道事件已寫入 alarm_events（通知中心訂閱用）。</summary>
    public event EventHandler<(int Cell, AlarmEventRecord Record)>? AlarmEvent;

    public bool HasActiveSessions { get; private set; }

    /// <summary>依頻道清單連線到前 <paramref name="count"/> 路（自 <paramref name="startIndex"/> 起循環）。</summary>
    public async Task ConnectAsync(IReadOnlyList<ChannelInfo> channels, int startIndex, int count)
    {
        // 重新連線即重建全部工作階段，保持 cell 對應一致
        await DisconnectAllAsync();

        if (channels.Count == 0)
        {
            return;
        }

        for (var i = 0; i < count && channels.Count > 0; i++)
        {
            var ch = channels[(startIndex + i) % channels.Count];

            var session = new ChannelSession(ch.Id, ch.Name, ch.MainStreamUrl, _store, _recordingsRoot, _snapshotsRoot, ch.MotionEnabled, _detection, IsTamperEnabled());
            var cell = i;
            session.FrameArrived += (_, f) => FrameArrived?.Invoke(this, (cell, f));
            session.StateChanged += (_, st) =>
            {
                if (st == RtspState.Reconnecting)
                {
                    _offline.MarkOffline(ch.Id);
                }
                else if (st == RtspState.Streaming)
                {
                    _offline.MarkOnline(ch.Id);
                }

                StateChanged?.Invoke(this, (cell, ch, st));
            };
            session.AiDetections += (_, d) => AiDetections?.Invoke(this, (cell, d));
            session.EventInserted += (_, r) => AlarmEvent?.Invoke(this, (cell, r));
            _sessions[ch.Id] = session;

            await session.StartMonitoringAsync();
        }

        HasActiveSessions = _sessions.Values.Any(s => s.IsMonitoring);
        if (HasActiveSessions)
        {
            _ = _health.Change(1_000, 1_000);
        }
        else
        {
            _ = _health.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>M39：是否啟用遮蔽偵測（app_settings `detect.tamper.enabled`；重新連線時讀取）。</summary>
    private bool IsTamperEnabled()
        => string.Equals(
            new SettingsRepository(_store).Get("detect.tamper.enabled"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>中斷所有監看。</summary>
    public async Task DisconnectAllAsync()
    {
        foreach (var kv in _sessions)
        {
            await TeardownAsync(kv.Value);
        }

        HasActiveSessions = false;
        _health.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>開啟／關閉指定頻道錄影。</summary>
    public async Task SetRecordingAsync(int channelId, bool recording)
    {
        if (_sessions.TryGetValue(channelId, out var session))
        {
            await session.SetRecordingAsync(recording);
        }
    }

    /// <summary>是否有任何頻道正在錄影。</summary>
    public bool AnyRecording() => _sessions.Values.Any(s => s.IsRecording);

    /// <summary>指定頻道是否正在錄影。</summary>
    public bool IsRecording(int channelId) => _sessions.TryGetValue(channelId, out var s) && s.IsRecording;

    /// <summary>M14：套用單一頻道 AI 策略（enabled＝是否推理；intervalMs＝取樣間隔）。</summary>
    public void SetAiPolicy(int channelId, bool enabled, int intervalMs)
    {
        if (_sessions.TryGetValue(channelId, out var session))
        {
            session.ConfigureAi(enabled, intervalMs);
        }
    }

    /// <summary>播放狀態（連線中／流媒體中）。</summary>
    public int CountStreaming()
    {
        var connected = 0;
        foreach (var s in _sessions.Values)
        {
            if (s.IsMonitoring && (s.Client.State == RtspState.Streaming || s.Client.State == RtspState.Connecting))
            {
                connected++;
            }
        }

        return connected;
    }

    private async void OnHealthTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var kv in _sessions)
        {
            var session = kv.Value;
            if (!session.IsMonitoring)
            {
                continue;
            }

            if (session.Client.State == RtspState.Streaming &&
                (DateTime.UtcNow - session.LastFrameUtc).TotalSeconds > StaleSeconds)
            {
                var info = _channels.Get(kv.Key);
                var cell = GetCell(kv.Key);
                _offline.MarkOffline(kv.Key);
                var restarted = await RestartAsync(session);
                if (restarted)
                {
                    _offline.MarkOnline(kv.Key);
                }

                if (info is not null)
                {
                    HealthRestart?.Invoke(this, (cell, info));
                }
            }
        }
    }

    private async Task<bool> RestartAsync(ChannelSession session)
    {
        await session.Client.StopAsync();
        try
        {
            await session.Client.StartAsync();
            session.ResetDetection();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int GetCell(int channelId)
    {
        var cells = _sessions.Keys.OrderBy(k => k).ToList();
        return cells.IndexOf(channelId);
    }

    private async Task TeardownAsync(ChannelSession session)
    {
        await session.StopAsync();
        _sessions.TryRemove(session.ChannelId, out _);
        session.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _health.Dispose();
        _detection?.Dispose();
        foreach (var kv in _sessions)
        {
            kv.Value.Dispose();
        }

        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}