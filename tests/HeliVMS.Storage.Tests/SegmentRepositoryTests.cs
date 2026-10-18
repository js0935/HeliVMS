using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class SegmentRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _repo;

    public SegmentRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentRepository(_store);
        EnsureChannel(1);
        EnsureChannel(2);
        EnsureChannel(3);
        EnsureChannel(4);
    }

    [Fact]
    public void Initialize_SetsWalMode()
    {
        var mode = _store.Query(
            "PRAGMA journal_mode;",
            static r =>
            {
                r.Read();
                return r.GetString(0);
            });

        Assert.Equal("wal", mode);
    }

    [Fact]
    public void BeginAndComplete_RoundTrips()
    {
        var start = DateTime.UtcNow.AddMinutes(-10);
        var end = start.AddSeconds(60);

        var id = _repo.BeginSegment(1, "main", @"D:\tmp\seg-001.mp4", start);
        _repo.CompleteSegment(id, end, 4096, 60, new string('a', 64));

        var records = _repo.ListByRange(1, "main", start.AddSeconds(-1), end.AddSeconds(1));

        var seg = Assert.Single(records);
        Assert.Equal(id, seg.Id);
        Assert.Equal(1, seg.ChannelId);
        Assert.Equal("main", seg.Stream);
        Assert.Equal(@"D:\tmp\seg-001.mp4", seg.FilePath);
        Assert.Equal(4096, seg.SizeBytes);
        Assert.Equal(60, seg.DurationSec);
        Assert.Equal(new string('a', 64), seg.Sha256);
        Assert.Equal(SegmentStatus.Final, seg.Status);
        Assert.Equal("mp4", seg.Format);
        Assert.Equal(start, seg.StartUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(end, seg.EndUtc!.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// 資料夾搬移只該掃到舊制 <c>ch…</c> 目錄的區段：已命名為頻道名稱的列（絕大多數）必須在 SQL
    /// 就濾掉，否則 24/7 錄影累積到千萬列時，每次啟動都會為了確認而整表載入。
    /// </summary>
    [Fact]
    public void ListLegacyFolderPaths只回傳舊制頻道目錄的區段()
    {
        var t = DateTime.UtcNow;
        var legacy = _repo.BeginSegment(1, "main", @"D:\rec\ch001\20260101\seg-a.mp4", t);
        _repo.CompleteSegment(legacy, t, 1, 15, "h");

        var named = _repo.BeginSegment(1, "main", @"D:\rec\前門\20260101\seg-b.mp4", t.AddSeconds(1));
        _repo.CompleteSegment(named, t, 1, 15, "h");

        var other = _repo.BeginSegment(2, "main", @"D:\rec\other\20260101\seg-c.mp4", t.AddSeconds(2));
        _repo.CompleteSegment(other, t, 1, 15, "h");

        var paths = _repo.ListLegacyFolderPaths();

        var only = Assert.Single(paths);
        Assert.Equal(legacy, only.Id);
        Assert.Equal(1, only.ChannelId);
        Assert.Equal(@"D:\rec\ch001\20260101\seg-a.mp4", only.FilePath);
    }

    /// <summary>頻道名稱中若含「ch」但不含前置分隔符（例如「前門ch」），不得被當成舊制目錄。</summary>
    [Fact]
    public void ListLegacyFolderPaths不誤判名稱含ch的目錄()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(1, "main", @"D:\rec\前門ch\20260101\seg-a.mp4", t);
        _repo.CompleteSegment(id, t, 1, 15, "h");

        Assert.Empty(_repo.ListLegacyFolderPaths());
    }

    /// <summary>更新路徑是搬移回寫索引的手段；寫錯就等於把既有錄影指向不存在的位置。</summary>
    [Fact]
    public void UpdateFilePath會改寫指定區段的路徑()
    {
        var id = _repo.BeginSegment(1, "main", @"D:\rec\ch001\20260101\seg-a.mp4", DateTime.UtcNow);

        _repo.UpdateFilePath(id, @"D:\rec\前門\20260101\seg-a.mp4");

        Assert.Equal(@"D:\rec\前門\20260101\seg-a.mp4", _repo.Get(id)!.FilePath);
    }

    [Fact]
    public void ListByRange_FiltersByTimeAndStream()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(2, "main", @"D:\tmp\a.mp4", t.AddHours(-3));
        _repo.CompleteSegment(id, t.AddHours(-2), 100, 3600, "h1");
        var id2 = _repo.BeginSegment(2, "main", @"D:\tmp\b.mp4", t.AddHours(-1));
        _repo.CompleteSegment(id2, t, 200, 3600, "h2");
        _repo.BeginSegment(2, "sub", @"D:\tmp\c.mp4", t.AddHours(-3));

        var early = _repo.ListByRange(2, "main", t.AddHours(-4), t.AddHours(-2));
        var late = _repo.ListByRange(2, "main", t.AddHours(-2), t.AddHours(1));

        Assert.Single(early);
        Assert.Equal("D:\\tmp\\a.mp4", early[0].FilePath);
        Assert.Single(late);
        Assert.Equal("D:\\tmp\\b.mp4", late[0].FilePath);

        // sub 串流不混入 main 查詢
        Assert.Empty(_repo.ListByRange(2, "sub", t.AddHours(-2.5), t.AddHours(-2)));
    }

    /// <summary>
    /// 跨過窗起點的區段要出現在清單裡，否則回放／HLS 會從下一段才開始。
    ///
    /// 一段 60 秒的錄影 [10:00:00, 10:01:00)，使用者從 10:00:30 開始回放時，
    /// 覆蓋到 10:00:30 的那一段必須包含在內。舊口徑只看 start_time，會把它整段排除，
    /// 於是播放清單開頭直接缺掉前 30 秒，畫面一跳就少一段，而日誌毫無線索。
    /// </summary>
    [Fact]
    public void ListByRange_涵蓋窗起點跨段的區段()
    {
        var t = DateTime.UtcNow;
        var before = _repo.BeginSegment(2, "main", @"D:\tmp\before.mp4", t.AddHours(-3));
        _repo.CompleteSegment(before, t.AddHours(-2), 512, 3600, "h0");
        var straddle = _repo.BeginSegment(2, "main", @"D:\tmp\straddle.mp4", t.AddMinutes(-60));
        _repo.CompleteSegment(straddle, t.AddMinutes(-30), 1024, 1800, "h1");
        var after = _repo.BeginSegment(2, "main", @"D:\tmp\after.mp4", t);
        _repo.CompleteSegment(after, t.AddMinutes(30), 2048, 1800, "h2");

        // straddle [t-60m, t-30m] 跨過窗起點 → 必須在；after 在窗內 → 必須在；
        // before [t-3h, t-2h] 早於窗起點且已經結束 → 不得混入。
        var window = _repo.ListByRange(2, "main", t.AddMinutes(-50), t.AddMinutes(45));

        Assert.Equal(new[] { @"D:\tmp\straddle.mp4", @"D:\tmp\after.mp4" },
            window.Select(s => s.FilePath).ToArray());
    }

    /// <summary>
    /// 用量摘要要同時給出位元組與段數，且只算 final 段。
    ///
    /// 設定畫面以前是自己列舉錄影目錄算用量，但用
    /// <c>SearchOption.TopDirectoryOnly</c> 掃根層，而檔案在
    /// <c>recordings/chXXX/yyyyMMdd/</c> 兩層底下，所以用量長期顯示 0B。
    /// 改成讀區段索引之後，這裡就是那道口徑的唯一守護：
    /// 一旦把 tmp／corrupt 算進去，畫面上的數字會比配額清理實際使用的數字大，
    /// 使用者會看到「畫面寫 5GB、清理卻到 20GB 才動」，然後認為系統壞了。
    /// </summary>
    [Fact]
    public void 用量摘要只算final段並同時給出段數()
    {
        EnsureChannel(7);
        var t = DateTime.UtcNow;
        var a = _repo.BeginSegment(7, "main", @"D:\tmp\u1.mp4", t.AddHours(-3));
        _repo.CompleteSegment(a, t.AddHours(-2), 4096, 3600, "u1");
        var b = _repo.BeginSegment(7, "main", @"D:\tmp\u2.mp4", t.AddHours(-1));
        _repo.CompleteSegment(b, t, 1024, 3600, "u2");

        // 錄製中與損壞的段落不該被算進「已完成的錄影用量」。
        _repo.BeginSegment(7, "main", @"D:\tmp\u3.tmp", t.AddMinutes(-5));

        var (bytes, count) = _repo.GetTotalUsageSummary();

        Assert.Equal(4096 + 1024, bytes);
        Assert.Equal(2, count);
    }

    /// <summary>沒有任何錄影時用量是 0 與 0，不是例外。</summary>
    [Fact]
    public void 沒有錄影時用量摘要為零()
        => Assert.Equal((0L, 0), _repo.GetTotalUsageSummary());

    /// <summary>
    /// 用量摘要必須與 <see cref="SegmentRepository.GetTotalUsage"/> 同一個口徑。
    ///
    /// 兩者只要有一個納入 non-final 段，設定畫面顯示的數字就會和配額清理
    /// 實際使用的數字分岔，而這正是使用者看不出磁碟為什麼快滿的原因。
    /// </summary>
    [Fact]
    public void 用量摘要與總用量口徑一致()
    {
        EnsureChannel(8);
        var t = DateTime.UtcNow;
        var ok = _repo.BeginSegment(8, "main", @"D:\tmp\v1.mp4", t.AddHours(-1));
        _repo.CompleteSegment(ok, t, 2048, 3600, "v1");
        _repo.BeginSegment(8, "main", @"D:\tmp\v2.tmp", t.AddMinutes(-1));

        Assert.Equal(_repo.GetTotalUsage(), _repo.GetTotalUsageSummary().Bytes);
    }

    [Fact]
    public void Get_ReturnsSingleSegmentById()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(3, "main", @"D:\tmp\get-me.mp4", t);
        _repo.CompleteSegment(id, t.AddSeconds(30), 900, 30, "sha-get");

        var seg = _repo.Get(id);

        Assert.NotNull(seg);
        Assert.Equal(id, seg!.Id);
        Assert.Equal(@"D:\tmp\get-me.mp4", seg.FilePath);
        Assert.Equal("sha-get", seg.Sha256);
        Assert.Equal(SegmentStatus.Final, seg.Status);
    }

    [Fact]
    public void Get_UnknownIdReturnsNull()
    {
        Assert.Null(_repo.Get(987654321));
    }

    [Fact]
    public void MarkCorrupt_UpdatesStatus()
    {
        var t = DateTime.UtcNow;
        var id = _repo.BeginSegment(3, "main", @"D:\tmp\bad.mp4", t);
        _repo.MarkCorrupt(id);

        var records = _repo.ListByRange(3, "main", t.AddSeconds(-1), t.AddSeconds(1));

        Assert.Equal(SegmentStatus.Corrupt, Assert.Single(records).Status);
    }

    [Fact]
    public void GetChannelTotalSize_SumsFinalOnly()
    {
        var t = DateTime.UtcNow;
        var idA = _repo.BeginSegment(4, "main", @"D:\tmp\a.mp4", t);
        _repo.CompleteSegment(idA, t.AddSeconds(30), 150, 30, "h1");
        var idB = _repo.BeginSegment(4, "main", @"D:\tmp\b.mp4", t.AddSeconds(60));
        _repo.CompleteSegment(idB, t.AddSeconds(90), 350, 30, "h2");
        var idC = _repo.BeginSegment(4, "main", @"D:\tmp\c.mp4", t.AddSeconds(120));
        _repo.MarkCorrupt(idC);
        var idD = _repo.BeginSegment(4, "main", @"D:\tmp\d.mp4", t.AddSeconds(180));

        // 只有 final 計入（tmp 不計）
        Assert.Equal(500, _repo.GetChannelTotalSize(4));
    }

    [Fact]
    public void Iso_And_FromIso_RoundTrip()
    {
        var utc = new DateTime(2026, 9, 13, 7, 30, 15, 123, DateTimeKind.Utc);
        var iso = SqliteStore.Iso(utc);
        Assert.Equal("2026-09-13T07:30:15.123Z", iso);
        Assert.Equal(utc, SqliteStore.FromIso(iso));
    }

    [Fact]
    public void ChannelRepository_AddAndList_Works()
    {
        var channels = new ChannelRepository(_store);
        var id = channels.Add("測試", "rtsp://127.0.0.1/x", "rtsp://127.0.0.1/y");
        var list = channels.List();

        Assert.Contains(list, c => c.Id == id && c.Name == "測試" && c.SubStreamUrl == "rtsp://127.0.0.1/y");
    }

    [Fact]
    public void DeviceRepository_DpapiProtect_RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string secret = "NVR-密碼-123";
        var encrypted = DeviceRepository.Protect(secret);
        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, DeviceRepository.Unprotect(encrypted));
    }

    /// <summary>
    /// 保留清理所需的 <c>(status, start_time)</c> 複合索引必須存在。
    ///
    /// 現場症狀：16 路 30 天全時錄影（數十萬至百萬列）下，保留清理「刪一筆、重查一次」，
    /// 而查詢以 status 過濾、以全域 start_time 排序，原本唯一的索引前置欄位卻是 channel_id，
    /// 一個都用不上——每次清理都整表掃描＋排序，磁碟只增不減。
    /// </summary>
    [Fact]
    public void 資料庫具有保留清理所需的status與start_time複合索引()
    {
        var count = _store.Query(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_seg_status_time';",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });

        Assert.Equal(1, count);
    }

    /// <summary>
    /// 保留清理的查詢計畫必須真的用上那個複合索引，而不是整表掃描後排序。
    /// 索引存在但計畫沒用上，等於沒有效果——這正是最容易「加了索引卻沒改善」的陷阱。
    /// </summary>
    [Fact]
    public void 保留清理查詢會使用status與start_time複合索引()
    {
        var plan = _store.Query(
            "EXPLAIN QUERY PLAN SELECT id FROM segments WHERE status = 'final' AND start_time < $cut ORDER BY start_time LIMIT 16;",
            static r =>
            {
                var sb = new System.Text.StringBuilder();
                while (r.Read())
                {
                    sb.Append(r.GetString(3)).Append('\n');
                }

                return sb.ToString();
            },
            cmd => cmd.Parameters.AddWithValue("$cut", "2026-01-01T00:00:00.000Z"));

        Assert.Contains("idx_seg_status_time", plan);
    }

    /// <summary>
    /// <c>ListRetiredAfter</c> 要以 <c>(start_time, id)</c> 游標由舊到新分頁：保留清理靠它跳過
    /// 受保存鎖定而刪不掉的舊區段、繼續往新的方向找，才不會被鎖住的前綴卡死。
    /// </summary>
    [Fact]
    public void ListRetiredAfter以游標由舊到新分頁()
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = Final(1, t, 100);
        var b = Final(1, t.AddSeconds(60), 100);
        var c = Final(1, t.AddSeconds(120), 100);
        var cutoff = t.AddHours(1);

        var page1 = _repo.ListRetiredAfter(cutoff, DateTime.MinValue, 0, 2);
        Assert.Equal(new[] { a, b }, page1.Select(s => s.Id).ToArray());

        var page2 = _repo.ListRetiredAfter(cutoff, page1[^1].StartUtc, page1[^1].Id, 2);
        Assert.Equal(new[] { c }, page2.Select(s => s.Id).ToArray());
    }

    /// <summary><c>ListOldestFinalAfter</c> 同樣以游標前進，且只回傳 final（不含 tmp／corrupt）。</summary>
    [Fact]
    public void ListOldestFinalAfter只回傳final且依時間前進()
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = Final(2, t, 100);
        _ = _repo.BeginSegment(2, "main", @"D:\tmp\in-progress.mp4", t.AddSeconds(30));
        var b = Final(2, t.AddSeconds(60), 100);

        var all = _repo.ListOldestFinalAfter(DateTime.MinValue, 0, 10);
        Assert.Equal(new[] { a, b }, all.Select(s => s.Id).ToArray());

        var afterA = _repo.ListOldestFinalAfter(t, a, 10);
        Assert.Equal(new[] { b }, afterA.Select(s => s.Id).ToArray());
    }

    /// <summary>
    /// 完整性自我檢查的取樣清單只回傳 final 且尚未檢查的段，並以 <c>start_time</c> 由舊至新分頁。
    ///
    /// 這是抽查能「走過全部歷史區段」的唯一保證：若已檢查的段還留在清單裡，每一輪都重驗最舊那批，
    /// 較新的段永遠輪不到，於是壞掉的新錄影永遠不會被發現（見 <c>MediaIntegrityChecker</c>）。
    /// </summary>
    [Fact]
    public void ListFinalUnchecked只回傳未檢查的final段並向前推進()
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = Final(1, t, 100);
        var b = Final(1, t.AddSeconds(60), 100);
        var c = Final(1, t.AddSeconds(120), 100);
        var inProgress = _repo.BeginSegment(1, "main", @"D:\tmp\wip.mp4", t.AddSeconds(180));
        var corrupt = Final(1, t.AddSeconds(240), 100);
        _repo.MarkCorrupt(corrupt);

        var first = _repo.ListFinalUnchecked(2);
        Assert.Equal(new[] { a, b }, first.Select(s => s.Id).ToArray());

        _repo.MarkChecked(a, t.AddDays(1));
        _repo.MarkChecked(b, t.AddDays(1));

        var second = _repo.ListFinalUnchecked(10);
        Assert.Equal(new[] { c }, second.Select(s => s.Id).ToArray());

        // 錄製中（tmp）與損壞（corrupt）的段永不進入抽查清單。
        Assert.DoesNotContain(inProgress, second.Select(s => s.Id));
    }

    /// <summary><c>MarkChecked</c> 後該段即退出待檢查清單——抽查分頁能前進的關鍵。</summary>
    [Fact]
    public void MarkChecked後區段退出待檢查清單()
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Final(1, t, 100);
        Assert.Single(_repo.ListFinalUnchecked(10));

        _repo.MarkChecked(id, t);

        Assert.Empty(_repo.ListFinalUnchecked(10));
    }

    /// <summary>沒有任何 final 段時待檢查清單是空的，不是例外。</summary>
    [Fact]
    public void 沒有final段時待檢查清單為空()
        => Assert.Empty(_repo.ListFinalUnchecked(10));

    /// <summary>
    /// 支撐待檢查取樣的部分索引必須存在：千萬列歷史下，若每輪取樣都整表掃描，
    /// 抽查本身就會變成負擔（而它原本是為了保護磁碟才存在的）。
    /// </summary>
    [Fact]
    public void 資料庫具有完整性抽查所需的未檢查段部分索引()
    {
        var count = _store.Query(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_seg_unchecked';",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });

        Assert.Equal(1, count);
    }

    /// <summary>
    /// 汰除候選清單把事件模式（<c>recording_mode = 2</c>）的區段排在前面，同層再依時間由舊至新，
    /// 且只含收尾（final）的段。
    ///
    /// <para>
    /// 這是磁碟滿策略第二階段「優先刪事件段」的排序來源（§11.1）：事件段是全時段的補充，
    /// 磁碟吃緊時先淘汰補充品、保住連續錄影。排序寫錯的症狀是配額壓力下先刪連續錄影、
    /// 留下零散事件段，時間軸出現無聲的空洞。
    /// </para>
    /// </summary>
    [Fact]
    public void ListReclaimCandidates優先取事件模式區段且同層由舊至新()
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        _store.Execute("UPDATE channels SET recording_mode = 2 WHERE id = 2;");

        var alwaysOld = Final(1, t, 100);                 // 全時段，較舊
        var eventNew = Final(2, t.AddSeconds(60), 100);   // 事件段，較新
        var alwaysNew = Final(1, t.AddSeconds(120), 100); // 全時段，最新
        var inProgress = _repo.BeginSegment(1, "main", @"D:\tmp\wip.mp4", t.AddSeconds(180));

        var order = _repo.ListReclaimCandidates(10).Select(s => s.Id).ToArray();

        Assert.Equal(new[] { eventNew, alwaysOld, alwaysNew }, order);
        Assert.DoesNotContain(inProgress, order);
    }

    /// <summary>
    /// 回放日曆的「當天有錄影」粗體只認可播放的 final 段：未收尾（tmp）與已標記損壞（corrupt）
    /// 的段一律不算，否則日期會粗體、點下去卻什麼都沒有。
    /// </summary>
    [Fact]
    public void 日曆逐日查詢只認可播放的final段()
    {
        var day = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        Final(1, day.AddHours(9), 100);
        _repo.BeginSegment(1, "main", @"D:\tmp\wip.mp4", day.AddDays(1).AddHours(9));
        var corrupt = Final(1, day.AddDays(2).AddHours(9), 100);
        _repo.MarkCorrupt(corrupt);

        var flags = _repo.HasSegmentsInWindows(
            1,
            "main",
            [(day, day.AddDays(1)), (day.AddDays(1), day.AddDays(2)), (day.AddDays(2), day.AddDays(3))]);

        Assert.Equal(new[] { true, false, false }, flags);
    }

    /// <summary>
    /// 23:58 起、00:03 才收尾的段必須讓前後兩天都粗體：操作員在前一天看到粗體、
    /// 次日卻空的，會以為漏錄。這也是 start_time 下界回看一天的由來。
    /// </summary>
    [Fact]
    public void 跨午夜才結束的段讓前後兩天都算有錄影()
    {
        var day = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var start = day.AddHours(23).AddMinutes(58);
        var id = _repo.BeginSegment(1, "main", @"D:\tmp\night.mp4", start);
        _repo.CompleteSegment(id, start.AddMinutes(5), 100, 300, "h");

        var flags = _repo.HasSegmentsInWindows(
            1, "main", [(day, day.AddDays(1)), (day.AddDays(1), day.AddDays(2))]);

        Assert.Equal(new[] { true, true }, flags);
    }

    /// <summary>
    /// 口徑必須是重疊式：09:55–10:05 的段覆蓋 10:00 開始的窗口，即使 start 落在窗口之前
    /// 也算（只看 start 的話，整天第一格會漏掉跨入的第一段）。
    /// </summary>
    [Fact]
    public void 跨進窗口的段算有錄影()
    {
        var day = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var id = _repo.BeginSegment(1, "main", @"D:\tmp\overlap.mp4", day.AddHours(9).AddMinutes(55));
        _repo.CompleteSegment(id, day.AddHours(10).AddMinutes(5), 100, 600, "h");

        var flags = _repo.HasSegmentsInWindows(1, "main", [(day.AddHours(10), day.AddHours(11))]);

        Assert.True(flags[0]);
    }

    /// <summary>窗口開始前就收尾的段與該窗口無關，不得讓日期誤加粗。</summary>
    [Fact]
    public void 窗口前就結束的段不算有錄影()
    {
        var day = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var id = _repo.BeginSegment(1, "main", @"D:\tmp\ended.mp4", day.AddHours(8));
        _repo.CompleteSegment(id, day.AddHours(9), 100, 3600, "h");

        var flags = _repo.HasSegmentsInWindows(1, "main", [(day.AddHours(10), day.AddHours(11))]);

        Assert.False(flags[0]);
    }

    /// <summary>
    /// 回傳值與輸入窗口逐位對應：日曆把 42 格直接依索引取用，錯位一格就會把粗體
    /// 標到隔壁的日期上，而且完全不報錯。
    /// </summary>
    [Fact]
    public void 旗標與輸入窗口等長且逐位對應()
    {
        var day = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        Final(1, day.AddHours(1), 100);
        Final(1, day.AddDays(2).AddHours(1), 100);

        var flags = _repo.HasSegmentsInWindows(
            1,
            "main",
            [(day, day.AddDays(1)),
             (day.AddDays(1), day.AddDays(2)),
             (day.AddDays(2), day.AddDays(3)),
             (day.AddDays(3), day.AddDays(4))]);

        Assert.Equal(4, flags.Count);
        Assert.Equal(new[] { true, false, true, false }, flags);
    }

    [Fact]
    public void 沒有窗口時回傳空清單()
        => Assert.Empty(_repo.HasSegmentsInWindows(1, "main", []));

    /// <summary>日曆只會查所選頻道／主串流，別台或子串流的段不該讓日期加粗。</summary>
    [Fact]
    public void 別的頻道或串流的段不算()
    {
        var day = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        Final(2, day.AddHours(1), 100);
        var sub = _repo.BeginSegment(1, "sub", @"D:\tmp\sub.mp4", day.AddHours(2));
        _repo.CompleteSegment(sub, day.AddHours(2).AddMinutes(1), 100, 60, "h");

        var flags = _repo.HasSegmentsInWindows(1, "main", [(day, day.AddDays(1))]);

        Assert.False(flags[0]);
    }

    private long Final(int channelId, DateTime startUtc, long sizeBytes)
    {
        var id = _repo.BeginSegment(channelId, "main", $@"D:\tmp\{Guid.NewGuid():N}.mp4", startUtc);
        _repo.CompleteSegment(id, startUtc.AddSeconds(60), sizeBytes, 60, "h");
        return id;
    }

    private void EnsureChannel(int id)
    {
        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES ($id, $n, $u, 0);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", $"ch-{id}");
                cmd.Parameters.AddWithValue("$u", $"rtsp://test/{id}");
            });
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
            // 測試清理
        }
    }
}