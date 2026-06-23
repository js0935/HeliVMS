// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Drawing;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace HeliVMS.Services {
    public sealed unsafe class VideoFrameConverter : IDisposable {
        private readonly IntPtr _convertedFrameBufferPtr;
        private readonly Size _destinationSize;
        private readonly byte_ptr4 _dstData;
        private readonly int4 _dstLinesize;
        private readonly SwsContext* _pConvertContext;
        private readonly AVPixelFormat _destinationPixelFormat;
        private readonly AVPixelFormat _sourcePixelFormat;

        public AVPixelFormat SourcePixelFormat => _sourcePixelFormat;
        public AVPixelFormat DestinationPixelFormat => _destinationPixelFormat;

        public VideoFrameConverter(Size sourceSize, AVPixelFormat sourcePixelFormat,
            Size destinationSize, AVPixelFormat destinationPixelFormat) {
            _destinationSize = destinationSize;

            _destinationPixelFormat = destinationPixelFormat;

            if (sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_YUVJ420P)
                sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
            else if (sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_YUVJ422P)
                sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_YUV422P;
            else if (sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_YUVJ444P)
                sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_YUV444P;
            _sourcePixelFormat = sourcePixelFormat;

            _pConvertContext = ffmpeg.sws_getContext(sourceSize.Width,
                sourceSize.Height,
                sourcePixelFormat,
                destinationSize.Width,
                destinationSize.Height,
                destinationPixelFormat,
                1 | 0x4000, // SWS_BILINEAR | SWS_ACCURATE_RND
                null,
                null,
                null);
            if (_pConvertContext is null) // IDE0270 suppressed: pointer type cannot use ?? or ThrowIfNull
                throw new ApplicationException("Could not initialize the conversion context.");
            var convertedFrameBufferSize = ffmpeg.av_image_get_buffer_size(destinationPixelFormat,
                destinationSize.Width,
                destinationSize.Height,
                1);
            _convertedFrameBufferPtr = Marshal.AllocHGlobal(convertedFrameBufferSize);
            _dstData = new byte_ptr4();
            _dstLinesize = new int4();

            ffmpeg.av_image_fill_arrays(ref _dstData,
                ref _dstLinesize,
                (byte*)_convertedFrameBufferPtr,
                destinationPixelFormat,
                destinationSize.Width,
                destinationSize.Height,
                1);
        }

        public void Dispose() {
            if (_convertedFrameBufferPtr != IntPtr.Zero) {
                Marshal.FreeHGlobal(_convertedFrameBufferPtr);
            }
            if (_pConvertContext is not null) {
                ffmpeg.sws_freeContext(_pConvertContext);
            }
        }

        public int GetBufferSize() {
            return ffmpeg.av_image_get_buffer_size(_destinationPixelFormat,
                _destinationSize.Width,
                _destinationSize.Height,
                1);
        }

        public AVFrame Convert(AVFrame sourceFrame) {
            ffmpeg.sws_scale(_pConvertContext,
                sourceFrame.data,
                sourceFrame.linesize,
                0,
                sourceFrame.height,
                _dstData,
                _dstLinesize);

            var data = new byte_ptr8();
            data.UpdateFrom(_dstData);
            var linesize = new int8();
            linesize.UpdateFrom(_dstLinesize);

            return new AVFrame {
                data = data,
                linesize = linesize,
                width = _destinationSize.Width,
                height = _destinationSize.Height,
                format = (int)_destinationPixelFormat
            };
        }

        /// <summary>Convert directly into a caller-provided native buffer, zero-copy from sws_scale.</summary>
        public void ConvertTo(AVFrame sourceFrame, IntPtr destination) {
            var dstData = new byte_ptr8();
            dstData[0] = (byte*)destination;
            var dstLinesize = new int8();
            dstLinesize[0] = _dstLinesize[0];

            ffmpeg.sws_scale(_pConvertContext,
                sourceFrame.data,
                sourceFrame.linesize,
                0,
                sourceFrame.height,
                dstData,
                dstLinesize);
        }

        /// <summary>Convert directly into a managed byte[].</summary>
        public unsafe void ConvertTo(AVFrame sourceFrame, byte[] destination) {
            fixed (byte* dstPtr = destination) {
                ConvertTo(sourceFrame, (IntPtr)dstPtr);
            }
        }
    }
}
