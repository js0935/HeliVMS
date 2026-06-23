// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace HeliVMS.Services {
    public sealed unsafe class VideoStreamDecoder : IDisposable {
        private readonly AVCodecContext* _pCodecContext;
        private AVFormatContext* _pFormatContext;
        private readonly AVFrame* _pFrame;
        private readonly AVPacket* _pPacket;
        private readonly AVFrame* _receivedFrame;
        private readonly int _streamIndex;
        private readonly CancellationToken _cancellationToken;
        private readonly AsyncRtspIO? _asyncRtsp;
        private GCHandle _gcHandle;
        /// <summary>True once avformat_open_input succeeds; Dispose uses this to choose close_input vs free_context.</summary>
        private bool _formatContextOpened;

        private delegate int InterruptDelegate(void* opaque);
        private static readonly InterruptDelegate _interruptHandler = InterruptCallback;
        private static readonly IntPtr _interruptFnPtr =
            Marshal.GetFunctionPointerForDelegate(_interruptHandler);

        public VideoStreamDecoder(string url, AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            : this(url, CancellationToken.None, HWDeviceType) { }

        public VideoStreamDecoder(string url, CancellationToken cancellationToken,
            AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            : this(url, null, cancellationToken, HWDeviceType) { }

        public VideoStreamDecoder(string url, AsyncRtspIO? asyncRtsp, CancellationToken cancellationToken,
            AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
            _cancellationToken = cancellationToken;
            _asyncRtsp = asyncRtsp;

            try {
                if (asyncRtsp != null) {
                    _pFormatContext = asyncRtsp.CreateFormatContext();
                } else {
                    _pFormatContext = ffmpeg.avformat_alloc_context();
                }
                _receivedFrame = ffmpeg.av_frame_alloc();

                // Interrupt callback: allows av_read_frame to be cancelled
                _gcHandle = GCHandle.Alloc(this, GCHandleType.WeakTrackResurrection);
                fixed (IntPtr* p = &_interruptFnPtr) {
                    _pFormatContext->interrupt_callback.callback =
                        *(AVIOInterruptCB_callback_func*)p;
                }
                _pFormatContext->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_gcHandle);

                if (asyncRtsp == null) {
                    AVDictionary* options = null;
                    ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                    ffmpeg.av_dict_set(&options, "buffer_size", "2097152", 0);     // UDP fallback socket buffer (TCP silently ignores this key)
                    ffmpeg.av_dict_set(&options, "recv_buffer_size", "2097152", 0);// Kernel TCP receive buffer — actually effective for TCP
                    ffmpeg.av_dict_set(&options, "max_delay", "100000", 0);         // 100ms max delay (ultra-low-latency)
                    ffmpeg.av_dict_set(&options, "stimeout", "20000000", 0);        // 20s socket timeout
                    ffmpeg.av_dict_set(&options, "fifo_size", "512000", 0);         // UDP fallback BFS fifo buffer (TCP silently ignores this key)
                    ffmpeg.av_dict_set(&options, "reorder_queue_size", "2000", 0);  // RTP reorder buffer: reassemble out-of-order packets
                    ffmpeg.av_dict_set(&options, "rw_timeout", "5000000", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_at_eof", "1", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_on_network_error", "1", 0);
                    ffmpeg.av_dict_set(&options, "analyzeduration", "0", 0);        // skip format analysis — open instantly
                    ffmpeg.av_dict_set(&options, "probesize", "32", 0);             // minimal probe — point-and-connect
                    ffmpeg.av_dict_set(&options, "buf_size", "1024000", 0);

                    var pFormatContext = _pFormatContext;
                    var openRet = ffmpeg.avformat_open_input(&pFormatContext, url, null, &options);
                    if (openRet < 0) {
                        // avformat_open_input failed: it already freed the context and set *ps to NULL.
                        // _pFormatContext is now a dangling pointer — prevent Dispose from touching it.
                        _pFormatContext = null;
                        ffmpeg.av_dict_free(&options);
                        openRet.ThrowExceptionIfError();
                    }
                    _pFormatContext = pFormatContext;
                    _formatContextOpened = true;
                    ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
                    ffmpeg.av_dict_free(&options);
                } else {
                    ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
                }

                AVCodec* codec = null;
                _streamIndex = ffmpeg
                    .av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0)
                    .ThrowExceptionIfError();
                _pCodecContext = ffmpeg.avcodec_alloc_context3(codec);

                if (HWDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
                    var hwRet = ffmpeg.av_hwdevice_ctx_create(
                        &_pCodecContext->hw_device_ctx, HWDeviceType, null, null, 0);
                    if (hwRet < 0) {
                        // HW init failed — fall back to software decode
                        _pCodecContext->hw_device_ctx = null;
                    }
                }

                ffmpeg.avcodec_parameters_to_context(_pCodecContext, _pFormatContext->streams[_streamIndex]->codecpar)
                    .ThrowExceptionIfError();

                // Enable multi-threaded decoding: auto-detect thread count, slice-level parallelism
                _pCodecContext->thread_count = 0;                           // 0 = FFmpeg auto-detects CPU cores
                _pCodecContext->thread_type = ffmpeg.FF_THREAD_SLICE;       // slice-level threading — multiple cores assemble ONE frame, guaranteeing top/bottom integrity
                ffmpeg.avcodec_open2(_pCodecContext, codec, null).ThrowExceptionIfError();

                CodecName = ffmpeg.avcodec_get_name(codec->id);
                FrameSize = new Size(_pCodecContext->width, _pCodecContext->height);
                PixelFormat = _pCodecContext->pix_fmt;

                _pPacket = ffmpeg.av_packet_alloc();
                _pFrame = ffmpeg.av_frame_alloc();
            } catch {
                Dispose();
                throw;
            }
        }

        private static int InterruptCallback(void* opaque) {
            try {
                var handle = GCHandle.FromIntPtr((IntPtr)opaque);
                if (handle.IsAllocated && handle.Target is VideoStreamDecoder decoder)
                    return decoder._cancellationToken.IsCancellationRequested ? 1 : 0;
            } catch { }
            return 0;
        }

        public string CodecName { get; }
        public Size FrameSize { get; private set; }
        public AVPixelFormat PixelFormat { get; private set; }

        /// <summary>
        /// Update FrameSize and PixelFormat from the decoded frame (needed when codec context reports 0x0).
        /// Returns true if the values were updated.
        /// </summary>
        public bool UpdateFromDecodedFrame(AVFrame* frame) {
            if (frame->width > 0 && frame->height > 0 && (FrameSize.Width != frame->width || FrameSize.Height != frame->height)) {
                FrameSize = new Size(frame->width, frame->height);
                PixelFormat = (AVPixelFormat)frame->format;
                return true;
            }
            return false;
        }

        public void Dispose() {
            if (_pFormatContext is not null) {
                try { _pFormatContext->interrupt_callback = default; } catch { }
            }

            try { var pFrame = _pFrame; ffmpeg.av_frame_free(&pFrame); } catch { }
            try { var pPacket = _pPacket; ffmpeg.av_packet_free(&pPacket); } catch { }

            try { if (_pCodecContext != null) { var pCtx = _pCodecContext; ffmpeg.avcodec_free_context(&pCtx); } } catch { }

            // Format context: close_input if fully opened, free_context if only allocated, skip if null
            if (_pFormatContext is not null) {
                try {
                    if (_formatContextOpened) {
                        var p = _pFormatContext;
                        ffmpeg.avformat_close_input(&p);
                    } else if (_asyncRtsp == null) {
                        // Allocated via avformat_alloc_context but avformat_open_input never ran (or failed
                        // before calling it). avformat_close_input is invalid here — just free the struct.
                        ffmpeg.avformat_free_context(_pFormatContext);
                    }
                } catch { }
                _pFormatContext = null;
            }

            try { if (_receivedFrame is not null) { var p = _receivedFrame; ffmpeg.av_frame_free(&p); } } catch { }

            try { if (_gcHandle.IsAllocated) _gcHandle.Free(); } catch { }
        }

        public bool TryDecodeNextFrame(out AVFrame frame) {
            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_frame_unref(_receivedFrame);
            int error;

            do {
                try {
                    do {
                        ffmpeg.av_packet_unref(_pPacket);
                        error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);

                        if (error == ffmpeg.AVERROR_EOF) {
                            // Flush decoder to drain remaining frames
                            ffmpeg.avcodec_send_packet(_pCodecContext, null).ThrowExceptionIfError();
                            while (true) {
                                var flushRet = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
                                if (flushRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || flushRet == ffmpeg.AVERROR_EOF) {
                                    break;
                                }
                                flushRet.ThrowExceptionIfError();
                                frame = *_pFrame;
                                return true;
                            }
                            frame = *_pFrame;
                            return false;
                        }

                        error.ThrowExceptionIfError();
                    } while (_pPacket->stream_index != _streamIndex);

                    ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket).ThrowExceptionIfError();
                } finally {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            if (error < 0) error.ThrowExceptionIfError();

            // If HW device context is present, try transferring from GPU to system memory
            if (_pCodecContext->hw_device_ctx is not null) {
                var hwRet = ffmpeg.av_hwframe_transfer_data(_receivedFrame, _pFrame, 0);
                if (hwRet >= 0) {
                    frame = *_receivedFrame;
                    // HW transfer changes pixel format to SW format (e.g. NV12) — update for consumers
                    PixelFormat = (AVPixelFormat)frame.format;
                    UpdateFromDecodedFrame(_receivedFrame);
                } else {
                    // HW transfer failed — skip this frame instead of passing GPU-resident memory to sws_scale
                    frame = default;
                    return false;
                }
            } else {
                frame = *_pFrame;
            }

            return true;
        }

        /// <summary>
        /// Read and discard one packet from the stream without decoding.
        /// Used by the throttling path to drain network buffers when emitting frames at target rate.
        /// </summary>
        /// <returns>true if a packet was consumed; false on EOF/error</returns>
        public bool TrySkipNextPacket() {
            ffmpeg.av_packet_unref(_pPacket);
            var error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);
            if (error == ffmpeg.AVERROR_EOF) return false;
            if (error < 0) return false;
            ffmpeg.av_packet_unref(_pPacket);
            return true;
        }

        public class FFmpegException(string message, int errorCode) : Exception(message) {
            public int ErrorCode { get; } = errorCode;
        }
    }
}

namespace HeliVMS.Services {
    internal static class FFmpegExtensions {
        /// <summary>FFmpeg.AutoGen v8.0.0.1 missing ThrowExceptionIfError — reimplemented here</summary>
        internal static int ThrowExceptionIfError(this int error) {
            if (error < 0) {
                throw new VideoStreamDecoder.FFmpegException($"FFmpeg error code: {error}", error);
            }
            return error;
        }
    }
}
