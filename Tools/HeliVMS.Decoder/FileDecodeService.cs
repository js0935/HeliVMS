using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Serilog;

namespace HeliVMS.Decoder;

public sealed unsafe class FileDecodeService(string cameraId) : IDisposable
{
    private AVFormatContext* _pFormatContext;
    private AVCodecContext* _pCodecContext;
    private AVFrame* _pFrame;
    private AVPacket* _pPacket;
    private VideoFrameConverter? _converter;
    private int _streamIndex = -1;
    private long _durationMicroseconds;
    private int _frameWidth;
    private int _frameHeight;
    private int _targetDecodeHeight;
    private bool _isDisposed;
    private readonly string _cameraId = cameraId;

    private CancellationTokenSource? _stopCts;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private double _playbackRate = 1.0;
    private double _frameAccumulator;
    private int _baseFrameMs = 33;

    private readonly Lock _seekLock = new();
    private volatile bool _seekPending;
    private long _seekTargetPts = AV_NOPTS_VALUE;

    private const long AV_NOPTS_VALUE = unchecked((long)0x8000000000000000);

    private Thread? _decodeThread;

    private bool _isLiveStream;

    // HW acceleration
    private AVHWDeviceType _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    private bool _useHwAccel;
    private AVFrame* _hwReceivedFrame;

    public void SetHwAccel(AVHWDeviceType type) => _hwDeviceType = type;

    public void SetTargetDecodeHeight(int targetHeight) => _targetDecodeHeight = targetHeight;

    private Size ComputeDestSize(Size source)
    {
        if (_targetDecodeHeight <= 0 || _targetDecodeHeight >= source.Height)
            return source;
        var w = source.Width * _targetDecodeHeight / source.Height;
        if (w < 2) w = 2;
        return new Size(w & ~1, _targetDecodeHeight & ~1);
    }

    public long DurationMicroseconds => _durationMicroseconds;
    public bool IsPlaying => _isPlaying;

    public event Action<long, int, int, byte[], int>? FrameDecoded;
    public event Action<long, long>? PositionChanged;
    public event Action<bool>? StatusChanged;
    public event Action? EofReached;
    public event Action<string>? ErrorOccurred;

