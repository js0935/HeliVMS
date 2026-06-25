using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Serilog;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace HeliVMS.Services {

    /// <summary>
    /// Pre-allocated native memory ring buffer for zero-GC frame pipeline.
    /// All slots are allocated once via <see cref="NativeMemory.Alloc"/> and reused in round-robin fashion.
    /// </summary>
    internal sealed unsafe class NativeRingBuffer : IDisposable {
        private readonly byte* _buffer;
        private readonly int _slotSize;
        private readonly int _slotCount;
        private int _writeIndex;
        /// <summary>Bitmask — bit i = 1 means slot i is occupied (published but not yet released).</summary>
        private volatile int _occupancyMask;
        private readonly int[] _generations;
        private int _globalGen;
        private bool _disposed;

        public int SlotSize => _slotSize;
        public int SlotCount => _slotCount;

        /// <summary>Slot index for a given ring buffer pointer, or -1 if invalid.</summary>
        public int GetSlotIndex(IntPtr ptr) {
            var delta = (byte*)ptr - _buffer;
            var idx = (int)(delta / _slotSize);
            if ((uint)idx < (uint)_slotCount) return idx;
            return -1;
        }

        /// <summary>Generation stamp for the slot. Matches while the data is valid (not yet overwritten).</summary>
        public int GetGeneration(int slotIndex) {
            if ((uint)slotIndex < (uint)_slotCount) return Volatile.Read(ref _generations[slotIndex]);
            return -1;
        }

        public NativeRingBuffer(int slotSize, int slotCount) {
            if (slotSize <= 0) throw new ArgumentOutOfRangeException(nameof(slotSize));
            if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
            _slotSize = slotSize;
            _slotCount = slotCount;
            _generations = new int[slotCount];
            _buffer = (byte*)NativeMemory.Alloc((nuint)(slotSize * slotCount));
        }

        /// <summary>Claim next slot and mark it occupied. Force-steals the oldest slot if all are occupied.</summary>
        public IntPtr GetNextSlot() {
            int idx;
            for (var i = 0; i < _slotCount; i++) {
                idx = _writeIndex;
                _writeIndex = (_writeIndex + 1) % _slotCount;
                var bit = 1 << idx;
                if ((_occupancyMask & bit) == 0) {
                    Interlocked.Or(ref _occupancyMask, bit);
                    Volatile.Write(ref _generations[idx], ++_globalGen);
                    return (IntPtr)(_buffer + idx * _slotSize);
                }
            }
            // All slots occupied — force-steal the next one (decoder too fast, drop oldest)
            idx = _writeIndex;
            _writeIndex = (_writeIndex + 1) % _slotCount;
            Interlocked.Or(ref _occupancyMask, 1 << idx);
            Volatile.Write(ref _generations[idx], ++_globalGen);
            return (IntPtr)(_buffer + idx * _slotSize);
        }

        /// <summary>Mark a previously claimed slot as free for reuse.</summary>
        public void ReleaseSlot(IntPtr ptr) {
            var offset = (byte*)ptr - _buffer;
            var idx = (int)(offset / _slotSize);
            if ((uint)idx < (uint)_slotCount) {
                Interlocked.And(ref _occupancyMask, ~(1 << idx));
            }
        }

        public void Dispose() {
            if (!_disposed) {
                _disposed = true;
                if (_buffer != null) {
                    NativeMemory.Free(_buffer);
                }
            }
        }
    }

    public unsafe class FFmpegStreamingService : IDisposable {
        private readonly Lock _lock = new();
        /// <summary>Limits concurrent FFmpeg decode operations across all cameras (prevents thread pool starvation)</summary>
        private static readonly SemaphoreSlim DecodeThrottle = new(8, 8);
        private bool _semaphoreAcquired;
        private VideoStreamDecoder? _decoder;
        private VideoFrameConverter? _converter;
        private NativeRingBuffer? _ringBuffer;
        private CancellationTokenSource? _stopCts;
        private CancellationTokenSource? _interruptCts;
        private Thread? _streamingThread;
        private bool _isInitialized;
        private Timer? _watchdogTimer;
        private bool _isDisposed;

        /// <summary>
        /// Fired when each frame is decoded, passing a pointer into the native ring buffer (BGRA32),
        /// width, height, and data size. The pointer is valid only during the handler invocation;
        /// the subscriber must copy or process synchronously.
        /// </summary>
        public event Action<IntPtr, int, int, int>? FrameReady;

        /// <summary>Hardware acceleration device type for decode (default: auto-detect)</summary>
        public AVHWDeviceType HwDeviceType { get; set; } = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

        /// <summary>
        /// Fired when playback status changes: true = playing, false = stopped or connection failed.
        /// </summary>
        public event Action<bool>? PlayStatus;

        public bool IsPlaying { get; private set; }

        private string? _rtspUrl;
        private string? _rtspUsername;
        private string? _rtspPassword;
        private AsyncRtspIO? _asyncRtsp;
        private int _reconnectAttempt;
        private long _lastFrameTicks = DateTime.MinValue.Ticks;
        private long _connectStartTicks = DateTime.MinValue.Ticks;

        private bool _watchdogFired;
        private int _watchdogStuckCount;

        /// <summary>Fired when stream is stuck: av_read_frame cannot be interrupted, upper layer needs to force restart</summary>
        public event Action? StreamStuck;

        /// <summary>Use async RTSP client instead of FFmpeg's built-in RTSP demuxer.</summary>
        public bool UseAsyncRtsp { get; set; }

        /// <summary>Frame receive timeout in milliseconds (default 10s)</summary>
        public int FrameTimeoutMs { get; set; } = 10_000;
        /// <summary>Maximum reconnect interval (exponential backoff cap 30s)</summary>
        public int MaxReconnectDelayMs { get; set; } = 30_000;
        /// <summary>Initial reconnect delay (ms)</summary>
        public int InitialReconnectDelayMs { get; set; } = 2000;

        /// <summary>Target decode height (0 = original resolution), width scaled proportionally</summary>
        public int TargetDecodeHeight { get; set; } = 0;
        /// <summary>Target output framerate (cap); 0 or higher than source means no limit</summary>
        public int TargetFps { get; set; } = 0;

        private int _frameWidth;
        private int _frameHeight;
        private int _frameStride;
        private long _lastEmitTimestamp;

        /// <summary>Number of pre-allocated ring buffer slots (3-5 recommended)</summary>
        public int RingBufferSlotCount { get; set; } = 4;

        /// <summary>Release a ring buffer slot pointer back to the pool. Safe to call from any thread.</summary>
        public void ReleaseFrame(IntPtr ptr) {
            if (ptr == IntPtr.Zero) return;
            _ringBuffer?.ReleaseSlot(ptr);
        }

        public int GetFrameGeneration(IntPtr ptr) {
            if (_ringBuffer is null) return -1;
            var slotIdx = _ringBuffer.GetSlotIndex(ptr);
            if (slotIdx < 0) return -1;
            return _ringBuffer.GetGeneration(slotIdx);
        }

        public bool ValidateFrame(IntPtr ptr, int generation) {
            return GetFrameGeneration(ptr) == generation;
        }

        public FFmpegStreamingService() { }

        private static void Log(string message) => Serilog.Log.Debug("[HeliVMS] {Msg}", message);

        private static volatile bool _ffmpegInitialized;
        private static readonly Lock _ffmpegLock = new();

        private static void EnsureFFmpegInitialized() {
            if (_ffmpegInitialized) { return; }
            lock (_ffmpegLock) {
                if (_ffmpegInitialized) { return; }
                FFmpegBinariesHelper.RegisterFFmpegBinaries();
                DynamicallyLoadedBindings.Initialize();
                _ffmpegInitialized = true;
            }
        }

        public static void InitializeFFmpeg() => EnsureFFmpegInitialized();

        private System.Drawing.Size ComputeDestSize(System.Drawing.Size sourceSize) {
            if (TargetDecodeHeight <= 0 || TargetDecodeHeight >= sourceSize.Height) {
                return sourceSize;
            }

            var w = sourceSize.Width * TargetDecodeHeight / sourceSize.Height;
            if (w < 2) { w = 2; }
            return new System.Drawing.Size(w & ~1, TargetDecodeHeight & ~1);
        }

        /// <summary>
        /// Non-blocking playback: connection and initialization happen on a background thread, returns immediately.
        /// Success or failure is reported via the PlayStatus event.
        /// </summary>
        public void Play(string rtspUrl, string username, string password) {
            lock (_lock) {
                _isDisposed = false;
                if (_isInitialized) {
                    _stopCts?.Cancel();
                }

                _rtspUsername = username;
                _rtspPassword = password;
                _reconnectAttempt = 0;
                _lastFrameTicks = DateTime.MinValue.Ticks;
                EnsureFFmpegInitialized();

                _stopCts = new CancellationTokenSource();
                _interruptCts = new CancellationTokenSource();
                var stopToken = _stopCts.Token;

                var thread = new Thread(() => {
                    _semaphoreAcquired = DecodeThrottle.Wait(0);
                    try {
                        StartStreaming(rtspUrl, username, password, stopToken);
                    } finally {
                        if (_semaphoreAcquired) DecodeThrottle.Release();
                    }
                }) { IsBackground = true, Name = $"FFmpegDecode-{rtspUrl}" };
                thread.Start();
                _streamingThread = thread;
            }
        }

        /// <summary>
        /// Start watchdog timer:
        /// 1. If no frame received within FrameTimeoutMs (10s) -> interrupt current connection
        /// 2. If interrupt is ineffective -> stream is stuck in FFmpeg layer
        ///    -> fire StreamStuck event for upper layer to force restart
        /// </summary>
        private static readonly TimerCallback _watchdogCallback = WatchdogTick;

        private void StartWatchdog() {
            _watchdogTimer?.Dispose();
            _watchdogStuckCount = 0;
            _watchdogFired = false;
            _watchdogTimer = new Timer(_watchdogCallback, this, FrameTimeoutMs, 1000);
        }

        private static void WatchdogTick(object? state) {
            var self = (FFmpegStreamingService)state!;
            try {
                var lastTicks = Volatile.Read(ref self._lastFrameTicks);
                if (lastTicks == DateTime.MinValue.Ticks) {
                    var connectTicks = Volatile.Read(ref self._connectStartTicks);
                    if (connectTicks != DateTime.MinValue.Ticks) {
                        lastTicks = connectTicks;
                    } else {
                        self._watchdogStuckCount = 0;
                        self._watchdogFired = false;
                        return;
                    }
                }

                var elapsed = (DateTime.UtcNow - new DateTime(lastTicks)).TotalMilliseconds;
                if (elapsed >= self.FrameTimeoutMs) {
                    if (self._watchdogFired) {
                        return;
                    }

                    self._watchdogStuckCount++;
                    Log($"[HeliVMS] Watchdog: no frame for {elapsed:F0}ms (stuck #{self._watchdogStuckCount}), interrupting...");

                    self._interruptCts?.Cancel();

                    if (self._watchdogStuckCount >= 1) {
                        Log("[HeliVMS] Watchdog: interrupt failed, force-restarting streaming service...");
                        self._watchdogStuckCount = 0;
                        self._watchdogFired = true;
                        Volatile.Write(ref self._lastFrameTicks, DateTime.UtcNow.Ticks);
                        try { self.StreamStuck?.Invoke(); } catch { }
                    }
                } else {
                    self._watchdogStuckCount = 0;
                    self._watchdogFired = false;
                }
            } catch (ObjectDisposedException) { } catch (Exception ex) {
                Log($"[HeliVMS] Watchdog error: {ex.Message}");
            }
        }

        private void StartStreaming(string rtspUrl, string username, string password, CancellationToken stopToken) {
            try {
                if (stopToken.IsCancellationRequested) {
                    Log("[HeliVMS] Stream cancelled before startup");
                    return;
                }

                var url = rtspUrl;
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)) {
                    var uri = new Uri(rtspUrl);
                    var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
                    url = $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{uri.PathAndQuery}";
                }

                _rtspUrl = url;
                Log($"[HeliVMS] Connecting to: {url}");
                Volatile.Write(ref _connectStartTicks, DateTime.UtcNow.Ticks);

                if (!OpenConnection(stopToken)) {
                    return;
                }

                _isInitialized = true;
                StreamingLoop(stopToken);
            } catch (OperationCanceledException) {
                Log("[HeliVMS] Stream cancelled during startup");
            } catch (Exception ex) {
                Log($"[HeliVMS] Error starting stream: {ex.Message}\n{ex.StackTrace}");
            } finally {
                Cleanup();
                IsPlaying = false;
                NotifyPlayStatus(false);
            }
        }

        /// <summary>Create decoder and converter with linked token (stop + interrupt), returns success</summary>
        private bool OpenConnection(CancellationToken stopToken) {
            try {
                _converter?.Dispose();
                _converter = null;
                _decoder?.Dispose();
                _decoder = null;
                _ringBuffer?.Dispose();
                _ringBuffer = null;

                if (string.IsNullOrEmpty(_rtspUrl)) { return false; }

                _interruptCts?.Dispose();
                _interruptCts = new CancellationTokenSource();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stopToken, _interruptCts.Token);

                if (UseAsyncRtsp) {
                    _asyncRtsp?.Dispose();
                    _asyncRtsp = new AsyncRtspIO(_rtspUrl, "live");
                    if (!_asyncRtsp.Open(stopToken))
                        return false;
                    _decoder = new VideoStreamDecoder(_rtspUrl, _asyncRtsp, linkedCts.Token, HwDeviceType);
                } else {
                    _decoder = new VideoStreamDecoder(_rtspUrl, linkedCts.Token, HwDeviceType);
                }

                var sourceSize = _decoder.FrameSize;
                if (sourceSize.Width > 0 && sourceSize.Height > 0) {
                    var sourcePixelFormat = _decoder.PixelFormat;
                    var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                    var destSize = ComputeDestSize(sourceSize);
                    _converter = new VideoFrameConverter(sourceSize, sourcePixelFormat, destSize, destinationPixelFormat);

                    _frameWidth = destSize.Width;
                    _frameHeight = destSize.Height;
                    _frameStride = _frameWidth * 4;

                    var bufferSize = _converter.GetBufferSize();
                    _ringBuffer = new NativeRingBuffer(bufferSize, RingBufferSlotCount);
                }

                _isInitialized = true;
                _reconnectAttempt = 0;
                Log($"[HeliVMS] Connection opened: {_frameWidth}x{_frameHeight} ring={RingBufferSlotCount}slots");
                return true;
            } catch (Exception ex) {
                Log($"[HeliVMS] OpenConnection failed: {ex.Message}");
                return false;
            }
        }

        private void NotifyPlayStatus(bool isPlaying) {
            Log($"[HeliVMS] NotifyPlayStatus({isPlaying})");
            try { PlayStatus?.Invoke(isPlaying); } catch { }
        }

        private void EnsureRingBuffer() {
            if (_ringBuffer is not null) return;
            if (_converter is null) return;
            var bufferSize = _converter.GetBufferSize();
            _ringBuffer = new NativeRingBuffer(bufferSize, RingBufferSlotCount);
        }

        private void StreamingLoop(CancellationToken stopToken) {
            StartWatchdog();

            var targetInterval = TargetFps > 0 ? TimeSpan.FromMilliseconds(1000.0 / TargetFps) : TimeSpan.Zero;
            var throttleTicks = targetInterval > TimeSpan.Zero
                ? (long)(targetInterval.TotalSeconds * Stopwatch.Frequency)
                : 0L;

            try {
                while (!stopToken.IsCancellationRequested) {
                    if (_decoder is null || !_isInitialized) {
                        Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                        if (WaitForReconnect(stopToken)) {
                            NotifyPlayStatus(true);
                        }
                        continue;
                    }

                    if (throttleTicks > 0 && _lastEmitTimestamp != 0) {
                        var elapsed = Stopwatch.GetTimestamp() - _lastEmitTimestamp;
                        if (elapsed < throttleTicks) {
                            if (_decoder.TrySkipNextPacket()) {
                                Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                            }
                            continue;
                        }
                    }
                    _lastEmitTimestamp = Stopwatch.GetTimestamp();

                    try {
                        if (_decoder.TryDecodeNextFrame(out var frame)) {
                            Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                            _reconnectAttempt = 0;

                            var framePixFmt = (AVPixelFormat)frame.format;
                            var frameW = frame.width;
                            var frameH = frame.height;

                            if (_converter is not null && (_converter.SourcePixelFormat != framePixFmt || _converter.DestinationPixelFormat != AVPixelFormat.AV_PIX_FMT_BGRA)) {
                                _converter.Dispose();
                                _converter = null;
                                _ringBuffer?.Dispose();
                                _ringBuffer = null;
                            }

                            if (_converter is null) {
                                var sourceSize = new Size(frameW, frameH);
                                if (sourceSize.Width > 0 && sourceSize.Height > 0) {
                                    var destSize = ComputeDestSize(sourceSize);
                                    _converter = new VideoFrameConverter(sourceSize, framePixFmt, destSize, AVPixelFormat.AV_PIX_FMT_BGRA);
                                    _frameWidth = destSize.Width;
                                    _frameHeight = destSize.Height;
                                    _frameStride = _frameWidth * 4;
                                    EnsureRingBuffer();
                                } else {
                                    continue;
                                }
                            }

                            var framePtr = _ringBuffer!.GetNextSlot();
                            _converter.ConvertTo(frame, framePtr);
                            FrameReady?.Invoke(framePtr, _frameWidth, _frameHeight, _ringBuffer.SlotSize);
                        } else {
                            Log("[HeliVMS] Stream ended, reconnecting...");
                            CleanupConnection();
                        }
                    } catch (OperationCanceledException) {
                        CleanupConnection();
                    } catch (Exception ex) {
                        Log($"[HeliVMS] Streaming error: {ex.Message}");
                        CleanupConnection();
                    }
                }
            } finally {
                IsPlaying = false;
            }
        }

        private bool WaitForReconnect(CancellationToken stopToken) {
            var delay = _reconnectAttempt == 0
                ? InitialReconnectDelayMs
                : Math.Min(InitialReconnectDelayMs * (1 << Math.Min(_reconnectAttempt, 4)), MaxReconnectDelayMs);

            _reconnectAttempt++;
            Log($"[HeliVMS] Reconnecting in {delay}ms (attempt {_reconnectAttempt})...");

            var remaining = delay;
            while (remaining > 0 && !stopToken.IsCancellationRequested) {
                Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                var chunk = Math.Min(2000, remaining);
                using var mre = new ManualResetEventSlim(false);
                try { mre.Wait(chunk, stopToken); } catch (OperationCanceledException) { return false; }
                remaining -= chunk;
            }
            if (stopToken.IsCancellationRequested) { return false; }

            Volatile.Write(ref _connectStartTicks, DateTime.UtcNow.Ticks);
            return OpenConnection(stopToken);
        }

        private void CleanupConnection() {
            _isInitialized = false;

            try { _decoder?.Dispose(); } catch (Exception ex) { Log($"[HeliVMS] CleanupConnection: decoder dispose error: {ex.Message}"); }
            _decoder = null;

            try { _converter?.Dispose(); } catch (Exception ex) { Log($"[HeliVMS] CleanupConnection: converter dispose error: {ex.Message}"); }
            _converter = null;

            try { _asyncRtsp?.Dispose(); } catch (Exception ex) { Log($"[HeliVMS] CleanupConnection: asyncRtsp dispose error: {ex.Message}"); }
            _asyncRtsp = null;
        }

        public void Stop() {
            lock (_lock) {
                _stopCts?.Cancel();
                _interruptCts?.Cancel();
                _isInitialized = false;
            }

            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            IsPlaying = false;
        }

        private void Cleanup() {
            lock (_lock) {
                if (_isDisposed) { return; }
                _isDisposed = true;
            }
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            CleanupConnection();
            _ringBuffer?.Dispose();
            _ringBuffer = null;
            _interruptCts?.Dispose();
            _interruptCts = null;
            _stopCts?.Dispose();
            _stopCts = null;
            _streamingThread = null;
        }

        public void Dispose() {
            Stop();
            try { _streamingThread?.Join(1000); } catch { }
            Cleanup();
            GC.SuppressFinalize(this);
        }

        ~FFmpegStreamingService() {
            Dispose();
        }
    }
}