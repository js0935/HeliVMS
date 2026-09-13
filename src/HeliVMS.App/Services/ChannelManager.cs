using System.Collections.Concurrent;
using System.IO;
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
    private bool _disposed;

    public ChannelManager(SqliteStore store, string recordingsRoot, string snapshotsRoot)
    {
        _store = store;
        _channels = new ChannelRepository(store);
        _recordingsRoot = recordingsRoot;
        _snapshotsRoot = snapshotsRoot;
        _health = new System.Threading.Timer(OnHealthTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>單一頻道畫面抵達。</summary>
    public event EventHandler<(int Cell, VideoFrame Frame)>? FrameArrived;

    /// <summary>單一頻道狀態轉換。</summary>
    public event EventHandler<(int Cell, ChannelInfo Channel, RtspState State)>? StateChanged;

    /// <summary>健康檢查要求重連。</summary>
    public event EventHandler<(int Cell, ChannelInfo Channel)>? HealthRestart;

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

            var session = new ChannelSession(ch.Id, ch.Name, ch.MainStreamUrl, _store, _recordingsRoot, _snapshotsRoot, ch.MotionEnabled);
            var cell = i;
            session.FrameArrived += (_, f) => FrameArrived?.Invoke(this, (cell, f));
            session.StateChanged += (_, st) => StateChanged?.Invoke(this, (cell, ch, st));
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
                await RestartAsync(session);
                if (info is not null)
                {
                    HealthRestart?.Invoke(this, (cell, info));
                }
            }
        }
    }

    private async Task RestartAsync(ChannelSession session)
    {
        await session.Client.StopAsync();
        try
        {
            await session.Client.StartAsync();
        }
        catch (Exception)
        {
            // 重連失敗由下個 tick 再試
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
        foreach (var kv in _sessions)
        {
            kv.Value.Dispose();
        }

        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}