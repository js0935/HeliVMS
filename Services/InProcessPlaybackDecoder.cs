using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using HeliVMS.Controls;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// 同程序播放解碼器 — 在 VMS 主行程內直接使用 FFmpeg.AutoGen 進行檔案解碼。<br/>
/// 優勢：無外部程序、無 Named Pipe、無序列化開銷。<br/>
/// 劣勢：解碼器崩潰會直接影響主行程（不像 DecoderSession 可隔離崩潰）。<br/>
/// 支援播放速率控制、動態 Seek、目標 FPS 限制、解析度縮放。
/// </summary>
public sealed unsafe class InProcessPlaybackDecoder(string cameraId) : IPlaybackDecoder {
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
    /// <summary>解碼迴圈是否繼續執行</summary>
    private volatile bool _isPlaying;
    /// <summary>暫停狀態（volatile 確保跨執行緒可見性）</summary>
    private volatile bool _isPaused;
    /// <summary>暫停事件：Set = 繼續，Reset = 暫停</summary>
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private double _playbackRate = 1.0;
    /// <summary>倍速播放的累計幀計數器（用於降採樣）</summary>
    private double _frameAccumulator;
    /// <summary>目標幀間隔（毫秒），根據 targetFps 計算</summary>
    private int _baseFrameMs = 33;

    private readonly Lock _seekLock = new();
    private volatile bool _seekPending;
    private long _seekTargetPts = AV_NOPTS_VALUE;

    private const long AV_NOPTS_VALUE = unchecked((long)0x8000000000000000);

    private Thread? _decodeThread;

    public string CameraId => _cameraId;
    public long DurationMicroseconds => _durationMicroseconds;
    public bool IsRunning => _decodeThread is { IsAlive: true };

    /// <summary>解出一幀畫面，交予 PlaybackCoordinator 進行 PTS 同步</summary>
    public event Action<PooledBuffer>? FrameReady;
    /// <summary>播放位置變更通知</summary>
    public event Action<long, long>? PositionChanged;
    /// <summary>播放/暫停狀態變更</summary>
    public event Action<bool>? PlaybackStatusChanged;
    /// <summary>檔案播放完畢</summary>
    public event Action? EOFReached;

