using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 由資料庫的設備資料建立 ONVIF 服務的唯一入口。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端有三個地方要跟攝影機講 ONVIF：設定中心的 IO 頁（列出 DI/DO 端子、測試繼電器）、
/// IO 視窗的 DO 寫入器，以及 DI 事件監視器。先前每一處都自己組位址與密碼，只要其中一處
/// 忘了 <see cref="DeviceRepository.SafeUnprotect"/>（舊資料可能是明文、或由別台機器加密），
/// 就變成「只有那個功能連不上」，而且錯誤只會表現成逾時。
/// </para>
/// <para>
/// 這裡刻意只有組裝、不含任何 I/O：測試可以用它釘住位址格式與憑證解析，
/// 不必真的開連線。
/// </para>
/// </remarks>
public static class OnvifServiceFactory
{
    /// <summary>建立服務；<paramref name="device"/> 需有 IP 與連接埠。</summary>
    public static OnvifDeviceService Create(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Create(device.Ip, device.Port, device.Username, device.PasswordEncrypted);
    }

    /// <summary>以原始欄位建立服務（給只有部分欄位的呼叫端與測試使用）。</summary>
    public static OnvifDeviceService Create(string ip, int port, string? username, string? passwordEncrypted)
    {
        var host = $"http://{ip}:{port}/onvif/device_service";
        var password = string.IsNullOrWhiteSpace(passwordEncrypted)
            ? null
            : DeviceRepository.SafeUnprotect(passwordEncrypted);
        return new OnvifDeviceService(host, username, password);
    }
}
