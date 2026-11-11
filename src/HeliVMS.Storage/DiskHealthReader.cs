using System.Management;

namespace HeliVMS.Storage;

/// <summary>一個磁碟區（<c>D:\</c>）對應到的實體磁碟健康取樣。</summary>
/// <param name="Root">磁碟區根（<c>D:\</c>）。</param>
/// <param name="Model">型號；讀不到為 <see langword="null"/>。</param>
/// <param name="Serial">序號；讀不到為 <see langword="null"/>。</param>
/// <param name="Attributes">S.M.A.R.T. 取樣；讀不到為 <see langword="null"/>（＝未知，不是健康）。</param>
public readonly record struct VolumeDiskHealth(
    string Root,
    string? Model,
    string? Serial,
    SmartAttributes? Attributes)
{
    /// <summary>這一顆的健康等級。</summary>
    public DiskHealthLevel Level => DiskHealth.Evaluate(Attributes);
}

/// <summary>磁碟健康取樣來源（測試注入用；生產環境是 <see cref="WmiDiskHealthSource"/>）。</summary>
public interface IDiskHealthSource
{
    /// <summary>列出本機固定磁碟區的健康取樣；讀不到該磁碟時仍要回一筆（欄位為 null）。</summary>
    IReadOnlyList<VolumeDiskHealth> Read();
}

/// <summary>
/// 以 WMI 讀取磁碟健康的實作。
/// </summary>
/// <remarks>
/// <para>
/// 三個 WMI 介面：<c>Win32_LogicalDisk</c>（磁碟區）、<c>Win32_DiskPartition</c>／
/// <c>Win32_DiskDrive</c>＋兩個關聯類別（磁碟區 → 實體磁碟），以及 <c>root\wmi</c> 的
/// <c>MSStorageDriver_FailurePredictStatus</c>／<c>MSStorageDriver_ATAPISmartData</c>（S.M.A.R.T.，
/// 以 <c>InstanceName</c> 對上磁碟的 <c>PNPDeviceID</c>）。
/// </para>
/// <para>
/// <b>每一段都各自 fail-open</b>：不是管理員、控制器不外露 S.M.A.R.T.、USB／RAID 橋接後面看不到
/// 實體碟、WMI 服務忙碌——任何一種都只讓該欄位變 <see langword="null"/>（＝未知），
/// 不會讓整個健康查詢丟例外。錄影不該因為「健康查詢讀不到」而受影響。
/// </para>
/// <para>
/// 這裡刻意沒有任何判斷邏輯：判斷在 <see cref="DiskHealth"/>（可行為測試）。WMI 這層在 CI 上
/// 無法驗證，所以它的唯一責任是「把讀到的東西原樣帶出來，讀不到就說讀不到」。
/// </para>
/// </remarks>
public sealed class WmiDiskHealthSource : IDiskHealthSource
{
    public IReadOnlyList<VolumeDiskHealth> Read()
    {
        var results = new List<VolumeDiskHealth>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady && drive.DriveType == DriveType.Fixed;
            }
            catch (IOException)
            {
                continue;
            }

            if (!ready)
            {
                continue;
            }

            var root = drive.Name;                       // 例如 "D:\"
            string? model = null;
            string? serial = null;
            SmartAttributes? attributes = null;

            try
            {
                var deviceId = root.TrimEnd('\\');       // WMI 的 DeviceID 是 "D:"
                if (FindPhysicalDisk(deviceId) is (true, { } pnp, var diskModel, var diskSerial))
                {
                    model = diskModel;
                    serial = diskSerial;
                    attributes = ReadSmart(pnp);
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 讀不到就是未知（欄位維持 null），不要往上丟。
            }

            results.Add(new VolumeDiskHealth(root, model, serial, attributes));
        }

        return results;
    }

    /// <summary>
    /// 磁碟區 → 實體磁碟（型號、序號、PNPDeviceID）。找不到時回 <c>(false, null, null, null)</c>。
    /// </summary>
    private static (bool Found, string? PnpDeviceId, string? Model, string? Serial) FindPhysicalDisk(string deviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, null, null, null);
        }

        var partitionId = QueryScalar(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID=\"{deviceId}\"}} WHERE AssocClass=Win32_LogicalDiskToPartition",
            "DeviceID");
        if (string.IsNullOrEmpty(partitionId))
        {
            return (false, null, null, null);
        }

        foreach (var disk in Query(
            "SELECT PNPDeviceID, Model, SerialNumber FROM Win32_DiskDrive"))
        {
            using (disk)
            {
                var pnp = disk["PNPDeviceID"] as string;

                // 反向關聯：這顆實體碟上是否有那個分割區。
                if (string.IsNullOrEmpty(pnp) || !OwnsPartition(pnp, partitionId!, out var model, out var serial))
                {
                    continue;
                }

                return (true, pnp, model, serial);
            }
        }

        return (false, null, null, null);
    }

    private static bool OwnsPartition(string pnpDeviceId, string partitionId, out string? model, out string? serial)
    {
        model = null;
        serial = null;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var partition in Query(
            $"ASSOCIATORS OF {{Win32_DiskDrive.PNPDeviceID=\"{pnpDeviceId.Replace("\\", "\\\\")}\"}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
        {
            using (partition)
            {
                if (!string.Equals(partition["DeviceID"] as string, partitionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 同一顆碟的型號／序號（從磁碟本身再查一次，關聯結果不帶這些欄位）。
                foreach (var drive in Query(
                    $"SELECT Model, SerialNumber FROM Win32_DiskDrive WHERE PNPDeviceID=\"{pnpDeviceId.Replace("\\", "\\\\")}\""))
                {
                    using (drive)
                    {
                        model = (drive["Model"] as string)?.Trim();
                        serial = (drive["SerialNumber"] as string)?.Trim();
                    }
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>S.M.A.R.T.：故障預測與 ATA 屬性區（都是 <c>root\wmi</c>，通常需要管理員）。</summary>
    private static SmartAttributes? ReadSmart(string pnpDeviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var instance = pnpDeviceId.Replace("\\", "\\\\");

        bool? predictFailure = null;
        foreach (var row in Query(
            $"SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus WHERE InstanceName=\"{instance}\"",
            @"root\wmi"))
        {
            using (row)
            {
                if (row["PredictFailure"] is bool flag)
                {
                    predictFailure = flag;
                }
            }
        }

        SmartAttributes? attributes = null;
        foreach (var row in Query(
            $"SELECT VendorSpecific FROM MSStorageDriver_ATAPISmartData WHERE InstanceName=\"{instance}\"",
            @"root\wmi"))
        {
            using (row)
            {
                if (row["VendorSpecific"] is byte[] vendor)
                {
                    attributes = DiskHealth.ParseAtaSmart(vendor);
                }
            }
        }

        attributes = attributes is { } parsed ? DiskHealth.WithPredictFailure(parsed, predictFailure) : null;

        // 控制器只給了「有沒有預測故障」時，仍然是有用的資訊。
        return attributes ?? (predictFailure is null
            ? null
            : new SmartAttributes(predictFailure, null, null, null, null));
    }

    private static string? QueryScalar(string query, string property, string scope = @"root\cimv2")
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var row in Query(query, scope))
        {
            using (row)
            {
                return row[property] as string;
            }
        }

        return null;
    }

    private static IEnumerable<ManagementObject> Query(string query, string scope = @"root\cimv2")
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query));
        foreach (var result in searcher.Get())
        {
            yield return (ManagementObject)result;
        }
    }
}
