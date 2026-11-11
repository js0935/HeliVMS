namespace HeliVMS.Storage;

/// <summary>錄影磁碟的健康報告。</summary>
/// <param name="Volumes">錄影磁碟區的健康取樣（與錄影根目錄對得上的那些）。</param>
/// <param name="Worst">最嚴重的一顆（見 <see cref="DiskHealth.Worst"/>）。</param>
/// <param name="Failing">預測故障／有待重新配置磁區的顆數。</param>
/// <param name="Warning">已重新配置磁區或溫度偏高的顆數。</param>
public sealed record DiskHealthReport(
    IReadOnlyList<VolumeDiskHealth> Volumes,
    DiskHealthLevel Worst,
    int Failing,
    int Warning)
{
    /// <summary>組一份報告（純計算：等級與計數都由取樣推得）。</summary>
    public static DiskHealthReport From(IReadOnlyList<VolumeDiskHealth> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        var failing = 0;
        var warning = 0;
        var levels = new List<DiskHealthLevel>(volumes.Count);
        foreach (var volume in volumes)
        {
            var level = volume.Level;
            levels.Add(level);
            if (level == DiskHealthLevel.Failing)
            {
                failing++;
            }
            else if (level == DiskHealthLevel.Warning)
            {
                warning++;
            }
        }

        return new DiskHealthReport(volumes, DiskHealth.Worst(levels), failing, warning);
    }
}

/// <summary>
/// 磁碟健康的協調入口（「碟要壞了」與 <c>DiskPressureCoordinator</c> 的「碟滿了」是兩件事）。
/// </summary>
/// <remarks>
/// <para>
/// 桌面（每小時保留迴圈）與 Headless Agent（<c>RetentionWorker</c>）共用同一個入口，
/// 讀同一份設定（錄影根目錄）——與磁碟壓力同一條路徑、同一組磁碟，兩個數字才不會互相矛盾。
/// </para>
/// <para>
/// <b>只在等級改變時寫稽核</b>：一顆已經在預測故障的硬碟會連續好幾天維持同一個等級，
/// 每小時一筆會把稽核表灌滿、把真正的事件淹掉（與磁碟壓力同一條規則）。
/// 狀態存在 <c>app_settings</c> 的執行期鍵，用原始 UPSERT 寫（不記 <c>settings.set</c>）。
/// </para>
/// <para>
/// <b>讀不到一律 fail-open</b>：WMI 不是管理員、控制器不外露 S.M.A.R.T.、遠端桌面下的
/// 虛擬碟——任何一種都只讓結果變成 <see cref="DiskHealthLevel.Unknown"/>（顯示「未提供」），
/// 不影響錄影、也不誤報健康。
/// </para>
/// </remarks>
public static class DiskHealthCoordinator
{
    /// <summary>上一次回報的健康等級（去重用的執行期狀態鍵）。</summary>
    public const string LevelKey = "disk.health.level";

    /// <summary>稽核動作：錄影磁碟健康等級改變。</summary>
    public const string AuditAction = "disk.health";

    /// <summary>
    /// 讀取目前的健康狀態，<b>不寫任何紀錄</b>。供設定頁與 API 顯示。
    /// </summary>
    public static DiskHealthReport ReadConfigured(
        SqliteStore store,
        string? fallbackRoot,
        IDiskHealthSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var roots = RecordingVolumeRoots(store, fallbackRoot);
        if (roots.Count == 0)
        {
            return DiskHealthReport.From([]);
        }

        IReadOnlyList<VolumeDiskHealth> samples;
        try
        {
            samples = (source ?? new WmiDiskHealthSource()).Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 健康查詢失敗不該讓設定頁／API 失敗：回空清單（＝未提供）。
            samples = [];
        }

        var volumes = new List<VolumeDiskHealth>();
        foreach (var sample in samples)
        {
            // 比對用「磁碟代號」口徑（D:），顯示保留呼叫端原本的字串（D:\）。
            if (roots.Contains(RecordingStorage.DriveRootOf(sample.Root)))
            {
                volumes.Add(sample);
            }
        }

        return DiskHealthReport.From(volumes);
    }

