using System.Diagnostics;
using System.IO;
using HeliVMS.Controls;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// 多路同步播放協調器 — 主從架構 (Master-Slave) 的核心。<br/>
/// Master 頻道驅動時間軸，所有 Slave 頻道基於 PTS 比對自動跟隨：<br/>
/// • PTS 落後 Master 超過 500ms → 丟棄該幀 (不顯示)<br/>
/// • PTS 落後 Master 超過 3s → 自動 Seek 同步（10 秒冷卻）<br/>
/// • 支援兩種解碼器實作：外部 DecoderSession (Named Pipe) 或 InProcessPlaybackDecoder
/// </summary>
public sealed class PlaybackCoordinator(IVideoIndexService videoIndexService, IRecordingService recordingService) : IDisposable {
    private readonly Dictionary<string, IPlaybackDecoder> _players = new();
    private readonly Dictionary<string, long> _lastFrameTimestamps = new();
    private readonly Dictionary<string, long> _lastSlaveResyncTicks = new();
    private readonly Dictionary<string, string> _cameraFilePaths = new();
    private readonly Dictionary<string, long> _cameraSegmentStartUs = new();
    private readonly object _dictLock = new();

    private readonly IVideoIndexService _videoIndexService = videoIndexService;
    private readonly IRecordingService _recordingService = recordingService;
    private readonly string _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");

    private PlaybackState _state = PlaybackState.Stopped;
    private readonly Lock _stateLock = new();
    private double _playbackRate = 1.0;
    /// <summary>Master 頻道目前播放位置（微秒，相對於該頻道 segment 起點）</summary>
    private long _lastMasterPosition;
    private long _lastMasterPositionReport;
    private CancellationTokenSource? _seekCts;
    private const int SeekDebounceMs = 150;

    private const long PositionReportIntervalTicks = 100_000;

    /// <summary>Slave PTS 容許落後上限（500ms）— 超過此值直接丟棄幀</summary>
    private const long MaxPtsDriftUs = 500_000;
    /// <summary>Slave 嚴重落後臨界值（3s）— 觸發自動 Seek 重同步</summary>
    private const long MaxPtsDriftBeforeResyncUs = 3_000_000;

    /// <summary>目前 Master 頻道編號（volatile 確保多執行緒可見性）</summary>
    private volatile string? _masterCameraId;
    /// <summary>Master 頻道的 segment 起點偏移（微秒，從午夜起算），用於計算絕對 PTS</summary>
    private long _masterSegmentStartOffsetUs;
    /// <summary>目前 segment 結束時間，用於 segment 切換</summary>
    private DateTime _currentSegmentEndTime = DateTime.MaxValue;

    /// <summary>某頻道收到新幀（參數：cameraId, PooledBuffer）</summary>
    public event Action<string, PooledBuffer>? ChannelFrameReady;
    /// <summary>某頻道播放/暫停狀態變更</summary>
    public event Action<string, bool>? ChannelStatusChanged;
    /// <summary>Master 頻道位置變更（參數：目前位置微秒, 總時長微秒）</summary>
    public event Action<long, long>? MasterPositionChanged;
    /// <summary>整體播放狀態變更</summary>
    public event Action<PlaybackState>? StateChanged;
    /// <summary>Master 頻道播放完畢</summary>
    public event Action? MasterEOFReached;

    public PlaybackState State => _state;
    public int ActiveChannelCount {
        get { lock (_dictLock) return _players.Count; }
    }
    public double PlaybackRate => _playbackRate;
    /// <summary>設為 true 時使用同程序解碼器 (InProcessPlaybackDecoder)，否則使用外部程序 (DecoderSession)</summary>
    public bool UseInProcessDecoder { get; set; }

    // ─── Per-camera event handler dispatch ────────────────────────────

    private sealed class CameraEventHandlers {
        private readonly PlaybackCoordinator _owner;
        private readonly string _cameraId;
        private readonly long _throttleTicks;

        public CameraEventHandlers(PlaybackCoordinator owner, string cameraId, long throttleTicks) {
            _owner = owner;
            _cameraId = cameraId;
            _throttleTicks = throttleTicks;
        }