    public void Open(string filePath, int targetDecodeHeight = 0)
    {
        _targetDecodeHeight = targetDecodeHeight;
        if (_decodeThread is { IsAlive: true })
        {
            Stop();
            if (!_decodeThread.Join(5000))
            {
                Log.Warning("[Decoder:{CameraId}] Previous decode thread did not exit in 5s, aborting", _cameraId);
                return;
            }
        }
        else
        {
            Stop();
        }

        _isLiveStream = filePath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
        _stopCts = new CancellationTokenSource();
        var token = _stopCts.Token;

        _decodeThread = new Thread(() => DecodeLoop(filePath, token))
        {
            Name = $"Decode-{_cameraId}",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _decodeThread.Start();
    }

    public void Pause()
    {
        _isPaused = true;
        _pauseEvent.Reset();
    }

    public void Resume()
    {
        _isPaused = false;
        _pauseEvent.Set();
    }

    public void Seek(long microseconds)
    {
        lock (_seekLock)
        {
            _seekTargetPts = microseconds;
            _seekPending = true;
        }
    }

    public void SetPlaybackRate(double rate)
    {
        _playbackRate = Math.Clamp(rate, 0.25, 32.0);
        _frameAccumulator = 0;
    }

    public void SetTargetFps(double fps)
    {
        _baseFrameMs = fps > 0 ? (int)Math.Clamp(1000.0 / fps, 33, 500) : 33;
    }

    public void Stop()
    {
        _pauseEvent.Set();
        _stopCts?.Cancel();
        _isPlaying = false;
        _isPaused = false;
    }

    private void DecodeLoop(string filePath, CancellationToken token)
    {
        byte[]? decodeBuffer = null;
        int decodeBufferLen = 0;
        bool pipeWriteBlocked = false;
        int consecutiveFrameDrops = 0;
        const int maxReconnectAttempts = 10;
        int reconnectAttempt = 0;
        const int reconnectDelayMs = 3000;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!OpenFile(filePath))
                {
                    if (_isLiveStream && reconnectAttempt < maxReconnectAttempts)
                    {
                        reconnectAttempt++;
                        Log.Warning("[Decoder:{CameraId}] Live stream open failed, reconnecting (attempt {Attempt}/{Max})",
                            _cameraId, reconnectAttempt, maxReconnectAttempts);
                        Thread.Sleep(reconnectDelayMs * (int)Math.Min(reconnectAttempt, 5));
                        continue;
                    }
                    NotifyStatus(false);
                    return;
                }

                reconnectAttempt = 0;
                _isPlaying = true;
                NotifyStatus(true);

                var frameStopwatch = Stopwatch.StartNew();
                var positionTimer = Stopwatch.StartNew();
                long lastReportedPts = 0;

                while (!token.IsCancellationRequested && _isPlaying)
                {
                    if (!_isLiveStream && _seekPending)
                    {
                        PerformSeek();
                        frameStopwatch.Restart();
                    }

                    if (!_isLiveStream && _isPaused)
                    {
                        try { _pauseEvent.Wait(token); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    try
                    {
                        frameStopwatch.Restart();

                        if (DecodeNextFrame(out var frame))
                        {
                            var pts = frame.pts;
                            if (pts != AV_NOPTS_VALUE)
                            {
                                var stream = _pFormatContext->streams[_streamIndex];
                                var timeBase = stream->time_base;
                                var ptsUs = pts * timeBase.num * 1_000_000 / timeBase.den;
                                lastReportedPts = ptsUs;

                                if (!_isLiveStream && positionTimer.ElapsedMilliseconds >= 100)
                                {
                                    PositionChanged?.Invoke(ptsUs, _durationMicroseconds);
                                    positionTimer.Restart();
                                }
                            }

                            var framePixFmt = (AVPixelFormat)frame.format;

                            // Recreate converter if pixel format changed
                            if (_converter != null && frame.width > 0 && frame.height > 0 &&
                                (_converter.SourcePixelFormat != framePixFmt ||
                                 _converter.DestinationPixelFormat != AVPixelFormat.AV_PIX_FMT_BGRA))
                            {
                                _converter.Dispose();
                                _converter = null;
                            }

                            if (_converter == null && frame.width > 0 && frame.height > 0)
                            {
                                var sourceSize = new Size(frame.width, frame.height);
                                var destSize = ComputeDestSize(sourceSize);
                                var destPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                                _converter = new VideoFrameConverter(sourceSize, framePixFmt, destSize, destPixelFormat);
                                _frameWidth = destSize.Width;
                                _frameHeight = destSize.Height;
                                var bufSize = _frameWidth * 4 * _frameHeight;
                                if (decodeBuffer != null)
                                    ArrayPool<byte>.Shared.Return(decodeBuffer);
                                decodeBuffer = ArrayPool<byte>.Shared.Rent(bufSize);
                                decodeBufferLen = bufSize;
                                Log.Information("[Decoder:{CameraId}] Resolution: {W}x{H} (target height {TargetH})",
                                    _cameraId, _frameWidth, _frameHeight, _targetDecodeHeight);
                            }

                            bool displayThisFrame = true;
                            if (!_isLiveStream && _playbackRate > 1.0)
                            {
                                _frameAccumulator += _playbackRate;
                                displayThisFrame = _frameAccumulator >= 1.0;
                                if (displayThisFrame)
                                    _frameAccumulator -= 1.0;
                            }

                            if (_isLiveStream && consecutiveFrameDrops >= 15)
                            {
                                displayThisFrame = true;
                                consecutiveFrameDrops = 0;
                            }

                            if (displayThisFrame && _converter != null && decodeBuffer != null)
                            {
                                var converted = _converter.Convert(frame);
                                var bufferSize = converted.linesize[0] * converted.height;

                                if (decodeBufferLen < bufferSize)
                                {
                                    ArrayPool<byte>.Shared.Return(decodeBuffer);
                                    decodeBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                                    decodeBufferLen = bufferSize;
                                }

                                Marshal.Copy((IntPtr)converted.data[0], decodeBuffer, 0, bufferSize);

                                var before = Environment.TickCount64;
                                FrameDecoded?.Invoke(lastReportedPts, _frameWidth, _frameHeight, decodeBuffer, bufferSize);
                                var writeMs = (int)(Environment.TickCount64 - before);

                                if (_isLiveStream && writeMs > _baseFrameMs && !pipeWriteBlocked)
                                {
                                    pipeWriteBlocked = true;
                                    Log.Warning("[Decoder:{CameraId}] Pipe write took {WriteMs}ms > target frame {TargetMs}ms, dropping frames",
                                        _cameraId, writeMs, _baseFrameMs);
                                }
                                else if (_isLiveStream && writeMs <= _baseFrameMs / 2)
                                {
                                    pipeWriteBlocked = false;
                                }

                                if (_isLiveStream && pipeWriteBlocked && consecutiveFrameDrops < 15)
                                {
                                    displayThisFrame = false;
                                    consecutiveFrameDrops++;
                                }
                                else
                                {
                                    consecutiveFrameDrops = 0;
                                }
                            }

                            if (displayThisFrame)
                            {
                                var targetFrameMs = _isLiveStream
                                    ? _baseFrameMs
                                    : (int)(_baseFrameMs / _playbackRate);
                                var elapsed = (int)frameStopwatch.ElapsedMilliseconds;
                                var remaining = targetFrameMs - elapsed;
                                if (remaining > 1)
                                    Thread.Sleep(remaining);
                            }
                        }
                        else
                        {
                            if (_isLiveStream)
                            {
                                Log.Warning("[Decoder:{CameraId}] Live stream ended, reconnecting...", _cameraId);
                                break; // break inner loop → reconnect in outer loop
                            }
                            Log.Debug("[Decoder:{CameraId}] EOF reached for {Path}", _cameraId, filePath);
                            try { EofReached?.Invoke(); } catch { }
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (_isLiveStream)
                        {
                            Log.Warning(ex, "[Decoder:{CameraId}] Live stream decode error, reconnecting...", _cameraId);
                            break; // break inner loop → reconnect
                        }
                        Log.Warning(ex, "[Decoder:{CameraId}] Decode error", _cameraId);
                        Thread.Sleep(33);
                    }
                }

                _isPlaying = false;
                NotifyStatus(false);
                if (!_isLiveStream)
                    PositionChanged?.Invoke(lastReportedPts, _durationMicroseconds);

                // Live stream: reconnect with backoff
                if (_isLiveStream && !token.IsCancellationRequested)
                {
                    CleanupDecoder();
                    reconnectAttempt++;
                    if (reconnectAttempt <= maxReconnectAttempts)
                    {
                        var delay = reconnectDelayMs * reconnectAttempt;
                        Log.Warning("[Decoder:{CameraId}] Reconnecting in {Delay}ms (attempt {Attempt}/{Max})",
                            _cameraId, delay, reconnectAttempt, maxReconnectAttempts);
                        Thread.Sleep(delay);
                        continue;
                    }
                    Log.Error("[Decoder:{CameraId}] Max reconnection attempts ({Max}) reached",
                        _cameraId, maxReconnectAttempts);
                    ErrorOccurred?.Invoke("Max reconnection attempts reached");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Decoder:{CameraId}] Decode loop fatal", _cameraId);
        }
        finally
        {
            _isPlaying = false;
            NotifyStatus(false);
            CleanupDecoder();
            if (decodeBuffer != null)
                ArrayPool<byte>.Shared.Return(decodeBuffer);
        }
    }

    private bool OpenFile(string filePath)
    {
        AVFormatContext* pFormatCtx = null;
        AVCodecContext* pCodecCtx = null;
        AVPacket* pPkt = null;
        AVFrame* pFrm = null;
        VideoFrameConverter? converter = null;
        _useHwAccel = false;

        try
        {
            pFormatCtx = ffmpeg.avformat_alloc_context();
            if (pFormatCtx == null)
            {
                Log.Error("[Decoder:{CameraId}] avformat_alloc_context returned null", _cameraId);
                return false;
            }

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);

            if (_isLiveStream)
            {
                ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&options, "stimeout", "5000000", 0);
                if (filePath.Contains('@') == false)
                    Log.Information("[Decoder:{CameraId}] RTSP URL has no credentials — may fail with 401", _cameraId);
            }

            AVFormatContext* pCtx = pFormatCtx;
            var openRet = ffmpeg.avformat_open_input(&pCtx, filePath, null, &options);
            pFormatCtx = pCtx;
            if (openRet < 0)
            {
                var errMsg = FFmpegHelper.av_strerror(openRet);
                Log.Error("[Decoder:{CameraId}] avformat_open_input failed: {Error} (url={Url})", _cameraId, errMsg, filePath);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                _pFormatContext = null;
                _pCodecContext = null;
                _pPacket = null;
                _pFrame = null;
                _converter = null;
                _streamIndex = -1;
                if (_isLiveStream)
                    ErrorOccurred?.Invoke($"Stream open failed: {errMsg}");
                return false;
            }

            var infoRet = ffmpeg.avformat_find_stream_info(pFormatCtx, null);
            if (infoRet < 0)
            {
                Log.Error("[Decoder:{CameraId}] avformat_find_stream_info failed: {Error}", _cameraId, FFmpegHelper.av_strerror(infoRet));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                _pFormatContext = null;
                _pCodecContext = null;
                _pPacket = null;
                _pFrame = null;
                _converter = null;
                _streamIndex = -1;
                if (_isLiveStream)
                    ErrorOccurred?.Invoke($"Stream info failed: {FFmpegHelper.av_strerror(infoRet)}");
                return false;
            }

            AVCodec* codec = null;
            var streamRet = ffmpeg.av_find_best_stream(
                pFormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (streamRet < 0)
            {
                Log.Error("[Decoder:{CameraId}] av_find_best_stream failed: {Error}", _cameraId, FFmpegHelper.av_strerror(streamRet));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                _pFormatContext = null;
                _pCodecContext = null;
                _pPacket = null;
                _pFrame = null;
                _converter = null;
                _streamIndex = -1;
                if (_isLiveStream)
                    ErrorOccurred?.Invoke($"No video stream: {FFmpegHelper.av_strerror(streamRet)}");
                return false;
            }
            _streamIndex = streamRet;

            _durationMicroseconds = pFormatCtx->duration;
            if (_durationMicroseconds <= 0 && _streamIndex >= 0)
            {
                var st = pFormatCtx->streams[_streamIndex];
                if (st->duration > 0)
                {
                    var tb = st->time_base;
                    _durationMicroseconds = st->duration * tb.num * 1_000_000 / tb.den;
                }
            }
            if (_durationMicroseconds <= 0)
                _durationMicroseconds = 0;

            if (codec == null)
            {
                Log.Error("[Decoder:{CameraId}] No suitable codec found", _cameraId);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            pCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (pCodecCtx == null)
            {
                Log.Error("[Decoder:{CameraId}] avcodec_alloc_context3 returned null", _cameraId);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            if (_hwDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                var hwRet = ffmpeg.av_hwdevice_ctx_create(
                    &pCodecCtx->hw_device_ctx, _hwDeviceType, null, null, 0);
                if (hwRet >= 0)
                {
                    _useHwAccel = true;
                    Log.Information("[Decoder:{CameraId}] HW acceleration enabled: {Type}",
                        _cameraId, ffmpeg.av_hwdevice_get_type_name(_hwDeviceType));
                }
                else
                {
                    _useHwAccel = false;
                    Log.Warning("[Decoder:{CameraId}] HW acceleration init failed for {Type}: {Error}, falling back to software",
                        _cameraId, ffmpeg.av_hwdevice_get_type_name(_hwDeviceType),
                        FFmpegHelper.av_strerror(hwRet));
                }
            }

            var ret = ffmpeg.avcodec_parameters_to_context(
                pCodecCtx, pFormatCtx->streams[_streamIndex]->codecpar);
            if (ret < 0)
            {
                Log.Error("[Decoder:{CameraId}] avcodec_parameters_to_context failed: {Error}", _cameraId, FFmpegHelper.av_strerror(ret));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            // 錯誤韌性：單執行緒解碼 + 處理截斷串流 + 快速模式（防止 h264 損毀導致原生 crash）
            pCodecCtx->thread_count = 1;
            pCodecCtx->workaround_bugs |= ffmpeg.FF_BUG_TRUNCATED;
            pCodecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            ret = ffmpeg.avcodec_open2(pCodecCtx, codec, null);
            if (ret < 0)
            {
                Log.Error("[Decoder:{CameraId}] avcodec_open2 failed: {Error}", _cameraId, FFmpegHelper.av_strerror(ret));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            pPkt = ffmpeg.av_packet_alloc();
            pFrm = ffmpeg.av_frame_alloc();
            var hwFrm = _useHwAccel ? ffmpeg.av_frame_alloc() : null;

            if (pPkt == null || pFrm == null || (_useHwAccel && hwFrm == null))
            {
                Log.Error("[Decoder:{CameraId}] Failed to allocate packet/frame", _cameraId);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                if (hwFrm != null) { var f = hwFrm; ffmpeg.av_frame_free(&f); }
                return false;
            }
            _hwReceivedFrame = hwFrm;

            _frameWidth = pCodecCtx->width;
            _frameHeight = pCodecCtx->height;

            _pFormatContext = pFormatCtx;
            _pCodecContext = pCodecCtx;
            _pPacket = pPkt;
            _pFrame = pFrm;

            Log.Information("[Decoder:{CameraId}] Opened: {Path} {W}x{H} duration={Dur}s",
                _cameraId, filePath, _frameWidth, _frameHeight, _durationMicroseconds / 1_000_000);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Decoder:{CameraId}] OpenFile failed", _cameraId);
            FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);

            _pFormatContext = null;
            _pCodecContext = null;
            _pPacket = null;
            _pFrame = null;
            _converter = null;
            _streamIndex = -1;

            if (_isLiveStream)
                ErrorOccurred?.Invoke($"Stream open failed: {ex.Message}");

            return false;
        }
    }

    private static unsafe void FreeContext(
        ref AVFormatContext* pFmt, ref AVCodecContext* pCod,
        ref AVPacket* pPkt, ref AVFrame* pFrm, ref VideoFrameConverter? conv)
    {
        if (pPkt != null) { var p = pPkt; ffmpeg.av_packet_free(&p); pPkt = null; }
        if (pFrm != null) { var p = pFrm; ffmpeg.av_frame_free(&p); pFrm = null; }
        if (pCod != null) { var ctx = pCod; ffmpeg.avcodec_free_context(&ctx); pCod = null; }
        if (pFmt != null) { var p = pFmt; ffmpeg.avformat_close_input(&p); pFmt = null; }
        conv?.Dispose();
        conv = null;
    }

    private bool DecodeNextFrame(out AVFrame frame)
    {
        frame = default;
        ffmpeg.av_frame_unref(_pFrame);

        while (true)
        {
            int error;
            do
            {
                ffmpeg.av_packet_unref(_pPacket);
                error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);

                if (error == ffmpeg.AVERROR_EOF)
                    return false;

                if (error < 0)
                    return false;
            }
            while (_pPacket->stream_index != _streamIndex);

            ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket);
            ffmpeg.av_packet_unref(_pPacket);

            error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            if (error == 0)
            {
                if (_useHwAccel)
                {
                    ffmpeg.av_frame_unref(_hwReceivedFrame);
                    var hwRet = ffmpeg.av_hwframe_transfer_data(_hwReceivedFrame, _pFrame, 0);
                    if (hwRet < 0)
                    {
                        Log.Warning("[Decoder:{CameraId}] HW frame transfer failed ({Error}), retrying",
                            _cameraId, FFmpegHelper.av_strerror(hwRet));
                        continue;
                    }
                    frame = *_hwReceivedFrame;
                }
                else
                {
                    frame = *_pFrame;
                }
                return true;
            }

            if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue;

            return false;
        }
    }

    private void PerformSeek()
    {
        if (_pFormatContext == null || _streamIndex < 0)
            return;

        try
        {
            long targetPts;
            lock (_seekLock)
            {
                targetPts = _seekTargetPts;
                _seekPending = false;
            }

            var stream = _pFormatContext->streams[_streamIndex];
            var timeBase = stream->time_base;
            long targetTimestamp = targetPts * timeBase.den / (1_000_000 * timeBase.num);

            ffmpeg.av_seek_frame(_pFormatContext, _streamIndex, targetTimestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_pCodecContext);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Decoder:{CameraId}] Seek error", _cameraId);
        }
    }

