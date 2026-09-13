using System.Collections.Concurrent;
using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// AI 偵測高頻寫入管線：佇列接收（非阻塞）＋定時批次 flush 至 detections 表，
/// 降低 UI/推理執行緒與 SQLite 之間的耦合與鎖競爭。
/// </summary>
public sealed class DetectionWriter : IDisposable
{
    private readonly DetectionRepository _repo;
    private readonly ConcurrentQueue<DetectionRecord> _queue = new();
    private readonly System.Threading.Timer _timer;
    private readonly TimeSpan _flushInterval;
    private readonly int _batchSize;
    private bool _disposed;

    public DetectionWriter(SqliteStore store, TimeSpan? flushInterval = null, int batchSize = 256)
    {
        _repo = new DetectionRepository(store);
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(2);
        _batchSize = batchSize;
        _timer = new System.Threading.Timer(_ => Flush(), null, _flushInterval, _flushInterval);
    }

    public long PendingCount => _queue.Count;

    /// <summary>將一筆偵測放入佇列（執行緒安全；呼叫端勿在 UI 執行緒做重工作）。</summary>
    public void Enqueue(DetectionRecord record)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(record);
    }

    /// <summary>同幀多筆批次入佇列。</summary>
    public void EnqueueRange(IEnumerable<DetectionRecord> records)
    {
        foreach (var r in records)
        {
            Enqueue(r);
        }
    }

    /// <summary>立即寫空佇列（斷言/交付前呼叫；批次上限控制單一交易大小）。</summary>
    public void Flush()
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        var batch = new List<DetectionRecord>(_batchSize);
        while (batch.Count < _batchSize && _queue.TryDequeue(out var r))
        {
            batch.Add(r);
        }

        _repo.AddBatch(batch);
        if (!_queue.IsEmpty)
        {
            Flush();
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
        Flush();
    }
}