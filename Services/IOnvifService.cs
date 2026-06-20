using HeliVMS.Models;

namespace HeliVMS.Services;

/// <summary>ONVIF protocol service for camera discovery and PTZ control (supports 3200+ ONVIF cameras)</summary>
public interface IOnvifService
{
    /// <summary>Scan subnet for ONVIF devices</summary>
    Task<List<OnvifDiscoveryResult>> ScanSubnetAsync(string subnet, int onvifPort, string username, string password,
        IProgress<(int current, int total, string ip)>? progress = null);

    /// <summary>Discover a single camera by IP via ONVIF</summary>
    Task<OnvifDiscoveryResult?> DiscoverCameraAsync(string ip, int port, string username, string password);

    /// <summary>Discover camera with automatic port fallback</summary>
    Task<OnvifDiscoveryResult?> DiscoverCameraWithPortFallbackAsync(string ip, string username, string password, int? preferredPort = null);

    /// <summary>Probe device info (manufacturer, model name)</summary>
    Task<(string manufacturer, string model, string name)> ProbeDeviceInfoAsync(string ip, int port, string username, string password);

    /// <summary>
    /// Resolve RTSP stream URLs via ONVIF + brand-specific overrides + QCTek fallback
    /// </summary>
    Task<(string MainUrl, string SubUrl)> TryResolveStreamUrlsAsync(
        string ip, int onvifPort, string username, string password,
        string fallbackMainUrl, string? fallbackSubUrl);

    // ── PTZ Control ──

    /// <summary>PTZ continuous move</summary>
    Task<bool> PTZ_ContinuousMoveAsync(string ip, int port, string username, string password, float x, float y, float zoom);

    /// <summary>PTZ absolute move</summary>
    Task<bool> PTZ_AbsoluteMoveAsync(string ip, int port, string username, string password, float x, float y, float zoom);

    /// <summary>PTZ relative move</summary>
    Task<bool> PTZ_RelativeMoveAsync(string ip, int port, string username, string password, float x, float y, float zoom);

    /// <summary>PTZ stop</summary>
    Task<bool> PTZ_StopAsync(string ip, int port, string username, string password);

    /// <summary>PTZ goto preset</summary>
    Task<bool> PTZ_GotoPresetAsync(string ip, int port, string username, string password, string presetToken);

    /// <summary>Get PTZ presets</summary>
    Task<List<OnvifPreset>> PTZ_GetPresetsAsync(string ip, int port, string username, string password);

    /// <summary>Set PTZ preset</summary>
    Task<bool> PTZ_SetPresetAsync(string ip, int port, string username, string password, string presetName);

    /// <summary>Remove PTZ preset</summary>
    Task<bool> PTZ_RemovePresetAsync(string ip, int port, string username, string password, string presetToken);

    /// <summary>Get PTZ status</summary>
    Task<OnvifPTZStatus?> PTZ_GetStatusAsync(string ip, int port, string username, string password);

    /// <summary>Get PTZ configuration</summary>
    Task<OnvifPTZConfiguration?> PTZ_GetConfigurationAsync(string ip, int port, string username, string password);
}
