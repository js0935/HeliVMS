using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// 磁碟上一個「有未建索引錄影」的頻道資料夾。
/// </summary>
/// <param name="FolderPath">資料夾完整路徑（<c>&lt;root&gt;\&lt;頻道資料夾&gt;</c>）。</param>
/// <param name="FolderName">資料夾名稱（頻道名稱或舊制 <c>chNNN</c>）。</param>
/// <param name="ChannelId">依資料夾名稱自動對到的頻道；對不到時為 null，由操作員指定。</param>
/// <param name="FileCount">尚未建索引的段數。</param>
/// <param name="SizeBytes">這些段的檔案大小總和。</param>
/// <param name="FromUtc">最早一段的開始時間（UTC）。</param>
/// <param name="ToUtc">最晚一段的開始時間（UTC）。</param>
public sealed record RecordingImportFolder(
    string FolderPath,
    string FolderName,
    int? ChannelId,
    int FileCount,
    long SizeBytes,
    DateTime FromUtc,
    DateTime ToUtc);

/// <summary>一次磁碟錄影匯入的結果。</summary>
/// <param name="Folders">實際處理的資料夾數。</param>
/// <param name="Imported">新增的區段列數。</param>
/// <param name="Revived">原本被標成 missing／tmp／corrupt、因檔案仍在而復活的列數。</param>
/// <param name="Skipped">已在索引中或無法解析而略過的檔案數。</param>
/// <param name="Bytes">新增段（含復活）的檔案大小總和。</param>
public readonly record struct RecordingImportReport(
    int Folders,
    int Imported,
    int Revived,
    int Skipped,
    long Bytes)
{
    /// <summary>本輪是否補回任何可回放的錄影。</summary>
    public bool AnyImported => Imported > 0 || Revived > 0;
}

/// <summary>
/// 把磁碟上「索引裡沒有」的錄影段補回 <c>segments</c> 表。
/// </summary>
/// <remarks>
/// <para>
/// 症狀是「磁碟裡明明有錄影，回放卻什麼都沒有」：索引與檔案是兩份獨立的事實，
/// 資料庫被重建、還原成舊備份、頻道刪掉又重加（<c>segments</c> 是 CASCADE）、
/// 或整顆錄影碟換到另一台機器時，檔案都還在，索引卻已經不認得它們。
/// 既有的 <see cref="SegmentIndexReconciler"/> 只處理反方向（索引有、磁碟沒有），
/// 沒有它就不會有人把檔案補回索引——操作員只能看到一個空回放，日誌完全乾淨。
/// </para>
/// <para>
/// 對應頻道的規則刻意與錄影端共用 <see cref="RecordingFolderNaming.Resolve"/>：
/// 資料夾名稱是頻道名稱（同名頻道會附加 <c>_chNNN</c>），舊資料則是 <c>chNNN</c>。
/// 兩邊各寫一份命名規則一定會漂移，屆時新舊資料夾只有一邊認得出來。
/// 兩條都對不上時<b>不猜</b>：回報 <see cref="RecordingImportFolder.ChannelId"/> 為 null，
/// 由操作員在匯入畫面指定——把 A 台的錄影掛到 B 台名下比看不到更糟（證據錯置）。
/// </para>
/// <para>
/// 開始時間取自檔名（錄影端以 UTC 的 <c>HHmmssfff</c> 命名）與 <c>yyyyMMdd</c> 日期資料夾；
/// 結束時間取檔案的 <c>LastWriteTimeUtc</c>（ffmpeg 收尾關檔的時刻）。複製、還原回來的檔案
/// 會被蓋成「現在」，所以超過一小時一律視為不可信，改用下一段的開始時間推算，最少也會給一個
/// 正的時長——零長度的段在回放時間軸上不會被畫出來，等於匯入了卻還是看不到。
/// 時間解析不出來的檔案直接略過：亂猜一個時間只會讓時間軸出現一段對不上的空白，比不匯入更難查。
/// </para>
/// </remarks>
public sealed class RecordingDiskImporter
{
    /// <summary>錄影段檔名首碼；與 <see cref="SegmentRepository.SegmentFileMarker"/> 同一個事實來源。</summary>
    private const string SegmentPrefix = SegmentRepository.SegmentFileMarker;

