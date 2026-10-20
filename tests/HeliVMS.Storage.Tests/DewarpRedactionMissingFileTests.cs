using HeliVMS.Recording;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 矯正與遮蔽服務在「索引說有、磁碟上已經沒有」時的處理。
///
/// <para>
/// 兩者都以 concat demuxer 拼接範圍內的 final 段：清單裡只要有一個檔案不存在，ffmpeg 就整批
/// 失敗，使用者看到的是「矯正／遮蔽壞了」而不是「其中幾段被刪了」。索引對帳會把這些段標成
/// missing，但使用者按下按鈕的當下不能等那一輪，所以服務自己要先剔除缺檔——一段都不剩時
/// 丟出可讀的錯誤，而不是把不存在的路徑餵給 ffmpeg 再回一段看不懂的 stderr。
/// </para>
/// </summary>
public sealed class DewarpRedactionMissingFileTests : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _dbPath;
    private readonly int _chId;

    public DewarpRedactionMissingFileTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-dewarp-redact-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _chId = new ChannelRepository(_store).Add("CAM1", "rtsp://x/1");
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
        }
    }

    /// <summary>把一個 final 段寫進索引，但檔案刻意不存在於磁碟。</summary>
    private void SeedFinalWithoutFile(DateTime startUtc)
    {
        var segRepo = new SegmentRepository(_store);
        var missing = Path.Combine(Path.GetTempPath(), $"helivms-gone-{Guid.NewGuid():N}", "seg-100000000.mp4");
        var id = segRepo.BeginSegment(_chId, "main", missing, startUtc);
        segRepo.CompleteSegment(id, startUtc.AddSeconds(15), 128, 15, "sha");
    }

    [Fact]
    public async Task 矯正範圍只剩不存在的檔案時丟出可讀錯誤()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedFinalWithoutFile(start);

        var svc = new DewarpService(_store);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DewarpAsync(new DewarpRequest(
                _chId, start, start.AddMinutes(1), "out.mp4", new DewarpSettings())));

        // 必須是「檔案不存在」而不是「無段落」：前者代表對帳/剔除有跑，後者代表根本沒查到段。
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task 遮蔽範圍只剩不存在的檔案時丟出可讀錯誤()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedFinalWithoutFile(start);

        var svc = new RedactionService(_store);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RedactAsync(new RedactionRequest(
                _chId, start, start.AddMinutes(1), "out.mp4",
                new[] { new RedactionRoi(0, 0, 10, 10) })));

        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task 範圍內沒有任何段落時訊息是無段落而非缺檔()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var svc = new DewarpService(_store);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DewarpAsync(new DewarpRequest(
                _chId, start, start.AddMinutes(1), "out.mp4", new DewarpSettings())));

        Assert.Contains("無錄影段落", ex.Message);
    }
}
