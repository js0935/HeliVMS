using System.Diagnostics;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>一輪小時合併的結果計數。</summary>
/// <param name="MergedHours">本輪完成合併的小時數（每個小時：N 個段檔 → 1 個檔）。</param>
/// <param name="DeletedFiles">本輪刪掉的小檔數（含清掉的孤兒檔）。</param>
/// <param name="FailedHours">合併失敗（remux 或長度驗證不過）的小時數；原檔與索引都不動。</param>
/// <param name="SkippedHours">因保全、缺檔、單一段等原因本輪跳過的小時數。</param>
public readonly record struct HourlyMergeReport(
    int MergedHours = 0,
    int DeletedFiles = 0,
    int FailedHours = 0,
    int SkippedHours = 0);

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

    public HourlySegmentMerger(
        SegmentRepository repo,
        LegalHoldRepository? legalHolds = null,
        IRemuxer? remuxer = null,
        Func<DateTime>? clock = null)
    {
        _repo = repo;
        _legalHolds = legalHolds;
        _remuxer = remuxer ?? new FfmpegRemuxer();
        _clock = clock ?? (() => DateTime.UtcNow);
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
        var failed = 0;
        var skipped = 0;

        // 最舊的先：清理也從最舊刪，合併最早的小時最不會白工。
        foreach (var (dir, rows) in groups
                     .OrderBy(g => g.Value.Min(r => r.StartUtc)))
        {
            if (merged >= HourlyMergePolicy.MaxHoursPerSweep)
            {
                break;
            }

            rows.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            var (verdict, removedFiles) = MergeOne(dir, rows, nowUtc);
            switch (verdict)
            {
                case MergeVerdict.Merged:
                    merged++;
                    deleted += removedFiles;
                    break;
                case MergeVerdict.CleanedOrphans:
                    // 已合併過的小時，清掉上次沒刺掉的殘留小檔。
                    deleted += removedFiles;
                    break;
                case MergeVerdict.Skipped:
                    skipped++;
                    break;
                case MergeVerdict.Failed:
                    failed++;
                    break;
            }
        }

        return new HourlyMergeReport(merged, deleted, failed, skipped);
    }

    private enum MergeVerdict { Merged, CleanedOrphans, Skipped, Failed }

    private (MergeVerdict Verdict, int RemovedFiles) MergeOne(string dir, List<SegmentRecord> rows, DateTime nowUtc)
    {
        // 小時必須整段結束滿一小時：合併中的小時絕不能還在被錄或剛被回放收檔。
        var groupEnd = rows.Max(r => PlaybackSegmentTiming.EndOf(r));
        if (groupEnd > nowUtc.AddHours(-HourlyMergePolicy.MinAgeHours))
        {
            return (MergeVerdict.Skipped, 0);
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
            return alreadyMerged.Count > 0
                ? CleanLeftovers(dir, _repo.ListPathsInFolder(dir), alreadyMerged[0].FilePath)
                : (MergeVerdict.Skipped, 0);
        }

        var channel = todo[0].ChannelId;
        var start = todo[0].StartUtc;
        var end = todo[^1].EndUtc ?? groupEnd;
        if (_legalHolds?.IsLocked(channel, start, end) == true)
        {
            // 被保全的錄影是證據：合併會重寫檔案與索引列，等同動到證據本體，整組跳過。
            return (MergeVerdict.Skipped, 0);
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
                return (MergeVerdict.Skipped, 0);
            }

            try
            {
                File.Delete(mergedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (MergeVerdict.Failed, 0);
            }
        }

        // 任一檔不在磁碟上就整組跳過：合併一半的清單會讓 ffmpeg 整批失敗，
        // 而缺的那幾段該由對帳（SegmentIndexReconciler）標 missing，不是這裡猜。
        foreach (var row in todo)
        {
            if (!File.Exists(row.FilePath))
            {
                return (MergeVerdict.Skipped, 0);
            }
        }

        var tmpPath = mergedPath + ".tmp";
        var expected = todo.Sum(r => Math.Max(0, (PlaybackSegmentTiming.EndOf(r) - r.StartUtc).TotalSeconds));
        try
        {
            _remuxer.Remux([.. todo.Select(r => r.FilePath)], tmpPath);

            var actual = _remuxer.ProbeDurationSeconds(tmpPath);
            var tolerance = Math.Max(
                DurationToleranceBase.TotalSeconds,
                expected * 0.02);
            if (actual is null || Math.Abs(actual.Value - expected) > tolerance)
            {
                // 長度對不上＝封裝過程掉了內容。這個檔不能換上去：索引說有一小時、
                // 檔案只有 40 分鐘，回放與匯出會在缺的位置黑屏。原檔與索引都不動。
                TryDelete(tmpPath);
                return (MergeVerdict.Failed, 0);
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
            return (MergeVerdict.Failed, 0);
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
            return (MergeVerdict.Failed, 0);
        }

        // 交易：新列進來、舊列出場，同一個原子動作。放在刪檔之前——交易失敗（磁碟忙、鎖逾時）
        // 時原檔還在、索引還對，只是多了一個孤兒 merged 檔等下一輪清；反過來做就是「索引指著
        // 已被刪掉的檔案」。
        try
        {
            _repo.ReplaceWithMerged(
                new SegmentRecord
                {
                    ChannelId = channel,
                    Stream = todo[0].Stream,
                    StartUtc = start,
                    EndUtc = end,
                    FilePath = mergedPath,
                    SizeBytes = size,
                    DurationSec = Math.Max(0, (end - start).TotalSeconds),
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
            return (MergeVerdict.Failed, 0);
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

        return (MergeVerdict.Merged, todo.Count);
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
