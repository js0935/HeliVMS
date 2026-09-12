using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Licensing;

/// <summary>
/// 機器指紋提供者（§19：授權綁定機器）。
/// 以本機實體 MAC 位址為基礎計算穩定指紋；無可用 MAC 才退回主機名稱。
/// </summary>
public static class MachineIdProvider
{
    /// <summary>取得目前機器指紋（32 hex，跨平台、穩定）。</summary>
    public static string GetFingerprint()
    {
        string source;
        try
        {
            source = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic =>
                    nic.OperationalStatus == OperationalStatus.Up &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .FirstOrDefault(mac => mac.Length >= 12)
                ?? Environment.MachineName;
        }
        catch (Exception)
        {
            source = Environment.MachineName;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }
}