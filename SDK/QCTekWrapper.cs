// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Runtime.InteropServices;
using System.Text;

namespace HeliVMS.SDK;

/// <summary>
/// QCTek SDK P/Invoke wrapper
/// Provides .NET interop for QC_Onvif.dll and other native DLLs
/// (HeliNVR -> HeliVMS port, only includes ONVIF RTSP URL query functions)
/// </summary>
public static class QCTekWrapper
{
    private const string DLL_PATH = "DLL\\";

    #region QC_Onvif.dll

    /// <summary>ONVIF authentication check</summary>
    /// <param name="deviceIp">Device IP address</param>
    /// <param name="username">Username</param>
    /// <param name="password">Password</param>
    /// <returns>0 for success, non-zero for failure</returns>
    [DllImport(DLL_PATH + "QC_Onvif.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int OnvifCheckAuth(
        string deviceIp,
        string username,
        string password);

    /// <summary>Query RTSP URL (core fallback function)</summary>
    /// <param name="deviceIp">Device IP address</param>
    /// <param name="username">Username</param>
    /// <param name="password">Password</param>
    /// <param name="streamUrl">Output stream URL</param>
    /// <param name="urlSize">URL buffer size</param>
    /// <returns>0 for success, non-zero for failure</returns>
    [DllImport(DLL_PATH + "QC_Onvif.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int OnvifQueryRtspUrl(
        string deviceIp,
        string username,
        string password,
        StringBuilder streamUrl,
        int urlSize);

    /// <summary>Get random UUID</summary>
    [DllImport(DLL_PATH + "QC_Onvif.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetRandomUUID(
        StringBuilder uuid,
        int uuidSize);

    /// <summary>Get preset name</summary>
    [DllImport(DLL_PATH + "QC_Onvif.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetPresetName(
        int presetIndex,
        StringBuilder presetName,
        int nameSize);

    /// <summary>Execute preset tour</summary>
    [DllImport(DLL_PATH + "QC_Onvif.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int OperatePresetTour(
        int tourIndex);

    #endregion

    #region QC_Network.dll

    /// <summary>Break all network processes</summary>
    [DllImport(DLL_PATH + "QC_Network.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BreakAllNetProc();

    #endregion

    #region QC_Device.dll

    /// <summary>Connect device (RTMP/ONVIF/RTSP/SIP)</summary>
    [DllImport(DLL_PATH + "QC_Device.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Connect(
        string protocol,
        string url,
        string username,
        string password);

    /// <summary>Disconnect device</summary>
    [DllImport(DLL_PATH + "QC_Device.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int DeviceDisconnect(
        int deviceHandle);

    /// <summary>Shutdown device</summary>
    [DllImport(DLL_PATH + "QC_Device.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Shutdown(
        int deviceHandle);

    /// <summary>Query device information</summary>
    [DllImport(DLL_PATH + "QC_Device.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int QueryDeviceInformation(
        int deviceHandle,
        StringBuilder deviceInfo,
        int infoSize);

    #endregion
}