        public void OnPlaybackStatusChanged(bool playing) => _owner.HandlePlaybackStatusChanged(_cameraId, playing);
        public void OnEOFReached() => _owner.HandleEOFReached(_cameraId);
        public void OnFrameReady(PooledBuffer pb) => _owner.HandleFrameReady(_cameraId, _throttleTicks, pb);
        public void OnPositionChanged(long pts, long duration) => _owner.HandlePositionChanged(_cameraId, pts, duration);
    }

    private void HandlePlaybackStatusChanged(string cameraId, bool playing) {
        ChannelStatusChanged?.Invoke(cameraId, playing);
    }

    private void HandleEOFReached(string cameraId) {
        if (cameraId == _masterCameraId)
            MasterEOFReached?.Invoke();
    }

    private void HandleFrameReady(string cameraId, long throttleTicks, PooledBuffer pb) {
        var now = Stopwatch.GetTimestamp();

        lock (_dictLock) {
            if (_lastFrameTimestamps.TryGetValue(cameraId, out var last)) {
                var elapsed = (long)((now - last) * 1_000_000.0 / _timestampFrequency);
                if (elapsed > 0 && elapsed < throttleTicks) {
                    pb.Dispose();
                    return;
                }
            }
            _lastFrameTimestamps[cameraId] = now;
        }

        if (cameraId != _masterCameraId && _masterCameraId is not null && _lastMasterPosition > 0) {
            long masterAbsUs, slaveSegStartUs;
            lock (_dictLock) {
                masterAbsUs = _masterSegmentStartOffsetUs + _lastMasterPosition;
                slaveSegStartUs = _cameraSegmentStartUs.TryGetValue(cameraId, out var ss) ? ss : 0L;
            }
            var slaveAbsUs = slaveSegStartUs + pb.PtsMicroseconds;
            var driftUs = slaveAbsUs - masterAbsUs;

            if (driftUs < -MaxPtsDriftUs) {
                pb.Dispose();

                if (driftUs < -MaxPtsDriftBeforeResyncUs) {
                    long targetPtsUs = 0;
                    IPlaybackDecoder? svc = null;
                    lock (_dictLock) {
                        if (_lastSlaveResyncTicks.TryGetValue(cameraId, out var lastResync)) {
                            var sinceResyncUs = (long)((now - lastResync) * 1_000_000.0 / _timestampFrequency);
                            if (sinceResyncUs < 10_000_000) return;
                        }
                        _lastSlaveResyncTicks[cameraId] = now;

                        if (_cameraSegmentStartUs.TryGetValue(cameraId, out var segStart)) {
                            targetPtsUs = Math.Max(0, masterAbsUs - segStart);
                            _players.TryGetValue(cameraId, out svc);
                        }
                    }
                    svc?.Seek(targetPtsUs);
                }
                return;
            }
        }

        ChannelFrameReady?.Invoke(cameraId, pb);
    }

    private void HandlePositionChanged(string cameraId, long pts, long duration) {
        if (cameraId == _masterCameraId) {
            _lastMasterPosition = pts;
            var now = Stopwatch.GetTimestamp();
            var elapsedUs = (long)((now - _lastMasterPositionReport) * 1_000_000.0 / _timestampFrequency);
            if (elapsedUs >= PositionReportIntervalTicks) {
                _lastMasterPositionReport = now;
                MasterPositionChanged?.Invoke(pts, duration);
            }
        }
    }

    // ─── Core methods ────────────────────────────────────────────────

