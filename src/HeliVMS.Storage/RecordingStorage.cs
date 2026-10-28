using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影根目錄的政策（§2.1）：錄影一律避開系統碟。
///
/// 系統碟被錄影填滿之後，Windows 本身、事件記錄與所有暫存檔會一起失效，症狀是
/// 「錄到一半整台當掉」，而日誌乾淨。因此挑碟規則做成可注入的純函式：
/// <see cref="ResolveRoot"/> 只依輸入決定路徑，磁碟清單與系統碟代號都能在測試中固定。
///
/// 桌面、Agent、WebApi 三個程序必須算出<b>同一個</b>錄影根目錄，否則會出現
/// 「保留清單對 A 目錄下手、串流政策只認 B 目錄」，症狀是遠端永遠播不到。所有端點
/// 一律經過 <see cref="ResolveConfiguredRoot"/>／<see cref="ResolveRootFromEnvironment"/>
/// 這兩個入口取得根目錄。
/// </summary>
public static class RecordingStorage
{
    /// <summary>磁碟組態的唯讀快照，供挑碟純函式使用。</summary>
    public readonly record struct Drive(string Root, bool IsReady, bool IsSystem, long TotalBytes);

    /// <summary>設定中心磁碟下拉使用的磁碟資訊（含可用空間）。</summary>
    public readonly record struct DriveChoice(string Root, long TotalBytes, long FreeBytes);

    /// <summary>錄影磁碟清單項目（保留）。</summary>
    public readonly record struct RecordingDisk(string Root, bool Primary, long TotalBytes, long FreeBytes);

    /// <summary>錄影磁碟的 API/UI 傳輸物件（保留）。</summary>
    public sealed class RecordingDiskDto
    {
        public string Root { get; set; } = string.Empty;

        public bool Primary { get; set; }
    }

    /// <summary>非系統碟上的專用錄影目錄名（相對於磁碟根目錄）。</summary>
    public const string RecordingsDirName = "HeliVMSRecordings";

    /// <summary>明確覆寫錄影根目錄的環境變數。</summary>
    public const string RootVariable = "HELIVMS_RECORDINGS_ROOT";

    /// <summary>設定中心選定的錄影根目錄（共用資料庫設定鍵）。</summary>
    public const string RootSettingKey = "recording.root";

    /// <summary>曾用過的錄影根目錄清單（分號分隔），切換磁碟後供配額清理汰除舊錄影。</summary>
    public const string KnownRootsSettingKey = "recording.known_roots";

    /// <summary>錄影磁碟清單設定鍵（保留）。</summary>
    public const string DisksSettingKey = "recording.disks";

    /// <summary>主要錄影磁碟設定鍵（保留）。</summary>
    public const string PrimaryRootSettingKey = "recording.primary_root";

    /// <summary>錄影磁碟保留的最小可用空間設定鍵（位元組）；低於此值就不再往該碟寫新段。</summary>
    public const string MinFreeSettingKey = "recording.min_free_bytes";

    /// <summary>
    /// 錄影磁碟保留的最小可用空間預設值（2 GiB）。
    ///
    /// 磁碟完全寫滿的後果不是「這段沒錄到」而是「整台錄影停住」：ffmpeg 寫不出暫存檔、
    /// 每段都失敗、退避到 5 分鐘一次，而畫面照常，操作員要等到想回放才發現好幾天沒錄。
    /// 留一段安全邊界讓排程有時間把新段挪到下一顆碟，代價只是一小塊空間。
    /// </summary>
    public const long DefaultMinFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 解析錄影根目錄。
    ///
    /// 優先序：明確指定的 <paramref name="explicitRoot"/>（一律照用，即使位於系統碟——
    /// 那是刻意的覆寫）→ 資料根目錄下的 <c>recordings</c>（不在系統碟時）→ 容量最大的
    /// 非系統固定磁碟之 <c>\HeliVMSRecordings</c>。找不到替代固定磁碟時退回資料根目錄下的
    /// <c>recordings</c>——寧可錄在系統碟，也不要完全不錄。
    ///
    /// <paramref name="listDrives"/>／<paramref name="systemDrive"/> 可注入，讓測試不受開發機
    /// 實際磁碟組態影響。
    /// </summary>
    public static string ResolveRoot(
        string dataRoot,
        string? explicitRoot = null,
        Func<IReadOnlyList<Drive>>? listDrives = null,
        string? systemDrive = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return explicitRoot;
        }

