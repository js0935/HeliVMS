using System.Diagnostics;
using HeliVMS.Alarms;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>單一頻道之生命週期封裝：監看連線（RtspClient）＋選用錄影（SegmentRecorder）＋運動偵測與健康訊息。</summary>
public sealed class ChannelSession : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _recordingsRoot;
    private MotionEventEngine? _motion;
    private AiEventEngine? _ai;
    private TamperEventEngine? _tamper;
    private readonly Stopwatch _motionClock = new();
    private long _lastMotionMs;
    private long _lastTamperMs;
    private SegmentRecorder? _recorder;
    private bool _disposed;

    public ChannelSession(int channelId, string name, string url, SqliteStore store, string recordingsRoot, string snapshotsRoot, bool motionEnabled, IDetectionEngine? detection, bool tamperEnabled = false)
    {
        ChannelId = channelId;
        Name = name;
        Url = url;
        _store = store;
        _recordingsRoot = recordingsRoot;
        Client = new RtspClient(url) { MaxFramesPerSecond = 15 };
        Client.FrameDecoded += OnClientFrame;
        Client.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
        Client.Reconnecting += (_, _) => StateChanged?.Invoke(this, RtspState.Reconnecting);

        if (motionEnabled)
        {
            _motion = new MotionEventEngine(channelId, new AlarmEventRepository(_store), snapshotsRoot);
            _motion.EventInserted += (_, r) => EventInserted?.Invoke(this, r);
            if (detection is not null)
            {
                _ai = new AiEventEngine(channelId, new AlarmEventRepository(_store), snapshotsRoot, detection);
                _ai.EventInserted += (_, r) => EventInserted?.Invoke(this, r);
                _ai.DetectionsReady += (_, d) =>
                {
                    LastAiUtc = d.SnapshotUtc;
                    AiDetections?.Invoke(this, d);
                };
            }
        }

        if (tamperEnabled)
        {
            _tamper = new TamperEventEngine(channelId, new AlarmEventRepository(_store), snapshotsRoot);
            _tamper.EventInserted += (_, r) => EventInserted?.Invoke(this, r);
        }

        _motionClock.Start();
    }

    public int ChannelId { get; }

    public string Name { get; }

    public string Url { get; }

    public RtspClient Client { get; }

    public bool IsMonitoring { get; private set; }

    public bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>最後收到畫面之時間（健康監控用）。</summary>
    public DateTime LastFrameUtc { get; private set; } = DateTime.MinValue;

    public event EventHandler<VideoFrame>? FrameArrived;

    public event EventHandler<RtspState>? StateChanged;

    /// <summary>該頻道事件已寫入 alarm_events（通知中心訂閱用）。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>該頻道每幀完整 AI 偵測（即時監看疊加用）。</summary>
    public event EventHandler<DetectionsFrame>? AiDetections;

    /// <summary>最近一次 AI 推理結果時間（null 表示尚未有）。</summary>
    public DateTime? LastAiUtc { get; private set; }

    /// <summary>M14：套用 AI 每格策略（啟用與否＋取樣間隔 ms）；停用時清窗捨棄待決幀。</summary>
    public void ConfigureAi(bool enabled, int sampleIntervalMs)
    {
        if (_ai is null)
        {
            return;
        }

        _ai.MinSampleIntervalMs = sampleIntervalMs;
        _ai.Enabled = enabled;
    }

    public async Task StartMonitoringAsync()
    {
        if (!IsMonitoring && !_disposed)
        {
            await Client.StartAsync();
            IsMonitoring = !_disposed;
        }
    }

    public async Task SetRecordingAsync(bool recording)
    {
        if (recording && _recorder is null)
        {
            var recorder = new SegmentRecorder(new SegmentRepository(_store));
            await recorder.StartAsync(ChannelId, Url, _recordingsRoot, "main", segmentSeconds: 15);
            _recorder = recorder;
        }
        else if (!recording && _recorder is not null)
        {
            var recorder = _recorder;
            _recorder = null;
            await recorder.StopAsync();
        }
    }

    public async Task StopAsync()
    {
        await SetRecordingAsync(recording: false);
        _motion?.Flush();
        _ai?.Flush();
        _tamper?.Flush();
        if (IsMonitoring)
        {
            await Client.StopAsync();
            IsMonitoring = false;
        }
    }

    /// <summary>健康重連後清除偵測窗口，避免殘留假事件。</summary>
    public void ResetDetection()
    {
        _motion?.Reset();
        _ai?.Reset();
        _tamper?.Reset();
    }

    private void OnClientFrame(object? sender, VideoFrame frame)
    {
        LastFrameUtc = DateTime.UtcNow;
        FrameArrived?.Invoke(this, frame);

        // 運動偵測抽樣：約每 250ms 一幀（低解析度網格成本可忽略）
        if (_motion is not null && _motionClock.ElapsedMilliseconds - _lastMotionMs >= 250)
        {
            _lastMotionMs = _motionClock.ElapsedMilliseconds;
            _motion.OnFrame(frame);
        }

        // 遮蔽偵測抽樣：約每 250ms 一幀（引擎內部需連續 N 次才開窗）
        if (_tamper is not null && _motionClock.ElapsedMilliseconds - _lastTamperMs >= 250)
        {
            _lastTamperMs = _motionClock.ElapsedMilliseconds;
            _tamper.OnFrame(frame);
        }

        // AI 取樣（推理佇列，引擎內部節流 400ms）
        _ai?.OnFrame(frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Client.FrameDecoded -= OnClientFrame;
        }
        catch (InvalidOperationException)
        {
            // 事件解除失敗時忽略
        }

        _motion?.Dispose();
        _ai?.Dispose();
        _tamper?.Dispose();
        _recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}