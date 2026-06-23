using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// 同程序錄影引擎 — 使用 FFmpeg.AutoGen 直接對 RTSP 串流進行分段錄影。<br/>
/// 取代傳統 ffmpeg.exe 的執行緒/行程開銷，支援兩種 RTSP 取得方式：<br/>
/// 1. FFmpeg 內建 RTSP demuxer（預設）<br/>
/// 2. AsyncRtspIO（使用自製非同步 RTSP + 原始 H264/H265 demuxer）— 非阻塞 IO
/// </summary>
public sealed unsafe class SegmentRecorder(string rtspUrl, string outputPattern, string cameraId, int segmentSeconds = 600) : IDisposable {
    private AVFormatContext* _inputCtx;
    private AVFormatContext* _outputCtx;
    private int _videoStreamIndex = -1;
    private long _bytesWritten;
    private AsyncRtspIO? _asyncRtsp;

    private Thread? _recordThread;
    private CancellationTokenSource? _cts;
    private readonly string _rtspUrl = rtspUrl;
    private readonly string _outputPattern = outputPattern;
    private readonly string _cameraId = cameraId;
    private readonly int _segmentSeconds = segmentSeconds;
    /// <summary>執行緒安全停止旗標（volatile 確保跨執行緒可見性）</summary>
    private volatile bool _isRunning;

    /// <summary>新 segment 產生時觸發（參數：segment 檔案路徑），供 VideoIndexService 即時索引</summary>
    public event Action<string>? SegmentCreated;
    /// <summary>錄影錯誤時觸發（參數：錯誤訊息）</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>啟用 AsyncRtspIO 而非 FFmpeg 內建 RTSP demuxer</summary>
    public bool UseAsyncRtsp { get; set; }

    public bool IsRunning => _isRunning;
    public long BytesWritten => _bytesWritten;

