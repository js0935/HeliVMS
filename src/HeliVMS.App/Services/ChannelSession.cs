using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>單一頻道之生命週期封裝：監看連線（RtspClient）＋選用錄影（SegmentRecorder）與健康訊息。</summary>
public sealed class ChannelSession : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _recordingsRoot;
    private SegmentRecorder? _recorder;
    private bool _disposed;

    public ChannelSession(int channelId, string name, string url, SqliteStore store, string recordingsRoot)
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
        if (IsMonitoring)
        {
            await Client.StopAsync();
            IsMonitoring = false;
        }
    }

    private void OnClientFrame(object? sender, VideoFrame frame)
    {
        LastFrameUtc = DateTime.UtcNow;
        FrameArrived?.Invoke(this, frame);
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

        _recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}