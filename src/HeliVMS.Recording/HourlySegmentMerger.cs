using System.Diagnostics;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>小時合併失敗的原因。</summary>
/// <remarks>
/// 為什麼要分原因而不是只數「失敗幾個小時」：這幾種失敗的處置完全不同——remux 失敗多半是來源檔
/// 損壞（要匯出證據、必要時人工處理），長度對不上是封裝掉了內容（不可換列），換列失敗是資料庫忙
/// （下一輪自然會好），孤兒檔刪不掉是被防毒／備份占用（檔案權限問題）。只寫「合併失敗」等於什麼都
/// 沒說，現場只能猜。
/// </remarks>
public enum MergeFailureReason
{
    /// <summary>remux 子行程失敗或 <c>.tmp</c> 寫不出來。</summary>
    RemuxFailed,

    /// <summary>ffprobe 讀不到輸出長度。</summary>
    DurationProbeFailed,

    /// <summary>輸出長度與輸入內容對不上（封裝過程掉了內容，換上去回放會黑屏）。</summary>
    DurationMismatch,

    /// <summary>算雜湊／讀檔案大小失敗。</summary>
    HashFailed,

    /// <summary>換列的交易失敗（資料庫忙、鎖逾時）。</summary>
    IndexSwapFailed,

    /// <summary>磁碟上有同名孤兒檔，卻刪不掉（被其他行程占用）。</summary>
    OrphanDeleteFailed,
}

/// <summary>一個合併失敗的小時（供提示列、日誌與 Agent log 指出「哪裡一直在失敗」）。</summary>
/// <param name="FolderPath">段檔所在的小時資料夾（<c>&lt;root&gt;\&lt;頻道&gt;\yyyyMMdd\HH</c>）。</param>
/// <param name="HourStartUtc">這一小時的第一個段起點。</param>
/// <param name="Reason">失敗原因。</param>
/// <param name="Detail">這一輪看到的數字或例外訊息（給人看的，不參與判斷）。</param>
public readonly record struct HourlyMergeFailure(
    string FolderPath,
    DateTime HourStartUtc,
    MergeFailureReason Reason,
    string? Detail = null);

/// <summary>一輪小時合併的結果計數。</summary>
/// <param name="MergedHours">本輪完成合併的小時數（每個小時：N 個段檔 → 1 個檔）。</param>
/// <param name="DeletedFiles">本輪刪掉的小檔數（含清掉的孤兒檔）。</param>
/// <param name="SkippedHours">因保全、缺檔、單一段等原因本輪跳過的小時數。</param>
/// <param name="Failures">本輪失敗的小時（含原因）。原檔與索引都不動，下一輪重試。</param>
/// <remarks>
/// 舊版只帶一個 <c>FailedHours</c> 計數，而它<b>沒有任何地方讀</b>——沒有稽核、沒有日誌、沒有 UI。
/// 於是「某一小時一直合併不起來」在現場是完全隱形的：檔案數沒有變少，卻沒有任何線索說明為什麼。
/// 實測 <c>C:\HeliVMSData\index.db</c> 對上磁碟：59 個可合併的小時裡有 33 個會停在失敗分支
/// （索引段長比檔案長 3–22%，長度驗證永遠不過），而日誌乾淨。現在失敗會帶資料夾、小時與原因。
/// </remarks>
public readonly record struct HourlyMergeReport(
    int MergedHours = 0,
    int DeletedFiles = 0,
    int SkippedHours = 0,
    IReadOnlyList<HourlyMergeFailure>? Failures = null)
{
    /// <summary>本輪失敗的小時數。</summary>
    public int FailedHours => Failures?.Count ?? 0;
}