    public void Start() {
        if (_isRunning) return;
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _recordThread = new Thread(RecordLoop) {
            Name = $"Rec-{_cameraId}",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _recordThread.Start();
    }

    public void Stop() {
        _isRunning = false;
        _cts?.Cancel();
        _recordThread?.Join(5000);
        CleanupOutput();
        CleanupInput();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 錄影主迴圈：<br/>
    /// 1. OpenInput() — 開啟 RTSP 連線（含指數退避重連，最多 10 次）<br/>
    /// 2. CreateSegmentOutput() — 建立 segment muxer（或 mpegts 回退）<br/>
    /// 3. RecordSegment() — 持續讀取並寫入封包，直到斷線或異常<br/>
    /// 錄影斷線後返回步驟 1 自動重連
    /// </summary>
    private void RecordLoop() {
        const int maxReconnect = 10;
        var reconnectCount = 0;

        try {
            while (!_cts!.Token.IsCancellationRequested && _isRunning) {
                if (!OpenInput()) {
                    if (reconnectCount++ < maxReconnect) {
                        var delay = 3000 * Math.Min(reconnectCount, 5);
                        _cts.Token.WaitHandle.WaitOne(delay);
                        continue;
                    }
                    ErrorOccurred?.Invoke("Max reconnection attempts reached");
                    return;
                }
                reconnectCount = 0;

                while (!_cts.Token.IsCancellationRequested && _isRunning) {
                    if (!CreateSegmentOutput())
                        break;

                    var ok = RecordSegment();
                    CleanupOutput();

                    if (!ok) break;
                }

                CleanupInput();
                if (!_cts.Token.IsCancellationRequested && _isRunning)
                    _cts.Token.WaitHandle.WaitOne(1000);
            }
        } catch (Exception ex) {
            Log.Error(ex, "[SegmentRecorder:{CameraId}] Record loop fatal", _cameraId);
        } finally {
            CleanupOutput();
            CleanupInput();
            _isRunning = false;
        }
    }

    /// <summary>
    /// 開啟 RTSP 輸入串流：<br/>
    /// • UseAsyncRtsp=true → 使用 AsyncRtspIO 自訂非同步 RTSP 連線 + 原始 H264/H265 demuxer<br/>
    /// • UseAsyncRtsp=false → 使用 FFmpeg 內建 RTSP demuxer（預設）
    /// </summary>
    private bool OpenInput() {
        try {
            if (UseAsyncRtsp) {
                _asyncRtsp?.Dispose();
                _asyncRtsp = new AsyncRtspIO(_rtspUrl, _cameraId);
                if (!_asyncRtsp.OpenAsync().GetAwaiter().GetResult())
                    return false;

                _inputCtx = _asyncRtsp.CreateFormatContext();

                var r1 = ffmpeg.avformat_find_stream_info(_inputCtx, null);
                if (r1 < 0) return false;

                AVCodec* c1 = null;
                _videoStreamIndex = ffmpeg.av_find_best_stream(
                    _inputCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &c1, 0);
                if (_videoStreamIndex < 0) return false;

                return true;
            }

            var ctx = ffmpeg.avformat_alloc_context();
            if (ctx == null) return false;

            // FFmpeg RTSP 連線選項：TCP 傳輸、5 秒超時、網路異常自動重連
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&opts, "rw_timeout", "5000000", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_at_eof", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_streamed", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_on_network_error", "1", 0);
            ffmpeg.av_dict_set(&opts, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&opts, "analyzeduration", "50000000", 0);
            ffmpeg.av_dict_set(&opts, "probesize", "50000000", 0);

            var pCtx = ctx;
            var ret = ffmpeg.avformat_open_input(&pCtx, _rtspUrl, null, &opts);
            ctx = pCtx;
            ffmpeg.av_dict_free(&opts);
            if (ret < 0) return false;

            ret = ffmpeg.avformat_find_stream_info(ctx, null);
            if (ret < 0) return false;

            AVCodec* codec = null;
            _videoStreamIndex = ffmpeg.av_find_best_stream(
                ctx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (_videoStreamIndex < 0) return false;

            _inputCtx = ctx;
            return true;
        } catch (Exception ex) {
            Log.Error(ex, "[SegmentRecorder:{CameraId}] OpenInput failed", _cameraId);
            return false;
        }
    }

    /// <summary>
    /// 使用 FFmpeg segment muxer 建立分段輸出。<br/>
    /// 優點：segment_time + segment_atclocktime + strftime 可自動產生按時分割的檔案。<br/>
    /// 若 FFmpeg 編譯時未啟用 segment muxer（write_header 失敗），則退回 FallbackMpegtsOutput。
    /// </summary>
    private bool CreateSegmentOutput() {
        try {
            AVFormatContext* ctx = null;
            ffmpeg.avformat_alloc_output_context2(&ctx, null, "segment", _outputPattern);
            if (ctx == null) return false;

            var st = ffmpeg.avformat_new_stream(ctx, null);
            if (st == null) { ffmpeg.avformat_free_context(ctx); return false; }

            var inputSt = _inputCtx->streams[_videoStreamIndex];
            var codecpar = st->codecpar;
            ffmpeg.avcodec_parameters_copy(codecpar, inputSt->codecpar);
            codecpar->codec_tag = 0;
            st->time_base = inputSt->time_base;

            // 分段參數：600 秒/段、對齊時鐘、日期時間檔名、重置時間戳
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "segment_time", _segmentSeconds.ToString(), 0);
            ffmpeg.av_dict_set(&opts, "segment_atclocktime", "1", 0);
            ffmpeg.av_dict_set(&opts, "strftime", "1", 0);
            ffmpeg.av_dict_set(&opts, "reset_timestamps", "1", 0);

            var ret = ffmpeg.avformat_write_header(ctx, &opts);
            ffmpeg.av_dict_free(&opts);
            if (ret < 0) {
                ffmpeg.avformat_free_context(ctx);
                return FallbackMpegtsOutput();
            }

            _outputCtx = ctx;
            return true;
        } catch (Exception ex) {
            Log.Error(ex, "[SegmentRecorder:{CameraId}] CreateSegmentOutput failed", _cameraId);
            return false;
        }
    }

    /// <summary>
    /// 回退方案：使用 mpegts muxer + 手動 ExpandPattern() 產生檔案名稱。<br/>
    /// 當 segment muxer 不可用時（如部分 FFmpeg 編譯版本），仍可正常分段錄影。
    /// segment 完成時觸發 SegmentCreated 事件供外部即時索引。
    /// </summary>
    private bool FallbackMpegtsOutput() {

        try {
            AVFormatContext* ctx = null;
            ffmpeg.avformat_alloc_output_context2(&ctx, null, "mpegts", null);
            if (ctx == null) return false;

            var st = ffmpeg.avformat_new_stream(ctx, null);
            if (st == null) { ffmpeg.avformat_free_context(ctx); return false; }

            var inputSt = _inputCtx->streams[_videoStreamIndex];
            var codecpar = st->codecpar;
            ffmpeg.avcodec_parameters_copy(codecpar, inputSt->codecpar);
            codecpar->codec_tag = 0;
            st->time_base = inputSt->time_base;

            var filePath = ExpandPattern(_outputPattern);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var ret = ffmpeg.avio_open(&ctx->pb, filePath, ffmpeg.AVIO_FLAG_WRITE);
            if (ret < 0) { ffmpeg.avformat_free_context(ctx); return false; }

            ret = ffmpeg.avformat_write_header(ctx, null);
            if (ret < 0) { ffmpeg.avformat_free_context(ctx); return false; }

            _outputCtx = ctx;
            SegmentCreated?.Invoke(filePath);
            return true;
        } catch (Exception ex) {
            Log.Error(ex, "[SegmentRecorder:{CameraId}] FallbackMpegtsOutput failed", _cameraId);
            return false;
        }
    }

    private bool RecordSegment() {
        var packet = ffmpeg.av_packet_alloc();
        try {
            while (!_cts!.Token.IsCancellationRequested && _isRunning) {
                ffmpeg.av_packet_unref(packet);
                var ret = ffmpeg.av_read_frame(_inputCtx, packet);

                if (ret == ffmpeg.AVERROR_EOF) {
                    _cts.Token.WaitHandle.WaitOne(3000);
                    return false;
                }
                if (ret < 0) {
                    Thread.Sleep(100);
                    continue;
                }

                if (packet->stream_index != _videoStreamIndex)
                    continue;

                var outCtx = _outputCtx;
                if (outCtx == null) continue;

                packet->stream_index = 0;
                var writeRet = ffmpeg.av_interleaved_write_frame(outCtx, packet);
                if (writeRet >= 0)
                    _bytesWritten += packet->size;
            }
            return true;
        } catch (Exception ex) {
            Log.Error(ex, "[SegmentRecorder:{CameraId}] RecordSegment error", _cameraId);
            return false;
        } finally {
            var p = packet;
            ffmpeg.av_packet_free(&p);
        }
    }

    private void CleanupOutput() {
        try {
            var ctx = _outputCtx;
            if (ctx != null) {
                ffmpeg.av_write_trailer(ctx);
                if (ctx->pb != null && (ctx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_close(ctx->pb);
                ffmpeg.avformat_free_context(ctx);
            }
        } catch (Exception ex) {
            Log.Warning(ex, "[SegmentRecorder:{CameraId}] CleanupOutput error", _cameraId);
        }
        _outputCtx = null;
    }

    private void CleanupInput() {
        if (_asyncRtsp != null) {
            _inputCtx = null;
            _asyncRtsp.Dispose();
            _asyncRtsp = null;
            _videoStreamIndex = -1;
            return;
        }

        try {
            var ctx = _inputCtx;
            if (ctx != null) {
                var p = ctx;
                ffmpeg.avformat_close_input(&p);
            }
        } catch (Exception ex) {
            Log.Warning(ex, "[SegmentRecorder:{CameraId}] CleanupInput error", _cameraId);
        }
        _inputCtx = null;
        _videoStreamIndex = -1;
    }

    private static string ExpandPattern(string pattern) {
        var now = DateTime.Now;
        return pattern
            .Replace("%Y", now.ToString("yyyy"))
            .Replace("%m", now.ToString("MM"))
            .Replace("%d", now.ToString("dd"))
            .Replace("%H", now.ToString("HH"))
            .Replace("%M", now.ToString("mm"))
            .Replace("%S", now.ToString("ss"));
    }

    public void Dispose() {
        Stop();
        _cts?.Dispose();
    }
}
