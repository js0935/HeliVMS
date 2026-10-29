using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 磁碟錄影補回索引（<see cref="RecordingDiskImporter"/>）的行為契約。
/// </summary>
/// <remarks>
/// 現場症狀是「磁碟裡有錄影、回放裡什麼都沒有」：索引被重建、還原成舊備份、頻道刪掉又
/// 重加（<c>segments</c> 是 CASCADE），或整顆錄影碟換機器時，檔案都還在，索引卻不認得。
/// 這裡鎖住三件容易默默做錯的事：資料夾怎麼對回頻道（含舊制 <c>chNNN</c>）、
/// 時間怎麼從檔名還原，以及重複掃描不能把同一段匯入兩次（回放時間軸會出現重複段）。
/// </remarks>
public sealed class RecordingDiskImporterTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 9, 16, 53, 10, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteStore _store;

    public RecordingDiskImporterTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-import-{Guid.NewGuid():N}");
        _root = Path.Combine(dir, "recordings");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(dir, "index.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public void 掃描會依頻道名稱找到對應的錄影資料夾()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("攝影機01", Start, size: 4096);

        var folder = Assert.Single(Scan());

        Assert.Equal(id, folder.ChannelId);
        Assert.Equal("攝影機01", folder.FolderName);
        Assert.Equal(1, folder.FileCount);
        Assert.Equal(Start, folder.FromUtc);
        Assert.Equal(Start, folder.ToUtc);
    }

    [Fact]
    public void 舊制ch編號資料夾也能對回頻道()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("ch001", Start, size: 1024);

        Assert.Equal(id, Assert.Single(Scan()).ChannelId);
    }

    /// <summary>
    /// 舊目錄（例如 ch072）對不到任何頻道時，不能自己猜一個。
    /// </summary>
    /// <remarks>
    /// 這正是實機上「C:\HeliVMSData\recordings\ch072 有 10 月的錄影、回放卻空白」的形狀：
    /// 頻道 72 已經被刪掉。把它的錄影隨便掛到別的頻道名下，等於讓證據指向錯的攝影機；
    /// 正確做法是回報「未指定」，由操作員在匯入畫面選。
    /// </remarks>
    [Fact]
    public void 對不到頻道的資料夾會回報為未指定()
    {
        AddChannel("攝影機01");
        WriteSegment("ch072", Start, size: 2048);

        var folder = Assert.Single(Scan());

        Assert.Null(folder.ChannelId);
        Assert.Equal("ch072", folder.FolderName);
    }

    [Fact]
    public void 匯入後回放查得到且時間與時長正確()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("攝影機01", Start, size: 4096, durationSeconds: 20);
        var folder = Assert.Single(Scan());

        var report = Import(folder, id);

        Assert.Equal(1, report.Imported);
        Assert.Equal(0, report.Skipped);

        var rows = new SegmentRepository(_store).ListByRange(
            id, "main", Start.AddMinutes(-1), Start.AddMinutes(1));
        var row = Assert.Single(rows);
        Assert.Equal(Start, row.StartUtc);
        Assert.Equal(SegmentStatus.Final, row.Status);
        Assert.Equal(4096, row.SizeBytes);
        Assert.Equal(20, row.DurationSec!.Value, 3);
        Assert.EndsWith("seg-165310000.mp4", row.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 重複掃描不會把同一段匯入兩次()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("攝影機01", Start, size: 4096);

        Import(Assert.Single(Scan()), id);

        Assert.Empty(Scan());
        Assert.Equal(0, Import(
            new RecordingImportFolder(
                Path.Combine(_root, "攝影機01"), "攝影機01", id, 1, 4096, Start, Start),
            id).Imported);
    }

    [Fact]
    public void 被標成missing的段在檔案還在時會復活()
    {
        var id = AddChannel("攝影機01");
        var path = WriteSegment("攝影機01", Start, size: 4096);
        var repo = new SegmentRepository(_store);
        var segmentId = repo.BeginSegment(id, "main", path, Start);
        repo.CompleteSegment(segmentId, Start.AddSeconds(20), 4096, 20, "abc");
        repo.MarkMissing(segmentId);
        // ListByRange 不篩狀態（呼叫端負責只取 final），所以這裡看狀態而不是「有沒有列」。
        Assert.DoesNotContain(
            repo.ListByRange(id, "main", Start.AddMinutes(-1), Start.AddMinutes(1)),
            r => r.Status == SegmentStatus.Final);

        var report = Import(Assert.Single(Scan()), id);

        Assert.Equal(1, report.Revived);
        Assert.Equal(0, report.Imported);
        Assert.Contains(
            repo.ListByRange(id, "main", Start.AddMinutes(-1), Start.AddMinutes(1)),
            r => r.Status == SegmentStatus.Final);
    }

    [Fact]
    public void 沒有收尾時間的檔案仍會匯入且時長不是零()
    {
        var id = AddChannel("攝影機01");
        // 複製、還原回來的檔案常把 LastWriteTimeUtc 蓋成「現在」，而不是收尾時刻。
        WriteSegment("攝影機01", Start, size: 4096, lastWriteUtc: Start.AddDays(3));

        Import(Assert.Single(Scan()), id);

        var row = Assert.Single(new SegmentRepository(_store).ListByRange(
            id, "main", Start.AddMinutes(-1), Start.AddMinutes(1)));
        Assert.Equal(Start, row.StartUtc);

        // 零長度的段在回放時間軸上會被略過，等於「匯入了還是看不到」。
        Assert.NotNull(row.DurationSec);
        Assert.True(row.DurationSec > 0, "時長必須是正值，否則時間軸畫不出這一段");
        Assert.NotNull(row.EndUtc);
        Assert.True(row.EndUtc > row.StartUtc);
    }

    /// <summary>
    /// 收尾時間不可信時，用下一段的開始時間推算時長；稀疏的事件錄影則退回固定值。
    /// </summary>
    /// <remarks>
    /// 實機孤兒錄影（<c>C:\HeliVMSData\recordings\ch072</c>）的 LastWriteTime 全都比檔名時間
    /// 晚了十幾個小時，若因此記成零長度，匯入後時間軸仍然一片空白——正是使用者回報的症狀。
    /// </remarks>
    [Fact]
    public void 收尾時間不可信時用下一段推算時長且不會無限延伸()
    {
        var id = AddChannel("攝影機01");
        var bad = Start.AddDays(3);
        WriteSegment("攝影機01", Start, size: 1024, lastWriteUtc: bad);
        WriteSegment("攝影機01", Start.AddSeconds(20), size: 1024, lastWriteUtc: bad);
        // 第三段自己收尾正常，距離第二段 20 秒；第四段隔了兩小時（稀疏事件錄影的形狀）。
        WriteSegment("攝影機01", Start.AddSeconds(40), size: 1024, durationSeconds: 20);
        WriteSegment("攝影機01", Start.AddHours(2), size: 1024, lastWriteUtc: bad);

        Import(Assert.Single(Scan()), id);

        var rows = new SegmentRepository(_store)
            .ListByRange(id, "main", Start.AddMinutes(-1), Start.AddHours(3))
            .Where(r => r.Status == SegmentStatus.Final)
            .OrderBy(r => r.StartUtc)
            .ToList();

        Assert.Equal(4, rows.Count);
        Assert.Equal(20, rows[0].DurationSec!.Value, 3);
        Assert.Equal(20, rows[1].DurationSec!.Value, 3);
        Assert.Equal(20, rows[2].DurationSec!.Value, 3);
        // 兩小時的缺口不能讓最後一段蓋掉整個下午，退回固定值。
        Assert.Equal(10, rows[3].DurationSec!.Value, 3);
    }

    /// <summary>
    /// 匯入的段必須真的畫在回放時間軸上（索引有、畫面沒有＝使用者仍然看不到錄影）。
    /// </summary>
    [Fact]
    public void 匯入後回放時間軸畫得出這一段()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("攝影機01", Start, size: 4096, lastWriteUtc: Start.AddDays(3));

        Import(Assert.Single(Scan()), id);

        var rows = new SegmentRepository(_store).ListByRange(
            id, "main", Start.Date, Start.Date.AddDays(1));
        var timeline = PlaybackTimelineBuilder.Build(rows, [], Start.Date);

        var bar = Assert.Single(timeline.Bars);
        Assert.True(bar.WidthFraction > 0);
    }

    [Fact]
    public void 暫存檔與空檔不會被匯入()
    {
        AddChannel("攝影機01");
        var dir = Path.Combine(_root, "攝影機01", "20261009");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "seg-165310.mp4.tmp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "seg-165320.mp4"), []);
        File.WriteAllBytes(Path.Combine(dir, "random.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "seg-notatime.mp4"), [1, 2, 3]);

        Assert.Empty(Scan());
    }

    [Fact]
    public void 對不到頻道的資料夾預設是略過而不是猜一個頻道()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("ch072", Start, size: 1024);

        var folder = Assert.Single(Scan());

        Assert.Equal(0, RecordingDiskImporter.DefaultChannelId(folder));
        Assert.NotEqual(id, RecordingDiskImporter.DefaultChannelId(folder));
    }

    [Fact]
    public void 對得到頻道的資料夾預設就選好那個頻道()
    {
        var id = AddChannel("攝影機01");
        WriteSegment("攝影機01", Start, size: 1024);

        Assert.Equal(id, RecordingDiskImporter.DefaultChannelId(Assert.Single(Scan())));
    }

    /// <summary>
    /// 畫面每一列的選擇要收斂成實際要寫入的清單：略過的不送、同一資料夾只送一次。
    /// </summary>
    /// <remarks>
    /// 同一顆碟可能同時是「目前根目錄」與「已知舊根目錄」，掃描去重後仍可能在清單裡
    /// 出現兩列同路徑；若兩列都送進匯入，回報的段數會重複計算，操作員看到的數字就對不上。
    /// </remarks>
    [Fact]
    public void 略過的資料夾不匯入且同一資料夾只匯一次()
    {
        var id = AddChannel("攝影機01");
        var folder = Path.Combine(_root, "攝影機01");

        var picked = RecordingDiskImporter.SelectAssignments(
        [
            (folder, 0),
            (folder, id),
            (folder.ToUpperInvariant(), id),
        ]);

        var only = Assert.Single(picked);
        Assert.Equal(folder, only.FolderPath);
        Assert.Equal(id, only.ChannelId);
    }

    private int AddChannel(string name)
    {
        var id = new ChannelRepository(_store).Add(name, "rtsp://127.0.0.1/live");
        return id;
    }

    private IReadOnlyList<RecordingImportFolder> Scan() =>
        new RecordingDiskImporter(new SegmentRepository(_store))
            .Scan([_root], new ChannelRepository(_store).List());

    private RecordingImportReport Import(RecordingImportFolder folder, int channelId) =>
        new RecordingDiskImporter(new SegmentRepository(_store))
            .Import([(folder.FolderPath, channelId)]);

    /// <summary>依錄影端的命名規則寫一個段檔，並把收尾時間設成開始後 <paramref name="durationSeconds"/> 秒。</summary>
    private string WriteSegment(
        string folder,
        DateTime startUtc,
        int size,
        int durationSeconds = 20,
        DateTime? lastWriteUtc = null)
    {
        var dir = Path.Combine(_root, folder, startUtc.ToString("yyyyMMdd"), startUtc.ToString("HH"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"seg-{startUtc:HHmmssfff}.mp4");
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? startUtc.AddSeconds(durationSeconds));
        return path;
    }
}