    public void Open(string filePath, long seekUs = 0, double? targetFps = null, int targetDecodeHeight = 0) {
        _targetDecodeHeight = targetDecodeHeight;
        if (_decodeThread is { IsAlive: true })
            Stop();

        if (targetFps.HasValue)
            _baseFrameMs = targetFps.Value > 0 ? (int)Math.Clamp(1000.0 / targetFps.Value, 33, 500) : 33;

        _stopCts = new CancellationTokenSource();
        var token = _stopCts.Token;

        _decodeThread = new Thread(() => DecodeLoop(filePath, seekUs, token)) {
            Name = $"InProcDecode-{_cameraId}",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _decodeThread.Start();
    }

    public void Pause() {
        _isPaused = true;
        _pauseEvent.Reset();
    }

    public void Resume() {
        _isPaused = false;
        _pauseEvent.Set();
    }

    public void Seek(long microseconds) {
        lock (_seekLock) {
            _seekTargetPts = microseconds;
            _seekPending = true;
        }
    }

    public void SetPlaybackRate(double rate) {
        _playbackRate = Math.Clamp(rate, 0.25, 32.0);
        _frameAccumulator = 0;
    }

    public void SetTargetFps(double fps) {
        _baseFrameMs = fps > 0 ? (int)Math.Clamp(1000.0 / fps, 33, 500) : 33;
    }

    public void Stop() {
        _pauseEvent.Set();
        _stopCts?.Cancel();
        _isPlaying = false;
        _isPaused = false;
    }

    /// <summary>
    /// 解碼主迴圈（在專屬執行緒上執行）：<br/>
    /// 1. 開啟檔案 → 2. Seek 至起始點 → 3. 循環 DecodeNextFrame<br/>
    /// • 支援暫停/繼續 (ManualResetEventSlim)<br/>
    /// • 支援倍速播放（frameAccumulator 降採樣）<br/>
    /// • 首次解出幀時建立 VideoFrameConverter（已知原始解析度）<br/>
    /// • 使用 NativeMemory 直接轉換到 PooledBuffer（零託管分配）<br/>
    /// • 每 100ms 回報一次播放位置
    /// </summary>
    private void DecodeLoop(string filePath, long initialSeekUs, CancellationToken token) {


        try {
            if (!OpenFile(filePath)) {
                NotifyStatus(false);
                return;
            }

            if (initialSeekUs > 0) {
                lock (_seekLock) {
                    _seekTargetPts = initialSeekUs;
                    _seekPending = true;
                }
            }

            _isPlaying = true;
            NotifyStatus(true);

            var frameStopwatch = Stopwatch.StartNew();
            var positionTimer = Stopwatch.StartNew();
            long lastReportedPts = 0;

            while (!token.IsCancellationRequested && _isPlaying) {
                if (_seekPending) {
                    PerformSeek();
                    frameStopwatch.Restart();
                    positionTimer.Restart();
                }

                if (_isPaused) {
                    try { _pauseEvent.Wait(token); } catch (OperationCanceledException) { break; }
                    continue;
                }

                try {
                    frameStopwatch.Restart();

                    if (DecodeNextFrame(out var frame)) {
                        var pts = frame.pts;
                        if (pts != AV_NOPTS_VALUE) {
                            var stream = _pFormatContext->streams[_streamIndex];
                            var timeBase = stream->time_base;
                            var ptsUs = pts * timeBase.num * 1_000_000 / timeBase.den;
                            lastReportedPts = ptsUs;

                            if (positionTimer.ElapsedMilliseconds >= 100) {
                                PositionChanged?.Invoke(ptsUs, _durationMicroseconds);
                                positionTimer.Restart();
                            }
                        }

                        // Recreate converter if pixel format changed (HW decode → SW transfer)
                        if (_converter != null && frame.width > 0 && frame.height > 0 &&
                            ((AVPixelFormat)frame.format != _converter.SourcePixelFormat ||
                             _converter.DestinationPixelFormat != AVPixelFormat.AV_PIX_FMT_BGRA)) {
                            _converter.Dispose();
                            _converter = null;
                        }

                        // 延遲初始化轉換器：取得第一幀的實際解析度後才建立
                        if (_converter == null && frame.width > 0 && frame.height > 0) {
                            var sourceSize = new Size(frame.width, frame.height);
                            var destSize = ComputeDestSize(sourceSize);
                            var sourcePixelFormat = (AVPixelFormat)frame.format;
                            var destPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                            _converter = new VideoFrameConverter(sourceSize, sourcePixelFormat, destSize, destPixelFormat);
                            _frameWidth = destSize.Width;
                            _frameHeight = destSize.Height;
                            Log.Information("[InProcDecoder:{CameraId}] Resolution: {W}x{H} (target height {TargetH})",
                                _cameraId, _frameWidth, _frameHeight, _targetDecodeHeight);
                        }

                        // 倍速播放降採樣：frameAccumulator 計算是否應顯示此幀
                        var displayThisFrame = true;
                        if (_playbackRate > 1.0) {
                            _frameAccumulator += _playbackRate;
                            displayThisFrame = _frameAccumulator >= 1.0;
                            if (displayThisFrame)
                                _frameAccumulator -= 1.0;
                        }

                        if (displayThisFrame && _converter != null) {
                            var bufferSize = _converter.GetBufferSize();

                            var pb = new PooledBuffer(bufferSize);
                            _converter.ConvertTo(frame, pb.DataPtr);
                            pb.Width = _frameWidth;
                            pb.Height = _frameHeight;
                            pb.DataSize = bufferSize;
                            pb.PtsMicroseconds = lastReportedPts;

                            FrameReady?.Invoke(pb);
                        }

                        // 根據 targetFps 與播放速率控制幀輸出間隔
                        if (displayThisFrame) {
                            var targetFrameMs = (int)(_baseFrameMs / _playbackRate);
                            var elapsed = (int)frameStopwatch.ElapsedMilliseconds;
                            var remaining = targetFrameMs - elapsed;
                            if (remaining > 1)
                                Thread.Sleep(remaining);
                        }
                    } else {
                        Log.Debug("[InProcDecoder:{CameraId}] EOF reached", _cameraId);
                        try { EOFReached?.Invoke(); } catch { }
                        return;
                    }
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    Log.Warning(ex, "[InProcDecoder:{CameraId}] Decode error", _cameraId);
                    Thread.Sleep(33);
                }
            }
        } catch (Exception ex) {
            Log.Error(ex, "[InProcDecoder:{CameraId}] Decode loop fatal", _cameraId);
        } finally {
            _isPlaying = false;
            NotifyStatus(false);
            CleanupDecoder();
        }
    }

    public void Dispose() {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _decodeThread?.Join(3000);
        _pauseEvent.Dispose();
        _stopCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private Size ComputeDestSize(Size source) {
        if (_targetDecodeHeight <= 0 || _targetDecodeHeight >= source.Height)
            return source;
        var w = source.Width * _targetDecodeHeight / source.Height;
        if (w < 2) w = 2;
        return new Size(w & ~1, _targetDecodeHeight & ~1);
    }

    /// <summary>
    /// 開啟影片檔案並初始化 FFmpeg 解碼器：<br/>
    /// 1. avformat_open_input — 開啟多媒體容器<br/>
    /// 2. avformat_find_stream_info — 取得串流資訊<br/>
    /// 3. av_find_best_stream — 找到最佳視訊串流<br/>
    /// 4. avcodec_open2 — 初始化視訊解碼器<br/>
    /// 解碼加速設定：thread_count=1（單執行緒）、FF_BUG_TRUNCATED + AV_CODEC_FLAG2_FAST
    /// </summary>
    private bool OpenFile(string filePath) {
        AVFormatContext* pFormatCtx = null;
        AVCodecContext* pCodecCtx = null;
        AVPacket* pPkt = null;
        AVFrame* pFrm = null;
        VideoFrameConverter? converter = null;

        try {
            pFormatCtx = ffmpeg.avformat_alloc_context();
            if (pFormatCtx == null) {
                Log.Error("[InProcDecoder:{CameraId}] avformat_alloc_context returned null", _cameraId);
                return false;
            }

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);

            var pCtx = pFormatCtx;
            var openRet = ffmpeg.avformat_open_input(&pCtx, filePath, null, &options);
            pFormatCtx = pCtx;
            if (openRet < 0) {
                Log.Error("[InProcDecoder:{CameraId}] avformat_open_input failed: {Error} (url={Url})",
                    _cameraId, AvErrorString(openRet), filePath);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            var infoRet = ffmpeg.avformat_find_stream_info(pFormatCtx, null);
            if (infoRet < 0) {
                Log.Error("[InProcDecoder:{CameraId}] avformat_find_stream_info failed: {Error}",
                    _cameraId, AvErrorString(infoRet));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            AVCodec* codec = null;
            var streamRet = ffmpeg.av_find_best_stream(
                pFormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (streamRet < 0) {
                Log.Error("[InProcDecoder:{CameraId}] av_find_best_stream failed: {Error}",
                    _cameraId, AvErrorString(streamRet));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }
            _streamIndex = streamRet;

            // 從容器 duration 或串流 duration 取得總時長
            _durationMicroseconds = pFormatCtx->duration;
            if (_durationMicroseconds <= 0 && _streamIndex >= 0) {
                var st = pFormatCtx->streams[_streamIndex];
                if (st->duration > 0) {
                    var tb = st->time_base;
                    _durationMicroseconds = st->duration * tb.num * 1_000_000 / tb.den;
                }
            }
            if (_durationMicroseconds <= 0)
                _durationMicroseconds = 0;

            if (codec == null) {
                Log.Error("[InProcDecoder:{CameraId}] No suitable codec found", _cameraId);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            pCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (pCodecCtx == null) {
                Log.Error("[InProcDecoder:{CameraId}] avcodec_alloc_context3 returned null", _cameraId);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            var ret = ffmpeg.avcodec_parameters_to_context(
                pCodecCtx, pFormatCtx->streams[_streamIndex]->codecpar);
            if (ret < 0) {
                Log.Error("[InProcDecoder:{CameraId}] avcodec_parameters_to_context failed: {Error}",
                    _cameraId, AvErrorString(ret));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            pCodecCtx->thread_count = 1;
            pCodecCtx->workaround_bugs |= ffmpeg.FF_BUG_TRUNCATED;
            pCodecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            ret = ffmpeg.avcodec_open2(pCodecCtx, codec, null);
            if (ret < 0) {
                Log.Error("[InProcDecoder:{CameraId}] avcodec_open2 failed: {Error}",
                    _cameraId, AvErrorString(ret));
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            pPkt = ffmpeg.av_packet_alloc();
            pFrm = ffmpeg.av_frame_alloc();

            if (pPkt == null || pFrm == null) {
                Log.Error("[InProcDecoder:{CameraId}] Failed to allocate packet/frame", _cameraId);
                FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
                return false;
            }

            _frameWidth = pCodecCtx->width;
            _frameHeight = pCodecCtx->height;

            _pFormatContext = pFormatCtx;
            _pCodecContext = pCodecCtx;
            _pPacket = pPkt;
            _pFrame = pFrm;

            Log.Information("[InProcDecoder:{CameraId}] Opened: {Path} {W}x{H} duration={Dur}s",
                _cameraId, filePath, _frameWidth, _frameHeight, _durationMicroseconds / 1_000_000);
            return true;
        } catch (Exception ex) {
            Log.Error(ex, "[InProcDecoder:{CameraId}] OpenFile failed", _cameraId);
            FreeContext(ref pFormatCtx, ref pCodecCtx, ref pPkt, ref pFrm, ref converter);
            _pFormatContext = null;
            _pCodecContext = null;
            _pPacket = null;
            _pFrame = null;
            _converter = null;
            _streamIndex = -1;
            return false;
        }
    }

    private static unsafe void FreeContext(
        ref AVFormatContext* pFmt, ref AVCodecContext* pCod,
        ref AVPacket* pPkt, ref AVFrame* pFrm, ref VideoFrameConverter? conv) {
        if (pPkt != null) { var p = pPkt; ffmpeg.av_packet_free(&p); pPkt = null; }
        if (pFrm != null) { var p = pFrm; ffmpeg.av_frame_free(&p); pFrm = null; }
        if (pCod != null) { var ctx = pCod; ffmpeg.avcodec_free_context(&ctx); pCod = null; }
        if (pFmt != null) { var p = pFmt; ffmpeg.avformat_close_input(&p); pFmt = null; }
        conv?.Dispose();
        conv = null;
    }

    /// <summary>
    /// 解碼下一幀：<br/>
    /// 1. av_read_frame 讀取下一個封包（跳過非視訊串流）<br/>
    /// 2. avcodec_send_packet 送入解碼器<br/>
    /// 3. avcodec_receive_frame 取得解碼後的幀<br/>
    /// 若解碼器需要更多封包 (EAGAIN) 則回到步驟 1
    /// </summary>
    private bool DecodeNextFrame(out AVFrame frame) {
        frame = default;
        ffmpeg.av_frame_unref(_pFrame);

        while (true) {
            int error;
            do {
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
            if (error == 0) {
                frame = *_pFrame;
                return true;
            }

            if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue;

            return false;
        }
    }

    /// <summary>
    /// 執行 Seek 操作：將目標微秒轉換為串流時間基底後呼叫 av_seek_frame。<br/>
    /// seekLock 確保 Seek 與 DecodeNextFrame 間的原子性，避免競爭條件。
    /// </summary>
    private void PerformSeek() {
        if (_pFormatContext == null || _streamIndex < 0)
            return;

        try {
            long targetPts;
            lock (_seekLock) {
                targetPts = _seekTargetPts;
                _seekPending = false;
            }

            var stream = _pFormatContext->streams[_streamIndex];
            var timeBase = stream->time_base;
            var targetTimestamp = targetPts * timeBase.den / (1_000_000 * timeBase.num);

            ffmpeg.av_seek_frame(_pFormatContext, _streamIndex, targetTimestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_pCodecContext);
        } catch (Exception ex) {
            Log.Warning(ex, "[InProcDecoder:{CameraId}] Seek error", _cameraId);
        }
    }

    private static string AvErrorString(int errorCode) {
        try {
            var buf = new byte[256];
            fixed (byte* p = buf) {
                var ret = ffmpeg.av_strerror(errorCode, p, (ulong)buf.Length);
                if (ret >= 0) {
                    var len = Array.IndexOf<byte>(buf, 0);
                    return len >= 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, len) : errorCode.ToString();
                }
            }
        } catch { }
        return $"error #{errorCode}";
    }

    private void NotifyStatus(bool playing) {
        try { PlaybackStatusChanged?.Invoke(playing); } catch { }
    }

    private unsafe void CleanupDecoder() {
        if (_pFrame != null) {
            var p = _pFrame;
            try { ffmpeg.av_frame_free(&p); } catch { }
            _pFrame = null;
        }
        if (_pPacket != null) {
            var p = _pPacket;
            try { ffmpeg.av_packet_free(&p); } catch { }
            _pPacket = null;
        }
        if (_pCodecContext != null) {
            var ctx = _pCodecContext;
            try {
                var avctx = &ctx;
                ffmpeg.avcodec_free_context(avctx);
            } catch { }
            _pCodecContext = null;
        }
        if (_pFormatContext != null) {
            var p = _pFormatContext;
            try { ffmpeg.avformat_close_input(&p); } catch { }
            _pFormatContext = null;
        }
        _converter?.Dispose();
        _converter = null;
        _streamIndex = -1;
    }
}
