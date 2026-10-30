using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage;

/// <summary>
/// 一輪索引對帳的結果。
/// <para>
/// <see cref="Unverified"/> 是「無法判定」而不是「不存在」：磁碟沒掛上時整顆的段都落在這裡。
/// 兩者分開才有辦法讓呼叫端說出正確的話——「N 段錄影檔已不存在」與「錄影磁碟未就緒」
/// 對操作員是完全不同的兩件事，混在一起就變成一個沒人看得懂的數字。
/// </para>
/// </summary>
public readonly record struct ReconcileReport(
    int Checked,
    int MarkedMissing,
    int Unverified,
    int FoldersRemoved = 0)
{
    /// <summary>本輪是否真的把某些段標成了 missing（呼叫端據此決定要不要寫稽核、提示）。</summary>
    public bool AnyMarked => MarkedMissing > 0;
}

/// <summary>
/// 索引與磁碟的對帳：把「索引說有、磁碟上沒有」的 final 區段標成
/// <see cref="SegmentStatus.Missing"/>。
///
/// <para>
/// 為什麼需要它：回放清單、回放日曆、用量統計與汰除候選全部只讀 <c>segments</c> 表，
/// 而表裡的路徑不保證還在。使用者把錄影磁碟上的資料刪掉（換硬碟、清空間、搬檔）之後，
/// 回放仍然列出整天的「錄影」，點下去才失敗——操作員看到的是「明明有錄影卻播不出來」，
/// 而且日曆上每一天都還是粗體，看起來像系統在說謊。
/// </para>
/// <para>
/// 一輪分兩段，因為兩種刪除形狀的成本差三個數量級：
/// </para>
/// <list type="number">
/// <item>
/// <b>資料夾層級</b>：整個 <c>yyyyMMdd</c> 資料夾被刪掉時，每個段逐一 stat 只是白花 I/O，
/// 一次 <c>Directory.Exists</c> 就能涵蓋整天的段。這也是最常見的形狀（換硬碟、清空間）。
/// 資料夾清單由 <see cref="SegmentRepository.ListFinalFolders"/> 從索引一次問出來，
/// 成本只與「天數 × 頻道數」有關，所以<b>整段歷史</b>都能在一輪內涵蓋。
/// </item>
/// <item>
/// <b>逐檔</b>：資料夾還在、裡面少數檔案被刪（或搬走）時才需要逐一 stat，因此只做
/// 最近的 <c>batchSize</c> 個 final 段。舊資料夾裡的個別檔案不逐檔驗是刻意的取捨：
/// 網路錄影根目錄上每個 stat 要毫秒級，一小時掃一次整個歷史會壓垮錄影 I/O；而使用者一打開
/// 那一天，回放（<see cref="VerifyExisting"/>）就會逐檔驗並修正顯示。
/// </item>
/// </list>
/// <para>
/// 判定口徑只有一個：<see cref="File.Exists(string)"/>。<b>不</b>用 ffprobe 判斷，因為那會讓
/// 對帳從「幾千次 stat」變成「幾千個外部行程」，而這條路徑每個小時都會跑；檔案損壞的偵測
/// 由錄影模組的完整性檢查（<c>MediaIntegrityChecker</c>，ffprobe 抽查）負責，兩者互補且刻意不重疊。
/// </para>
/// <para>
/// 安全閥（磁碟掉線）：只有在檔案的<b>磁碟區根目錄存在</b>時才敢說檔案不見了。D: 被拔掉時
/// <c>D:\</c> 不存在，整個磁碟區的段都只算「無法判定」，索引原封不動；否則一次掉線就會把
/// 整顆硬碟的錄影索引全部標成 missing，等磁碟回來時資料還在、索引卻已經報廢。
/// 網路路徑同理：<c>\\server\share\</c> 連不上時判定為無法判定。
/// </para>
/// <para>
/// 這裡刻意沒有內部狀態、也沒有掃描游標：行程重啟、桌面與 Headless Agent 共用同一個資料庫時
/// 行為才一致，而「從最新的一端重來」正是操作員最在意的那一端。
/// </para>
/// </summary>
public sealed class SegmentIndexReconciler
{
    /// <summary>
    /// 單輪逐檔檢查的段數上限。
    /// <para>
    /// 「最近的 2000 段」在 8 路每段一分鐘的部署裡大約是四小時，足夠涵蓋使用者剛刪完檔再打開
    /// 回放的當下；磁碟掉線或整個資料夾被刪這種大範圍的情況走資料夾層級，不受這個上限影響。
    /// </para>
    /// </summary>
    public const int DefaultBatchSize = 2000;

