using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 小時合併（<see cref="HourlySegmentMerger"/>）的行為測試。
///
/// <para>
/// 這一步會重寫磁碟上的檔案與索引列，危險不在合併而在「刪除」——順序錯了就是
/// 「異常結束吃掉錄影」。測試用替身 remuxer（不碰外部進程）把每個失敗點逐一釘住：
/// 原檔與索引在任何失敗下都必須原封不動。
/// </para>
/// </summary>
public sealed class HourlySegmentMergerTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Hour14 = Day.AddHours(14);

    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly FakeRemuxer _remuxer = new();

    public HourlySegmentMergerTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-merge-{Guid.NewGuid():N}");
        _root = Path.Combine(dir, "recordings");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(dir, "index.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        EnsureChannel(1);
    }

    /// <summary>segments 對 channels 有外部鍵：測試至少要有一台頻道。</summary>
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
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private HourlySegmentMerger NewMerger(DateTime now)
        => new(_segments, new LegalHoldRepository(_store), _remuxer, clock: () => now);

    [Fact]
    public void 超過一小時的完整小時會被合併並換成單一索引列()
    {
        var rows = SeedHour(Hour14, segments: 3, minutesEach: 20);
        var now = Hour14.AddHours(2); // 小時在 15:00 結束，現在 16:30 → 夠老
        _remuxer.Probe = _ => 3600;   // 三段各 20 分鐘

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.MergedHours);
        Assert.Equal(3, report.DeletedFiles);

        var mergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-140000000.mp4");
        Assert.True(File.Exists(mergedPath), "合併檔應該落在同一個小時資料夾");
        Assert.False(File.Exists(mergedPath + ".tmp"), "暫存檔不該留下");

        foreach (var row in rows)
        {
            Assert.False(File.Exists(row.FilePath), "原檔要在索引換列之後才刪，這裡應該已刪");
        }

        // 索引：三列換一列，涵蓋範圍與總長不變。
        var final = _segments.ListFinalBetween(Hour14, Hour14.AddHours(1));
        var mergedRow = Assert.Single(final);
        Assert.Equal(rows[0].StartUtc, mergedRow.StartUtc);
        Assert.Equal(rows[^1].EndUtc, mergedRow.EndUtc);
        Assert.Equal("merged", mergedRow.Tier);
        Assert.Equal(3600, mergedRow.DurationSec ?? 0, 1);
        Assert.False(string.IsNullOrEmpty(mergedRow.Sha256), "合併檔要重算 SHA-256");
    }

    [Fact]
    public void 不足一小時前結束的小時不會被合併()
    {
        var rows = SeedHour(Hour14, segments: 2, minutesEach: 20);
        var now = Hour14.AddHours(1).AddMinutes(10); // 15:00 結束，現在 15:10 → 太新

        var report = NewMerger(now).Run(now);

        Assert.Equal(0, report.MergedHours);
        Assert.Equal(1, report.SkippedHours);
        foreach (var row in rows)
        {
            Assert.True(File.Exists(row.FilePath));
        }
        Assert.Equal(2, _segments.ListFinalBetween(Hour14, Hour14.AddHours(1)).Count);
    }

    [Fact]
    public void remux失敗時原檔與索引都不動()
    {
        var rows = SeedHour(Hour14, segments: 3, minutesEach: 20);
        _remuxer.FailRemux = true;
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.FailedHours);
        Assert.Equal(0, report.MergedHours);
        foreach (var row in rows)
        {
            Assert.True(File.Exists(row.FilePath), "合併失敗不能動原檔");
        }
        Assert.Equal(3, _segments.ListFinalBetween(Hour14, Hour14.AddHours(1)).Count);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(rows[0].FilePath)!, "*.tmp"));
    }

    [Fact]
    public void 長度驗證不過時不會換列()
    {
        var rows = SeedHour(Hour14, segments: 3, minutesEach: 20);
        _remuxer.Probe = _ => 1800; // 應該是 3600，封裝掉了內容
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.FailedHours);
        foreach (var row in rows)
        {
            Assert.True(File.Exists(row.FilePath), "長度對不上的檔案不能換上去");
        }
        Assert.Equal(3, _segments.ListFinalBetween(Hour14, Hour14.AddHours(1)).Count);
    }

    [Fact]
    public void 法律保全中的小時跳過()
    {
        var rows = SeedHour(Hour14, segments: 2, minutesEach: 20);
        var holds = new LegalHoldRepository(_store);
        holds.Add(rows[0].ChannelId, Hour14, Hour14.AddHours(1), "案件 A", "tester", DateTime.UtcNow);
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.SkippedHours);
        Assert.Equal(0, report.MergedHours);
        Assert.True(File.Exists(rows[0].FilePath), "被保全的錄影不能被重寫");
    }

    [Fact]
    public void 缺檔的小時整組跳過()
    {
        var rows = SeedHour(Hour14, segments: 3, minutesEach: 20);
        File.Delete(rows[1].FilePath); // 索引說有、磁碟沒有
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.SkippedHours);
        Assert.True(File.Exists(rows[0].FilePath));
        Assert.True(File.Exists(rows[2].FilePath));
        Assert.Equal(3, _segments.ListFinalBetween(Hour14, Hour14.AddHours(1)).Count);
    }

    [Fact]
    public void 只有一段的純錄不會為了檔數被動()
    {
        var rows = SeedHour(Hour14, segments: 1, minutesEach: 50);
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(0, report.MergedHours);
        Assert.Equal(1, report.SkippedHours);
        Assert.True(File.Exists(rows[0].FilePath));
    }

    [Fact]
    public void 交易前斷電留下的孤兒合併檔會被刺掉重做()
    {
        var rows = SeedHour(Hour14, segments: 2, minutesEach: 20);
        var mergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-140000000.mp4");
        File.WriteAllBytes(mergedPath, new byte[512]); // 索引裡沒有它＝孤兒
        _remuxer.Probe = _ => 2400;
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.MergedHours);
        Assert.True(File.Exists(mergedPath), "重做的合併檔應該在");
        var final = _segments.ListFinalBetween(Hour14, Hour14.AddHours(1));
        Assert.Single(final);
    }

    [Fact]
    public void 上一輪換列成功但刪檔失敗的殘留會被清掉()
    {
        var rows = SeedHour(Hour14, segments: 2, minutesEach: 20);
        // 手動重現「換列成功、刪檔失敗」：索引裡只有 merged 列，舊檔還躺著。
        var mergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-140000000.mp4");
        File.WriteAllBytes(mergedPath, new byte[512]);
        _segments.ReplaceWithMerged(
            new SegmentRecord
            {
                ChannelId = 1,
                Stream = "main",
                StartUtc = rows[0].StartUtc,
                EndUtc = rows[^1].EndUtc,
                FilePath = mergedPath,
                SizeBytes = 512,
                DurationSec = 2400,
                Status = SegmentStatus.Final,
                Sha256 = "x",
                Tier = "merged",
            },
            [.. rows.Select(r => r.Id)]);
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(0, report.MergedHours);
        Assert.True(report.DeletedFiles >= 2, "殘留的小檔要被清掉");
        foreach (var row in rows)
        {
            Assert.False(File.Exists(row.FilePath), "索引裡已經沒有這列，留著會被磁碟匯入器誤認");
        }
        Assert.True(File.Exists(mergedPath), "合併檔本身是索引指的檔案，不能動");
    }

    [Fact]
    public void 每輪最多合併四個小時()
    {
        for (var h = 0; h < 6; h++)
        {
            SeedHour(Day.AddHours(h), segments: 2, minutesEach: 20);
        }
        _remuxer.Probe = _ => 2400;
        var now = Day.AddHours(8);

        var report = NewMerger(now).Run(now);

        Assert.Equal(HourlyMergePolicy.MaxHoursPerSweep, report.MergedHours);
        Assert.Equal(8, report.DeletedFiles); // 每個小時 2 個段檔 × 4 小時
    }

    /// <summary>
    /// 一小時裡「先被合併過的那一段」不能擋住後半段。
    /// </summary>
    /// <remarks>
    /// 合併只處理當時已收尾的段：第一輪把 14:00／14:10 併成 merged-140000000.mp4，而 14:30／14:40
    /// 是之後才 final 的。舊版用「這一小時最早那一列的檔名」判斷整小時合併過了，看到 merged-* 就直接
    /// 進清孤兒分支，於是後半段<b>永遠</b>留在原地，每一輪還白跑一次查詢。
    /// 實測現場索引有 7 個資料夾是這個形狀（11-07 23 時、11-08 01 時…，每個小時仍留著 2–3 個小檔），
    /// 合併的意義（備份／防毒／SMB 少掃小檔）正好消失。
    /// </remarks>
    [Fact]
    public void 已經合併過的子區間不會擋住同一小時後半段的合併()
    {
        var head = SeedHour(Hour14, segments: 2, minutesEach: 10);
        var headMergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-140000000.mp4");
        File.WriteAllBytes(headMergedPath, new byte[512]);
        _segments.ReplaceWithMerged(
            new SegmentRecord
            {
                ChannelId = 1,
                Stream = "main",
                StartUtc = head[0].StartUtc,
                EndUtc = head[^1].EndUtc,
                FilePath = headMergedPath,
                SizeBytes = 512,
                DurationSec = 1200,
                Status = SegmentStatus.Final,
                Sha256 = "x",
                Tier = "merged",
            },
            [.. head.Select(r => r.Id)]);

        // 真實的合併會在換列之後刪掉原檔；這裡照做，否則那些已不在索引裡的 seg 檔
        // 會在第二輪被 CleanLeftovers 當成孤兒清掉（那時 deleted 就不是 0，測的就是別的事了）。
        foreach (var row in head)
        {
            File.Delete(row.FilePath);
        }

        // 後半段：第一輪合併當時還沒收尾，之後才變成 final。
        var tail = new[] { AddSegment(Hour14.AddMinutes(30), 10), AddSegment(Hour14.AddMinutes(40), 10) };
        _remuxer.Probe = _ => 1200;
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.MergedHours);
        var tailMergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-143000000.mp4");
        Assert.True(File.Exists(tailMergedPath), "後半段要併成它自己的合併檔");
        foreach (var row in tail)
        {
            Assert.False(File.Exists(row.FilePath), "併進去的段檔要在換列之後刪掉");
        }

        Assert.True(File.Exists(headMergedPath), "前一輪的合併檔是索引指的檔案，不能動");
        Assert.Equal(2, _segments.ListFinalBetween(Hour14, Hour14.AddHours(1)).Count);

        // 再跑一輪不能重做、也不能把兩個合併檔互相清掉。
        var again = NewMerger(now.AddHours(1)).Run(now.AddHours(1));
        Assert.Equal(0, again.MergedHours);
        Assert.Equal(0, again.DeletedFiles);
        Assert.True(File.Exists(headMergedPath));
        Assert.True(File.Exists(tailMergedPath));
    }

    /// <summary>
    /// 同名檔已經被索引指著時，不能當成「斷電留下的孤兒」刪掉重做。
    /// </summary>
    /// <remarks>
    /// 磁碟離線時保留策略會把列標成 <c>missing</c>，磁碟接回來之後檔案還在、列卻還沒復活
    /// （復活是 <c>AdoptOrReviveFinal</c>／重掃的事）。這中間如果剛好跑一輪合併，而這一小時
    /// 又出現了新的段，目標檔名正好就是那一列指著的路徑——把「磁碟上有同名檔」一律當孤兒刪掉，
    /// 等於刪掉一筆索引還指著的錄影，而且日誌乾淨（這種形狀最貴）。
    /// 寧可這一輪跳過（Skipped），也不要動它。
    /// </remarks>
    [Fact]
    public void 同名的合併檔已被索引指著時不會被刪掉重做()
    {
        // 這一小時後半段有兩個新的段（需要合併），目標檔名會是 merged-143000000.mp4。
        var segs = SeedHour(Hour14.AddMinutes(30), segments: 2, minutesEach: 10);
        var mergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-143000000.mp4");
        File.WriteAllBytes(mergedPath, new byte[512]);

        // 同一個路徑被另一條串流的列指著，而且狀態是 missing（磁碟離線時被標掉、檔案已回來）。
        var id = _segments.BeginSegment(1, "sub", mergedPath, Hour14.AddMinutes(30));
        _segments.CompleteSegment(id, Hour14.AddMinutes(40), 512, 600, "sha-sub");
        _store.Execute("UPDATE segments SET status = 'missing' WHERE id = $id;", cmd =>
            cmd.Parameters.AddWithValue("$id", id));

        _remuxer.Probe = _ => 1200;
        var now = Hour14.AddHours(2);

        var report = NewMerger(now).Run(now);

        Assert.True(File.Exists(mergedPath), "索引還指著這個檔，不能刪");
        Assert.Equal(0, report.MergedHours);
        Assert.Equal(0, report.DeletedFiles);
        Assert.Equal(1, report.SkippedHours);
        foreach (var row in segs)
        {
            Assert.True(File.Exists(row.FilePath), "原檔與原索引都要原封不動");
        }
    }

    /// <summary>
    /// 合併完成後檔案必須是關著的（雜湊不能把自己的檔案鎖住）。
    /// </summary>
    /// <remarks>
    /// <c>SHA256.HashData(File.OpenRead(path))</c> 開的 FileStream 沒有人處置：Windows 上它是一個
    /// 開著的讀取控制代碼（FileShare.Read），於是一段 60 MB 的合併檔在下一次 GC 之前不能刪、
    /// 也不能被保留策略汰除，而換列失敗時的收尾刪除更是必定失敗（見下一條測試）。
    /// 這一條用「獨占開啟」把「沒有任何控制代碼還開著」釘住。
    /// </remarks>
    [Fact]
    public void 合併完成後檔案沒有被雜湊讀取留在開啟狀態()
    {
        SeedHour(Hour14, segments: 2, minutesEach: 20);
        _remuxer.Probe = _ => 2400;
        var now = Hour14.AddHours(2);

        Assert.Equal(1, NewMerger(now).Run(now).MergedHours);

        var mergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-140000000.mp4");
        using var exclusive = File.Open(mergedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    /// <summary>
    /// 換列失敗時，剛產出的合併檔要刪掉，不能留成「索引看不到、回收也看不到」的孤兒。
    /// </summary>
    /// <remarks>
    /// 現場實測：<c>merged-170348710.mp4</c>（63.9 MB）在磁碟上、索引裡沒有這一列，而同小時 12 段
    /// 仍是 final 且檔案都在——那是「改名成功、換列沒成功、收尾刪不掉」的殘骸（程式在合併途中被
    /// 關閉也會留下同樣形狀）。它不在索引裡，所以配額與壓力汰除都碰不到它，只能等下一次合併
    /// 剛好走到這個資料夾才被清掉。失敗時刪掉它，才是把「下一輪重做」變成必然。
    /// </remarks>
    [Fact]
    public void 換列失敗時不會留下刪不掉的合併檔()
    {
        var rows = SeedHour(Hour14, segments: 2, minutesEach: 20);
        _remuxer.Probe = _ => 2400;
        var now = Hour14.AddHours(2);

        // 模擬交易期間的資料庫錯誤（磁碟忙、鎖逾時）：INSERT 這一步一定失敗。
        _store.Execute(
            "CREATE TRIGGER merge_boom BEFORE INSERT ON segments BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        var report = NewMerger(now).Run(now);

        Assert.Equal(1, report.FailedHours);
        var mergedPath = Path.Combine(_root, "ch1", "20261009", "14", "merged-140000000.mp4");
        Assert.False(File.Exists(mergedPath), "換列沒成功就不能留下這個檔（它不在索引裡，沒有東西會回收它）");
        Assert.False(File.Exists(mergedPath + ".tmp"));
        foreach (var row in rows)
        {
            Assert.True(File.Exists(row.FilePath), "原檔與原索引都要原封不動");
        }
        Assert.Equal(2, _segments.ListFinalBetween(Hour14, Hour14.AddHours(1)).Count);
    }

    private List<SegmentRecord> SeedHour(
        DateTime hourStartUtc,
        int segments,
        double minutesEach,
        int channelId = 1)
    {
        var rows = new List<SegmentRecord>();
        for (var i = 0; i < segments; i++)
        {
            rows.Add(AddSegment(hourStartUtc.AddMinutes(i * minutesEach), minutesEach, channelId));
        }

        return rows;
    }

    /// <summary>在起點所屬的小時資料夾寫一個段檔並收尾成一列 final。</summary>
    private SegmentRecord AddSegment(DateTime startUtc, double minutes, int channelId = 1)
    {
        var dir = Path.Combine(
            _root, $"ch{channelId}", startUtc.ToString("yyyyMMdd"), startUtc.ToString("HH"));
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"seg-{startUtc:HHmmssfff}.mp4");
        File.WriteAllBytes(path, new byte[1024]);
        var id = _segments.BeginSegment(channelId, "main", path, startUtc);
        _segments.CompleteSegment(id, startUtc.AddMinutes(minutes), 1024, minutes * 60, $"sha-{id}");
        return _segments.Get(id)!;
    }

    /// <summary>remux 替身：抄第一個輸入的 bytes 當成產物，讓檔案語意為真；探測由測試指定。</summary>
    private sealed class FakeRemuxer : HourlySegmentMerger.IRemuxer
    {
        public bool FailRemux { get; set; }

        public Func<string, double?>? Probe { get; set; }

        public List<(string Output, int InputCount)> Calls { get; } = [];

        public void Remux(IReadOnlyList<string> inputs, string outputTmp)
        {
            if (FailRemux)
            {
                throw new InvalidOperationException("remux boom");
            }

            Calls.Add((outputTmp, inputs.Count));
            File.Copy(inputs[0], outputTmp, overwrite: true);
        }

        public double? ProbeDurationSeconds(string file) => Probe?.Invoke(file);
    }
}
