using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 索引對帳在區段索引層的行為：把「索引說有、磁碟上沒有」的段標成
/// <see cref="SegmentStatus.Missing"/> 的幾個前提。
///
/// <para>
/// 這些保證直接對應操作員回報的症狀：他把錄影磁碟上的資料刪掉之後，回放清單與日曆仍然顯示
/// 整天的錄影，點下去才失敗。修正必須只動「已封檔」的段——把正在寫入的 tmp 段標成遺失等於
/// 弄丟當下的錄影，把 retired 段標成遺失則會讓保留清理的紀錄說謊。
/// </para>
/// </summary>
public class SegmentIndexReconcileRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _repo;

    public SegmentIndexReconcileRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-reconcile-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentRepository(_store);
        EnsureChannel(1);
        EnsureChannel(2);
    }

    [Fact]
    public void 標記遺失只動已封檔的段()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var final = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100000000.mp4", t);
        var tmp = _repo.BeginSegment(1, "main", @"D:\rec\cam_1\20261019\seg-100001000.mp4", t.AddSeconds(1));
        var corrupt = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100002000.mp4", t.AddSeconds(2));
        _repo.MarkCorrupt(corrupt);
        var retired = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100003000.mp4", t.AddSeconds(3));
        SetStatus(retired, "retired");

        _repo.MarkMissing(final);
        _repo.MarkMissing(tmp);
        _repo.MarkMissing(corrupt);
        _repo.MarkMissing(retired);

        Assert.Equal(SegmentStatus.Missing, StatusOf(final));
        Assert.Equal(SegmentStatus.Temporary, StatusOf(tmp));
        Assert.Equal(SegmentStatus.Corrupt, StatusOf(corrupt));
        Assert.Equal(SegmentStatus.Retired, StatusOf(retired));
    }

    [Fact]
    public void 資料夾層級標記只涵蓋該資料夾底下的段()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

        // 頻道資料夾名稱帶底線是刻意的：用 LIKE 比對前綴時 `_` 是萬用字元，
        // 一次就會把 camA1 這個鄰居一起標掉（兩者的錄影都還在磁碟上）。
        var target = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100000000.mp4", t);
        var neighborFolder = SeedFinal(1, @"D:\rec\camA1\20261019\seg-100000000.mp4", t.AddSeconds(1));
        var neighborDay = SeedFinal(1, @"D:\rec\cam_1\20261018\seg-235959000.mp4", t.AddSeconds(2));
        var otherChannel = SeedFinal(2, @"D:\rec\cam_2\20261019\seg-100000000.mp4", t.AddSeconds(3));
        var tmpInside = _repo.BeginSegment(1, "main", @"D:\rec\cam_1\20261019\seg-100004000.mp4", t.AddSeconds(4));

        _repo.MarkMissingInFolder(@"D:\rec\cam_1\20261019\");

        Assert.Equal(SegmentStatus.Missing, StatusOf(target));
        Assert.Equal(SegmentStatus.Final, StatusOf(neighborFolder));
        Assert.Equal(SegmentStatus.Final, StatusOf(neighborDay));
        Assert.Equal(SegmentStatus.Final, StatusOf(otherChannel));
        Assert.Equal(SegmentStatus.Temporary, StatusOf(tmpInside));
    }

    [Fact]
    public void 標記遺失後用量統計不再計入該段()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100000000.mp4", t, sizeBytes: 1000);
        var gone = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100001000.mp4", t.AddSeconds(1), sizeBytes: 2000);

        Assert.Equal(3000L, _repo.GetChannelTotalSize(1));

        _repo.MarkMissing(gone);

        // 索引列還在（才回答得出「這天原本有錄影」），但它不佔磁碟，不能再算進用量，
        // 否則磁碟早就刪空的機器仍會回報「錄影佔了 3KB」、永遠觸發不了保留清理。
        Assert.Equal(1000L, _repo.GetChannelTotalSize(1));
        Assert.Equal(1000L, _repo.GetTotalUsage());
        Assert.Equal(2, _repo.ListByRange(1, "main", t.AddSeconds(-1), t.AddMinutes(1)).Count);
    }

    [Fact]
    public void 資料夾分組以段檔命名為界()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100000000.mp4", t);
        SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100001000.mp4", t.AddSeconds(1));
        SeedFinal(2, @"D:\rec\cam_2\20261018\seg-090000000.mp4", t.AddSeconds(2));

        // 不是本產品的命名（舊版、人工搬動）→ 取不出資料夾，歸到空字串那一組，
        // 讓呼叫端逐檔檢查，而不是靜靜漏掉。
        SeedFinal(1, @"D:\rec\cam_1\20261019\video.mp4", t.AddSeconds(3));

        var folders = _repo.ListFinalFolders();

        Assert.Contains((@"D:\rec\cam_1\20261019", 2L), folders);
        Assert.Contains((@"D:\rec\cam_2\20261018", 1L), folders);
        Assert.Contains((string.Empty, 1L), folders);
    }

    [Fact]
    public void 最新段查詢由新到舊()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100000000.mp4", t);
        var second = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100001000.mp4", t.AddSeconds(1));
        var third = SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100002000.mp4", t.AddSeconds(2));

        var newest = _repo.ListFinalNewest(2);

        Assert.Equal(new[] { third, second }, newest.Select(s => s.Id));

        // 已標成 missing 的不再出現在候選裡（對帳才不會每一輪重複 stat 同一批）。
        _repo.MarkMissing(third);
        Assert.Equal(new[] { second }, _repo.ListFinalNewest(1).Select(s => s.Id));
    }

    [Fact]
    public void 取不出資料夾的段另外列出()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        SeedFinal(1, @"D:\rec\cam_1\20261019\seg-100000000.mp4", t);
        var legacy = SeedFinal(1, @"D:\rec\cam_1\20261019\legacy-100001000.mp4", t.AddSeconds(1));

        var rows = _repo.ListFinalWithoutFolderMarker(10);

        var row = Assert.Single(rows);
        Assert.Equal(legacy, row.Id);
    }

    private long SeedFinal(int channelId, string path, DateTime startUtc, long sizeBytes = 100)
    {
        var id = _repo.BeginSegment(channelId, "main", path, startUtc);
        _repo.CompleteSegment(id, startUtc.AddSeconds(60), sizeBytes, 60, "sha256");
        return id;
    }

    private void SetStatus(long id, string status)
        => _store.Execute(
            "UPDATE segments SET status = $s WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$id", id);
            });

    private SegmentStatus StatusOf(long id)
        => _store.Query(
            "SELECT status FROM segments WHERE id = $id;",
            static r =>
            {
                r.Read();
                var text = r.GetString(0);
                return text switch
                {
                    "tmp" => SegmentStatus.Temporary,
                    "final" => SegmentStatus.Final,
                    "corrupt" => SegmentStatus.Corrupt,
                    "missing" => SegmentStatus.Missing,
                    "retired" => SegmentStatus.Retired,
                    _ => throw new InvalidOperationException($"未知狀態 {text}"),
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