    /// <summary>段檔副檔名；只收已完成收尾的正式檔，<c>.tmp</c> 交給 <c>TemporarySegmentRecovery</c>。</summary>
    private const string SegmentExtension = ".mp4";

    /// <summary>
    /// 由開始時間推算結束時間的上限：正常一段是幾十秒，超過一小時代表檔案的
    /// <c>LastWriteTimeUtc</c> 不是收尾時刻（複製、還原都會蓋成當下時間），寧可記 0 秒。
    /// </summary>
    private static readonly TimeSpan MaxInferredDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// 沒有可信收尾時間時，改用「下一段開始」推算時長的上限：正常一段幾十秒，
    /// 稀疏的事件錄影下一段可能隔了好幾小時，不能讓一段蓋掉整個下午。
    /// </summary>
    private static readonly TimeSpan MaxEstimatedDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 完全無從推算時的時長。與 <see cref="PlaybackTimelineBuilder"/> 的預設值一致：
    /// 零長度的段不會被畫在時間軸上，索引裡有、畫面上沒有，等於回報使用者「這裡沒有錄影」。
    /// </summary>
    private const double FallbackDurationSec = 10;

    private readonly SegmentRepository _repo;

    public RecordingDiskImporter(SegmentRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    /// <summary>
    /// 掃描各錄影根目錄，回傳「有未建索引錄影」的資料夾；已索引的檔案不列入。
    /// </summary>
    /// <param name="roots">要掃描的根目錄（目前根目錄 ＋ 已知舊根目錄）；不存在的直接跳過。</param>
    /// <param name="channels">目前所有頻道，用來把資料夾名稱對回頻道。</param>
    public IReadOnlyList<RecordingImportFolder> Scan(
        IEnumerable<string> roots,
        IReadOnlyList<ChannelInfo> channels)
    {
        var byId = channels.Where(c => c.Id > 0).ToDictionary(c => c.Id);
        var byFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, folder) in RecordingFolderNaming.Resolve(channels))
        {
            byFolder[folder] = id;
        }

