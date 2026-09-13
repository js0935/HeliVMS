using System.Collections.Concurrent;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影排程服務（M10）：依 recording_schedule 規則在背景對未監看中的頻道啟動／停止錄影。
/// 每 <see cref="ReconcileInterval"/> 檢查一次時段（預設 30 秒）；已由即時監看畫面（cell）錄製之頻道不重複開錄，
/// 人工錄影不受影響。錄影參照 <see cref="SegmentRecorder"/>（fMP4＋SHA-256 分段）。
/// </summary>
public sealed class RecordingScheduler : IDisposable
{
    private readonly SqliteStore _store;
    private readonly RecordingScheduleRepository _repo;
    private readonly ChannelRepository _channels;
    private readonly string _recordingsRoot;
    private readonly Func<int, bool>? _isCellRecording;
    private readonly ConcurrentDictionary<int, SegmentRecorder> _active = new();
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public RecordingScheduler(
        SqliteStore store,
        string recordingsRoot,
        Func<int, bool>? isCellRecording = null,
        TimeSpan? reconcileInterval = null)
    {
        _store = store;
        _recordingsRoot = recordingsRoot;
        _isCellRecording = isCellRecording;
        _repo = new RecordingScheduleRepository(store);
        _channels = new ChannelRepository(store);
        ReconcileInterval = reconcileInterval ?? TimeSpan.FromSeconds(30);
        _timer = new System.Threading.Timer(OnTick, null, ReconcileInterval, ReconcileInterval);
    }

    public TimeSpan ReconcileInterval { get; }

    /// <summary>目前由排程啟動錄影之頻道 ID。</summary>
    public IReadOnlyCollection<int> ActiveChannelIds => _active.Keys.ToList();

    /// <summary>最近一次調和錯誤（null 表示無誤）。</summary>
    public string? LastError { get; private set; }

    /// <summary>排程開始／停止錄影之診斷訊息。</summary>
    public event EventHandler<string>? Activity;

    private async void OnTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>調和一次：啟動命中時段的頻道錄影、停止已在時段外者。</summary>
    public async Task ReconcileAsync()
    {
        var now = DateTime.Now;
        var schedules = _repo.List().Where(s => s.Enabled).ToList();
        var channelById = _channels.List().ToDictionary(c => c.Id);

        foreach (var sched in schedules)
        {
            if (!sched.IsActiveAt(now) || _disposed)
            {
                continue;
            }

            if (_active.ContainsKey(sched.ChannelId))
            {
                continue;
            }

            if (_isCellRecording?.Invoke(sched.ChannelId) == true)
            {
                continue;
            }

            if (!channelById.TryGetValue(sched.ChannelId, out var ch))
            {
                continue;
            }

            var recorder = new SegmentRecorder(new SegmentRepository(_store));
            await recorder.StartAsync(ch.Id, ch.MainStreamUrl, _recordingsRoot, "main", segmentSeconds: 15);
            if (_active.TryAdd(sched.ChannelId, recorder))
            {
                Activity?.Invoke(this, $"排程錄影開始：{ch.Name}");
            }
            else
            {
                await recorder.StopAsync();
                await recorder.DisposeAsync();
            }
        }

        foreach (var id in _active.Keys.ToList())
        {
            var stillActive = schedules.Any(s => s.ChannelId == id && s.IsActiveAt(now));
            if (stillActive || _disposed)
            {
                continue;
            }

            if (_active.TryRemove(id, out var recorder))
            {
                await recorder.StopAsync();
                await recorder.DisposeAsync();
                Activity?.Invoke(this, $"排程錄影結束：ch{id:000}");
            }
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var id in _active.Keys.ToList())
        {
            if (_active.TryRemove(id, out var recorder))
            {
                await recorder.StopAsync();
                await recorder.DisposeAsync();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}