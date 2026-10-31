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

    private List<SegmentRecord> SeedHour(
        DateTime hourStartUtc,
        int segments,
        double minutesEach,
        int channelId = 1)
    {
        var dir = Path.Combine(
            _root, $"ch{channelId}", hourStartUtc.ToString("yyyyMMdd"), hourStartUtc.ToString("HH"));
        Directory.CreateDirectory(dir);

        var rows = new List<SegmentRecord>();
        for (var i = 0; i < segments; i++)
        {
            var start = hourStartUtc.AddMinutes(i * minutesEach);
            var path = Path.Combine(dir, $"seg-{start:HHmmssfff}.mp4");
            File.WriteAllBytes(path, new byte[1024]);
            var id = _segments.BeginSegment(channelId, "main", path, start);
            var end = start.AddMinutes(minutesEach);
            _segments.CompleteSegment(id, end, 1024, minutesEach * 60, $"sha-{i}");
            rows.Add(_segments.Get(id)!);
        }

        return rows;
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