/// <summary>
/// 把「同一個小時資料夾裡、已收尾的段」重新封裝成單一檔案（<c>merged-HHmmssfff.mp4</c>）。
///
/// <para>
/// 動機見 <see cref="HourlyMergePolicy"/>：備份、同步、防毒、SMB 瀏覽都在掃小檔。
/// 每台每天 288 檔（預設 300 秒段長）合併後剩 24 檔。
/// </para>
/// <para>
/// 這一步的危險不在合併本身，而在「刪除」：刪錯順序就是 AGENTS.md 記過的
/// 「異常結束吃掉錄影」。因此固定順序是：
/// remux 寫 <c>.tmp</c> → ffprobe 驗長度 ≈ 各段總和 → 改名成正式檔 →
/// SQLite 一個交易把 N 列換成 1 列 → <b>之後才</b>刪原檔。
/// 任何一步失敗都留下完整原檔與原索引，下一輪重試；交易後刪檔失敗的殘留
/// 小檔由下一輪的孤兒清理收掉（它們已不在索引裡，會被磁碟匯入器誤認，必須清）。
/// </para>
/// <para>
/// 只 remux（<c>-c copy</c>）不重編碼：同攝影機同串流的段參數一致，concat demuxer
/// 直接搬封包，畫質零損失、成本約等於磁碟速度。重新編碼是匯出（疊浮水印／遮蔽）的事。
/// </para>
/// </summary>
public sealed class HourlySegmentMerger
{
    /// <summary>合併檔的長度驗證容差：ffprobe 與索引的秒數都有捨入，給固定下限加比例上限。</summary>
    public static readonly TimeSpan DurationToleranceBase = TimeSpan.FromSeconds(3);

    /// <summary>remux 與 ffprobe 的抽象：測試注入替身，脫離外部進程。</summary>
    public interface IRemuxer
    {
        /// <summary>把輸入段依序封裝成單一 MP4（輸出已是 .tmp 檔）。</summary>
        void Remux(IReadOnlyList<string> inputs, string outputTmp);

        /// <summary>回傳媒體檔的實際長度（秒）；探測失敗回 <c>null</c>。</summary>
        double? ProbeDurationSeconds(string file);
    }

    private readonly SegmentRepository _repo;
    private readonly LegalHoldRepository? _legalHolds;
    private readonly IRemuxer _remuxer;
    private readonly Func<DateTime> _clock;
    private readonly string _logPath;

    /// <param name="repo">段索引。</param>
    /// <param name="legalHolds">法律保全（被保全的小時整組跳過）。</param>
    /// <param name="remuxer">remux 與探測實作；null 用真的 ffmpeg／ffprobe。</param>
    /// <param name="clock">時鐘（測試注入）。</param>
    /// <param name="logPath">失敗診斷日誌；null 表示 <c>%TEMP%\helivms-merge.log</c>。</param>
    public HourlySegmentMerger(
        SegmentRepository repo,
        LegalHoldRepository? legalHolds = null,
        IRemuxer? remuxer = null,
        Func<DateTime>? clock = null,
        string? logPath = null)
    {
        _repo = repo;
        _legalHolds = legalHolds;
        _remuxer = remuxer ?? new FfmpegRemuxer();
        _clock = clock ?? (() => DateTime.UtcNow);
        _logPath = logPath ?? Path.Combine(Path.GetTempPath(), "helivms-merge.log");
    }

    public HourlyMergeReport Run(DateTime nowUtc)
    {
        var from = nowUtc.AddDays(-HourlyMergePolicy.MaxLookbackDays);
        var cutoff = nowUtc.AddHours(-HourlyMergePolicy.MinAgeHours);
        var all = _repo.ListFinalBetween(from, cutoff);

        // 依「段檔所在資料夾」分組：同一台同一小時本來就在同一個資料夾
        //（<root>\<頻道>\yyyyMMdd\HH）。用資料夾而不是 (channel, hour) 分組，是因為
        // 跨磁碟搬移後同一小時可能分落兩處——那就各合各的，不要假設它們相鄰。
        var groups = new Dictionary<string, List<SegmentRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in all)
        {
            if (seg.Status != SegmentStatus.Final || string.IsNullOrEmpty(seg.FilePath))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(seg.FilePath);
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            if (!groups.TryGetValue(dir, out var list))
            {
                list = [];
                groups[dir] = list;
            }

            list.Add(seg);
        }

        var merged = 0;
        var deleted = 0;
        var skipped = 0;
        var failures = new List<HourlyMergeFailure>();

