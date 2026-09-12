using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace HeliVMS.Decoder;

internal static class FFmpegHelper
{
#pragma warning disable IDE1006 // Naming rule: FFmpeg API name
    public static unsafe string av_strerror(int error)
#pragma warning restore IDE1006
    {
        var bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
        var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
        return message ?? string.Empty;
    }

    public static int ThrowExceptionIfError(this int error)
    {
        if (error < 0) throw new ApplicationException(av_strerror(error));
        return error;
    }
}
