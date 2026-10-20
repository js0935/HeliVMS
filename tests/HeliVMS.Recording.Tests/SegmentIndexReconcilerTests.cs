using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 索引對帳的行為：把「索引說有、磁碟上沒有」的段標成遺失。
///
/// <para>
/// 症狀是操作員把錄影磁碟上的資料刪掉（換硬碟、清空間、搬檔）之後，回放清單與日曆仍然顯示
/// 整天的錄影，點下去才失敗。這裡釘住的除了「有刪掉就要標」，還有兩個反過來會造成災難的保證：
/// 磁碟線掉時<b>不准</b>把整顆硬碟標成遺失（等到磁碟回來時資料還在、索引卻已經報廢），
/// 以及正在寫入的段不准被動到。
/// </para>
/// <para>
/// 磁碟區就緒與檔案存在都以替身注入，測試完全不碰真實磁碟；唯一的例外是資料夾層級的案例，
/// 那裡的資料夾本來就不該存在（正是被刪掉的形狀）。資料夾層級那條路徑要有價值，就得確定它
/// <b>不會</b>退化成逐檔 stat，所以該案例連 stat 次數都一起斷言。
/// </para>
/// </summary>
public class SegmentIndexReconcilerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _repo;
    private readonly string _liveFolder;
    private int _seed;

    public SegmentIndexReconcilerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-reconciler-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentRepository(_store);

        // 「資料夾還在」的案例需要一個真的存在的資料夾；本測試不讀寫裡面的檔案。
        _liveFolder = Path.Combine(Path.GetTempPath(), $"helivms-live-{Guid.NewGuid():N}", "20261019");
        Directory.CreateDirectory(_liveFolder);

        EnsureChannel(1);
    }

    [Fact]
    public void 檔案還在的段會保留並維持已封檔()
    {
        var rows = new[] { Seed(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id) };
        var reconciler = New(fileExists: _ => true);

        var (playable, report) = reconciler.VerifyExisting(rows);

        Assert.Single(playable);
        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.MarkedMissing);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
    }

    [Fact]
    public void 檔案不存在的段被標成遺失且不再回傳()
    {
        var rows = new[] { Seed(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id) };
        var reconciler = New(fileExists: _ => false);

        var (playable, report) = reconciler.VerifyExisting(rows);

        Assert.Empty(playable);
        Assert.Equal(1, report.MarkedMissing);
        Assert.Equal(SegmentStatus.Missing, StatusOf(id));
    }

    [Fact]
    public void 正在寫入的段不會被標成遺失()
    {
        // tmp 是 ffmpeg 正在寫的暫存檔：它的檔案「還不存在」是正常的，標下去等於弄丟當下的錄影。
        var tmpId = _repo.BeginSegment(1, "main", @"D:\rec\cam\20261019\seg-100000000.mp4.tmp", Utc(0));
        var tmp = new SegmentRecord
        {
            Id = tmpId,
            ChannelId = 1,
            Stream = "main",
            FilePath = @"D:\rec\cam\20261019\seg-100000000.mp4.tmp",
            StartUtc = Utc(0),
            Status = SegmentStatus.Temporary,
        };
        var reconciler = New(fileExists: _ => false);

        var (playable, report) = reconciler.VerifyExisting(new[] { tmp });

        var kept = Assert.Single(playable);
        Assert.Equal(tmpId, kept.Id);
        Assert.Equal(0, report.Checked);
        Assert.Equal(0, report.MarkedMissing);
        Assert.Equal(SegmentStatus.Temporary, StatusOf(tmpId));
    }

    [Fact]
    public void 磁碟區未就緒時不判定也不標記()
    {
        var rows = new[] { Seed(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id) };
        var reconciler = New(
            fileExists: _ => throw new InvalidOperationException("磁碟區不在時不該去問檔案存不存在"),
            isVolumeReady: _ => false);

        var (playable, report) = reconciler.VerifyExisting(rows);

        // 留下來是刻意的：回放要用它來說「磁碟未就緒」，而不是靜靜變成空清單。
        Assert.Single(playable);
        Assert.Equal(1, report.Unverified);
        Assert.Equal(0, report.MarkedMissing);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
    }

    [Fact]
    public void 整天的資料夾被刪除時以資料夾層級標記且不逐檔stat()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"helivms-gone-{Guid.NewGuid():N}", "20261019");
        Seed(Path.Combine(folder, "seg-100000000.mp4"), out var idA);
        Seed(Path.Combine(folder, "seg-100010000.mp4"), out var idB);

        // 逐檔 stat 一次都不該發生：資料夾本身不存在，一天上千段的 stat 全是白花 I/O。
        var stats = 0;
        var reconciler = New(fileExists: _ =>
        {
            stats++;
            return false;
        });

        var report = reconciler.Run(100);

        Assert.Equal(2, report.MarkedMissing);
        Assert.Equal(1, report.FoldersRemoved);
        Assert.Equal(0, stats);
        Assert.Equal(SegmentStatus.Missing, StatusOf(idA));
        Assert.Equal(SegmentStatus.Missing, StatusOf(idB));
    }

    [Fact]
    public void 磁碟區未就緒時不標記整天資料夾()
    {
        // 這是整個修正最重要的安全閥：D: 被拔掉時 D:\ 不存在，若照樣標記，
        // 一次掉線就會讓整顆硬碟的錄影索引報廢，而資料其實還在。
        Seed(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id);
        var reconciler = New(
            fileExists: _ => throw new InvalidOperationException("磁碟區不在時不該去問檔案存不存在"),
            isVolumeReady: path => !path.StartsWith(@"D:\", StringComparison.Ordinal));

        var report = reconciler.Run(100);

        Assert.Equal(0, report.MarkedMissing);
        Assert.Equal(0, report.FoldersRemoved);
        Assert.Equal(1, report.Unverified);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
    }

    [Fact]
    public void 資料夾還在時逐檔檢查檔案()
    {
        var present = Path.Combine(_liveFolder, "seg-100000000.mp4");
        var absent = Path.Combine(_liveFolder, "seg-100010000.mp4");
        Seed(present, out _);
        Seed(absent, out var idAbsent);

        var reconciler = New(fileExists: path => path == present);

        var report = reconciler.Run(100);

        Assert.Equal(2, report.Checked);
        Assert.Equal(1, report.MarkedMissing);
        Assert.Equal(0, report.FoldersRemoved);
        Assert.Equal(SegmentStatus.Missing, StatusOf(idAbsent));
    }

    [Fact]
    public void 逐檔檢查只做最新的段數上限()
    {
        Seed(Path.Combine(_liveFolder, "seg-100000000.mp4"), out var oldest);
        Seed(Path.Combine(_liveFolder, "seg-100010000.mp4"), out _);
        Seed(Path.Combine(_liveFolder, "seg-100020000.mp4"), out _);

        var checkedPaths = new List<string>();
        var reconciler = New(fileExists: path =>
        {
            checkedPaths.Add(path);
            return true;
        });

        var report = reconciler.Run(1);

        // 舊資料夾裡的個別檔案不逐檔驗是刻意的取捨：網路錄影根目錄上一個 stat 要毫秒級，
        // 一小時掃一次整個歷史會壓垮錄影 I/O；使用者打開那一天時回放會逐檔驗。
        Assert.Equal(1, report.Checked);
        Assert.Single(checkedPaths);
        Assert.Equal(SegmentStatus.Final, StatusOf(oldest));
    }

    [Fact]
    public void 取不出資料夾的段走逐檔檢查()
    {
        // 舊版命名／人工搬動過的路徑取不出資料夾，不能因此被靜靜漏掉。
        Seed(@"D:\rec\cam\20261019\legacy-100000000.mp4", out var id);
        var reconciler = New(fileExists: _ => false);

        var report = reconciler.Run(100);

        // 「最新的一批」與「取不出資料夾的一批」會重疊，同一段只能算一次。
        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.MarkedMissing);
        Assert.Equal(0, report.FoldersRemoved);
        Assert.Equal(SegmentStatus.Missing, StatusOf(id));
    }

    [Fact]
    public void 指定資料夾對帳時不存在者整批標成遺失()
    {
        // 日曆在標粗體之前就地對帳用：只問資料夾存不存在，不逐一 stat 檔案。
        var folder = Path.Combine(Path.GetTempPath(), $"helivms-gone-{Guid.NewGuid():N}", "20261019");
        Seed(Path.Combine(folder, "seg-100000000.mp4"), out var idA);
        Seed(Path.Combine(folder, "seg-100010000.mp4"), out var idB);

        var reconciler = New(
            fileExists: _ => throw new InvalidOperationException("資料夾不在時不該逐一 stat 檔案"));

        var report = reconciler.VerifyFolders(new[] { folder });

        Assert.Equal(1, report.FoldersRemoved);
        Assert.Equal(2, report.MarkedMissing);
        Assert.Equal(SegmentStatus.Missing, StatusOf(idA));
        Assert.Equal(SegmentStatus.Missing, StatusOf(idB));
    }

    [Fact]
    public void 資料夾還在時不標記任何段()
    {
        Seed(Path.Combine(_liveFolder, "seg-100000000.mp4"), out var id);

        var reconciler = New(fileExists: _ => true);

        var report = reconciler.VerifyFolders(new[] { _liveFolder });

        Assert.Equal(0, report.FoldersRemoved);
        Assert.Equal(0, report.MarkedMissing);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
    }

    [Fact]
    public void 磁碟區未就緒時資料夾不標記()
    {
        Seed(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id);
        var reconciler = New(
            fileExists: _ => throw new InvalidOperationException("磁碟區不在時不該去問檔案"),
            isVolumeReady: _ => false);

        var report = reconciler.VerifyFolders(new[] { @"D:\rec\cam\20261019" });

        Assert.Equal(0, report.MarkedMissing);
        Assert.Equal(1, report.Unverified);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
    }

    [Fact]
    public void 重複的資料夾只檢查一次()
    {
        // 日曆 42 格可能有多格落在同一天（跨月檢視），重複的資料夾不該被 stat 兩次、
        // 更不該在稽核裡把段數算兩倍。
        var folder = Path.Combine(Path.GetTempPath(), $"helivms-gone-{Guid.NewGuid():N}", "20261019");
        Seed(Path.Combine(folder, "seg-100000000.mp4"), out _);

        var reconciler = New(fileExists: _ => false);

        var report = reconciler.VerifyFolders(new[] { folder, folder, folder });

        Assert.Equal(1, report.FoldersRemoved);
        Assert.Equal(1, report.MarkedMissing);
    }

    private static DateTime Utc(int seconds)
        => new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    private SegmentIndexReconciler New(
        Func<string, bool> fileExists,
        Func<string, bool>? isVolumeReady = null)
        => new(_repo, isVolumeReady ?? (_ => true), fileExists);

    /// <summary>
    /// 種一筆已封檔的段並回傳對應的索引列。
    /// <para>
    /// 列是直接建構的而非從資料庫讀回：對帳只看編號、狀態與路徑三個欄位，而「寫進資料庫的是什麼
    /// 就讀出什麼」由 <c>SegmentIndexReconcileRepositoryTests</c> 在儲存層釘住，這裡不必再繞一圈。
    /// </para>
    /// </summary>
    private SegmentRecord Seed(string path, out long id)
    {
        // 每個段給不同的起點：索引對 (channel_id, stream, start_time) 有唯一鍵，
        // 同一秒種兩段會被資料庫擋下來（真實錄影也不可能同時開始兩段）。
        var start = Utc(_seed++);
        id = _repo.BeginSegment(1, "main", path, start);
        _repo.CompleteSegment(id, start.AddSeconds(60), 1000, 60, "sha256");
        return new SegmentRecord
        {
            Id = id,
            ChannelId = 1,
            Stream = "main",
            FilePath = path,
            StartUtc = start,
            EndUtc = start.AddSeconds(60),
            SizeBytes = 1000,
            DurationSec = 60,
            Status = SegmentStatus.Final,
        };
    }

    private SegmentStatus StatusOf(long id)
        => _store.Query(
            "SELECT status FROM segments WHERE id = $id;",
            static r =>
            {
                r.Read();
                return r.GetString(0) switch
                {
                    "tmp" => SegmentStatus.Temporary,
                    "final" => SegmentStatus.Final,
                    "corrupt" => SegmentStatus.Corrupt,
                    "missing" => SegmentStatus.Missing,
                    "retired" => SegmentStatus.Retired,
                    var other => throw new InvalidOperationException($"未知狀態 {other}"),
                };
            },
            cmd => cmd.Parameters.AddWithValue("$id", id));

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
        var parent = Directory.GetParent(_liveFolder)?.FullName;
        if (parent is not null)
        {
            try
            {
                Directory.Delete(parent, recursive: true);
            }
            catch (IOException)
            {
                // 測試清理失敗不該讓測試紅。
            }
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}
