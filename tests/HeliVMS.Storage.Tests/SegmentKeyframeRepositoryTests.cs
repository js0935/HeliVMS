using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 關鍵幀索引（segment_keyframes）的讀寫契約：整批取代、依時排序、只回缺索引的 final 段。
/// </summary>
/// <remarks>
/// 這張表曾經只建 schema 卻無人讀寫；現在回放時間軸的關鍵幀刻度直接吃這個索引，
/// 口徑錯了會畫出不存在的刻度（tmp 段）或漏掉跨午夜的段（start 落在窗外）。
/// </remarks>
public sealed class SegmentKeyframeRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentKeyframeRepository _repo;

    public SegmentKeyframeRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-kf-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentKeyframeRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "關鍵幀頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/kf");
            });
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void 取代後可列出全部關鍵幀且由小到大()
    {
        var seg = BeginFinalSegment(startUtc: new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), durationSec: 60);

        _repo.Replace(seg, [new SegmentKeyframe(20, 8192), new SegmentKeyframe(0, 1024), new SegmentKeyframe(10, 4096)]);

        var rows = _repo.List(seg);
        Assert.Equal([0, 10, 20], rows.Select(r => r.TimeSec).ToArray());
        Assert.Equal([1024, 4096, 8192], rows.Select(r => r.MoofOffset).ToArray());
    }

    /// <summary>
    /// 重建必須整批取代：只補不清時，GOP 設定改變後的新舊列會混雜，
    /// 時間軸刻度密度會不一致（操作員看到的是上一批探測的殘留）。
    /// </summary>
    [Fact]
    public void 重建會整批取代舊列而不是只補新列()
    {
        var seg = BeginFinalSegment(startUtc: new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), durationSec: 60);
        _repo.Replace(seg, [new SegmentKeyframe(0, 1024), new SegmentKeyframe(30, 4096)]);

        _repo.Replace(seg, [new SegmentKeyframe(0, 2048)]);

        var row = Assert.Single(_repo.List(seg));
        Assert.Equal(0, row.TimeSec);
        Assert.Equal(2048, row.MoofOffset);
    }

    [Fact]
    public void 缺索引清單只包含區間內且無索引的final段()
    {
        var day = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var indexed = BeginFinalSegment(day.AddHours(1), 60);
        var missing = BeginFinalSegment(day.AddHours(2), 60);
        var tmp = BeginSegment(day.AddHours(3)); // tmp 段不可播放，不該被要求建索引

        _repo.Replace(indexed, [new SegmentKeyframe(0, 512)]);

        var ids = _repo.ListMissingIndexIds(1, "main", day, day.AddDays(1));

        Assert.Equal([missing], ids);
        Assert.DoesNotContain(indexed, ids);
        Assert.DoesNotContain(tmp, ids);
    }

    /// <summary>跨午夜的段（23:58 起、00:02 止）必須在隔天的區間也被列進去。</summary>
    [Fact]
    public void 跨午夜的段在隔天區間也會被列出()
    {
        var evening = new DateTime(2026, 3, 2, 23, 58, 0, DateTimeKind.Utc);
        var seg = BeginFinalSegment(evening, 240); // 240 秒 → 結束於隔日 00:02
        var nextDay = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        var ids = _repo.ListMissingIndexIds(1, "main", nextDay, nextDay.AddDays(1));

        Assert.Equal([seg], ids);
    }

    private long BeginFinalSegment(DateTime startUtc, double durationSec)
    {
        var segments = new SegmentRepository(_store);
        var id = segments.BeginSegment(1, "main", Path.Combine(Path.GetTempPath(), $"kf-{Guid.NewGuid():N}.mp4"), startUtc);
        segments.CompleteSegment(id, startUtc.AddSeconds(durationSec), 1024, durationSec, "0".PadLeft(64, '0'));
        return id;
    }

    private long BeginSegment(DateTime startUtc)
    {
        var segments = new SegmentRepository(_store);
        return segments.BeginSegment(1, "main", Path.Combine(Path.GetTempPath(), $"kf-tmp-{Guid.NewGuid():N}.mp4"), startUtc);
    }
}
