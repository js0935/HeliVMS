using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影區段索引（§4 segments 表；§15.6 區段記錄與存取）。
/// 時間戳一律 ISO8601 UTC（TEXT），狀態以文字暫存 tmp／final／corrupt。
/// </summary>
public sealed class SegmentRepository
{
    private readonly SqliteStore _store;
    private readonly SegmentShardRegistry? _registry;

    public SegmentRepository(SqliteStore store)
    {
        _store = store;
        try { _registry = new SegmentShardRegistry(store); } catch { _registry = null; }
    }

    public SegmentRepository(SqliteStore store, SegmentShardRegistry registry)
    {
        _store = store;
        _registry = registry;
    }

    /// <summary>開啟一個錄影區段（暫存檔，status=tmp），回傳區段 ID。</summary>
    public long BeginSegment(int channelId, string stream, string filePath, DateTime startUtc)
    {
        return _store.Query(
            """
            INSERT INTO segments (channel_id, stream, start_time, file_path, status)
            VALUES ($c, $s, $st, $p, 'tmp');
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", stream);
                cmd.Parameters.AddWithValue("$st", SqliteStore.Iso(startUtc));
                cmd.Parameters.AddWithValue("$p", filePath);
            });
    }

    /// <summary>完成錄影區段（status=final，含 SHA-256 與時長）。</summary>
    public void CompleteSegment(long id, DateTime endUtc, long sizeBytes, double durationSec, string sha256)
    {
        _store.Execute(
            """
            UPDATE segments
            SET end_time = $e, size_bytes = $s, duration_sec = $d, sha256 = $h, status = 'final'
            WHERE id = $id AND status = 'tmp';
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(endUtc));
                cmd.Parameters.AddWithValue("$s", sizeBytes);
                cmd.Parameters.AddWithValue("$d", durationSec);
                cmd.Parameters.AddWithValue("$h", sha256);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>標記區段異常（status=corrupt）。</summary>
    public void MarkCorrupt(long id)
    {
        _store.Execute(
            "UPDATE segments SET status = 'corrupt' WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>
    /// 標記區段的檔案已不存在（status=missing）。
    ///
    /// <para>
    /// 只動 <c>final</c> 的列：<c>tmp</c> 是正在寫入的段（磁碟缺席時本來就寫不進去，標它等於
    /// 把正在錄的段弄丟），<c>corrupt</c>／<c>missing</c> 不該被重複改寫，而 <c>retired</c>
    /// 是保留清理已經處理過的段——把它改成 missing 會讓清理紀錄失真。
    /// </para>
    /// <para>
    /// 標記而不刪列：列本身只有幾十位元組，留著才能回答「這天原本有錄影、只是檔案不在了」，
    /// 而用量統計與汰除候選都以 <c>status='final'</c> 為準，會自動把它排除。
    /// </para>
    /// </summary>
    public void MarkMissing(long id)
    {
        _store.Execute(
            "UPDATE segments SET status = 'missing' WHERE id = $id AND status = 'final';",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>
    /// 把某個資料夾底下所有 final 段一次標成 missing（同 <see cref="MarkMissing"/> 的理由只動 final）。
    ///
    /// <para>
    /// 供索引對帳用：整天的資料夾被刪掉時，逐檔 stat 只是白花 I/O（資料夾本身不存在，
    /// 一次判定就能涵蓋整天的段），而這是使用者手動清理磁碟後最常見的形狀。
    /// </para>
    /// <para>
    /// <paramref name="folderPrefix"/> 必須帶結尾分隔符號，比對用 <c>substr</c> 精確相等而非
    /// <c>LIKE</c>：路徑裡有 <c>_</c>（頻道資料夾名稱、使用者自選根目錄）時，LIKE 會把它當萬用字元，
    /// 一次就會誤標到同前綴的其他資料夾。
    /// </para>
    /// </summary>
    public void MarkMissingInFolder(string folderPrefix)
    {
        _store.Execute(
            """
            UPDATE segments SET status = 'missing'
            WHERE status = 'final' AND substr(file_path, 1, length($p)) = $p;
            """,
            cmd => cmd.Parameters.AddWithValue("$p", folderPrefix));
    }

    /// <summary>
    /// 某個資料夾底下目前仍是 final 的段數；供對帳在整批標記前先量出規模（統計回報用）。
    ///
    /// <para>
    /// 前綴比對口徑與 <see cref="MarkMissingInFolder"/> 完全相同（<c>substr</c> 精確相等而非
    /// <c>LIKE</c>），兩者若不同就會回報一個沒人看得懂的數字。
    /// </para>
    /// </summary>
    public long CountFinalInFolder(string folderPrefix)
    {
        return _store.Query(
            """
            SELECT COUNT(*) FROM segments
            WHERE status = 'final' AND substr(file_path, 1, length($p)) = $p;
            """,
            static r => r.Read() ? r.GetInt64(0) : 0L,
            cmd => cmd.Parameters.AddWithValue("$p", folderPrefix));
    }

    /// <summary>
    /// final 段依所在資料夾分組（資料夾、段數），供索引對帳以資料夾層級一次判定整天。
    ///
    /// <para>
    /// 資料夾由檔案路徑中檔名標記（<see cref="SegmentFileMarker"/>）之前的部分取出；取不出來的
    /// （舊版命名、人工搬動過的路徑）以空字串歸為一組，交由呼叫端逐檔檢查——讓它們靜靜消失會變成
    /// 「換了命名規則之後對帳就無聲失效」。回傳的成本只與「天數 × 頻道數」有關，所以整段歷史都能
    /// 在一輪內涵蓋，不必逐檔 stat。
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Folder, long Count)> ListFinalFolders()
    {
        return _store.Query(
            """
            SELECT CASE WHEN instr(file_path, $marker) > 1
                        THEN rtrim(substr(file_path, 1, instr(file_path, $marker) - 1), '/\')
                        ELSE '' END AS folder,
                   COUNT(*)
            FROM segments
            WHERE status = 'final'
            GROUP BY folder;
            """,
            static r =>
            {
                var list = new List<(string, long)>();
                while (r.Read())
                {
                    list.Add((r.GetString(0), r.GetInt64(1)));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$marker", SegmentFileMarker));
    }

    /// <summary>
    /// 與 <paramref name="fromUtc"/>..<paramref name="toUtc"/> 重疊的 final 段所在資料夾（去重）。
    ///
    /// <para>
    /// 供回放日曆在標記粗體之前就地對帳：月份格位對應的 42 個窗口先收斂成一個時間範圍問出
    /// 涉及的資料夾（數量 = 頻道 × 該月有錄影的天數），呼叫端每資料夾一次
    /// <c>Directory.Exists</c> 就能把「整資料夾被刪掉」的那天從粗體清掉，成本與逐格查詢同一等級。
    /// 資料夾取出規則與 <see cref="ListFinalFolders"/> 完全相同。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ListFinalFoldersInRange(
        int channelId,
        string stream,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(SqliteStore store)
        {
            try
            {
                var list = store.Query(
                    """
                    SELECT DISTINCT CASE WHEN instr(file_path, $marker) > 1
                                         THEN rtrim(substr(file_path, 1, instr(file_path, $marker) - 1), '/\')
                                         ELSE '' END AS folder
                    FROM segments
                    WHERE channel_id = $c AND stream = $s AND status = 'final'
                      AND start_time < $to
                      AND ((end_time IS NOT NULL AND end_time > $from)
                        OR (end_time IS NULL AND start_time >= $from));
                    """,
                    static r =>
                    {
                        var l = new List<string>();
                        while (r.Read()) l.Add(r.GetString(0));
                        return l;
                    },
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("$c", channelId);
                        cmd.Parameters.AddWithValue("$s", stream);
                        cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                        cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
                        cmd.Parameters.AddWithValue("$marker", SegmentFileMarker);
                    });
                foreach (var f in list) set.Add(f);
            }
            catch { }
        }

        Add(_store);

        if (_registry != null)
        {
            try
            {
                var from = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : fromUtc.ToUniversalTime();
                var to = toUtc.Kind == DateTimeKind.Utc ? toUtc : toUtc.ToUniversalTime();
                var cursor = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var endMonth = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                while (cursor <= endMonth)
                {
                    var shardPath = _registry.GetShardPath(cursor.Year, cursor.Month);
                    if (File.Exists(shardPath))
                    {
                        try { using var shard = _registry.OpenShard(shardPath); Add(shard); } catch { }
                    }
                    cursor = cursor.AddMonths(1);
                    var md = (cursor.Year - from.Year) * 12 + (cursor.Month - from.Month);
                    if (md > 60) break;
                }
            }
            catch { }
        }

        return set.ToList();
    }

    /// <summary>最新的 <paramref name="take"/> 個 final 段（由新到舊），供索引對帳逐檔檢查近期資料。</summary>
    public IReadOnlyList<SegmentRecord> ListFinalNewest(int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE status = 'final'
            ORDER BY id DESC
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords);
    }

    /// <summary>
    /// 路徑取不出資料夾（<see cref="ListFinalFolders"/> 的空字串組）的 final 段，最多
    /// <paramref name="take"/> 筆，供索引對帳逐檔檢查。
    /// </summary>
    public IReadOnlyList<SegmentRecord> ListFinalWithoutFolderMarker(int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE status = 'final' AND instr(file_path, $marker) <= 1
            ORDER BY id
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$marker", SegmentFileMarker));
    }

    /// <summary>段檔名的固定字首；資料夾判定與 <c>SegmentRecorder</c> 的命名共用這一個事實來源。</summary>
    public const string SegmentFileMarker = "seg-";

    /// <summary>
    /// 取出尚未做完整性檢查的 final 區段（由舊至新，最多 <paramref name="take"/> 筆）。
    ///
    /// <para>
    /// 供錄影完整性自我檢查（§11.1 寫盤保護、§15.7）分頁取樣。以 <c>checked_at_utc IS NULL</c>
    /// 過濾、由部分索引 <c>idx_seg_unchecked</c> 支撐，檢查過的不再取出，逐圈前進直到全部查過。
    /// 只回傳輕量欄位（編號、頻道、路徑），不載入整列——抽查一次只看少數段，但呼叫端不該為此背整張表的欄位。
    /// </para>
    /// </summary>
    public IReadOnlyList<(long Id, int ChannelId, string FilePath)> ListFinalUnchecked(int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, file_path
            FROM segments
            WHERE status = 'final' AND checked_at_utc IS NULL
            ORDER BY start_time
            LIMIT {Math.Max(1, take)};
            """,
            static r =>
            {
                var list = new List<(long, int, string)>();
                while (r.Read())
                {
                    list.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2)));
                }

                return list;
            });
    }

    /// <summary>標記區段已完成完整性檢查（含查得損壞者），使其不再被後續輪次重驗。</summary>
    public void MarkChecked(long id, DateTime whenUtc)
    {
        _store.Execute(
            "UPDATE segments SET checked_at_utc = $t WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(whenUtc));
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>依時間範圍列出區段（§3.4 時間軸回放）。</summary>
    /// <remarks>
    /// 口徑是「與 [from, to) 重疊」而非「start 落在窗內」：60 秒一段的錄影裡，
    /// 若使用者從 10:00:30 開始回放，覆蓋到 10:00:30 的那段（start=10:00:00）
    /// 必須出現在清單開頭，否則 HLS 播放清單與回放清單會從下一段才開始，
    /// 前 30 秒畫面直接缺角，而日誌完全看不出問題。`PlaybackTimelineBuilder`
    /// 本來就把區段裁切到窗內，也印證這裡的語意本來就該是重疊式。
    ///
    /// 未完成（tmp／corrupt）的段沒有 <c>end_time</c>，無法判斷重疊，退回
    /// 「start 落在窗內」的舊口徑：它們不可播放，只在起始日的時間軸上點一下，
    /// 不該因為結束時間未知就散落到之後每一天的查詢裡。
    /// </remarks>
    public IReadOnlyList<SegmentRecord> ListByRange(int channelId, string stream, DateTime fromUtc, DateTime toUtc)
    {
        var results = new List<SegmentRecord>();
        var seen = new HashSet<long>();

        void AddRange(SqliteStore store)
        {
            try
            {
                var list = store.Query(
                    """
                    SELECT id, channel_id, stream, start_time, end_time, file_path,
                           size_bytes, duration_sec, status, sha256, tier, origin_path
                    FROM segments
                    WHERE channel_id = $c AND stream = $s
                      AND start_time < $to
                      AND ((end_time IS NOT NULL AND end_time > $from)
                        OR (end_time IS NULL AND start_time >= $from))
                    ORDER BY start_time;
                    """,
                    ReadRecords,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("$c", channelId);
                        cmd.Parameters.AddWithValue("$s", stream);
                        cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                        cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
                    });

                foreach (var s in list)
                {
                    if (seen.Add(s.Id))
                    {
                        results.Add(s);
                    }
                }
            }
            catch { }
        }

        AddRange(_store);

        if (_registry != null)
        {
            try
            {
                var from = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : fromUtc.ToUniversalTime();
                var to = toUtc.Kind == DateTimeKind.Utc ? toUtc : toUtc.ToUniversalTime();
                var cursor = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var endMonth = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                if (to <= cursor.AddMonths(1))
                {
                    // same month or small range
                }
                while (cursor <= endMonth)
                {
                    var shardPath = _registry.GetShardPath(cursor.Year, cursor.Month);
                    if (File.Exists(shardPath))
                    {
                        try
                        {
                            using var shard = _registry.OpenShard(shardPath);
                            AddRange(shard);
                        }
                        catch { }
                    }
                    cursor = cursor.AddMonths(1);
                    var monthsDiff = (cursor.Year - from.Year) * 12 + (cursor.Month - from.Month);
                    if (monthsDiff > 48)
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        results.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return results;
    }

    /// <summary>
    /// 與 <paramref name="fromUtc"/>..<paramref name="toUtc"/> 重疊、且真的還能播的段。
    ///
    /// <para>
    /// 給調閱型消費者用（回放解碼、匯出、去扭曲、遠端播放清單）：<see cref="ListByRange"/>
    /// 為了診斷與對帳會把 tmp／corrupt／missing／retired 全都回傳，呼叫端得自己記得濾；
    /// 只要有一個消費端忘記，就會把已被刪除的段丟進 ffmpeg 的 concat 清單或解碼佇列，
    /// 症狀是「匯出整批失敗」或「播放到一半卡住」，而錯誤訊息指向檔案讀取、看不出是索引過期。
    /// 這裡把「還能播」的口徑收斂成一個查詢，讓每一個消費端不必各自複製同一條 WHERE。
    /// </para>
    /// <para>
    /// 只認 <c>status='final'</c>：tmp 正在寫、corrupt 已知損壞、missing 檔案已不在、
    /// retired 已被汰除，四者都不是可播放的錄影。
    /// </para>
    /// </summary>
    public IReadOnlyList<SegmentRecord> ListPlayableByRange(int channelId, string stream, DateTime fromUtc, DateTime toUtc)
    {
        var results = new List<SegmentRecord>();
        var seen = new HashSet<long>();

        void AddRange(SqliteStore store)
        {
            try
            {
                var list = store.Query(
                    """
                    SELECT id, channel_id, stream, start_time, end_time, file_path,
                           size_bytes, duration_sec, status, sha256, tier, origin_path
                    FROM segments
                    WHERE channel_id = $c AND stream = $s AND status = 'final'
                      AND start_time < $to
                      AND ((end_time IS NOT NULL AND end_time > $from)
                        OR (end_time IS NULL AND start_time >= $from))
                    ORDER BY start_time;
                    """,
                    ReadRecords,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("$c", channelId);
                        cmd.Parameters.AddWithValue("$s", stream);
                        cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                        cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
                    });

                foreach (var s in list)
                {
                    if (seen.Add(s.Id))
                    {
                        results.Add(s);
                    }
                }
            }
            catch { }
        }

        AddRange(_store);

        if (_registry != null)
        {
            try
            {
                var from = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : fromUtc.ToUniversalTime();
                var to = toUtc.Kind == DateTimeKind.Utc ? toUtc : toUtc.ToUniversalTime();
                var cursor = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var endMonth = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                while (cursor <= endMonth)
                {
                    var shardPath = _registry.GetShardPath(cursor.Year, cursor.Month);
                    if (File.Exists(shardPath))
                    {
                        try
                        {
                            using var shard = _registry.OpenShard(shardPath);
                            AddRange(shard);
                        }
                        catch { }
                    }
                    cursor = cursor.AddMonths(1);
                    var monthsDiff = (cursor.Year - from.Year) * 12 + (cursor.Month - from.Month);
                    if (monthsDiff > 60)
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        results.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return results;
    }

    /// <summary>
    /// 逐窗檢查是否各有至少一段可播放的 final 錄影，回傳與 <paramref name="windows"/> 等長的
    /// 布林清單（同順位對應：第 i 個元素回答第 i 個窗口）。
    ///
    /// <para>
    /// 供回放日曆把「當天有錄影」的日期加粗（§3.4）。口徑刻意與 <see cref="ListByRange"/>之後
    /// 由呼叫端過濾 <c>SegmentStatus.Final</c> 的結果一致——重疊式而非「start 落在窗內」，且只認
    /// status='final'：粗體是「點下去看得到東西」的指標，兩者若不同，操作員會看到有粗體的日期
    /// 點開是空的，或當天明明有錄影卻不粗。tmp／corrupt 段不可播放，一律不算。
    /// </para>
    /// <para>
    /// start_time 的下界刻意多往回看一天，是為了涵蓋「23:58 起、跨午夜才結束」的段；
    /// 若只寫 start_time &lt; to 沒有下界，索引 idx_seg_channel_time 的探尋無法提前停下
    /// （早於窗口卻很舊的段同樣滿足 start_time &lt; to），會退化成整段歷史掃描。
    /// 段長以分鐘計、遠小於一天，所以回看一天即完整涵蓋任何與窗口重疊的段。
    /// </para>
    /// <para>
    /// 一次陳述式回答全部窗口（每窗一次索引探尋），歷史累積千萬段也不影響；
    /// 改成撈出窗口內所有 start_time 再在記憶體判斷，24/7 錄影的六週就有約六萬列，
    /// 成本會隨錄影天數線性成長。
    /// </para>
    /// </summary>
    public IReadOnlyList<bool> HasSegmentsInWindows(
        int channelId,
        string stream,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> windows)
    {
        if (windows.Count == 0)
        {
            return [];
        }

        var values = string.Join(
            ", ",
            Enumerable.Range(0, windows.Count).Select(i => $"({i}, $f{i}, $t{i}, $l{i})"));

        var hits = _store.Query(
            $"""
            WITH w(i, f, t, l) AS (VALUES {values})
            SELECT w.i
            FROM w
            WHERE EXISTS (
                SELECT 1 FROM segments s
                WHERE s.channel_id = $c AND s.stream = $s AND s.status = 'final'
                  AND s.start_time >= w.l AND s.start_time < w.t
                  AND ((s.end_time IS NOT NULL AND s.end_time > w.f)
                    OR (s.end_time IS NULL AND s.start_time >= w.f))
            );
            """,
            static r =>
            {
                var list = new List<int>();
                while (r.Read())
                {
                    list.Add(r.GetInt32(0));
                }

                return list;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", stream);
                for (var i = 0; i < windows.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"$f{i}", SqliteStore.Iso(windows[i].StartUtc));
                    cmd.Parameters.AddWithValue($"$t{i}", SqliteStore.Iso(windows[i].EndUtc));
                    cmd.Parameters.AddWithValue($"$l{i}", SqliteStore.Iso(windows[i].StartUtc.AddDays(-1)));
                }
            });

        var flags = new bool[windows.Count];
        foreach (var i in hits)
        {
            flags[i] = true;
        }

        return flags;
    }

    /// <summary>統計某頻道錄影總量（位元組）。</summary>
    public long GetChannelTotalSize(int channelId)
    {
        return _store.Query(
            """
            SELECT COALESCE(SUM(size_bytes), 0)
            FROM segments WHERE channel_id = $c AND status = 'final';
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>統計全部頻道錄影總量（位元組，final 限定）。</summary>
    public long GetTotalUsage()
    {
        return _store.Query(
            """
            SELECT COALESCE(SUM(size_bytes), 0)
            FROM segments WHERE status = 'final';
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
    }

    /// <summary>
    /// 全部頻道的 final 區段用量與段數。
    ///
    /// 刻意與 <see cref="GetTotalUsage"/> 用同一個口徑（DB 索引裡的 final 段）：
    /// 設定畫面顯示的用量若與配額清理實際使用的數字不同，使用者會看到
    /// 「畫面寫 5GB，清理卻在 20GB 才動」，然後認為系統壞了。
    /// 一致比精確重要——真的要知道磁碟實際佔了多少，該看磁碟，不是這個畫面。
    /// </summary>
    public (long Bytes, int Count) GetTotalUsageSummary()
    {
        return _store.Query(
            """
            SELECT COALESCE(SUM(size_bytes), 0), COUNT(*)
            FROM segments WHERE status = 'final';
            """,
            static r =>
            {
                r.Read();
                return (r.GetInt64(0), r.GetInt32(1));
            });
    }

    /// <summary>
    /// 依配額汰除優先序取出候選 final 區段（§11.1 磁碟滿策略第二階段）。
    ///
    /// <para>
    /// 排序是「先天後地」兩層：<b>先</b>事件觸發錄影（<c>channels.recording_mode = Event</c>）
    /// 的區段、<b>再</b>其他錄影，同一層內一律由舊至新。事件段被擺在前面，是因為它們是
    /// 事件的補充片段，全時錄影才是調查時的主要證據；容量不足時先捨補充、後捨主證。
    /// </para>
    ///
    /// <para>
    /// 注意：事件觸發錄影（<see cref="HeliVMS.Shared.Models.RecordingMode.Event"/>）目前尚未接上
    /// 偵測觸發，所以第一層現在恆為空——這個排序是為事件錄影落地後準備的正確接縫，屆時不需再改
    /// 任何汰除邏輯，事件段自然會被優先汰除。用 <c>LEFT JOIN</c> 是為了不因頻道列缺失而漏掉區段。
    /// </para>
    /// </summary>
    public IReadOnlyList<SegmentRecord> ListReclaimCandidates(int take)
    {
        return _store.Query(
            $"""
            SELECT s.id, s.channel_id, s.stream, s.start_time, s.end_time, s.file_path,
                   s.size_bytes, s.duration_sec, s.status, s.sha256
            FROM segments s
            LEFT JOIN channels c ON c.id = s.channel_id
            WHERE s.status = 'final'
            ORDER BY CASE WHEN c.recording_mode = 2 THEN 0 ELSE 1 END, s.start_time, s.id
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords);
    }

    /// <summary>
    /// 從 <paramref name="afterStartUtc"/>／<paramref name="afterId"/> 之後取出最舊之 final 區段
    /// （浮水印清理分頁用；start_time 由舊至新）。
    ///
    /// <para>
    /// 與 <see cref="ListRetiredAfter"/> 同理：以游標單調前進，才能在受保存鎖定的區段夾在最舊端時
    /// 仍繼續往新的方向找可刪目標，不會整輪卡住（見 <c>RetentionService</c>）。
    /// </para>
    /// </summary>
    public IReadOnlyList<SegmentRecord> ListOldestFinalAfter(DateTime afterStartUtc, long afterId, int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE status = 'final'
              AND (start_time > $afterStart OR (start_time = $afterStart AND id > $afterId))
            ORDER BY start_time, id
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$afterStart", SqliteStore.Iso(afterStartUtc));
                cmd.Parameters.AddWithValue("$afterId", afterId);
            });
    }

    /// <summary>列出早於截止點之最舊 final 區段（保留期清理用；start_time 由舊至新）。</summary>
    public IReadOnlyList<SegmentRecord> ListRetired(DateTime cutoffUtc, int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE status = 'final' AND start_time < $cut
            ORDER BY start_time
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$cut", SqliteStore.Iso(cutoffUtc)));
    }

    /// <summary>
    /// 從 <paramref name="afterStartUtc"/>／<paramref name="afterId"/> 之後，列出早於截止點之最舊 final 區段
    /// （保留期清理分頁用；start_time 由舊至新）。
    ///
    /// <para>
    /// 保留清理必須能「跳過」受保存鎖定而刪不掉的舊區段、繼續往新的方向找可刪的目標。
    /// 若每次都只取最舊的一批，一旦最舊那批全部被鎖住，清理就會卡在原地永遠刪不動新的
    /// （見 <c>RetentionService</c>）。以 <c>(start_time, id)</c> 為游標單調前進即可繞過鎖住的前綴。
    /// </para>
    /// </summary>
    public IReadOnlyList<SegmentRecord> ListRetiredAfter(
        DateTime cutoffUtc, DateTime afterStartUtc, long afterId, int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE status = 'final' AND start_time < $cut
              AND (start_time > $afterStart OR (start_time = $afterStart AND id > $afterId))
            ORDER BY start_time, id
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$cut", SqliteStore.Iso(cutoffUtc));
                cmd.Parameters.AddWithValue("$afterStart", SqliteStore.Iso(afterStartUtc));
                cmd.Parameters.AddWithValue("$afterId", afterId);
            });
    }

    /// <summary>刪除區段記錄（檔案移除由配額策略負責）。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM segments WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>依編號取得單一區段（§14.3 串流：遠程只給編號，路徑一律取自索引）。</summary>
    public SegmentRecord? Get(long id)
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE id = $id;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$id", id)).FirstOrDefault();
    }

