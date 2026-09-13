using System.Collections.Concurrent;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// AI 物件偵測事件引擎（§5.2/5.3）：以固定頻率（400ms）抽取監看幀送入獨立推理佇列
/// （非同步 worker，絕不阻塞監看/錄影線程）；偵測到人/車即開啟 AI 事件窗口，
/// 目標消失超過冷卻窗口後收合為單一事件（ai_person / ai_vehicle）並儲存觸發幀快照。
/// </summary>
public sealed class AiEventEngine : IDisposable
{
    private const long IdleCooldownMs = 900;
    private const long MinAiEventMs = 600;

    private readonly int _channelId;
    private readonly AlarmEventRepository _repo;
    private readonly string _snapshotDir;
    private readonly IDetectionEngine _engine;
    private readonly float _minConfidence;
    private readonly FrameDifferenceMotionDetector _motion = new(0.5);

    private ConcurrentQueue<VideoFrame> _queue = new();
    private readonly object _gate = new();
    private volatile bool _running;
    private volatile bool _enabled = true;
    private int _sampleIntervalMs = 400;
    private Task _worker = Task.CompletedTask;
    private long _lastSampleMs;

    private bool _aiWindow;
    private DateTime _aiStartUtc;
    private DateTime _lastActiveUtc;
    private string? _aiEventType;
    private string _aiDetail = "";
    private float _aiPeakConf;
    private byte[] _snapshotPixels = [];
    private int _snapshotWidth;
    private int _snapshotHeight;

    public AiEventEngine(int channelId, AlarmEventRepository repo, string snapshotDir, IDetectionEngine engine, double motionSensitivity = 0.25, float minConfidence = 0.5f)
    {
        _channelId = channelId;
        _repo = repo;
        _snapshotDir = snapshotDir;
        _engine = engine;
        _minConfidence = minConfidence;
        _motion = new FrameDifferenceMotionDetector(motionSensitivity);
    }

    /// <summary>偵測到目標物件事件（供 UI 等訂閱）。</summary>
    public event EventHandler<Detection>? Detected;

    /// <summary>每幀完整偵測結果（供即時監看疊加）；SnapshotUtc 對應取樣幀時間。</summary>
    public event EventHandler<DetectionsFrame>? DetectionsReady;

    /// <summary>已執行推理幀數（診斷）。</summary>
    public int FramesInferred { get; private set; }

    /// <summary>送入取樣幀數（診斷）。</summary>
    public int FramesFed { get; private set; }

    /// <summary>通過 L0 運動門檻的幀數（診斷）。</summary>
    public int MotionGates { get; private set; }

    /// <summary>最近一次推理耗時 ms（診斷）。</summary>
    public int LastInferenceMs { get; private set; }

    /// <summary>最近一次推理例外（診斷）。</summary>
    public string? LastError { get; private set; }

    /// <summary>推理列長（診斷；正常 ≦1）。</summary>
    public int PendingCount => _queue.Count;

    /// <summary>取樣幀間隔 ms（M14 策略支援：單格 200／2×2 400／多格 800）。</summary>
    public int MinSampleIntervalMs
    {
        get => _sampleIntervalMs;
        set => _sampleIntervalMs = Math.Clamp(value, 100, 5000);
    }