    /// <summary>
    /// 批次查詢多台攝影機在指定日期的錄影記錄，依頻道編號排序。
    /// 使用 IVideoIndexService.QuerySegmentsByCamerasAsync 一次查詢所有攝影機，
    /// 避免逐台查詢造成 N+1 資料庫查詢。
    /// </summary>
    public async Task<List<CameraRecordingInfo>> QueryAvailableRecordingsAsync(
        IEnumerable<Camera> cameras, DateTime date) {
        var from = date.Date;
        var to = from.AddDays(1);
        var cameraList = new List<Camera>();
        foreach (var c in cameras) {
            if (c.IsEnabled) {
                cameraList.Add(c);
            }
        }

        if (cameraList.Count == 0) return [];

        var cameraIds = new List<string>(cameraList.Count);
        for (var i = 0; i < cameraList.Count; i++) {
            cameraIds.Add(cameraList[i].Id);
        }
        var cameraMap = new Dictionary<string, Camera>(cameraList.Count);
        for (var ci = 0; ci < cameraList.Count; ci++)
            cameraMap[cameraList[ci].Id] = cameraList[ci];

        List<VideoSegment> allSegments;
        try {
            allSegments = await _videoIndexService.QuerySegmentsByCamerasAsync(cameraIds, from, to).ConfigureAwait(false);
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] PlaybackCoordinator batch query error: {Msg}", ex.Message);
            return [];
        }

        var segmentsByCamera = new Dictionary<string, List<VideoSegment>>();
        for (var si = 0; si < allSegments.Count; si++) {
            var seg = allSegments[si];
            if (!segmentsByCamera.TryGetValue(seg.CameraId, out var list))
                segmentsByCamera[seg.CameraId] = list = [];
            list.Add(seg);
        }
        var result = new List<CameraRecordingInfo>(segmentsByCamera.Count);

