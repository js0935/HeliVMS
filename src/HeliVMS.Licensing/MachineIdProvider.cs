using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Licensing;

/// <summary>
/// 機器指紋提供者（§19.1：授權綁定機器）。
///
/// 規範設備碼：WMI 取得 CPU ProcessorId 與主機板序號 → SHA-256 → 取前 32 hex（64 bits）。
/// 選用硬體序號而非 MAC 位址的原因：MAC 會隨網卡更換、虛擬機搬移或 USB 網卡插拔而改變，
/// 會造成合法使用者被判定 MachineMismatch。
///
/// 舊版實作曾以實體 MAC 位址雜湊出 64 hex 指紋，僅保留 <see cref="GetLegacyFingerprint"/>
/// 以相容既有已簽發的授權，新簽發一律使用 <see cref="GetDeviceCode"/>。
/// </summary>
public static class MachineIdProvider
{
    private const int DeviceCodeLength = 32;

    private static readonly Lock CacheLock = new();
    private static string? _deviceCode;
    private static string? _legacyFingerprint;

    /// <summary>
    /// 取得規範設備碼（32 hex）。WMI 不可用或資料不足時逐級退回：
    /// 固定磁碟機 → 主機名稱；全部失敗則回傳固定字串 <c>UNKNOWN</c>，避免授權驗證因例外而中斷。
    /// </summary>
    public static string GetDeviceCode()
    {
        lock (CacheLock)
        {
            return _deviceCode ??= ComputeDeviceCode();
        }
    }

    /// <summary>取得目前機器指紋；等同 <see cref="GetDeviceCode"/>（保留舊 API 名稱相容）。</summary>
    public static string GetFingerprint() => GetDeviceCode();

    /// <summary>取得舊版 MAC 派生指紋（64 hex），僅供既有授權相容驗證使用。</summary>
    public static string GetLegacyFingerprint()
    {
        lock (CacheLock)
        {
            return _legacyFingerprint ??= ComputeLegacyFingerprint();
        }
    }

    /// <summary>取得本機可接受的所有機器代碼（規範設備碼優先，其後為舊版指紋）。</summary>
    public static IReadOnlyList<string> GetAcceptedCodes()
    {
        var codes = new List<string> { GetDeviceCode() };
        var legacy = GetLegacyFingerprint();
        if (!codes.Contains(legacy, StringComparer.OrdinalIgnoreCase))
        {
            codes.Add(legacy);
        }

        return codes;
    }

    private static string ComputeDeviceCode()
    {
        try
        {
            var parts = new List<string>();

            foreach (var value in QueryWmi("SELECT ProcessorId FROM Win32_Processor", "ProcessorId"))
            {
                parts.Add(value);
            }

            foreach (var value in QueryWmi("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"))
            {
                // 部分 OEM 出廠主板序號為預設佔位字串，納入只會降低唯一性。
                if (value.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add(value);
            }

            // CPU ProcessorId 在同型號機器上可能完全相同（常見於虛擬機），
            // 單獨使用會讓不同機器共用同一設備碼，故要求至少兩項來源。
            if (parts.Count < 2)
            {
                var drive = GetFixedDriveFallback();
                if (drive.Length > 0 && !parts.Contains(drive, StringComparer.Ordinal))
                {
                    parts.Add(drive);
                }
            }

            if (parts.Count < 2)
            {
                parts.Add(Environment.MachineName);
            }

            return HashJoined(parts)[..DeviceCodeLength];
        }
        catch (Exception)
        {
            return "UNKNOWN";
        }
    }

    private static string ComputeLegacyFingerprint()
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

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToUpperInvariant();
    }

    private static IEnumerable<string> QueryWmi(string query, string propertyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var results = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var value = item[propertyName]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        results.Add(value);
                    }
                }
            }
        }
        catch (Exception)
        {
            // WMI 可能被停用或無權限；呼叫端以其他來源補足。
        }

        foreach (var value in results)
        {
            yield return value;
        }
    }

    private static string GetFixedDriveFallback()
    {
        try
        {
            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed);
            if (drive is null)
            {
                return string.Empty;
            }

            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(drive.RootDirectory.FullName)))[..16];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string HashJoined(IEnumerable<string> parts)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))
            .ToUpperInvariant();
}