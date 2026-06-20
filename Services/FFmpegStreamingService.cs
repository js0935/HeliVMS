// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Diagnostics;
using System.Drawing;
using Serilog;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace HeliVMS.Services
{
    public unsafe class FFmpegStreamingService : IDisposable
    {
        private readonly object _lock = new object();
        private VideoStreamDecoder? _decoder;
        private VideoFrameConverter? _converter;
        private CancellationTokenSource? _stopCts;            // 永久停止用（只由 Stop() 觸發）
        private CancellationTokenSource? _interruptCts;       // 單次連線中斷用（watchdog 觸發，重連時重建）
        private Task? _streamingTask;
        private bool _isInitialized;
        private Timer? _watchdogTimer;
        private bool _isDisposed;

        /// <summary>
        /// Fired when each frame is decoded, passing an independent buffer copy (BGRA32), width, and height.
        /// Subscribers can safely use this buffer on any thread without competing with the decoder thread.
        /// </summary>
        public event Action<byte[], int, int>? FrameReady;

        /// <summary>
        /// Fired when playback status changes: true = playing, false = stopped or connection failed.
        /// </summary>
        public event Action<bool>? PlayStatus;

        public bool IsPlaying { get; private set; }

        private string? _rtspUrl;
        private string? _rtspUsername;
        private string? _rtspPassword;
        private int _reconnectAttempt;
        private long _lastFrameTicks = DateTime.MinValue.Ticks;
        private long _connectStartTicks = DateTime.MinValue.Ticks;

        private bool _watchdogFired;
        private int _watchdogStuckCount;

        /// <summary>Fired when stream is stuck: av_read_frame cannot be interrupted, upper layer needs to force restart</summary>
        public event Action? StreamStuck;

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
        private long _lastEmitTimestamp;  // 用於 FPS 限制的時間戳（Stopwatch ticks）

        public FFmpegStreamingService() { }

        private static void Log(string message) => Serilog.Log.Debug("[HeliVMS] {Msg}", message);

        private static volatile bool _ffmpegInitialized;
        private static readonly object _ffmpegLock = new();

        private static void EnsureFFmpegInitialized()
        {
            if (_ffmpegInitialized) { return; }
            lock (_ffmpegLock)
            {
                if (_ffmpegInitialized) { return; }
                FFmpegBinariesHelper.RegisterFFmpegBinaries();
                DynamicallyLoadedBindings.Initialize();
                _ffmpegInitialized = true;
            }
        }

        public static void InitializeFFmpeg() => EnsureFFmpegInitialized();

        private System.Drawing.Size ComputeDestSize(System.Drawing.Size sourceSize)
        {
            if (TargetDecodeHeight <= 0 || TargetDecodeHeight >= sourceSize.Height)
            {
                return sourceSize;
            }

            var w = sourceSize.Width * TargetDecodeHeight / sourceSize.Height;
            if (w < 2) { w = 2; }
            return new System.Drawing.Size(w & ~1, TargetDecodeHeight & ~1); // 確保偶數
        }

        /// <summary>
        /// Non-blocking playback: connection and initialization happen on a background thread, returns immediately.
        /// Success or failure is reported via the PlayStatus event.
        /// </summary>
        public void Play(string rtspUrl, string username, string password)
        {
            lock (_lock)
            {
                _isDisposed = false;
                if (_isInitialized)
                {
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

                var task = Task.Factory.StartNew(
                    () => StartStreaming(rtspUrl, username, password, stopToken),
                    TaskCreationOptions.LongRunning);
                _streamingTask = task;
            }
        }

        /// <summary>
        /// Start watchdog timer:
        /// 1. If no frame received within FrameTimeoutMs (10s) -> interrupt current connection
        /// 2. If interrupt is ineffective -> stream is stuck in FFmpeg layer
        ///    -> fire StreamStuck event for upper layer to force restart
        /// </summary>
        private static readonly TimerCallback _watchdogCallback = WatchdogTick;

        private void StartWatchdog()
        {
            _watchdogTimer?.Dispose();
            _watchdogStuckCount = 0;
            _watchdogFired = false;
            _watchdogTimer = new Timer(_watchdogCallback, this, FrameTimeoutMs, 1000);
        }

        private static void WatchdogTick(object? state)
        {
            var self = (FFmpegStreamingService)state!;
            try
            {
                var lastTicks = Volatile.Read(ref self._lastFrameTicks);
                if (lastTicks == DateTime.MinValue.Ticks)
                {
                    var connectTicks = Volatile.Read(ref self._connectStartTicks);
                    if (connectTicks != DateTime.MinValue.Ticks)
                    {
                        lastTicks = connectTicks;
                    }
                    else
                    {
                        self._watchdogStuckCount = 0;
                        self._watchdogFired = false;
                        return;
                    }
                }

                var elapsed = (DateTime.UtcNow - new DateTime(lastTicks)).TotalMilliseconds;
                if (elapsed >= self.FrameTimeoutMs)
                {
                    if (self._watchdogFired)
                    {
                        return;
                    }

                    self._watchdogStuckCount++;
                    Log($"[HeliVMS] Watchdog: no frame for {elapsed:F0}ms (stuck #{self._watchdogStuckCount}), interrupting...");

                    self._interruptCts?.Cancel();

                    if (self._watchdogStuckCount >= 1)
                    {
                        Log("[HeliVMS] Watchdog: interrupt failed, force-restarting streaming service...");
                        self._watchdogStuckCount = 0;
                        self._watchdogFired = true;
                        Volatile.Write(ref self._lastFrameTicks, DateTime.UtcNow.Ticks);
                        try { self.StreamStuck?.Invoke(); } catch { }
                    }
                }
                else
                {
                    self._watchdogStuckCount = 0;
                    self._watchdogFired = false;
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log($"[HeliVMS] Watchdog error: {ex.Message}");
            }
        }

        // Extracted streaming work to a private method to control synchronization
        private void StartStreaming(string rtspUrl, string username, string password, CancellationToken stopToken)
        {
            try
            {
                if (stopToken.IsCancellationRequested)
                {
                    Log("[HeliVMS] Stream cancelled before startup");
                    return;
                }

                var url = rtspUrl;
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    var uri = new Uri(rtspUrl);
                    var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
                    url = $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{uri.PathAndQuery}";
                }

                _rtspUrl = url;
                Log($"[HeliVMS] Connecting to: {url}");
                Volatile.Write(ref _connectStartTicks, DateTime.UtcNow.Ticks);

                // 初次連線（用 linked token：stop + interrupt）
                if (!OpenConnection(stopToken))
                {
                    // 連線失敗仍進入 StreamingLoop，由它負責重連
                }

                _isInitialized = true;
                StreamingLoop(stopToken);
            }
            catch (OperationCanceledException)
            {
                Log("[HeliVMS] Stream cancelled during startup");
            }
            catch (Exception ex)
            {
                Log($"[HeliVMS] Error starting stream: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Cleanup();
                IsPlaying = false;
                NotifyPlayStatus(false);
            }
        }

        /// <summary>Create decoder and converter with linked token (stop + interrupt), returns success</summary>
        private bool OpenConnection(CancellationToken stopToken)
        {
            try
            {
                _converter?.Dispose();
                _converter = null;
                _decoder?.Dispose();
                _decoder = null;

                if (string.IsNullOrEmpty(_rtspUrl)) { return false; }

                // 每次連線建立新的 interrupt token，watchdog 只取消這個，不影響 stopToken
                _interruptCts?.Dispose();
                _interruptCts = new CancellationTokenSource();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stopToken, _interruptCts.Token);
                _decoder = new VideoStreamDecoder(_rtspUrl, linkedCts.Token);

                var sourceSize = _decoder.FrameSize;
                if (sourceSize.Width > 0 && sourceSize.Height > 0)
                {
                    var sourcePixelFormat = _decoder.PixelFormat;
                    var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                    var destSize = ComputeDestSize(sourceSize);
                    _converter = new VideoFrameConverter(sourceSize, sourcePixelFormat, destSize, destinationPixelFormat);

                    _frameWidth = destSize.Width;
                    _frameHeight = destSize.Height;
                    _frameStride = _frameWidth * 4;
                }

                _isInitialized = true;
                _reconnectAttempt = 0;
                Log($"[HeliVMS] Connection opened: {_frameWidth}x{_frameHeight}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[HeliVMS] OpenConnection failed: {ex.Message}");
                return false;
            }
        }

        private void NotifyPlayStatus(bool isPlaying)
        {
            Log($"[HeliVMS] NotifyPlayStatus({isPlaying})");
            try { PlayStatus?.Invoke(isPlaying); } catch { }
        }

        private void StreamingLoop(CancellationToken stopToken)
        {
            StartWatchdog();

            var targetInterval = TargetFps > 0 ? TimeSpan.FromMilliseconds(1000.0 / TargetFps) : TimeSpan.Zero;
            var throttleTicks = targetInterval > TimeSpan.Zero
                ? (long)(targetInterval.TotalSeconds * Stopwatch.Frequency)
                : 0L;

            try
            {
                while (!stopToken.IsCancellationRequested)
                {
                    if (_decoder is null || !_isInitialized)
                    {
                        Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                        if (WaitForReconnect(stopToken))
                        {
                            NotifyPlayStatus(true);
                        }
                        continue;
                    }

                    try
                    {
                        if (_decoder.TryDecodeNextFrame(out var frame))
                        {
                            Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                            _reconnectAttempt = 0;

                            // FPS 限制：直接跳過未達間隔的影格，不 sleep 阻塞
                            if (throttleTicks > 0 && _lastEmitTimestamp != 0)
                            {
                                var elapsed = Stopwatch.GetTimestamp() - _lastEmitTimestamp;
                                if (elapsed < throttleTicks)
                                {
                                    continue;
                                }
                            }
                            _lastEmitTimestamp = Stopwatch.GetTimestamp();

                            if (_converter is null)
                            {
                                var sourceSize = new Size(frame.width, frame.height);
                                if (sourceSize.Width > 0 && sourceSize.Height > 0)
                                {
                                    var sourcePixelFormat = (AVPixelFormat)frame.format;
                                    var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                                    var destSize = ComputeDestSize(sourceSize);
                                    _converter = new VideoFrameConverter(sourceSize, sourcePixelFormat, destSize, destinationPixelFormat);
                                    _frameWidth = destSize.Width;
                                    _frameHeight = destSize.Height;
                                    _frameStride = _frameWidth * 4;
                                }
                                else
                                {
                                    continue;
                                }
                            }

                            var convertedFrame = _converter.Convert(frame);
                            var bufferSize = convertedFrame.linesize[0] * convertedFrame.height;

                            var uiBuffer = new byte[bufferSize];
                            fixed (byte* destPtr = uiBuffer)
                            {
                                Buffer.MemoryCopy(convertedFrame.data[0], destPtr, bufferSize, bufferSize);
                            }
                            FrameReady?.Invoke(uiBuffer, _frameWidth, _frameHeight);
                        }
                        else
                        {
                            Log("[HeliVMS] Stream ended, reconnecting...");
                            CleanupConnection();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        CleanupConnection();
                    }
                    catch (Exception ex)
                    {
                        Log($"[HeliVMS] Streaming error: {ex.Message}");
                        CleanupConnection();
                        // 發送 PlayStatus(false) 不在此處 — 由上層 finally 或重連路徑統一處理
                    }
                }
            }
            finally
            {
                IsPlaying = false;
            }
        }

        /// <summary>Wait with exponential backoff then try reconnecting, returns success</summary>
        private bool WaitForReconnect(CancellationToken stopToken)
        {
            // 指數退避：2s, 4s, 8s, 16s, 30s, 30s...
            var delay = _reconnectAttempt == 0
                ? InitialReconnectDelayMs
                : Math.Min(InitialReconnectDelayMs * (1 << Math.Min(_reconnectAttempt, 4)), MaxReconnectDelayMs);

            _reconnectAttempt++;
            Log($"[HeliVMS] Reconnecting in {delay}ms (attempt {_reconnectAttempt})...");

            // 分塊等待，每 2 秒更新 _lastFrameTicks 防止 watchdog 誤觸
            var remaining = delay;
            while (remaining > 0 && !stopToken.IsCancellationRequested)
            {
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

        private void CleanupConnection()
        {
            _isInitialized = false;

            // 解碼器可能在毀損狀態（如 native AV 連線中斷），
            // Dispose 可能再次觸發 native crash → 獨立 try-catch 防止程式終止
            try
            {
                _decoder?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"[HeliVMS] CleanupConnection: decoder dispose error: {ex.Message}");
            }
            _decoder = null;

            try
            {
                _converter?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"[HeliVMS] CleanupConnection: converter dispose error: {ex.Message}");
            }
            _converter = null;
        }

        public void Stop()
        {
            lock (_lock)
            {
                _stopCts?.Cancel();
                _interruptCts?.Cancel();
                _isInitialized = false;
            }

            // 串流引擎已透過 CTS 取消，StreamingLoop 的 catch(OperationCanceledException)
            // 會自行呼叫 CleanupConnection 釋放解碼器資源，因此無需阻塞等待。
            // Dispose() 會等待串流工作完成後再做最終清理。

            IsPlaying = false;
        }

        private void Cleanup()
        {
            lock (_lock)
            {
                if (_isDisposed) { return; }
                _isDisposed = true;
            }
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            CleanupConnection();
            _interruptCts?.Dispose();
            _interruptCts = null;
            _stopCts?.Dispose();
            _stopCts = null;
            _streamingTask = null;
        }

        public void Dispose()
        {
            Stop();
            // 等待串流背景工作確實結束，避免 Cleanup() 與 StreamingLoop 競爭 native FFmpeg 資源。
            // Cancel 後中斷回呼會讓 av_read_frame 快速返回，但 avcodec_send_packet / avcodec_receive_frame
            // 不檢查中斷回呼，若不等工作結束就釋放 native 記憶體 → 原生態 crash。
            // 等待期間 UI 執行緒會被短暫阻塞，但因 Dispose 只在頁面卸載/關閉時呼叫，對使用者無感。
            try { _streamingTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            Cleanup();
            GC.SuppressFinalize(this);
        }

        ~FFmpegStreamingService()
        {
            Dispose();
        }
    }
}
