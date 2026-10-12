using System;
using System.Collections.Generic;
using System.Linq;
using HeliVMS.Recording;
using HeliVMS.Shared;

namespace HeliVMS.Agent;

internal sealed class RecordingHostBridge : IRecordingHost, IDisposable
{
    private readonly RecordingScheduler _scheduler;
    private readonly List<RecordingStallEvent> _stalls = new();
    private readonly object _sync = new();
    private string? _lastActivity;

    public RecordingHostBridge(RecordingScheduler scheduler)
    {
        _scheduler = scheduler;
        _scheduler.RecordingStalled += OnStalled;
        _scheduler.Activity += OnActivity;
    }

    public event EventHandler<RecordingStallEvent>? StallOccurred;

    public RecordingStateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new RecordingStateSnapshot(
                _scheduler.ActiveChannelIds.ToArray(),
                _scheduler.LastError,
                _lastActivity,
                _stalls.ToArray(),
                DateTime.UtcNow);
        }
    }

    void IRecordingHost.Start()
    {
        // RecordingScheduler 建構時即啟動內部計時器，並在自己的週期上呼叫 ReconcileAsync，
        // 沒有也不需要額外的 Start。以前這裡用反射找一個不存在的 Start 方法，命中時才是
        // 意外（參數不合就擲例外）；保留空實作只為滿足 IRecordingHost。
    }

    public void Stop() => _scheduler.StopAllAsync().GetAwaiter().GetResult();
    public void Reconcile() => _scheduler.ReconcileAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        _scheduler.RecordingStalled -= OnStalled;
        _scheduler.Activity -= OnActivity;
    }

    private void OnActivity(object? sender, string message)
    {
        lock (_sync)
        {
            _lastActivity = message;
        }
    }

    private void OnStalled(object? sender, RecordingStallInfo e)
    {
        var message = e.Failure;
        var ev = new RecordingStallEvent(e.ChannelId, message ?? string.Empty, e.Failure, DateTime.UtcNow);
        lock (_sync)
        {
            _stalls.Add(ev);
            if (_stalls.Count > 50) _stalls.RemoveAt(0);
        }
        StallOccurred?.Invoke(this, ev);
    }
}