    private void NotifyStatus(bool playing)
    {
        try { StatusChanged?.Invoke(playing); } catch { }
    }

    private unsafe void CleanupDecoder()
    {
        if (_hwReceivedFrame != null)
        {
            var p = _hwReceivedFrame;
            try { ffmpeg.av_frame_free(&p); } catch { }
            _hwReceivedFrame = null;
        }
        if (_pFrame != null)
        {
            var p = _pFrame;
            try { ffmpeg.av_frame_free(&p); } catch { }
            _pFrame = null;
        }
        if (_pPacket != null)
        {
            var p = _pPacket;
            try { ffmpeg.av_packet_free(&p); } catch { }
            _pPacket = null;
        }
        if (_pCodecContext != null)
        {
            var ctx = _pCodecContext;
            try
            {
                AVCodecContext** avctx = &ctx;
                ffmpeg.avcodec_free_context(avctx);
            }
            catch { }
            _pCodecContext = null;
        }
        if (_pFormatContext != null)
        {
            var p = _pFormatContext;
            try { ffmpeg.avformat_close_input(&p); } catch { }
            _pFormatContext = null;
        }
        _converter?.Dispose();
        _converter = null;
        _streamIndex = -1;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _decodeThread?.Join(3000);
        _pauseEvent.Dispose();
        _stopCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