        foreach (var kvp in segmentsByCamera) {
            if (!cameraMap.TryGetValue(kvp.Key, out var camera)) continue;

            var segments = kvp.Value;
            double totalDuration = 0;
            long totalSize = 0;
            foreach (var seg in segments) {
                if (seg.EndTime.HasValue) {
                    totalDuration += (seg.EndTime.Value - seg.StartTime).TotalSeconds;
                }
                totalSize += seg.FileSize;
            }

            result.Add(new CameraRecordingInfo {
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

    /// <summary>
    /// 根據頻道數量計算 Wall-clock 節流間隔（微秒）。頻道越多，每幀間隔越長。<br/>
    /// 1ch=33ms(30fps), ≤4ch=66ms(15fps), ≤9ch=83ms(12fps), ≤16ch=125ms(8fps), &gt;16ch=200ms(5fps)
    /// </summary>
    private long ComputeFrameThrottleTicks() {
        int count;
        lock (_dictLock) { count = _players.Count; }
        if (count <= 1) return 33_000;
        if (count <= 4) return 66_000;
        if (count <= 9) return 83_000;
        if (count <= 16) return 125_000;
        return 200_000;
    }

    private static readonly double _timestampFrequency = Stopwatch.Frequency;

    /// <summary>
    /// 建立並註冊解碼器服務（InProcess 或 DecoderSession）。
    /// 註冊 FrameReady、PositionChanged、EOFReached 等事件處理器，
    /// 包含兩層節流機制：Wall-clock 節流 + PTS 漂移檢查。
    /// </summary>
    private IPlaybackDecoder CreateService(string cameraId) {
        var service = UseInProcessDecoder
            ? new InProcessPlaybackDecoder(cameraId) as IPlaybackDecoder
            : new DecoderSession(cameraId, _ffmpegPath);

        var throttleTicks = ComputeFrameThrottleTicks();
        var handlers = new CameraEventHandlers(this, cameraId, throttleTicks);

        service.PlaybackStatusChanged += handlers.OnPlaybackStatusChanged;
        service.EOFReached += handlers.OnEOFReached;
        service.FrameReady += handlers.OnFrameReady;
        service.PositionChanged += handlers.OnPositionChanged;

        return service;
    }

    /// <summary>
    /// 根據頻道數量設定目標解碼高度，以節省 CPU 及 Pipe 頻寬。<br/>
    /// ≤16ch=原始, 17-24ch=480p, 25-36ch=360p, ≥37ch=240p
    /// </summary>
    private static int ComputeTargetDecodeHeight(int channelCount) {
        if (channelCount <= 16) return 0;       // original resolution
        if (channelCount <= 24) return 480;     // 480p
        if (channelCount <= 36) return 360;     // 360p
        return 240;                              // 240p
    }

    /// <summary>
    /// 載入多頻道播放：建立所有頻道的解碼器，各自 seek 至目標時間。<br/>
    /// 第一個載入的頻道自動成為 Master，其他設為 Slave（目標幀率降為 5fps）。
    /// 若頻道數 >16 則啟用解析度縮放以降低系統負載。
    /// </summary>
    public void LoadCameras(List<CameraPlaybackSlot> slots, DateTime targetTime) {
        StopAll();

        var targetHeight = ComputeTargetDecodeHeight(slots.Count);

        foreach (var slot in slots) {
            if (slot.Camera is null || slot.Segment is null)
                continue;

            var cameraId = slot.Camera.Id;

            bool isMaster;
            lock (_dictLock) { isMaster = _masterCameraId is null; }

            long seekUs = 0;
            double? targetFps = null;
            var offset = (targetTime - slot.Segment.StartTime).TotalSeconds;
            if (offset > 0)
                seekUs = (long)(offset * 1_000_000);

            if (!isMaster)
                targetFps = 5;

            var service = CreateService(cameraId);
            service.Open(slot.Segment.FilePath, seekUs, targetFps, targetHeight);

            var segStartUs = (long)(slot.Segment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);

            lock (_dictLock) {
                if (!_players.ContainsKey(cameraId)) {
                    _players[cameraId] = service;
                    _cameraFilePaths[cameraId] = slot.Segment.FilePath;
                    _cameraSegmentStartUs[cameraId] = segStartUs;

                    if (_masterCameraId is null) {
                        _masterCameraId = cameraId;
                        _masterSegmentStartOffsetUs = segStartUs;
                        _lastMasterPosition = seekUs;
                        _currentSegmentEndTime = slot.Segment.EndTime ?? DateTime.MaxValue;
                    }
                }
            }
        }

        bool anyActive;
        lock (_dictLock) { anyActive = _players.Count > 0; }
        if (anyActive) {
            _state = PlaybackState.Playing;
            StateChanged?.Invoke(_state);
        }
    }

    public void ReloadMasterCamera(VideoSegment newSegment, DateTime targetTime) {
        if (_masterCameraId is null) return;

        var cameraId = _masterCameraId;

        IPlaybackDecoder? oldSvc;
        lock (_dictLock) {
            if (!_players.TryGetValue(cameraId, out oldSvc)) return;
            _players.Remove(cameraId);
        }
        oldSvc.Stop();
        oldSvc.Dispose();

        lock (_dictLock) {
            _lastFrameTimestamps.Remove(cameraId);
        }

        var service = CreateService(cameraId);

        var offset = (targetTime - newSegment.StartTime).TotalSeconds;
        var seekUs = offset > 0 ? (long)(offset * 1_000_000) : 0;
        service.Open(newSegment.FilePath, seekUs);

        lock (_dictLock) {
            _players[cameraId] = service;
            _cameraFilePaths[cameraId] = newSegment.FilePath;
            _cameraSegmentStartUs[cameraId] = (long)(newSegment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
            _lastFrameTimestamps[cameraId] = Stopwatch.GetTimestamp();
        }

        _state = PlaybackState.Playing;
        StateChanged?.Invoke(_state);
    }

    /// <summary>Reload all channels to new segments for synchronous cross-segment playback</summary>
    public void ReloadAllCameras(Dictionary<string, VideoSegment> newSegments, DateTime targetTime) {
        foreach (var (camId, segment) in newSegments) {
            IPlaybackDecoder? oldSvc;
            lock (_dictLock) {
                if (!_players.TryGetValue(camId, out oldSvc)) continue;
                _players.Remove(camId);
            }
            oldSvc.Stop();
            oldSvc.Dispose();

            lock (_dictLock) {
                _lastFrameTimestamps.Remove(camId);
            }

            var service = CreateService(camId);
            var offset = (targetTime - segment.StartTime).TotalSeconds;
            var seekUs = offset > 0 ? (long)(offset * 1_000_000) : 0;
            service.Open(segment.FilePath, seekUs);

            lock (_dictLock) {
                _players[camId] = service;
                _cameraFilePaths[camId] = segment.FilePath;
                _cameraSegmentStartUs[camId] = (long)(segment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
                _lastFrameTimestamps[camId] = Stopwatch.GetTimestamp();

                if (camId == _masterCameraId) {
                    _masterSegmentStartOffsetUs = (long)(segment.StartTime.TimeOfDay.TotalSeconds * 1_000_000);
                    _currentSegmentEndTime = segment.EndTime ?? DateTime.MaxValue;
                }
            }
        }

        if (newSegments.Count > 0) {
            _state = PlaybackState.Playing;
            StateChanged?.Invoke(_state);
        }
    }

    private IPlaybackDecoder? GetMasterService() {
        lock (_dictLock) {
            return _masterCameraId is not null && _players.TryGetValue(_masterCameraId, out var svc) ? svc : null;
        }
    }

    public void Play() {
        IPlaybackDecoder[] snap;
        lock (_stateLock) {
            if (_state != PlaybackState.Paused) { return; }
            _state = PlaybackState.Playing;
        }
        StateChanged?.Invoke(_state);
        lock (_dictLock) { snap = [.. _players.Values]; }
        foreach (var svc in snap) svc.Resume();
    }

    public void Pause() {
        IPlaybackDecoder[] snap;
        lock (_stateLock) {
            if (_state != PlaybackState.Playing) { return; }
            _state = PlaybackState.Paused;
        }
        StateChanged?.Invoke(_state);
        lock (_dictLock) { snap = [.. _players.Values]; }
        foreach (var svc in snap) svc.Pause();
    }

    public void SeekMaster(long microseconds) {
        var oldCts = Interlocked.Exchange(ref _seekCts, new CancellationTokenSource());
        oldCts?.Cancel();
        oldCts?.Dispose();
        var token = _seekCts.Token;
        var capturedPos = microseconds;

        _ = Task.Delay(SeekDebounceMs, token).ContinueWith(_ => {
            if (token.IsCancellationRequested) { return; }

            IPlaybackDecoder? master;
            lock (_stateLock) {
                _lastMasterPosition = capturedPos;
                master = GetMasterService();
            }
            master?.Seek(capturedPos);
            MasterPositionChanged?.Invoke(capturedPos, GetMasterDuration());
        }, token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
    }

    /// <summary>Seek to an absolute position (in microseconds)</summary>
    public void SeekAbsolute(long absoluteMicroseconds) {
        _seekCts?.Cancel();
        IPlaybackDecoder? master;
        lock (_stateLock) {
            var relativeUs = absoluteMicroseconds - _masterSegmentStartOffsetUs;
            _lastMasterPosition = Math.Max(0, relativeUs);
            master = GetMasterService();
        }
        master?.Seek(_lastMasterPosition);
        MasterPositionChanged?.Invoke(_lastMasterPosition, GetMasterDuration());
    }

    /// <summary>立即跳至指定微秒位置，同時同步 Seek 所有 Slave 頻道以維持多路同步</summary>
    public void SeekMasterImmediate(long microseconds) {
        _seekCts?.Cancel();
        var seeks = new List<(IPlaybackDecoder svc, long pts)>();
        IPlaybackDecoder? master = null;

        lock (_stateLock) {
            _lastMasterPosition = microseconds;
        }

        lock (_dictLock) {
            var masterStart = _masterSegmentStartOffsetUs;
            foreach (var kvp in _players) {
                if (kvp.Key == _masterCameraId) {
                    master = kvp.Value;
                    continue;
                }
                if (!_cameraSegmentStartUs.TryGetValue(kvp.Key, out var chStart)) continue;
                var absoluteTargetUs = masterStart + microseconds;
                var chRelativeUs = absoluteTargetUs - chStart;
                if (chRelativeUs < 0) chRelativeUs = 0;
                seeks.Add((kvp.Value, chRelativeUs));
            }
        }

        master?.Seek(microseconds);
        foreach (var (svc, pts) in seeks) svc.Seek(pts);
        MasterPositionChanged?.Invoke(microseconds, GetMasterDuration());
    }

    public void SeekMasterSeconds(double seconds) {
        SeekMaster((long)(seconds * 1_000_000));
    }

    public void SeekToStart() {
        SeekMasterImmediate(0);
    }

    public void SeekToEnd() {
        var dur = GetMasterDuration();
        SeekMasterImmediate(dur > 0 ? dur - 1_000_000 : 0);
    }

    public void StepForward(int frames = 1) {
        SeekMasterImmediate(_lastMasterPosition + 33_000 * frames);
    }

    public void StepBackward(int frames = 1) {
        SeekMasterImmediate(Math.Max(0, _lastMasterPosition - 33_000 * frames));
    }

    public void SetPlaybackRate(double rate) {
        _playbackRate = Math.Clamp(rate, 0.25, 32.0);
        IPlaybackDecoder[] snap;
        lock (_dictLock) { snap = [.. _players.Values]; }
        foreach (var svc in snap) svc.SetPlaybackRate(_playbackRate);
    }

    /// <summary>停止所有頻道解碼，釋放資源，重置狀態</summary>
    public void StopAll() {
        var oldCts = Interlocked.Exchange(ref _seekCts, null);
        oldCts?.Cancel();
        oldCts?.Dispose();

        IPlaybackDecoder[] snap;
        lock (_dictLock) {
            snap = [.. _players.Values];
            _players.Clear();
            _lastFrameTimestamps.Clear();
            _lastSlaveResyncTicks.Clear();
            _cameraFilePaths.Clear();
            _cameraSegmentStartUs.Clear();
        }

        foreach (var svc in snap) {
            svc.Stop();
            svc.Dispose();
        }

        _masterCameraId = null;
        _masterSegmentStartOffsetUs = 0;
        _lastMasterPosition = 0;
        _lastMasterPositionReport = 0;
        _currentSegmentEndTime = DateTime.MaxValue;
        _state = PlaybackState.Stopped;
        StateChanged?.Invoke(_state);
    }

    public long GetMasterDuration() {
        return GetMasterService()?.DurationMicroseconds ?? 0;
    }

    public string? GetMasterId() => _masterCameraId;

    public long GetMasterPosition() => _lastMasterPosition;

    public long GetMasterSegmentStartOffsetUs() => _masterSegmentStartOffsetUs;

    public DateTime GetMasterSegmentEndTime() => _currentSegmentEndTime;

    public DateTime GetMasterSegmentStartTime() {
        if (_masterCameraId is null) return DateTime.MinValue;
        return new DateTime(_masterSegmentStartOffsetUs * 10); // microseconds → ticks
    }

    public void SetMasterCamera(string cameraId) {
        lock (_stateLock) {
            bool exists;
            lock (_dictLock) { exists = _players.ContainsKey(cameraId); }
            if (!exists) return;
            _masterCameraId = cameraId;
            lock (_dictLock) {
                if (_cameraSegmentStartUs.TryGetValue(cameraId, out var startUs))
                    _masterSegmentStartOffsetUs = startUs;
            }
            _lastMasterPosition = 0;
        }
    }

    public void RemoveCamera(string cameraId) {
        IPlaybackDecoder? svc;
        lock (_dictLock) {
            if (!_players.TryGetValue(cameraId, out svc)) return;
            _players.Remove(cameraId);
        }
        svc.Stop();
        svc.Dispose();
        lock (_dictLock) {
            if (_masterCameraId == cameraId) {
                _masterCameraId = null;
                foreach (var k in _players.Keys) { _masterCameraId = k; break; }
            }
        }
    }

    public void Dispose() {
        StopAll();
    }
}

public class CameraRecordingInfo {
    public string CameraId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public int ChannelNumber { get; set; }
    public List<VideoSegment> Segments { get; set; } = [];
    public double TotalDurationSeconds { get; set; }
    public long TotalFileSize { get; set; }
    public string TotalSizeText => FormatSize(TotalFileSize);

    private static string FormatSize(long bytes) {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class CameraPlaybackSlot {
    public Camera? Camera { get; set; }
    public VideoSegment? Segment { get; set; }
}

public enum PlaybackState {
    Stopped,
    Playing,
    Paused
}
