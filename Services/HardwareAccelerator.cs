using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;

namespace HeliVMS.Services;

internal static class HardwareAccelerator {
    private static AVHWDeviceType? _cachedType;
    private static readonly object _cacheLock = new();

    public static AVHWDeviceType DetectBestType() {
        var cached = _cachedType;
        if (cached.HasValue) return cached.Value;

        lock (_cacheLock) {
            if (_cachedType.HasValue) return _cachedType.Value;

            AVHWDeviceType result = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

            try {
                FFmpegStreamingService.InitializeFFmpeg();

                AVHWDeviceType type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
                    if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV
                        || type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
                        || type == AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2) {
                        result = type;
                        break;
                    }
                }

                if (result == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
                    type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                    while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
                        result = type;
                    }
                }
            } catch (Exception ex) {
                Log($"HardwareAccelerator.DetectBestType: {ex.Message}");
            }

            _cachedType = result;
            Log($"HardwareAccelerator.DetectBestType: {result}");
            return result;
        }
    }

    [Conditional("DEBUG")]
    private static void Log(string msg) => Serilog.Log.Debug("[HeliVMS] {Msg}", msg);
}
