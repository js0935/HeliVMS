using System.Security.Cryptography;
using System.Text;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 中斷收尾的錄影救援（<see cref="TemporarySegmentRecovery"/>）。
///
/// 這裡守的是「關程式前錄的那一段還在」：索引寫的是正式檔名
/// <c>seg-HHmmssfff.mp4</c>，被硬砍的 ffmpeg 只留下 <c>+.tmp</c>，索引對帳因此判成 missing，
/// 而保留清理十分鐘後會把 <c>.tmp</c> 一起刪掉——那段影片就永久消失了。救援必須在
/// 清理之前把檔案救回索引，而且只能救「真的可以播」的檔案。
/// </summary>
public sealed class TemporarySegmentRecoveryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly string _recordingsRoot;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly int _channelId;

    public TemporarySegmentRecoveryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-recover-{Guid.NewGuid():N}.db");
        _recordingsRoot = Path.Combine(Path.GetTempPath(), $"helivms-recover-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingsRoot);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _channelId = new ChannelRepository(_store).Add("測試頻道", "rtsp://example.invalid/live");
    }

    /// <summary>
    /// 收尾那一刻被砍掉的區段必須變成可回放的 final 段：檔案改名、大小與 SHA-256 取自磁碟、
    /// 長度取「開始到檔案最後寫入」的牆鐘時間。
    ///
    /// 長度取 mtime 而不是設定分段秒數：救援時 ffmpeg 早已結束，沒有任何地方記得它實際錄了多久，
    /// 而檔案最後寫入時刻就是錄影真正停止的時刻。
    /// </summary>
    [Fact]
    public void 中斷收尾的暫存檔會改名為正式檔並寫進索引()
    {
        var bytes = Mp4Bytes();
        var (id, finalPath, tmpPath) = SeedTemporary(bytes, Now.AddMinutes(-5), Now.AddMinutes(-10));

        var report = NewRecovery().Run();

        Assert.Equal(1, report.Recovered);
        Assert.Equal(0, report.Discarded);
        Assert.True(report.AnythingChanged);

        Assert.False(File.Exists(tmpPath), "救回之後不能留下 .tmp，否則保留清理會再掃一次");
        Assert.True(File.Exists(finalPath), "正式檔必須存在，回放才找得到它");

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Final, row.Status);
        Assert.Equal(bytes.Length, row.SizeBytes);
        Assert.Equal(Sha256Of(bytes), row.Sha256);
        Assert.Equal(300d, row.DurationSec!.Value, 1);
        Assert.Equal(Now.AddMinutes(-5), row.EndUtc!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 開頭不是 MP4 的暫存檔要標成 corrupt 並刪除，不能改名成「看得到、播不出來」的正式段。
    ///
    /// 這種檔案（空檔、半個 box header、其他程式留下的垃圾）改什麼名字都播不出來，
    /// 留著只會讓索引對帳每輪再掃一次，而且回放清單會多一段永遠失敗的錄影。
    /// </summary>
    [Fact]
    public void 不是MP4的暫存檔會標成corrupt並刪除()
    {
        var garbage = Encoding.ASCII.GetBytes("this-is-not-an-mp4-file-just-garbage");
        var (id, finalPath, tmpPath) = SeedTemporary(garbage, Now.AddMinutes(-5), Now.AddMinutes(-10));

        var report = NewRecovery().Run();

        Assert.Equal(0, report.Recovered);
        Assert.Equal(1, report.Discarded);
        Assert.False(File.Exists(tmpPath));
        Assert.False(File.Exists(finalPath), "壞檔不該被改名成正式檔，否則時間軸會多一段打不開的錄影");

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Corrupt, row.Status);
    }

    /// <summary>
    /// 檔案時間在三十秒內視為「還在寫」，本輪不得搬動。
    ///
    /// 搬一個正在寫入的檔案會讓錄影中斷，而且 Windows 上會直接因共用違規失敗。
    /// 這裡刻意讓檔案的 mtime 只有五秒前，模擬剛開始寫的那一段。
    /// </summary>
    [Fact]
    public void 檔案時間太新時視為仍在寫入並跳過()
    {
        var (id, _, tmpPath) = SeedTemporary(Mp4Bytes(), Now.AddSeconds(-5), Now.AddMinutes(-10));

        var report = NewRecovery().Run();

        Assert.Equal(0, report.Recovered);
        Assert.Equal(1, report.Skipped);
        Assert.True(File.Exists(tmpPath), "正在寫的暫存檔不能被搬走");

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Temporary, row.Status);
    }

    /// <summary>
    /// 正式檔已經在（改名成功但索引沒寫進去，或兩份都留著）時，以正式檔為準就地完成，
    /// 殘留的暫存檔直接清掉。
    ///
    /// 這個形狀在「程式在 File.Move 之後、CompleteSegment 之前被砍」時會出現；
    /// 若只認 <c>.tmp</c>，這一輪會跳過它，而它永遠停在 tmp——明明檔案好好的卻不能回放。
    /// </summary>
    [Fact]
    public void 正式檔已存在時會就地完成並清掉殘留暫存檔()
    {
        var bytes = Mp4Bytes();
        var (id, finalPath, tmpPath) = SeedTemporary(
            bytes,
            Now.AddMinutes(-5),
            Now.AddMinutes(-10),
            writeTmp: true,
            writeFinal: true);

        var report = NewRecovery().Run();

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(tmpPath));
        Assert.True(File.Exists(finalPath));

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Final, row.Status);
        Assert.Equal(Sha256Of(bytes), row.Sha256);
    }

    /// <summary>
    /// 磁碟不在（D: 被拔掉、網路磁碟掉線）時，兩個檔案都找不到不能標成 corrupt，
    /// 也不能收斂成 missing：磁碟回來之後那些錄影其實還在。
    ///
    /// 磁碟拔掉時，每一段暫存列都會變成「兩檔皆無」；此時判死等於在磁碟回來之後報廢一段
    /// 其實還在的錄影。留著等下一輪，與索引對帳對「無法判定」的立場一致。
    /// </summary>
    [Fact]
    public void 磁碟不在時兩個檔案都找不到會留著等下一輪而不標成corrupt()
    {
        var (id, _, _) = SeedTemporary(
            Mp4Bytes(),
            Now.AddMinutes(-5),
            Now.AddMinutes(-10),
            writeTmp: false,
            writeFinal: false);

        var report = NewRecovery(isVolumeReady: _ => false).Run();

        Assert.Equal(0, report.Recovered);
        Assert.Equal(0, report.Discarded);
        Assert.Equal(0, report.Abandoned);
        Assert.Equal(1, report.Skipped);

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Temporary, row.Status);
    }

    /// <summary>
    /// 磁碟還在、正式檔與暫存檔卻都不在：這一段真的沒了，索引列要收斂成 missing 而不是
    /// 永遠停在 tmp。
    ///
    /// 停在 tmp 的列有兩個實際後果：①每一輪救援都會再 stat 它們一次，數量多起來之後
    /// （實機 182 筆）新的段會被批次上限擠掉；②磁碟匯入會把 tmp 當成「已索引」跳過。
    /// 選 missing 還留了後路——檔案若再出現（磁碟插回來、複製完成），匯入會把它復活。
    /// </summary>
    [Fact]
    public void 磁碟還在但兩個檔案都不在時會收斂成missing()
    {
        var (id, finalPath, _) = SeedTemporary(
            Mp4Bytes(),
            Now.AddMinutes(-5),
            Now.AddMinutes(-10),
            writeTmp: false,
            writeFinal: false);

        var report = NewRecovery().Run();

        Assert.Equal(0, report.Recovered);
        Assert.Equal(1, report.Abandoned);
        Assert.True(report.AnythingChanged, "索引列真的變了，呼叫端才知道要記一筆");

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Missing, row.Status);
        Assert.False(File.Exists(finalPath));
    }

    /// <summary>
    /// 剛開始錄的段在 <c>BeginSegment</c> 之後、ffmpeg 開檔之前是真的沒有檔案；
    /// 此時判死會讓收尾時的 <c>CompleteSegment</c>（條件含 status='tmp'）一行都寫不進去。
    ///
    /// 症狀會是「錄影檔在磁碟上、索引永遠停在 missing」——回放看不到，而日誌完全乾淨。
    /// </summary>
    [Fact]
    public void 剛開始錄而暫存檔還沒出現時不會被判成missing()
    {
        var (id, _, _) = SeedTemporary(
            Mp4Bytes(),
            Now.AddSeconds(-1),
            Now.AddSeconds(-1),
            writeTmp: false,
            writeFinal: false);

        var report = NewRecovery().Run();

        Assert.Equal(0, report.Abandoned);
        Assert.Equal(1, report.Skipped);

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Temporary, row.Status);
    }

    /// <summary>
    /// 索引記成 corrupt、正式檔卻好好地在磁碟上的段也要救回來。
    ///
    /// 這個形狀來自「改名成功 → 算雜湊或寫索引失敗」：檔案是完整的錄影，只是索引在收尾途中
    /// 出錯而被標成 corrupt。回放只讀 final，磁碟匯入又會把 corrupt 當「已索引」跳過，
    /// 不救它就等於永久遺失。
    /// </summary>
    [Fact]
    public void corrupt列的正式檔仍在時會救回final()
    {
        var bytes = Mp4Bytes();
        var (id, finalPath, _) = SeedTemporary(
            bytes,
            Now.AddMinutes(-5),
            Now.AddMinutes(-10),
            writeTmp: false,
            writeFinal: true);
        _segments.MarkCorrupt(id);

        var report = NewRecovery().Run();

        Assert.Equal(1, report.Recovered);

        var row = Assert.IsType<SegmentRecord>(_segments.Get(id));
        Assert.Equal(SegmentStatus.Final, row.Status);
        Assert.Equal(bytes.Length, row.SizeBytes);
        Assert.Equal(Sha256Of(bytes), row.Sha256);
        Assert.Equal(300d, row.DurationSec!.Value, 1);
    }

    private TemporarySegmentRecovery NewRecovery(Func<string, bool>? isVolumeReady = null)
        => new(_segments, () => Now, isVolumeReady);

    private (long Id, string FinalPath, string TmpPath) SeedTemporary(
        byte[] bytes,
        DateTime lastWriteUtc,
        DateTime startUtc,
        bool writeTmp = true,
        bool writeFinal = false)
    {
        var finalPath = Path.Combine(_recordingsRoot, $"seg-{Guid.NewGuid():N}.mp4");
        var tmpPath = finalPath + ".tmp";
        if (writeFinal)
        {
            File.WriteAllBytes(finalPath, bytes);
            File.SetLastWriteTimeUtc(finalPath, lastWriteUtc);
        }

        if (writeTmp)
        {
            File.WriteAllBytes(tmpPath, bytes);
            File.SetLastWriteTimeUtc(tmpPath, lastWriteUtc);
        }

        var id = _segments.BeginSegment(_channelId, "main", finalPath, startUtc);
        return (id, finalPath, tmpPath);
    }

    /// <summary>最小可辨識的 MP4：第一個 box 的型別必須是 <c>ftyp</c>（位移 4..7）。</summary>
    private static byte[] Mp4Bytes()
    {
        var bytes = new byte[64];
        bytes[3] = 0x18; // box 長度 24
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("isom").CopyTo(bytes, 8);
        return bytes;
    }

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (Directory.Exists(_recordingsRoot))
            {
                Directory.Delete(_recordingsRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // 測試清理失敗不該讓測試紅掉。
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
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