    /// <summary>是否啟用推疊（false＝省 CPU：停取樣與運動門檻，清佇列）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                Reset();
            }
        }
    }

    /// <summary>推送一幀（監看線程呼叫）；按取樣間隔（可依格大小調整）送入推理佇列。全域 maxConcurrency 由 AiConcurrencyScheduler 控管。</summary>
    public void OnFrame(VideoFrame frame)
    {
        if (!_enabled)
        {
            return;
        }

        FramesFed++;
        if (_motion.IsMotion(_motion.Update(frame)))
        {
            MotionGates++;
        }

        var nowMs = System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);
        if (nowMs - _lastSampleMs < _sampleIntervalMs)
        {
            return;
        }

        _lastSampleMs = nowMs;
        if (_queue.Count > 1)
        {
            return;   // 上一個推理尚未消化：丟棄，避免張數堆積
        }

        _queue.Enqueue(new VideoFrame
        {
            Width = frame.Width,
            Height = frame.Height,
            Pixels = (byte[])frame.Pixels.Clone(),
            TimestampUtc = frame.TimestampUtc,
            PtsMs = frame.PtsMs,
        });
        EnsureWorker();
    }

    /// <summary>stop 前收尾：關閉作用中窗口。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            FinalizeWindowIfIdle(force: true);
            DrainAndWait();
        }
    }

    /// <summary>頻道切換／斷線重連：捨棄進行中窗口與佇列。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _running = false;
            _aiWindow = false;
            _queue = new();
            _snapshotPixels = [];
        }
    }

    private void EnsureWorker()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _worker = Task.Run(WorkerLoop);
    }

    private void DrainAndWait()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // worker 異常不致命；重新開始即恢復
        }
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            if (!_queue.TryDequeue(out var frame))
            {
                Thread.Sleep(60);
                continue;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<Detection> dets = [];
                AiConcurrencyScheduler.Shared.RunSync(() =>
                {
                    lock (_gate)
                    {
                        dets = _engine.Run(frame);
                        OnDetections(frame, dets);
                        FinalizeWindowIfIdle(force: false);
                    }
                });

                LastInferenceMs = (int)sw.ElapsedMilliseconds;
                FramesInferred++;
                DetectionsReady?.Invoke(this, new DetectionsFrame(frame.TimestampUtc, dets));
            }
            catch (Exception ex)
            {
                // 單幀推理失敗略過，維持窗口活性
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private void OnDetections(VideoFrame frame, IReadOnlyList<Detection> detections)
    {
        Detection? best = null;
        foreach (var d in detections)
        {
            if (d.Confidence < _minConfidence || EventTypeFor(d.Class) is null)
            {
                continue;
            }

            if (best is null || d.Confidence > best.Confidence)
            {
                best = d;
            }
        }

        if (best is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!_aiWindow)
        {
            _aiWindow = true;
            _aiStartUtc = now;
            _aiEventType = EventTypeFor(best.Class);
            _aiPeakConf = best.Confidence;
            _snapshotPixels = (byte[])frame.Pixels.Clone();
            _snapshotWidth = frame.Width;
            _snapshotHeight = frame.Height;
        }
        else if (best.Confidence > _aiPeakConf)
        {
            _aiPeakConf = best.Confidence;
            _aiEventType = EventTypeFor(best.Class);
        }

        _lastActiveUtc = now;
        _aiDetail = $"{best.Class} conf={best.Confidence:0.00} bbox=({best.X:0.00},{best.Y:0.00},{best.W:0.00},{best.H:0.00})";
        Detected?.Invoke(this, best);
    }

    // 呼叫者須持鎖
    private void FinalizeWindowIfIdle(bool force)
    {
        if (!_aiWindow)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastActiveUtc).TotalMilliseconds < IdleCooldownMs)
        {
            return;
        }

        var end = _lastActiveUtc > _aiStartUtc ? _lastActiveUtc : now;
        var duration = (end - _aiStartUtc).TotalMilliseconds;
        _aiWindow = false;

        if (duration < MinAiEventMs)
        {
            return;
        }

        string? snapshotPath = null;
        if (_snapshotPixels.Length > 0 && _snapshotWidth > 0 && _snapshotHeight > 0)
        {
            try
            {
                Directory.CreateDirectory(_snapshotDir);
                snapshotPath = Path.Combine(_snapshotDir, $"ai-{_channelId}-{_aiStartUtc:HHmmss}.bmp");
                BmpSnapshotWriter.Save(
                    new VideoFrame
                    {
                        Width = _snapshotWidth,
                        Height = _snapshotHeight,
                        Pixels = _snapshotPixels,
                        TimestampUtc = _aiStartUtc,
                        PtsMs = 0,
                    },
                    snapshotPath);
            }
            catch (IOException)
            {
                snapshotPath = null;
            }
            catch (UnauthorizedAccessException)
            {
                snapshotPath = null;
            }
        }

        var id = _repo.Insert(_channelId, _aiEventType ?? "ai_person", _aiStartUtc, snapshotPath, _aiDetail);
        _repo.UpdateEnd(id, end, snapshotPath);
        _snapshotPixels = [];
    }

    private static string? EventTypeFor(string cls) =>
        cls switch
        {
            "person" => "ai_person",
            "car" or "bus" or "truck" or "motorcycle" or "bicycle" => "ai_vehicle",
            _ => null,
        };

    public void Dispose()
    {
        lock (_gate)
        {
            FinalizeWindowIfIdle(force: true);
            _running = false;
        }

        _snapshotPixels = [];
    }
}