    private readonly SegmentRepository _repo;
    private readonly Func<string, bool> _isVolumeReady;
    private readonly Func<string, bool> _fileExists;

    /// <param name="repo">區段索引。</param>
    /// <param name="isVolumeReady">
    /// 判定某路徑所在的磁碟區是否就緒；回 <c>false</c> 時該段一律算「無法判定」。
    /// 預設看磁碟區根目錄存不存在；測試注入替身以脫離真實磁碟。
    /// </param>
    /// <param name="fileExists">判定檔案是否存在；預設 <see cref="File.Exists(string)"/>，測試注入替身。</param>
    public SegmentIndexReconciler(
        SegmentRepository repo,
        Func<string, bool>? isVolumeReady = null,
        Func<string, bool>? fileExists = null)
    {
        _repo = repo;
        _isVolumeReady = isVolumeReady ?? DefaultIsVolumeReady;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>
    /// 跑一輪對帳：先以資料夾層級涵蓋整段歷史的「整天被刪」，再逐檔檢查最新的
    /// <paramref name="batchSize"/> 個 final 段。
    /// </summary>
    public ReconcileReport Run(int batchSize)
    {
        var removedFolders = 0;
        var marked = 0;
        var unverified = 0;

        // 情況一：已經在資料夾層級處理過的資料夾，逐檔那一段不要再算一次，
        // 否則磁碟掉線時同一批段會在 Unverified 裡被數兩次。
        var handledFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (folder, count) in _repo.ListFinalFolders())
        {
            if (folder.Length == 0)
            {
                // 路徑取不出資料夾（舊版命名、人工搬動）→ 交給逐檔那一段，不在此判定。
                continue;
            }

            if (!_isVolumeReady(folder))
            {
                unverified += (int)count;
                handledFolders.Add(folder);
                continue;
            }

            if (Directory.Exists(folder))
            {
                continue;
            }

            _repo.MarkMissingInFolder(folder + Path.DirectorySeparatorChar);
            removedFolders++;
            marked += (int)count;
            handledFolders.Add(folder);
        }

        // 情況二：逐檔檢查最新的段（含路徑取不出資料夾的那些）。
        // 兩個查詢的結果會重疊（取不出資料夾的段也可能落在「最新」那一批），以編號去重，
        // 否則同一段會被檢查兩次、統計數字也會跟著虛報。
        var rows = new List<SegmentRecord>(_repo.ListFinalNewest(batchSize));
        rows.AddRange(_repo.ListFinalWithoutFolderMarker(batchSize));

        var seen = new HashSet<long>();
        var checkedCount = 0;
        foreach (var row in rows)
        {
            if (row.Status != SegmentStatus.Final || !seen.Add(row.Id))
            {
                continue;
            }

            var folder = FolderOf(row.FilePath);
            if (folder.Length > 0 && handledFolders.Contains(folder))
            {
                continue;
            }

            checkedCount++;
            switch (CheckFile(row))
            {
                case FileVerdict.Missing:
                    _repo.MarkMissing(row.Id);
                    marked++;
                    break;
                case FileVerdict.Unverified:
                    unverified++;
                    break;
            }
        }

        return new ReconcileReport(checkedCount, marked, unverified, removedFolders);
    }

    /// <summary>
    /// 驗證一批已經讀出來的段（回放當日清單用），回傳可以播放的那些。
    ///
    /// <para>
    /// 只檢查 final：tmp 是正在寫入的暫存檔、corrupt／missing／retired 本來就不會被回放列出，
    /// 對它們做 stat 只是白花 I/O，而標記 tmp 更是災難（見
    /// <see cref="SegmentRepository.MarkMissing"/>）。
    /// </para>
    /// </summary>
    public (IReadOnlyList<SegmentRecord> Playable, ReconcileReport Report) VerifyExisting(
        IReadOnlyList<SegmentRecord> rows)
    {
        var kept = new List<SegmentRecord>(rows.Count);
        var checkedCount = 0;
        var marked = 0;
        var unverified = 0;

        foreach (var row in rows)
        {
            if (row.Status != SegmentStatus.Final)
            {
                kept.Add(row);
                continue;
            }

            checkedCount++;
            switch (CheckFile(row))
            {
                case FileVerdict.Missing:
                    _repo.MarkMissing(row.Id);
                    marked++;
                    break;
                case FileVerdict.Unverified:
                    unverified++;
                    kept.Add(row);
                    break;
                default:
                    kept.Add(row);
                    break;
            }
        }

        return (kept, new ReconcileReport(checkedCount, marked, unverified));
    }