    /// <summary>依時長計算統計（供 §15 配額/RPO 報告）。</summary>
    public IReadOnlyList<SegmentRecord> ListFinal(int channelId)
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE channel_id = $c AND status = 'final'
            ORDER BY start_time;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>全部 final 區段（開始時間由舊至新；§14.4 異地備援與統計用）。</summary>
    public IReadOnlyList<SegmentRecord> ListAllFinal()
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256, tier, origin_path
            FROM segments
            WHERE status = 'final'
            ORDER BY start_time;
            """,
            ReadRecords);
    }

    /// <summary>
    /// 只回傳「頻道資料夾仍是舊制 <c>ch…</c>」的區段（僅編號、頻道、路徑）。
    /// <para>
    /// 供資料夾搬移（<see cref="RecordingFolderMigrator"/>）使用。24/7 全時錄影下 segments 一年可達千萬列，
    /// 若每次都整表載入，單是啟動就會吃掉 GB 級記憶體；搬移只對「頻道名稱化之前的舊制目錄」有興趣，
    /// 已命名為頻道名稱的列（絕大多數）在 SQL 就先濾掉，穩定狀態下這裡一列都不會回傳。
    /// </para>
    /// <para>
    /// 過濾條件是路徑中含有 <c>\ch</c>（含分隔符），而非嚴格的 <c>ch{id}</c>：如此連「頻道被改名、
    /// 舊目錄含 ch」的殘留也能被帶出來搬正；已正確命名的目錄不會命中，不會被無謂載入。
    /// </para>
    /// </summary>
    public IReadOnlyList<(long Id, int ChannelId, string FilePath)> ListLegacyFolderPaths()
    {
        return _store.Query(
            "SELECT id, channel_id, file_path FROM segments WHERE instr(file_path, char(92) || 'ch') > 0 OR instr(file_path, '/ch') > 0;",
            static r =>
            {
                var list = new List<(long, int, string)>();
                while (r.Read())
                {
                    list.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2)));
                }

                return list;
            });
    }

    /// <summary>更新區段檔案路徑（資料夾搬移後回寫）。</summary>
    public void UpdateFilePath(long id, string filePath)
    {
        _store.Execute(
            "UPDATE segments SET file_path = $p WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$p", filePath);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    private static IReadOnlyList<SegmentRecord> ReadRecords(SqliteDataReader reader)
    {
        var list = new List<SegmentRecord>();
        while (reader.Read())
        {
            list.Add(new SegmentRecord
            {
                Id = reader.GetInt64(0),
                ChannelId = reader.GetInt32(1),
                Stream = reader.GetString(2),
                StartUtc = SqliteStore.FromIso(reader.GetString(3)),
                EndUtc = reader.IsDBNull(4) ? null : SqliteStore.FromIso(reader.GetString(4)),
                FilePath = reader.GetString(5),
                SizeBytes = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                DurationSec = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                Status = ParseStatus(reader.GetString(8)),
                Sha256 = reader.IsDBNull(9) ? null : reader.GetString(9),
                Tier = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : "hot",
                OriginPath = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
            });
        }

        return list;
    }

    private static SegmentStatus ParseStatus(string text) => text switch
    {
        "tmp" => SegmentStatus.Temporary,
        "final" => SegmentStatus.Final,
        "corrupt" => SegmentStatus.Corrupt,
        "missing" => SegmentStatus.Missing,
        "retired" => SegmentStatus.Retired,
        _ => SegmentStatus.Temporary,
    };
}