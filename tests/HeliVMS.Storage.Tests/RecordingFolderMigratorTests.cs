using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 既有錄影資料夾改成「以頻道名稱命名」的搬移（M311）。
///
/// <para>
/// 這個搬移只做一次、要能重複執行、而且**盡力而為**：檔案被佔用、目標已存在、來源已消失
/// 都不能讓整個啟動失敗。最關鍵的保證是「搬了檔案就一定要回寫索引」——回放、備援、保留
/// 清理都只認資料庫裡的路徑，漏寫就等於把既有錄影變成磁碟上的孤兒。
/// </para>
/// </summary>
public sealed class RecordingFolderMigratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly ChannelRepository _channels;

    public RecordingFolderMigratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-mig-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-mig-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _channels = new ChannelRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 舊制 <c>ch{id}</c> 資料夾要改名為頻道名稱，且索引裡的檔案路徑必須跟著更新——
    /// 這是回放與匯出找得到檔案的前提。
    /// </summary>
    [Fact]
    public void 舊資料夾會改名為頻道名稱並回寫索引()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        var segmentId = AddSegment(channelId, "ch001", "seg-a.mp4");

        var result = RecordingFolderMigrator.Run(_store);

        var expected = Path.Combine(_root, "前門", "20260101", "seg-a.mp4");
        Assert.Equal(1, result.MovedFolders);
        Assert.Equal(1, result.UpdatedSegments);
        Assert.True(File.Exists(expected));
        Assert.False(Directory.Exists(Path.Combine(_root, "ch001")));
        Assert.Equal(expected, _segments.Get(segmentId)!.FilePath);
    }

    /// <summary>目標目錄已存在時要合併，且不得覆蓋既有檔案。</summary>
    [Fact]
    public void 目標已存在時會合併並保留既有檔案()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        var segmentId = AddSegment(channelId, "ch001", "seg-a.mp4");

        var existing = Path.Combine(_root, "前門", "20260101", "other.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "既有");

        var result = RecordingFolderMigrator.Run(_store);

        Assert.Equal(1, result.MergedFolders);
        Assert.True(File.Exists(existing), "合併不得刪掉目標目錄裡原有的檔案");
        Assert.Equal("既有", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(_root, "前門", "20260101", "seg-a.mp4")));
        Assert.Equal(
            Path.Combine(_root, "前門", "20260101", "seg-a.mp4"),
            _segments.Get(segmentId)!.FilePath);
    }

    /// <summary>名稱已正確的資料夾不該被搬動或改路徑，否則每次啟動都在搬檔案。</summary>
    [Fact]
    public void 名稱已正確時不搬動也不改路徑()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        var segmentId = AddSegment(channelId, "前門", "seg-a.mp4");
        var original = _segments.Get(segmentId)!.FilePath;

        var result = RecordingFolderMigrator.Run(_store);

        Assert.False(result.Changed);
        // 名稱已正確時必須「直接放行」而不是走進合併流程：否則永遠把資料夾算成一次跳過，
        // SkippedFolders 每次啟動都非零，也就分不出「正常」與「真的搬不動」。
        Assert.Equal(0, result.SkippedFolders);
        Assert.Equal(original, _segments.Get(segmentId)!.FilePath);
        Assert.True(File.Exists(original));
    }

    /// <summary>
    /// 上一次搬到一半就中斷（檔案已在新目錄，索引還指著舊路徑）時，這次必須補回寫索引——
    /// 否則那個檔案會永遠是磁碟上的孤兒：回放、備援、保留清理都只認資料庫路徑。
    /// </summary>
    [Fact]
    public void 來源已搬走但索引未回寫時會補寫路徑()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        var segmentId = RegisterSegmentWithoutFile(channelId, "ch001", "seg-a.mp4");

        // 模擬「上一次已把資料夾搬成新名稱，但回寫前中斷」的最終磁碟狀態。
        var moved = Path.Combine(_root, "前門", "20260101", "seg-a.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllText(moved, "x");
        Assert.False(Directory.Exists(Path.Combine(_root, "ch001")));

        var result = RecordingFolderMigrator.Run(_store);

        Assert.Equal(1, result.UpdatedSegments);
        Assert.Equal(moved, _segments.Get(segmentId)!.FilePath);
    }

    /// <summary>來源已消失且目標也不存在時要跳過，且**不得**把索引改到不存在的位置。</summary>
    [Fact]
    public void 來源不存在時跳過且不改路徑()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        var segmentId = RegisterSegmentWithoutFile(channelId, "ch001", "seg-a.mp4");
        var original = _segments.Get(segmentId)!.FilePath;

        var result = RecordingFolderMigrator.Run(_store);

        Assert.Equal(1, result.SkippedFolders);
        Assert.Equal(0, result.UpdatedSegments);
        Assert.Equal(original, _segments.Get(segmentId)!.FilePath);
    }

    /// <summary>
    /// 同一個資料夾被兩個頻道的區段指到時（資料異常），要整組跳過而不是搬給其中一個頻道，
    /// 否則會把另一個頻道的錄影搬錯地方。
    /// </summary>
    [Fact]
    public void 同一資料夾被多個頻道指到時整組跳過()
    {
        var first = _channels.Add("前門", "rtsp://example.invalid/live");
        var second = _channels.Add("後門", "rtsp://example.invalid/live");
        AddSegment(first, "ch001", "seg-a.mp4");
        AddSegment(second, "ch001", "seg-b.mp4");

        var result = RecordingFolderMigrator.Run(_store);

        Assert.Equal(1, result.SkippedFolders);
        Assert.True(Directory.Exists(Path.Combine(_root, "ch001")));
    }

    /// <summary>有實際搬動時要留下稽核紀錄，讓「錄影跑到哪裡去」追得回來。</summary>
    [Fact]
    public void 有搬動時會寫入稽核紀錄()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        AddSegment(channelId, "ch001", "seg-a.mp4");

        RecordingFolderMigrator.Run(_store);

        var entry = Assert.Single(new AuditLogRepository(_store).List(
            new AuditLogQuery { Action = "recording.folder.migrate" }));
        Assert.Equal("system", entry.Actor);
    }

    /// <summary>搬移可重複執行：第二次跑不應再搬任何東西。</summary>
    [Fact]
    public void 重複執行不會再搬動()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        AddSegment(channelId, "ch001", "seg-a.mp4");

        RecordingFolderMigrator.Run(_store);
        var second = RecordingFolderMigrator.Run(_store);

        Assert.False(second.Changed);
    }

    /// <summary>
    /// 合併時若目標已有同名檔案，不得覆蓋既有檔案；同名檔案會留在舊資料夾而搬移結果標記為跳過，
    /// 以確保不改寫索引到錯誤狀態（整個資料夾當作未搬完成，下一次再處理）。
    /// </summary>
    [Fact]
    public void 合併時同名檔案已存在則不覆蓋且整組標記為跳過()
    {
        var channelId = _channels.Add("前門", "rtsp://example.invalid/live");
        var segmentId = AddSegment(channelId, "ch001", "seg-a.mp4");
        var originalPath = _segments.Get(segmentId)!.FilePath;

        var existing = Path.Combine(_root, "前門", "20260101", "seg-a.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "既有內容");

        var result = RecordingFolderMigrator.Run(_store);

        Assert.Equal(1, result.SkippedFolders);
        Assert.Equal(0, result.UpdatedSegments);
        Assert.Equal("既有內容", File.ReadAllText(existing));
        Assert.True(Directory.Exists(Path.Combine(_root, "ch001")), "因同名碰撞而未能完整合併，舊資料夾應保留");
        Assert.Equal(originalPath, _segments.Get(segmentId)!.FilePath);
    }

    private long AddSegment(int channelId, string folderName, string fileName)
    {
        var id = RegisterSegmentWithoutFile(channelId, folderName, fileName);
        var path = _segments.Get(id)!.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return id;
    }

    private long RegisterSegmentWithoutFile(int channelId, string folderName, string fileName)
    {
        var path = Path.Combine(_root, folderName, "20260101", fileName);
        var id = _segments.BeginSegment(channelId, "main", path, DateTime.UtcNow.AddMinutes(-1));
        _segments.CompleteSegment(id, DateTime.UtcNow, 1, 15, new string('a', 64));
        return id;
    }
}