        var defaultRoot = Path.Combine(dataRoot, "recordings");
        var systemRoot = DriveRootOf(systemDrive ?? DefaultSystemDrive());
        if (!string.Equals(DriveRootOf(defaultRoot), systemRoot, StringComparison.OrdinalIgnoreCase))
        {
            return defaultRoot;
        }

        var alternative = ChooseAlternativeDrive((listDrives ?? ProbeFixedDrives)(), systemRoot);
        return alternative.HasValue ? FolderForDrive(alternative.Value.Root) : defaultRoot;
    }

    /// <summary>
    /// 由環境解析錄影根目錄：讀 <c>HELIVMS_DATA</c> 當資料根目錄，並套用避開系統碟規則。
    ///
    /// Agent 曾直接拿資料根目錄當錄影根目錄，錄到 <c>&lt;資料根&gt;\chNNN</c>，桌面與回放
    /// 卻在 <c>&lt;資料根&gt;\recordings\chNNN</c> 找，頻道永遠顯示無錄影。任何一端沒走這個入口
    /// 就會重現。
    /// </summary>
    public static string ResolveRootFromEnvironment(
        Func<string, string?>? readVariable = null,
        Func<IReadOnlyList<Drive>>? listDrives = null,
        string? systemDrive = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        return ResolveRoot(DataRootLayout.ResolveRoot(read), read(RootVariable), listDrives, systemDrive);
    }

    /// <summary>
    /// 解析錄影根目錄：設定中心選定的 <paramref name="configuredRoot"/> 優先，未設定時才退回
    /// <c>HELIVMS_RECORDINGS_ROOT</c> 與避開系統碟的推導值。桌面、Agent、WebApi 皆走此入口，
    /// 確保三個程序算出同一個目錄。
    /// </summary>
    public static string ResolveConfiguredRoot(
        string dataRoot,
        string? configuredRoot,
        Func<string, string?>? readVariable = null,
        Func<IReadOnlyList<Drive>>? listDrives = null,
        string? systemDrive = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        var read = readVariable ?? Environment.GetEnvironmentVariable;
        return ResolveRoot(dataRoot, read(RootVariable), listDrives, systemDrive);
    }

    /// <summary>本機固定磁碟與其容量，供設定中心的錄影磁碟下拉使用（含系統碟）。</summary>
    public static IReadOnlyList<DriveChoice> ListFixedDrives()
    {
        var choices = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                choices.Add(new DriveChoice(
                    drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 磁碟在列舉後才退出（例如拔除）是常態；跳過即可，不該讓設定頁整個失敗。
            }
        }

        return choices;
    }

    /// <summary>
    /// 指定磁碟上的專用錄影目錄（<c>\HeliVMSRecordings</c>）。
    ///
    /// 裸磁碟代號（<c>D:</c>）補上根分隔符再組合：.NET Core 起 <c>D:</c> 被視為已根化，
    /// <c>Path.Combine("D:", "x")</c> 恰好給出 <c>D:\x</c>，但 <c>D:x</c> 本身是
    /// 「該磁碟目前目錄」的相對路徑（<c>GetFullPath</c> 仍會照相對路徑解析）——
    /// 明確補分隔符讓「錄影目錄一律絕對」不依賴這個平台細節。
    /// </summary>
    public static string FolderForDrive(string driveRoot)
    {
        var root = driveRoot.EndsWith(Path.DirectorySeparatorChar) || driveRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? driveRoot
            : driveRoot + Path.DirectorySeparatorChar;
        return Path.Combine(root, RecordingsDirName);
    }

    /// <summary>
    /// 把一個根目錄併入既有清單（分號分隔），已存在（不分大小寫）則原樣回傳。
    /// 切換錄影磁碟時保留舊根，讓配額清理仍能汰除舊磁碟上的既有錄影。
    /// </summary>
    public static string MergeKnownRoot(string? existing, string folder)
    {
        var roots = ParseKnownRoots(existing).ToList();
        if (!roots.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(folder);
        }

        return string.Join(";", roots);
    }

    /// <summary>解析分號分隔的已知錄影根目錄清單。</summary>
    public static IReadOnlyList<string> ParseKnownRoots(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 解析 <see cref="DisksSettingKey"/> 的錄影磁碟清單（JSON 陣列）。
    ///
    /// 空值或格式錯誤時回傳空清單：壞掉的設定值不該讓整個儲存頁開不起來。
    /// </summary>
    public static IReadOnlyList<RecordingDiskDto> ParseDisks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<RecordingDiskDto>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<RecordingDiskDto>>(value) ?? new List<RecordingDiskDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<RecordingDiskDto>();
        }
    }

    /// <summary>把錄影磁碟清單寫成 <see cref="DisksSettingKey"/> 的設定值（JSON 陣列）。</summary>
    public static string SerializeDisks(IEnumerable<RecordingDiskDto>? disks)
        => JsonSerializer.Serialize(disks is null ? new List<RecordingDiskDto>() : disks.ToList());

    /// <summary>取清單中標示為主要（Primary）的錄影磁碟根目錄；沒有時為 null。</summary>
    public static string? GetPrimaryRoot(IEnumerable<RecordingDiskDto>? disks)
    {
        if (disks is null)
        {
            return null;
        }

        foreach (var disk in disks)
        {
            if (disk.Primary)
            {
                return disk.Root;
            }
        }

        return null;
    }

    /// <summary>
    /// 列出本機固定磁碟與其可用空間，並標示主要錄影磁碟。
    /// 與 <see cref="ListFixedDrives"/> 的差別是多帶 <see cref="RecordingDisk.Primary"/> 旗標，
    /// 供多磁碟設定頁使用。
    /// </summary>
    public static IReadOnlyList<RecordingDisk> GetDisksWithSpace(string? primaryRoot = null)
    {
        var primary = Normalize(primaryRoot);
        var disks = new List<RecordingDisk>();
        foreach (var drive in ListFixedDrives())
        {
            var isPrimary = primary is not null &&
                string.Equals(Normalize(drive.Root), primary, StringComparison.OrdinalIgnoreCase);
            disks.Add(new RecordingDisk(drive.Root, isPrimary, drive.TotalBytes, drive.FreeBytes));
        }

        return disks;
    }

    /// <summary>
    /// 由磁碟清單（<see cref="RecordingDiskDto"/>）推出實際錄影目錄，<b>主要碟排首位</b>，
    /// 其餘依清單順序。同一目錄去重（不分大小寫）。
    ///
    /// 這是多磁碟錄影的優先序來源：寫入端每個區段都依此順序挑第一個還有空間的碟，
    /// 主要碟滿了才依序遞補。桌面、Agent、WebApi 三個程序都必須看到同一份順序，否則
    /// 會出現「寫在 A 碟、保留清單卻去 B 碟找」的落差。
    /// </summary>
    public static IReadOnlyList<string> DiskRoots(IEnumerable<RecordingDiskDto>? disks)
    {
        var list = (disks ?? Array.Empty<RecordingDiskDto>()).Where(static d => d is not null).ToList();
        var ordered = list.Where(static d => d.Primary).Concat(list.Where(static d => !d.Primary));

        return ordered
            .Where(static d => !string.IsNullOrWhiteSpace(d.Root))
            .Select(static d => FolderForDrive(d.Root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>資料夾所在磁碟的可用空間（位元組）；無法取得時回 0（視為不可用）。</summary>
    public static long FreeBytesFor(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return 0;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
            {
                return 0;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            // 磁碟在列舉後才退出（例如拔除）是常態；回 0 讓挑碟邏輯跳過它即可。
            return 0;
        }
    }

    /// <summary>
    /// 資料夾所在磁碟的總容量與可用空間（位元組）；無法取得時回 <c>(0, 0)</c>（容量未知）。
    ///
    /// <para>
    /// 供磁碟滿策略計算「已用率」用（§11.1）。與 <see cref="FreeBytesFor"/> 同一組錯誤處理：
    /// 磁碟中途退出是常態，取不到就當未知，交由呼叫端決定不動作，而不是讓監看整個失敗。
    /// </para>
    /// </summary>
    public static (long TotalBytes, long FreeBytes) DriveUsageFor(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return (0, 0);
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : (0, 0);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 選出下一段要寫入哪個錄影目錄：依 <paramref name="roots"/> 順序取第一個可用空間
    /// ≥ <paramref name="minFreeBytes"/> 者；全部都低於門檻時取可用空間最大者——寧可繼續
    /// 錄（可能寫滿某一顆），也不要因為全部踩到門檻而停止錄影。清單為空時回 null。
    /// </summary>
    public static string? ChooseRoot(
        IReadOnlyList<string> roots,
        long minFreeBytes,
        Func<string, long> freeBytes)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(freeBytes);

        string? fallback = null;
        long fallbackFree = -1;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var free = freeBytes(root);
            if (free >= minFreeBytes)
            {
                return root;
            }

            if (free > fallbackFree)
            {
                fallbackFree = free;
                fallback = root;
            }
        }

        return fallback;
    }

    /// <summary>解析設定的最小可用空間；未設定或無法解析時用 <see cref="DefaultMinFreeBytes"/>。</summary>
    public static long ParseMinFreeBytes(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0
            ? bytes
            : DefaultMinFreeBytes;

    /// <summary>
    /// 由設定建立「下一段寫哪」的選擇器：每次呼叫都重讀 <see cref="DisksSettingKey"/>，故
    /// 設定中心改磁碟後立即生效（不必重啟）。未設定磁碟清單、或挑不到任何目錄時，回
    /// <paramref name="fallbackRoot"/>（維持單碟行為）。
    /// </summary>
    public static Func<string> CreateRootSelector(
        SqliteStore store,
        string fallbackRoot,
        Func<string, long>? freeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var settings = new SettingsRepository(store);
        var probe = freeBytes ?? FreeBytesFor;
        return () =>
        {
            var roots = DiskRoots(ParseDisks(settings.Get(DisksSettingKey)));
            if (roots.Count == 0)
            {
                return fallbackRoot;
            }

            var minFree = ParseMinFreeBytes(settings.Get(MinFreeSettingKey));
            return ChooseRoot(roots, minFree, probe) ?? fallbackRoot;
        };
    }

    /// <summary>
    /// 由設定讀出「所有」錄影根目錄（磁碟清單＋退回根去重），供讀取端（遠端串流路徑政策、
    /// 配額清理）一次涵蓋多顆磁碟。寫入端只看主要碟起算的優先序，讀取端必須看全部。
    /// </summary>
    public static IReadOnlyList<string> ConfiguredRoots(SqliteStore store, string? fallbackRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        var settings = new SettingsRepository(store);
        var roots = new List<string>(DiskRoots(ParseDisks(settings.Get(DisksSettingKey))));
        if (!string.IsNullOrWhiteSpace(fallbackRoot))
        {
            roots.Add(fallbackRoot);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Drive? ChooseAlternativeDrive(IReadOnlyList<Drive> drives, string systemRoot)
    {
        Drive? chosen = null;
        foreach (var drive in drives)
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var root = DriveRootOf(drive.Root);
            if (drive.IsSystem || string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!chosen.HasValue ||
                drive.TotalBytes > chosen.Value.TotalBytes ||
                (drive.TotalBytes == chosen.Value.TotalBytes &&
                 string.CompareOrdinal(root, DriveRootOf(chosen.Value.Root)) < 0))
            {
                chosen = drive;
            }
        }

        return chosen;
    }

    private static IReadOnlyList<Drive> ProbeFixedDrives()
    {
        var systemRoot = DriveRootOf(DefaultSystemDrive());
        var drives = new List<Drive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed)
                {
                    drives.Add(new Drive(
                        drive.Name,
                        drive.IsReady,
                        string.Equals(DriveRootOf(drive.Name), systemRoot, StringComparison.OrdinalIgnoreCase),
                        drive.IsReady ? drive.TotalSize : 0));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 同上：磁碟中途退出時跳過，不讓挑碟失敗。
            }
        }

        return drives;
    }

    private static string DefaultSystemDrive() => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    /// <summary>
    /// 正規化磁碟／目錄路徑以供比較：裸磁碟代號（如 <c>C:</c>）不做
    /// <c>GetFullPath</c>（那會解析成該磁碟目前目錄），其餘取完整路徑並去尾部分隔符。
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(trimmed);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                trimmed,
                StringComparison.OrdinalIgnoreCase))
        {
            return DriveRootOf(trimmed);
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    /// <summary>
    /// 只取磁碟代號（如 <c>C:</c>，無尾部分隔符），供跨大小寫比較與設定正規化；
    /// 故意不用 <c>Path.GetFullPath</c>，因為它會把裸磁碟代號 <c>C:</c> 解析成該磁碟目前目錄，
    /// 導致系統碟比對失敗。<c>D:</c> 與 <c>D:\</c> 一律正規化成 <c>D:</c>。
    /// </summary>
    public static string DriveRootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return (Path.GetPathRoot(path) ?? path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }
}