    /// <summary>
    /// 讀一次並在等級改變時寫稽核；回傳本次報告。桌面與 Agent 的每小時迴圈呼叫。
    /// </summary>
    public static DiskHealthReport RunConfigured(
        SqliteStore store,
        string? fallbackRoot,
        DateTime nowUtc,
        IDiskHealthSource? source = null,
        AuditLogRepository? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var report = ReadConfigured(store, fallbackRoot, source);

        var settings = new SettingsRepository(store);

        // 狀態未設時視為「健康」：與磁碟壓力把未設值當 Normal 同一條規則——全新站台第一次跑
        // 而一切正常時不該產生一筆看起來像告警的稽核。反過來說，未知（讀不到 S.M.A.R.T.）
        // 不等於健康，所以「這台讀不到硬碟健康」仍會留下第一筆（之後維持同等級就不再寫）。
        var previous = settings.Get(LevelKey) ?? DiskHealthLevel.Healthy.ToString();
        if (string.Equals(previous, report.Worst.ToString(), StringComparison.Ordinal))
        {
            return report;
        }

        (audit ?? new AuditLogRepository(store)).Record(
            "system",
            AuditAction,
            AuditCategories.Recording,
            detail: Detail(report));

        WriteLevel(store, report.Worst, nowUtc);
        return report;
    }

    /// <summary>稽核明細：等級、顆數與每一顆的關鍵讀數（維運要能一眼看出是哪一顆）。</summary>
    private static string Detail(DiskHealthReport report)
    {
        var parts = new List<string>
        {
            $"level={report.Worst.ToString().ToLowerInvariant()}",
            $"failing={report.Failing}",
            $"warning={report.Warning}",
        };

        foreach (var volume in report.Volumes)
        {
            var attributes = volume.Attributes;
            var reads = new List<string>();
            if (attributes?.PredictFailure is { } predict)
            {
                reads.Add($"predict={predict.ToString().ToLowerInvariant()}");
            }

            if (attributes?.PendingSectors is { } pending)
            {
                reads.Add($"pending={pending}");
            }

            if (attributes?.ReallocatedSectors is { } reallocated)
            {
                reads.Add($"reallocated={reallocated}");
            }

            if (attributes?.TemperatureCelsius is { } temperature)
            {
                reads.Add($"temp={temperature}");
            }

            var model = string.IsNullOrWhiteSpace(volume.Model) ? "?" : volume.Model;
            parts.Add($"{volume.Root}{volume.Level.ToString().ToLowerInvariant()}({model} {string.Join(',', reads)})");
        }

        return string.Join(' ', parts);
    }

    /// <summary>目前設定的錄影磁碟代號（<c>D:</c> 口徑，見 <see cref="RecordingStorage.DriveRootOf"/>），去重。</summary>
    private static HashSet<string> RecordingVolumeRoots(SqliteStore store, string? fallbackRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RecordingStorage.ConfiguredRoots(store, fallbackRoot))
        {
            var drive = RecordingStorage.DriveRootOf(root);
            if (!string.IsNullOrEmpty(drive))
            {
                roots.Add(drive);
            }
        }

        return roots;
    }

    /// <summary>
    /// 以原始 UPSERT 寫狀態，刻意不走 <see cref="SettingsRepository.Set"/>：後者每寫一次就記一筆
    /// <c>settings.set</c> 稽核（給「誰改了設定」用），而這裡是機器自身的執行期狀態
    /// （與磁碟壓力 <c>DiskPressureCoordinator</c> 同一條規則）。
    /// </summary>
    private static void WriteLevel(SqliteStore store, DiskHealthLevel level, DateTime nowUtc)
    {
        store.Execute(
            """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$k", LevelKey);
                cmd.Parameters.AddWithValue("$v", level.ToString());
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(nowUtc));
            });
    }
}