        // 最舊的先：清理也從最舊刪，合併最早的小時最不會白工。
        foreach (var (dir, rows) in groups
                     .OrderBy(g => g.Value.Min(r => r.StartUtc)))
        {
            if (merged >= HourlyMergePolicy.MaxHoursPerSweep)
            {
                break;
            }

            rows.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            var outcome = MergeOne(dir, rows, nowUtc);
            switch (outcome.Verdict)
            {
                case MergeVerdict.Merged:
                    merged++;
                    deleted += outcome.RemovedFiles;
                    break;
                case MergeVerdict.CleanedOrphans:
                    // 已合併過的小時，清掉上次沒刪掉的殘留小檔。
                    deleted += outcome.RemovedFiles;
                    break;
                case MergeVerdict.Skipped:
                    skipped++;
                    break;
                case MergeVerdict.Failed:
                    // 失敗要「說得出是哪一小時、為什麼」：舊版只把數字加一，然後沒有任何人讀它。
                    var failure = new HourlyMergeFailure(
                        dir,
                        rows[0].StartUtc,
                        outcome.Reason,
                        outcome.Detail);
                    failures.Add(failure);
                    LogFailure(failure);
                    break;
            }
        }

        return new HourlyMergeReport(merged, deleted, skipped, failures);
    }

    /// <summary>
    /// 一行一筆失敗寫進 <c>%TEMP%\helivms-merge.log</c>（上限 256 KB，超過就截掉最舊的）。
    /// </summary>
    /// <remarks>
    /// 為什麼是檔案而不是稽核：同一個小時可能每一輪都失敗，而清理迴圈每小時跑一次——病態情況下
    /// 一天近百筆，寫進稽核就是把真正的訊號埋掉（同 DI 失敗稽核要節流的理由）。診斷日誌只給
    /// 「為什麼」的線索，提示列另外只講一句摘要，兩者都不會淹掉稽核。
    /// </remarks>
    private void LogFailure(HourlyMergeFailure failure)
    {
        // BoundedFileLog 自己吞 IOException／UnauthorizedAccessException（診斷寫入不該讓本體失敗），
        // 所以這裡不必再包一層 try。
        BoundedFileLog.Append(
            _logPath,
            $"{_clock():O} merge-failed reason={failure.Reason} "
            + $"hour={failure.HourStartUtc:yyyyMMdd HH} folder={failure.FolderPath} "
            + $"detail={failure.Detail}{Environment.NewLine}",
            256 * 1024);
    }

    private enum MergeVerdict { Merged, CleanedOrphans, Skipped, Failed }

    /// <summary>一個資料夾這一輪的處置結果。</summary>
    private readonly record struct MergeOutcome(
        MergeVerdict Verdict,
        int RemovedFiles = 0,
        MergeFailureReason Reason = MergeFailureReason.RemuxFailed,
        string? Detail = null)
    {
        public static MergeOutcome Ok(MergeVerdict verdict, int removedFiles = 0)
            => new(verdict, removedFiles);

        public static MergeOutcome Failed(MergeFailureReason reason, string? detail = null)
            => new(MergeVerdict.Failed, 0, reason, detail);
    }

    private MergeOutcome MergeOne(string dir, List<SegmentRecord> rows, DateTime nowUtc)
    {
        // 小時必須整段結束滿一小時：合併中的小時絕不能還在被錄或剛被回放收檔。
        var groupEnd = rows.Max(r => PlaybackSegmentTiming.EndOf(r));
        if (groupEnd > nowUtc.AddHours(-HourlyMergePolicy.MinAgeHours))
        {
            return MergeOutcome.Ok(MergeVerdict.Skipped);
        }

        // 這一小時裡「已經合併過的子區間」與「還沒合併的段」要分開看。
        //
        // 為什麼不能只用「最早那一列的檔名」判斷整小時合併過：合併只處理到當時已收尾的段，
        // 之後才收尾的段不在那一輪的清單裡（例如 11-07 23 時合併到 23:38，而 23:54／23:59
        // 兩段是後來才 final 的）。舊版看到最早那一列是 merged-*.mp4 就直接進「清孤兒」分支，
        // 於是那 2–3 個小檔<b>永遠</b>不會被併進去、每一輪都白跑一次查詢——實測索引裡有 7 個
        // 資料夾是這個形狀（每個小時仍留著 2–3 個小檔，合併的意義正好消失）。
        var alreadyMerged = rows.Where(r => IsMergedFile(r.FilePath)).ToList();
        var todo = rows.Where(r => !IsMergedFile(r.FilePath)).ToList();

        // 沒有可合併的殘段：已合併的小時只清理交易後刪檔失敗留下的孤兒
        //（它們已不在索引裡，留著會被磁碟匯入器誤認成新段）。
        if (todo.Count < 2)
        {
            if (alreadyMerged.Count == 0)
            {
                return MergeOutcome.Ok(MergeVerdict.Skipped);
            }

            var (verdict, removed) = CleanLeftovers(dir, _repo.ListPathsInFolder(dir), alreadyMerged[0].FilePath);
            return MergeOutcome.Ok(verdict, removed);
        }

        var channel = todo[0].ChannelId;
        var start = todo[0].StartUtc;
        var end = todo[^1].EndUtc ?? groupEnd;
        if (_legalHolds?.IsLocked(channel, start, end) == true)
        {
            // 被保全的錄影是證據：合併會重寫檔案與索引列，等同動到證據本體，整組跳過。
            return MergeOutcome.Ok(MergeVerdict.Skipped);
        }

        var mergedPath = Path.Combine(dir, MergedFileName(start));

        if (File.Exists(mergedPath))
        {
            // 磁碟上有同名檔，先問索引：有這一列就<b>不能刪</b>（那是某一列指著的內容，
            // 例如磁碟離線被標成 missing 之後又接回來），這一小時本輪跳過。
            // 索引裡沒有才是「換列之前斷電」留下的孤兒，直接刪掉重做——留著的話
            // 下一次 File.Move 會撞名，而且磁碟匯入器會把它當成整點的段。
            if (IsIndexed(dir, mergedPath))
            {
                return MergeOutcome.Ok(MergeVerdict.Skipped);
            }

            try
            {
                File.Delete(mergedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return MergeOutcome.Failed(MergeFailureReason.OrphanDeleteFailed, ex.Message);
            }
        }

        // 任一檔不在磁碟上就整組跳過：合併一半的清單會讓 ffmpeg 整批失敗，
        // 而缺的那幾段該由對帳（SegmentIndexReconciler）標 missing，不是這裡猜。
        foreach (var row in todo)
        {
            if (!File.Exists(row.FilePath))
            {
                return MergeOutcome.Ok(MergeVerdict.Skipped);
            }
        }

        var tmpPath = mergedPath + ".tmp";
        var indexedSeconds = todo.Sum(r => Math.Max(0, (PlaybackSegmentTiming.EndOf(r) - r.StartUtc).TotalSeconds));
        double contentSeconds;
        try
        {
            _remuxer.Remux([.. todo.Select(r => r.FilePath)], tmpPath);

            var actual = _remuxer.ProbeDurationSeconds(tmpPath);
            if (actual is null)
            {
                TryDelete(tmpPath);
                return MergeOutcome.Failed(MergeFailureReason.DurationProbeFailed, $"tmp={tmpPath}");
            }

            // 長度驗證的基準必須是「輸入檔自己的長度」，不能只信索引的 duration_sec。
            //
            // 索引的段長在舊版是牆鐘（行程活了多久），比檔案內容長 3–22%，Windows 睡眠或校時
            // 一次更會多出幾百甚至幾千秒；那時這裡拿索引當基準，等於「拿一個已知會說謊的尺去量
            // 一個正確的輸出」，於是這些小時<b>永遠</b>合併不起來。實測現場：59 個可合併的小時
            // 有 33 個停在失敗分支（例：攝影機05\20261109\02 索引合計 3505.2 秒、14 個檔案
            // 自己的長度合計 3065.4 秒，輸出 3065.4 秒——內容一個都沒少），而日誌乾淨。
            //
            // 所以順序是：先用索引（便宜，正常情況一次探測就夠）→ 對不上時才探測每一個輸入，
            // 用「輸入內容合計」再判一次。真的掉了內容仍然擋得住，只是多花一次探測。
            contentSeconds = indexedSeconds;
            if (Math.Abs(actual.Value - indexedSeconds) > Tolerance(indexedSeconds))
            {
                var measured = TrySumInputSeconds(todo);
                if (measured is null || Math.Abs(actual.Value - measured.Value) > Tolerance(measured.Value))
                {
                    // 輸出與「輸入內容合計」也對不上＝封裝過程真的掉了內容。這個檔不能換上去：
                    // 索引說有一小時、檔案只有 40 分鐘，回放與匯出會在缺的位置黑屏。原檔與索引都不動。
                    TryDelete(tmpPath);
                    return MergeOutcome.Failed(
                        MergeFailureReason.DurationMismatch,
                        $"out={actual.Value:0.###} index={indexedSeconds:0.###} inputs={measured?.ToString("0.###") ?? "unknown"}");
                }

                contentSeconds = measured.Value;
            }

            File.Move(tmpPath, mergedPath, overwrite: false);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            TryDelete(tmpPath);
            return MergeOutcome.Failed(MergeFailureReason.RemuxFailed, ex.Message);
        }

        long size;
        string sha256;
        try
        {
            size = new FileInfo(mergedPath).Length;

            // 這裡一定要把檔案關掉：`SHA256.HashData(File.OpenRead(path))` 開的 FileStream
            // 沒有人處置，Windows 上它就是一個開著的讀取控制代碼（FileShare.Read），
            // 於是①這一段 60 MB 的檔案在下一次 GC 之前不能被刪、不能被保留策略汰除；
            // ②下面換列失敗時那道 TryDelete(mergedPath) 保證刪不掉自己剛產出的檔，
            // 現場因此留下一個 63.9 MB、索引裡沒有它、保留策略也看不到的孤兒
            // （merged-170348710.mp4，磁碟上有、索引裡沒有，回收只認索引）。
            using (var stream = File.OpenRead(mergedPath))
            {
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
                    .ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(mergedPath);
            return MergeOutcome.Failed(MergeFailureReason.HashFailed, ex.Message);
        }

        // 交易：新列進來、舊列出場，同一個原子動作。放在刪檔之前——交易失敗（磁碟忙、鎖逾時）
        // 時原檔還在、索引還對，只是多了一個孤兒 merged 檔等下一輪清；反過來做就是「索引指著
        // 已被刪掉的檔案」。
        //
        // 合併列的長度用「合併檔自己的長度」，不是第一個到最後一個舊列的牆鐘跨度：
        // 舊列的 duration_sec 可能偏大（見上面的驗證），照抄等於把錯放進新列，
        // 而新列的長度會被時間軸、方向鍵與多路回放直接拿去算時刻。
        var mergedEnd = start.AddSeconds(contentSeconds);
        try
        {
            _repo.ReplaceWithMerged(
                new SegmentRecord
                {
                    ChannelId = channel,
                    Stream = todo[0].Stream,
                    StartUtc = start,
                    EndUtc = mergedEnd,
                    FilePath = mergedPath,
                    SizeBytes = size,
                    DurationSec = contentSeconds,
                    Status = SegmentStatus.Final,
                    Sha256 = sha256,
                    Tier = "merged",
                },
                [.. todo.Select(r => r.Id)]);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException
                or InvalidOperationException)
        {
            TryDelete(mergedPath);
            return MergeOutcome.Failed(MergeFailureReason.IndexSwapFailed, ex.Message);
        }

        // 索引已換成 merged 列，這裡才刪原檔。刪不掉（被防毒／備份占用）就留給下一輪清理。
        foreach (var row in todo)
        {
            try
            {
                File.Delete(row.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 下一輪的 CleanLeftovers 會再試。
            }
        }

        return MergeOutcome.Ok(MergeVerdict.Merged, todo.Count);
    }

    /// <summary>長度驗證的容差：固定下限加比例（ffprobe 與索引的秒數都有捨入）。</summary>
    private static double Tolerance(double expectedSeconds)
        => Math.Max(DurationToleranceBase.TotalSeconds, expectedSeconds * 0.02);

    /// <summary>
    /// 探測每一個輸入檔的長度並加總；任何一個探測不到就回 <c>null</c>
    /// （呼叫端據此保守地維持「不能換列」的判斷）。
    /// </summary>
    private double? TrySumInputSeconds(IReadOnlyList<SegmentRecord> todo)
    {
        var total = 0.0;
        foreach (var row in todo)
        {
            double? seconds;
            try
            {
                seconds = _remuxer.ProbeDurationSeconds(row.FilePath);
            }
            catch (Exception)
            {
                // 探測器本身拋例外與「探測不到」同義：寧可判失敗，也不要拿猜的長度換列。
                return null;
            }

            if (seconds is not > 0)
            {
                return null;
            }

            total += seconds.Value;
        }

        return total;
    }

    /// <summary>這是合併檔嗎（檔名 <c>merged-HHmmssfff.mp4</c>；磁碟匯入器用同一個字首認它）。</summary>
    private static bool IsMergedFile(string path)
        => Path.GetFileName(path).StartsWith("merged-", StringComparison.OrdinalIgnoreCase);

    /// <summary>這個資料夾裡有沒有任何一列（不分狀態）指著這個路徑。</summary>
    private bool IsIndexed(string dir, string path)
    {
        var normalized = Normalize(path);
        foreach (var (filePath, _) in _repo.ListPathsInFolder(dir))
        {
            if (Normalize(filePath).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>清掉「索引裡已是 merged 列」之後還留在小時資料夾裡的殘留段檔。</summary>
    private (MergeVerdict Verdict, int RemovedFiles) CleanLeftovers(
        string dir,
        IReadOnlyList<(string FilePath, SegmentStatus Status)> known,
        string mergedPath)
    {
        var knownSet = new HashSet<string>(known.Select(k => Normalize(k.FilePath)), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in SafeSegFiles(dir))
        {
            var normalized = Normalize(file);
            if (Normalize(mergedPath).Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || knownSet.Contains(normalized))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 刪不掉就下一輪再試；它對回放無害（索引沒有它）。
            }
        }

        return removed > 0 ? (MergeVerdict.CleanedOrphans, removed) : (MergeVerdict.Skipped, 0);
    }

    private static IReadOnlyList<string> SafeSegFiles(string dir)
    {
        try
        {
            // *.mp4 而不含 *.tmp：tmp 是本步驟或收尾中的半成品，不歸孤兒清理管。
            return Directory.EnumerateFiles(dir, "*.mp4")
                .Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Normalize(string path)
        => path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉的 tmp 交給 RetentionService.PurgeStaleTmp（十分鐘後會收）。
        }
    }

    /// <summary>合併檔名：與段檔同一套時間編碼（HHmmssfff），字首改為 merged-，
    /// 磁碟匯入器才能從檔名反推出正確的開始時間。</summary>
    public static string MergedFileName(DateTime startUtc)
        => $"merged-{startUtc:HHmmssfff}.mp4";

    /// <summary>ffmpeg 實作：concat demuxer + <c>-c copy</c>，只搬封包不重編碼。</summary>
    private sealed class FfmpegRemuxer : IRemuxer
    {
        public void Remux(IReadOnlyList<string> inputs, string outputTmp)
        {
            if (inputs.Count == 0)
            {
                throw new InvalidOperationException("no inputs");
            }

            var listPath = outputTmp + ".concat.txt";
            try
            {
                var list = new System.Text.StringBuilder();
                foreach (var input in inputs)
                {
                    // concat demuxer 的 quoting：路徑包單引號，內部單引號用 '\'' 跳脫。
                    list.Append("file '").Append(input.Replace("'", "'\\''")).Append("'\n");
                }

                File.WriteAllText(listPath, list.ToString());

                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Ffmpeg,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("concat");
                psi.ArgumentList.Add("-safe");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(listPath);
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("mp4");
                psi.ArgumentList.Add(outputTmp);

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("ffmpeg could not be started");

                // 同時抽乾 stdout／stderr，避免任一管線填滿使進程阻塞（同 MediaIntegrityChecker）。
                var stderrTask = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();

                if (!process.WaitForExit(600_000))
                {
                    try { process.Kill(true); } catch { /* 早已結束 */ }
                    throw new InvalidOperationException("ffmpeg merge timed out");
                }

                var stderr = stderrTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"ffmpeg merge failed ({process.ExitCode}): {Trim(stderr)}");
                }
            }
            finally
            {
                try { if (File.Exists(listPath)) File.Delete(listPath); }
                catch (IOException) { }
            }
        }

        public double? ProbeDurationSeconds(string file) => MediaDurationProbe.ProbeSeconds(file);

        private static string Trim(string text)
        {
            var value = text.Trim();
            return value.Length <= 400 ? value : value[..400];
        }
    }
}