    /// <summary>
    /// 就地驗證一批資料夾（回放日曆標記粗體前用）：資料夾不存在就把底下的 final 段一次標成
    /// missing，回傳這一輪的統計。
    ///
    /// <para>
    /// 與 <see cref="Run"/> 的資料夾層級是同一段判定，差別只在資料夾清單的來源：背景對帳問整段
    /// 歷史，這裡只問使用者正在看的那個時間窗（見 <see cref="SegmentRepository.ListFinalFoldersInRange"/>）。
    /// 日曆粗體只看 <c>status='final'</c>，所以標完的同一輪重算粗體就會消失——不必等每小時的
    /// 背景對帳，操作員刪完檔打開回放看到的才是真實狀態。
    /// </para>
    /// </summary>
    public ReconcileReport VerifyFolders(IReadOnlyList<string> folders)
    {
        var checkedFolders = 0;
        var marked = 0;
        var unverified = 0;
        var removedFolders = 0;

        // 呼叫端可能把同一個資料夾重複傳進來（跨午夜的段、重疊窗口），重複 stat 與重複標記都是白費。
        var handledFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (folder.Length == 0 || !handledFolders.Add(folder))
            {
                continue;
            }

            checkedFolders++;
            if (!_isVolumeReady(folder))
            {
                unverified++;
                continue;
            }

            if (Directory.Exists(folder))
            {
                continue;
            }

            marked += (int)_repo.CountFinalInFolder(folder + Path.DirectorySeparatorChar);
            _repo.MarkMissingInFolder(folder + Path.DirectorySeparatorChar);
            removedFolders++;
        }

        return new ReconcileReport(checkedFolders, marked, unverified, removedFolders);
    }

    /// <summary>
    /// 只跑「資料夾層級」的一輪對帳：對整個索引問一次 final 資料夾清單，資料夾不存在就把
    /// 底下的 final 段標成 missing。
    ///
    /// <para>
    /// 供「用量統計、配額顯示」這類只要數字對、不想背逐檔 stat 成本的呼叫端用（<see cref="Run"/>
    /// 的資料夾層級那一段）。成本只與「天數 × 頻道數」有關，且是同步可接受的量：整段歷史的
    /// 資料夾也就幾百到幾千個 <c>Directory.Exists</c>。逐檔那一段（<see cref="Run"/> 的第二階段）
    /// 仍然只有背景對帳會跑。
    /// </para>
    /// </summary>
    public ReconcileReport ReconcileFolders()
    {
        var folders = new List<string>();
        foreach (var (folder, _) in _repo.ListFinalFolders())
        {
            if (folder.Length > 0)
            {
                folders.Add(folder);
            }
        }

        return VerifyFolders(folders);
    }

    /// <summary>單一檔案的存在判定；順序是「磁碟區 → 檔案」，磁碟區不在就不必再問檔案。</summary>
    private FileVerdict CheckFile(SegmentRecord row)
    {
        if (!_isVolumeReady(row.FilePath))
        {
            return FileVerdict.Unverified;
        }

        return _fileExists(row.FilePath) ? FileVerdict.Present : FileVerdict.Missing;
    }

    /// <summary>
    /// 從段檔路徑取出所在資料夾，規則與
    /// <see cref="SegmentRepository.ListFinalFolders"/> 的 SQL 完全相同（取 <c>seg-</c> 之前、
    /// 去掉結尾分隔符號）；取不出來時回空字串。
    /// </summary>
    private static string FolderOf(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        var i = filePath.IndexOf(SegmentRepository.SegmentFileMarker, StringComparison.Ordinal);
        return i <= 0 ? string.Empty : filePath[..i].TrimEnd('/', '\\');
    }

    /// <summary>
    /// 預設的磁碟區就緒判定：路徑的磁碟區根目錄必須存在。
    /// </summary>
    /// <remarks>
    /// 不是看檔案所在的資料夾：使用者把整天的資料夾刪掉時，那個資料夾本來就不存在，
    /// 用它當安全閥會讓「刪光了」變成「永遠無法判定」，索引就再也不會被修正。
    /// 只看磁碟區根目錄，剛好把「磁碟掉了」（根目錄不存在）與「資料被刪了」（根目錄還在）分開。
    /// </remarks>
    public static bool IsVolumeReady(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var root = Path.GetPathRoot(filePath);
        return !string.IsNullOrEmpty(root) && Directory.Exists(root);
    }

    private static bool DefaultIsVolumeReady(string filePath) => IsVolumeReady(filePath);

    private enum FileVerdict
    {
        Present,
        Missing,
        Unverified,
    }
}