        var found = new List<RecordingImportFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in SafeEnumerate(root))
            {
                var folderPath = Normalize(dir);
                if (folderPath.Length == 0 || !seen.Add(folderPath))
                {
                    continue;
                }

                var folderName = Path.GetFileName(folderPath);
                var channelId = MatchChannel(folderName, byFolder, byId);
                var files = Collect(folderPath);
                if (files.Count == 0)
                {
                    continue;
                }

                found.Add(new RecordingImportFolder(
                    folderPath,
                    folderName,
                    channelId,
                    files.Count,
                    files.Sum(f => f.SizeBytes),
                    files.Min(f => f.StartUtc),
                    files.Max(f => f.StartUtc)));
            }
        }

        return found
            .OrderBy(f => f.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 匯入指定的資料夾；<paramref name="assignments"/> 是「資料夾 → 頻道」的決定。
    /// 同一個資料夾重複匯入是安全的：已索引的檔案不會再寫一次。
    /// </summary>
    public RecordingImportReport Import(
        IEnumerable<(string FolderPath, int ChannelId)> assignments)
    {
        var folders = 0;
        var imported = 0;
        var revived = 0;
        var skipped = 0;
        long bytes = 0;

        foreach (var (folderPath, channelId) in assignments)
        {
            if (channelId <= 0 || !Directory.Exists(folderPath))
            {
                continue;
            }

            folders++;
            foreach (var file in Collect(folderPath))
            {
                var outcome = _repo.AdoptOrReviveFinal(
                    channelId,
                    "main",
                    file.StartUtc,
                    file.EndUtc,
                    file.FilePath,
                    file.SizeBytes,
                    file.DurationSec);

                switch (outcome)
                {
                    case AdoptOutcome.Inserted:
                        imported++;
                        bytes += file.SizeBytes;
                        break;
                    case AdoptOutcome.Revived:
                        revived++;
                        bytes += file.SizeBytes;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
        }

        return new RecordingImportReport(folders, imported, revived, skipped, bytes);
    }

    /// <summary>
    /// 匯入畫面的預設選擇：自動對到頻道就用它，對不到就是 0（「略過」），不替操作員猜。
    /// </summary>
    public static int DefaultChannelId(RecordingImportFolder folder) => folder.ChannelId ?? 0;

    /// <summary>
    /// 從匯入畫面每一列的選擇挑出真正要寫入的 (資料夾, 頻道)。
    /// </summary>
    /// <remarks>
    /// 兩件事在這裡收斂，避免畫面各自實作而漂移：①選「略過」（0）的資料夾不匯入——
    /// 若把 0 傳下去，<see cref="Import"/> 也只會默默跳過，但呼叫端會得到一個看起來
    /// 成功、實際上什麼都沒做的結果；②同一個資料夾只匯一次，重複列舉（例如兩個根目錄
    /// 指向同一顆碟）會讓回報的段數重複計算。
    /// </remarks>
    public static IReadOnlyList<(string FolderPath, int ChannelId)> SelectAssignments(
        IEnumerable<(string FolderPath, int SelectedChannelId)> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<(string, int)>();
        foreach (var (folderPath, channelId) in rows)
        {
            if (channelId <= 0 || string.IsNullOrWhiteSpace(folderPath) || !seen.Add(folderPath))
            {
                continue;
            }

            picked.Add((folderPath, channelId));
        }

        return picked;
    }

    /// <summary>資料夾名稱 → 頻道編號：先比對目前的命名，再退回舊制 <c>chNNN</c>。</summary>
    private static int? MatchChannel(
        string folderName,
        IReadOnlyDictionary<string, int> byFolder,
        IReadOnlyDictionary<int, ChannelInfo> byId)
    {
        if (byFolder.TryGetValue(folderName, out var id) && byId.ContainsKey(id))
        {
            return id;
        }

        // 舊制 chNNN（例如 ch072）——頻道被刪掉又重加時編號可能不同，所以只在頻道真的
        // 存在時才對應，否則寧可讓操作員指定。
        if (folderName.Length > 2
            && folderName.StartsWith("ch", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(folderName.AsSpan(2), out var legacy)
            && byId.ContainsKey(legacy))
        {
            return legacy;
        }

        return null;
    }

    /// <summary>收集資料夾底下可匯入的段；同一秒的檔案只留一個（索引的唯一鍵是頻道＋時間）。</summary>
    /// <remarks>
    /// 時長一定要算出一個正值。回放時間軸（<see cref="PlaybackTimelineBuilder"/>）刻意略過零長度的段，
    /// 而索引裡「有這一列、時間軸上沒有這一條」等於告訴操作員這裡沒有錄影——明明檔案就在磁碟上。
    /// 收尾時間不可信時，先看下一段的開始時間（全時錄影是連續的，差額就是這段的長度），
    /// 再不行才退回 <see cref="FallbackDurationSec"/>。
    /// </remarks>
    private List<DiskSegment> Collect(string folderPath)
    {
        var known = KnownPaths(folderPath);
        var parsed = new List<(string Path, long Size, DateTime StartUtc, DateTime WrittenUtc)>();
        var starts = new HashSet<DateTime>();

        foreach (var path in SafeEnumerateFiles(folderPath))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(SegmentPrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(SegmentExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var full = Normalize(path);
            if (full.Length == 0)
            {
                continue;
            }

            long size;
            DateTime writtenUtc;
            try
            {
                var info = new FileInfo(full);
                size = info.Length;
                writtenUtc = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (size <= 0)
            {
                continue;
            }

            if (known.TryGetValue(full, out var status))
            {
                if (status is SegmentStatus.Final or SegmentStatus.Retired)
                {
                    // 已經在索引裡而且是完成狀態，磁碟上的檔案不必再看。
                    continue;
                }

                // tmp／corrupt 也代表「索引還沒認可這個檔案」（收尾途中失敗會留下這種列），
                // 只有在檔案本身真的像 MP4 時才採納：corrupt 的檔案常常是半截或垃圾，
                // 復活它等於把「看不到」換成「點下去報錯」，比不匯入更糟。
                if ((status is SegmentStatus.Temporary or SegmentStatus.Corrupt)
                    && !Mp4Signature.LooksLikeMp4(full))
                {
                    continue;
                }
            }

            if (!TryParseStartUtc(full, out var startUtc) || !starts.Add(startUtc))
            {
                continue;
            }

            parsed.Add((full, size, startUtc, writtenUtc));
        }

        // 依開始時間排序，「下一段」才有意義（檔名是時分秒，列舉順序不保證）。
        parsed.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));

        var files = new List<DiskSegment>(parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var (path, size, startUtc, writtenUtc) = parsed[i];
            var duration = InferredDuration(parsed, i);
            files.Add(new DiskSegment(path, startUtc, startUtc.AddSeconds(duration), size, duration));
        }

        return files;
    }

    /// <summary>這一段的時長（秒）；收尾時間不可信時改用下一段開始時間，最後退回固定值。</summary>
    private static double InferredDuration(
        IReadOnlyList<(string Path, long Size, DateTime StartUtc, DateTime WrittenUtc)> parsed,
        int index)
    {
        var (_, _, startUtc, writtenUtc) = parsed[index];

        if (writtenUtc > startUtc && writtenUtc - startUtc <= MaxInferredDuration)
        {
            return (writtenUtc - startUtc).TotalSeconds;
        }

        if (index + 1 < parsed.Count)
        {
            var gap = (parsed[index + 1].StartUtc - startUtc).TotalSeconds;
            if (gap > 0 && gap <= MaxEstimatedDuration.TotalSeconds)
            {
                return gap;
            }
        }

        return FallbackDurationSec;
    }

    /// <summary>
    /// 資料夾內已建索引的路徑與狀態。
    /// <para>
    /// <c>final</c>／<c>retired</c> 是「索引已經認可」的狀態，掃描時跳過；<c>missing</c>／
    /// <c>tmp</c>／<c>corrupt</c> 代表索引不認得磁碟上這個檔案（檔案被刪過、或收尾途中失敗），
    /// 由 <see cref="Collect"/> 進一步確認後採納。
    /// </para>
    /// </summary>
    private Dictionary<string, SegmentStatus> KnownPaths(string folderPath)
    {
        var known = new Dictionary<string, SegmentStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, status) in _repo.ListPathsInFolder(folderPath))
        {
            var normalized = Normalize(path);
            if (normalized.Length > 0)
            {
                known[normalized] = status;
            }
        }

        return known;
    }

    /// <summary>
    /// 從 <c>&lt;root&gt;\&lt;頻道&gt;\yyyyMMdd[\HH]\seg-HHmmss[fff].mp4</c> 解析開始時間（UTC）。
    /// </summary>
    private static bool TryParseStartUtc(string filePath, out DateTime startUtc)
    {
        startUtc = default;

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Length <= SegmentPrefix.Length
            || !name.StartsWith(SegmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = name[SegmentPrefix.Length..];
        if (digits.Length < 6 || digits.Length > 9
            || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var hour = int.Parse(digits.AsSpan(0, 2), System.Globalization.CultureInfo.InvariantCulture);
        var minute = int.Parse(digits.AsSpan(2, 2), System.Globalization.CultureInfo.InvariantCulture);
        var second = int.Parse(digits.AsSpan(4, 2), System.Globalization.CultureInfo.InvariantCulture);
        var millis = digits.Length >= 9
            ? int.Parse(digits.AsSpan(6, 3), System.Globalization.CultureInfo.InvariantCulture)
            : 0;

        if (hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        // 日期資料夾是 yyyyMMdd（錄影端以 UTC 命名）；往上找最近的一層。
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var leaf = Path.GetFileName(dir);
            if (leaf.Length == 8
                && int.TryParse(leaf.AsSpan(0, 4), out var year)
                && int.TryParse(leaf.AsSpan(4, 2), out var month)
                && int.TryParse(leaf.AsSpan(6, 2), out var day)
                && year >= 2000 && year <= 9999 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                try
                {
                    startUtc = new DateTime(year, month, day, hour, minute, second, millis, DateTimeKind.Utc);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    /// <summary>列舉第一層頻道資料夾；沒有權限的根目錄不該讓整次掃描失敗。</summary>
    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>遞迴列舉段檔；個別子資料夾讀不到時跳過，不讓一個壞目錄中斷整次掃描。</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string folderPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        try
        {
            return Directory.EnumerateFiles(folderPath, "*" + SegmentExtension, options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>正規化路徑（大小寫不敏感比較用）；失敗時回空字串，呼叫端略過該筆。</summary>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private readonly record struct DiskSegment(
        string FilePath,
        DateTime StartUtc,
        DateTime EndUtc,
        long SizeBytes,
        double DurationSec);
}
