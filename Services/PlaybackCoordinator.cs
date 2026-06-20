using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using HeliVMS.Controls;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

public sealed class PlaybackCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, DecoderSession> _players = new();
    private readonly ConcurrentDictionary<string, long> _lastFrameTimestamps = new();
    private readonly IVideoIndexService _videoIndexService;
    private readonly IRecordingService _recordingService;
    private readonly string _ffmpegPath;

    private PlaybackState _state = PlaybackState.Stopped;
    private readonly object _stateLock = new();
    private double _playbackRate = 1.0;
    private long _lastMasterPosition;
    private long _lastMasterPositionReport;
    private CancellationTokenSource? _seekCts;
    private const int SeekDebounceMs = 150;

    private const long PositionReportIntervalTicks = 100_000;

    private volatile string? _masterCameraId;
    private long _masterSegmentStartOffsetUs;
    private DateTime _currentSegmentEndTime = DateTime.MaxValue;
    private readonly ConcurrentDictionary<string, string> _cameraFilePaths = new();
    private readonly ConcurrentDictionary<string, long> _cameraSegmentStartUs = new();

    public event Action<string, PooledBuffer>? ChannelFrameReady;
    public event Action<string, bool>? ChannelStatusChanged;
    public event Action<long, long>? MasterPositionChanged;
    public event Action<PlaybackState>? StateChanged;
    public event Action? MasterEOFReached;

    public PlaybackState State => _state;
    public int ActiveChannelCount => _players.Count;
    public double PlaybackRate => _playbackRate;

    public PlaybackCoordinator(IVideoIndexService videoIndexService, IRecordingService recordingService)
    {
        _videoIndexService = videoIndexService;
        _recordingService = recordingService;
        _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");
    }

    public async Task<List<CameraRecordingInfo>> QueryAvailableRecordingsAsync(
        IEnumerable<Camera> cameras, DateTime date)
    {
        var from = date.Date;
        var to = from.AddDays(1);
        var cameraList = new List<Camera>();
        foreach (var c in cameras)
        {
            if (c.IsEnabled)
            {
                cameraList.Add(c);
            }
        }

        if (cameraList.Count == 0) return new List<CameraRecordingInfo>();

        var cameraIds = new List<string>(cameraList.Count);
        for (int i = 0; i < cameraList.Count; i++)
        {
            cameraIds.Add(cameraList[i].Id);
        }
        var cameraMap = new Dictionary<string, Camera>(cameraList.Count);
        for (int ci = 0; ci < cameraList.Count; ci++)
            cameraMap[cameraList[ci].Id] = cameraList[ci];

        List<VideoSegment> allSegments;
        try
        {
            allSegments = await _videoIndexService.QuerySegmentsByCamerasAsync(cameraIds, from, to).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] PlaybackCoordinator batch query error: {Msg}", ex.Message);
            return new List<CameraRecordingInfo>();
        }

        var segmentsByCamera = new Dictionary<string, List<VideoSegment>>();
        for (int si = 0; si < allSegments.Count; si++)
        {
            var seg = allSegments[si];
            if (!segmentsByCamera.TryGetValue(seg.CameraId, out var list))
                segmentsByCamera[seg.CameraId] = list = new List<VideoSegment>();
            list.Add(seg);
        }
        var result = new List<CameraRecordingInfo>(segmentsByCamera.Count);

        foreach (var kvp in segmentsByCamera)
        {
            if (!cameraMap.TryGetValue(kvp.Key, out var camera)) continue;

            var segments = kvp.Value;
            double totalDuration = 0;
            long totalSize = 0;
            foreach (var seg in segments)
            {
                if (seg.EndTime.HasValue)
                {
                    totalDuration += (seg.EndTime.Value - seg.StartTime).TotalSeconds;
                }
                totalSize += seg.FileSize;
            }

            result.Add(new CameraRecordingInfo
            {
                CameraId = camera.Id,
                CameraName = camera.Name,
                ChannelNumber = camera.ChannelNumber ?? 0,
                Segments = segments,
                TotalDurationSeconds = totalDuration,
                TotalFileSize = totalSize
            });
        }

        result.Sort((a, b) => a.ChannelNumber.CompareTo(b.ChannelNumber));
        return result;
    }

    private long ComputeFrameThrottleTicks()
    {
        int count = _players.Count;
        if (count <= 1) return 33_000;       // 30 fps
        if (count <= 4) return 66_000;       // 15 fps
        if (count <= 9) return 83_000;       // 12 fps
        if (count <= 16) return 125_000;     // 8 fps
        return 200_000;                       // 5 fps
    }

    private static readonly double _timestampFrequency = Stopwatch.Frequency;

    private DecoderSession CreateService(string cameraId)
    {
        var service = new DecoderSession(cameraId, _ffmpegPath);
        var throttleTicks = ComputeFrameThrottleTicks();
        var freq = _timestampFrequency;

        service.PlaybackStatusChanged += (playing) => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            ChannelStatusChanged?.Invoke(cameraId, playing);
        };
        service.EOFReached += () => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            if (cameraId == _masterCameraId)
                MasterEOFReached?.Invoke();
        };
        service.FrameReady += (pb) => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastFrameTimestamps.TryGetValue(cameraId, out var last))
            {
                var elapsed = (long)((now - last) * 1_000_000.0 / freq);
                if (elapsed > 0 && elapsed < throttleTicks)
                {
                    pb.Dispose(); // throttled frame: return buffer to pool
                    return;
                }
            }
            _lastFrameTimestamps[cameraId] = now;
            ChannelFrameReady?.Invoke(cameraId, pb);
        };
        service.PositionChanged += (pts, duration) => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            if (cameraId == _masterCameraId)
            {
                _lastMasterPosition = pts;
                var now = Stopwatch.GetTimestamp();
                var elapsedUs = (long)((now - _lastMasterPositionReport) * 1_000_000.0 / freq);
                if (elapsedUs >= PositionReportIntervalTicks)
                {
                    _lastMasterPositionReport = now;
                    MasterPositionChanged?.Invoke(pts, duration);
                }
            }
        };

        return service;
    }

    public void LoadCameras(List<CameraPlaybackSlot> slots, DateTime targetTime)
    {
        StopAll();

        foreach (var slot in slots)
        {
            if (slot.Camera is null || slot.Segment is null)
                continue;

            var cameraId = slot.Camera.Id;
            var isMaster = _masterCameraId is null;

            // 所有頻道都 seek 到目標時間（不僅 master），確保多路同步
            long seekUs = 0;
            double? targetFps = null;
            var offset = (targetTime - slot.Segment.StartTime).TotalSeconds;
            if (offset > 0)
                seekUs = (long)(offset * 1_000_000);

            if (!isMaster)
                targetFps = 5;

            var service = CreateService(cameraId);
            service.Open(slot.Segment.FilePath, seekUs, targetFps);

            if (_players.TryAdd(cameraId, service))
            {
                _cameraFilePaths[cameraId] = slot.Segment.FilePath;
                var segStartUs = (long)(slot.Segment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
                _cameraSegmentStartUs[cameraId] = segStartUs;

                if (_masterCameraId is null)
                {
                    _masterCameraId = cameraId;
                    _masterSegmentStartOffsetUs = segStartUs;
                    _currentSegmentEndTime = slot.Segment.EndTime ?? DateTime.MaxValue;
                }
            }
        }

        if (_players.Count > 0)
        {
            _state = PlaybackState.Playing;
            StateChanged?.Invoke(_state);
        }
    }

    public void ReloadMasterCamera(VideoSegment newSegment, DateTime targetTime)
    {
        if (_masterCameraId is null) return;

        var cameraId = _masterCameraId;

        if (_players.TryRemove(cameraId, out var oldSvc))
        {
            oldSvc.Stop();
            oldSvc.Dispose();
        }
        _lastFrameTimestamps.TryRemove(cameraId, out _);

        var service = CreateService(cameraId);

        var offset = (targetTime - newSegment.StartTime).TotalSeconds;
        long seekUs = offset > 0 ? (long)(offset * 1_000_000) : 0;
        service.Open(newSegment.FilePath, seekUs);

        _players.TryAdd(cameraId, service);
        _cameraFilePaths[cameraId] = newSegment.FilePath;
        _cameraSegmentStartUs[cameraId] = (long)(newSegment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
        _lastFrameTimestamps[cameraId] = Stopwatch.GetTimestamp();

        _state = PlaybackState.Playing;
        StateChanged?.Invoke(_state);
    }

    /// <summary>Reload all channels to new segments for synchronous cross-segment playback</summary>
    public void ReloadAllCameras(Dictionary<string, VideoSegment> newSegments, DateTime targetTime)
    {
        foreach (var (camId, segment) in newSegments)
        {
            if (_players.TryGetValue(camId, out var oldSvc))
            {
                oldSvc.Stop();
                oldSvc.Dispose();
            }
            else continue;

            _lastFrameTimestamps.TryRemove(camId, out _);

            var service = CreateService(camId);
            var offset = (targetTime - segment.StartTime).TotalSeconds;
            long seekUs = offset > 0 ? (long)(offset * 1_000_000) : 0;
            service.Open(segment.FilePath, seekUs);

            _players[camId] = service;
            _cameraFilePaths[camId] = segment.FilePath;
            _cameraSegmentStartUs[camId] = (long)(segment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
            _lastFrameTimestamps[camId] = Stopwatch.GetTimestamp();

            if (camId == _masterCameraId)
            {
                _masterSegmentStartOffsetUs = (long)(segment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
                _currentSegmentEndTime = segment.EndTime ?? DateTime.MaxValue;
            }
        }

        if (newSegments.Count > 0)
        {
            _state = PlaybackState.Playing;
            StateChanged?.Invoke(_state);
        }
    }

    private DecoderSession? GetMasterService() =>
        _masterCameraId is not null && _players.TryGetValue(_masterCameraId, out var svc) ? svc : null;

    public void Play()
    {
        lock (_stateLock)
        {
            if (_state != PlaybackState.Paused) { return; }
            foreach (var (_, svc) in _players) svc.Resume();
            _state = PlaybackState.Playing;
            StateChanged?.Invoke(_state);
        }
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (_state != PlaybackState.Playing) { return; }
            foreach (var (_, svc) in _players) svc.Pause();
            _state = PlaybackState.Paused;
            StateChanged?.Invoke(_state);
        }
    }

    public void SeekMaster(long microseconds)
    {
        var oldCts = Interlocked.Exchange(ref _seekCts, new CancellationTokenSource());
        oldCts?.Cancel();
        oldCts?.Dispose();
        var token = _seekCts.Token;
        var capturedPos = microseconds;

        _ = Task.Delay(SeekDebounceMs, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) { return; }

            lock (_stateLock)
            {
                _lastMasterPosition = capturedPos;
                GetMasterService()?.Seek(capturedPos);
            }
            MasterPositionChanged?.Invoke(capturedPos, GetMasterDuration());
        }, token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
    }

    /// <summary>Seek to an absolute position (in microseconds)</summary>
    public void SeekAbsolute(long absoluteMicroseconds)
    {
        _seekCts?.Cancel();
        lock (_stateLock)
        {
            var relativeUs = absoluteMicroseconds - _masterSegmentStartOffsetUs;
            _lastMasterPosition = Math.Max(0, relativeUs);
            GetMasterService()?.Seek(_lastMasterPosition);
        }
        MasterPositionChanged?.Invoke(_lastMasterPosition, GetMasterDuration());
    }

    /// <summary>Seek master immediately to a given position, seeking all channels to maintain sync</summary>
    public void SeekMasterImmediate(long microseconds)
    {
        _seekCts?.Cancel();
        lock (_stateLock)
        {
            _lastMasterPosition = microseconds;
            GetMasterService()?.Seek(microseconds);

            // 同步 seek 所有非 master 頻道至對應時間點
            var masterStart = _masterSegmentStartOffsetUs;
            foreach (var kvp in _players)
            {
                if (kvp.Key == _masterCameraId) continue;
                if (!_cameraSegmentStartUs.TryGetValue(kvp.Key, out var chStart)) continue;
                long absoluteTargetUs = masterStart + microseconds;
                long chRelativeUs = absoluteTargetUs - chStart;
                if (chRelativeUs < 0) chRelativeUs = 0;
                kvp.Value.Seek(chRelativeUs);
            }
        }
        MasterPositionChanged?.Invoke(microseconds, GetMasterDuration());
    }

    public void SeekMasterSeconds(double seconds)
    {
        SeekMaster((long)(seconds * 1_000_000));
    }

    public void SeekToStart()
    {
        SeekMasterImmediate(0);
    }

    public void SeekToEnd()
    {
        long dur = GetMasterDuration();
        SeekMasterImmediate(dur > 0 ? dur - 1_000_000 : 0);
    }

    public void StepForward(int frames = 1)
    {
        lock (_stateLock)
        {
            SeekMasterImmediate(_lastMasterPosition + 33_000 * frames);
        }
    }

    public void StepBackward(int frames = 1)
    {
        lock (_stateLock)
        {
            SeekMasterImmediate(Math.Max(0, _lastMasterPosition - 33_000 * frames));
        }
    }

    public void SetPlaybackRate(double rate)
    {
        _playbackRate = Math.Clamp(rate, 0.25, 32.0);
        foreach (var (_, svc) in _players) svc.SetPlaybackRate(_playbackRate);
    }

    public void StopAll()
    {
        var oldCts = Interlocked.Exchange(ref _seekCts, null);
        oldCts?.Cancel();
        oldCts?.Dispose();
        foreach (var (_, svc) in _players)
        {
            svc.Stop();
            svc.Dispose();
        }
        _players.Clear();
        _lastFrameTimestamps.Clear();
        _masterCameraId = null;
        _masterSegmentStartOffsetUs = 0;
        _lastMasterPosition = 0;
        _lastMasterPositionReport = 0;
        _currentSegmentEndTime = DateTime.MaxValue;
        _cameraFilePaths.Clear();
        _cameraSegmentStartUs.Clear();
        _state = PlaybackState.Stopped;
        StateChanged?.Invoke(_state);
    }

    public long GetMasterDuration()
    {
        return GetMasterService()?.DurationMicroseconds ?? 0;
    }

    public string? GetMasterId() => _masterCameraId;

    public long GetMasterPosition() => _lastMasterPosition;

    public long GetMasterSegmentStartOffsetUs() => _masterSegmentStartOffsetUs;

    public DateTime GetMasterSegmentEndTime() => _currentSegmentEndTime;

    public DateTime GetMasterSegmentStartTime()
    {
        if (_masterCameraId is null) return DateTime.MinValue;
        return new DateTime(_masterSegmentStartOffsetUs * 10); // microseconds → ticks
    }

    public void SetMasterCamera(string cameraId)
    {
        if (!_players.ContainsKey(cameraId)) return;
        _masterCameraId = cameraId;
        if (_cameraSegmentStartUs.TryGetValue(cameraId, out var startUs))
            _masterSegmentStartOffsetUs = startUs;
    }

    public void RemoveCamera(string cameraId)
    {
            if (_players.TryRemove(cameraId, out var svc))
            {
                svc.Stop();
                svc.Dispose();
                if (_masterCameraId == cameraId)
                {
                    _masterCameraId = null;
                    foreach (var k in _players.Keys) { _masterCameraId = k; break; }
                }
            }
    }

    public void Dispose()
    {
        StopAll();
    }
}

public class CameraRecordingInfo
{
    public string CameraId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public int ChannelNumber { get; set; }
    public List<VideoSegment> Segments { get; set; } = new();
    public double TotalDurationSeconds { get; set; }
    public long TotalFileSize { get; set; }
    public string TotalSizeText => FormatSize(TotalFileSize);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class CameraPlaybackSlot
{
    public Camera? Camera { get; set; }
    public VideoSegment? Segment { get; set; }
}

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused
}
