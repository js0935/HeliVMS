// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace HeliVMS.Services
{
    public sealed unsafe class VideoStreamDecoder : IDisposable
    {
        private readonly AVCodecContext* _pCodecContext;
        private readonly AVFormatContext* _pFormatContext;
        private readonly AVFrame* _pFrame;
        private readonly AVPacket* _pPacket;
        private readonly AVFrame* _receivedFrame;
        private readonly int _streamIndex;
        private readonly CancellationToken _cancellationToken;
        private GCHandle _gcHandle;

        private delegate int InterruptDelegate(void* opaque);
        private static readonly InterruptDelegate _interruptHandler = InterruptCallback;
        private static readonly IntPtr _interruptFnPtr =
            Marshal.GetFunctionPointerForDelegate(_interruptHandler);

        public VideoStreamDecoder(string url, AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            : this(url, CancellationToken.None, HWDeviceType) { }

        public VideoStreamDecoder(string url, CancellationToken cancellationToken,
            AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            _cancellationToken = cancellationToken;

            _pFormatContext = ffmpeg.avformat_alloc_context();
            _receivedFrame = ffmpeg.av_frame_alloc();

            // Interrupt callback: allows av_read_frame to be cancelled
            _gcHandle = GCHandle.Alloc(this, GCHandleType.WeakTrackResurrection);
            fixed (IntPtr* p = &_interruptFnPtr)
            {
                _pFormatContext->interrupt_callback.callback =
                    *(AVIOInterruptCB_callback_func*)p;
            }
            _pFormatContext->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_gcHandle);

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&options, "stimeout", "5000000", 0);
            // rw_timeout: socket I/O timeout to prevent av_read_frame from hanging indefinitely
            ffmpeg.av_dict_set(&options, "rw_timeout", "5000000", 0);
            // reconnect_*: FFmpeg auto-reconnect on network error for RTSP
            ffmpeg.av_dict_set(&options, "reconnect_at_eof", "1", 0);
            ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);
            ffmpeg.av_dict_set(&options, "reconnect_on_network_error", "1", 0);
            // nobuffer for low-latency streaming
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&options, "buf_size", "1024000", 0);
            ffmpeg.av_dict_set(&options, "analyzeduration", "50000000", 0);
            ffmpeg.av_dict_set(&options, "probesize", "50000000", 0);

            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_open_input(&pFormatContext, url, null, &options).ThrowExceptionIfError();
            ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
            ffmpeg.av_dict_free(&options);

            AVCodec* codec = null;
            _streamIndex = ffmpeg
                .av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0)
                .ThrowExceptionIfError();
            _pCodecContext = ffmpeg.avcodec_alloc_context3(codec);

            if (HWDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                ffmpeg.av_hwdevice_ctx_create(&_pCodecContext->hw_device_ctx, HWDeviceType, null, null, 0)
                    .ThrowExceptionIfError();
            }

            ffmpeg.avcodec_parameters_to_context(_pCodecContext, _pFormatContext->streams[_streamIndex]->codecpar)
                .ThrowExceptionIfError();
            ffmpeg.avcodec_open2(_pCodecContext, codec, null).ThrowExceptionIfError();

            CodecName = ffmpeg.avcodec_get_name(codec->id);
            FrameSize = new Size(_pCodecContext->width, _pCodecContext->height);
            PixelFormat = _pCodecContext->pix_fmt;

            _pPacket = ffmpeg.av_packet_alloc();
            _pFrame = ffmpeg.av_frame_alloc();
        }

        private static int InterruptCallback(void* opaque)
        {
            try
            {
                var handle = GCHandle.FromIntPtr((IntPtr)opaque);
                if (handle.IsAllocated && handle.Target is VideoStreamDecoder decoder)
                    return decoder._cancellationToken.IsCancellationRequested ? 1 : 0;
            }
            catch { }
            return 0;
        }

        public string CodecName { get; }
        public Size FrameSize { get; private set; }
        public AVPixelFormat PixelFormat { get; private set; }

        /// <summary>
        /// Update FrameSize and PixelFormat from the decoded frame (needed when codec context reports 0x0).
        /// Returns true if the values were updated.
        /// </summary>
        public bool UpdateFromDecodedFrame(AVFrame* frame)
        {
            if (frame->width > 0 && frame->height > 0 && (FrameSize.Width != frame->width || FrameSize.Height != frame->height))
            {
                FrameSize = new Size(frame->width, frame->height);
                PixelFormat = (AVPixelFormat)frame->format;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            // Clear interrupt callback before avformat_close_input to avoid stale callback
            // GC handle cleanup: Free before close to prevent crash
            try { _pFormatContext->interrupt_callback = default; } catch { }

            // Wrap native pointer cleanup in try-catch to prevent crash
            try { var pFrame = _pFrame; ffmpeg.av_frame_free(&pFrame); } catch { }
            try { var pPacket = _pPacket; ffmpeg.av_packet_free(&pPacket); } catch { }

            try
            {
                AVCodecContext** avctx = stackalloc[] { _pCodecContext };
                ffmpeg.avcodec_free_context(avctx);
            }
            catch { }

            try
            {
                var pFormatContext = _pFormatContext;
                ffmpeg.avformat_close_input(&pFormatContext);
            }
            catch { }

            try
            {
                var pReceivedFrame = _receivedFrame;
                if (pReceivedFrame is not null)
                    ffmpeg.av_frame_free(&pReceivedFrame);
            }
            catch { }

            // Free FFmpeg native resources
            // Reset interrupt callback before cleanup
            try { if (_gcHandle.IsAllocated) _gcHandle.Free(); } catch { }
        }

        public bool TryDecodeNextFrame(out AVFrame frame)
        {
            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_frame_unref(_receivedFrame);
            int error;

            do
            {
                try
                {
                    do
                    {
                        ffmpeg.av_packet_unref(_pPacket);
                        error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);

                        if (error == ffmpeg.AVERROR_EOF)
                        {
                            // Flush decoder to drain remaining frames
                            ffmpeg.avcodec_send_packet(_pCodecContext, null).ThrowExceptionIfError();
                            while (true)
                            {
                                var flushRet = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
                                if (flushRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || flushRet == ffmpeg.AVERROR_EOF)
                                {
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
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            if (error < 0) error.ThrowExceptionIfError();

            if (_pCodecContext->hw_device_ctx is not null)
            {
                ffmpeg.av_hwframe_transfer_data(_receivedFrame, _pFrame, 0).ThrowExceptionIfError();
                frame = *_receivedFrame;
            }
            else
                frame = *_pFrame;

            return true;
        }

        public class FFmpegException : Exception
        {
            public int ErrorCode { get; }
            public FFmpegException(string message, int errorCode) : base(message)
            {
                ErrorCode = errorCode;
            }
        }
    }
}

namespace HeliVMS.Services
{
    internal static class FFmpegExtensions
    {
        /// <summary>FFmpeg.AutoGen v8.0.0.1 missing ThrowExceptionIfError — reimplemented here</summary>
        internal static int ThrowExceptionIfError(this int error)
        {
            if (error < 0)
            {
                throw new VideoStreamDecoder.FFmpegException($"FFmpeg error code: {error}", error);
            }
            return error;
        }
    }
}
