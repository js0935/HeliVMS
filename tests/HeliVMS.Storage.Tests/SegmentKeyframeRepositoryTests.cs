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

    /// <summary>
    /// 一次查多段要與逐段查等價：每段的關鍵幀都只屬於自己，且段內仍由小到大。
    /// 回放視窗從逐段查改成一次查（一天上千段），口徑若跑掉就會把別段的刻度畫到這一段上。
    /// </summary>
    [Fact]
    public void 一次查多段與逐段查等價且各段由小到大()
    {
        var day = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var a = BeginFinalSegment(day.AddHours(1), 60);
        var b = BeginFinalSegment(day.AddHours(2), 60);
        _repo.Replace(a, [new SegmentKeyframe(20, 8192), new SegmentKeyframe(0, 1024), new SegmentKeyframe(10, 4096)]);
        _repo.Replace(b, [new SegmentKeyframe(5, 512)]);

        var all = _repo.ListForSegments([a, b]);

        Assert.Equal(2, all.Count);
        Assert.Equal(_repo.List(a).Select(r => r.TimeSec), all[a].Select(r => r.TimeSec));
        Assert.Equal([0, 10, 20], all[a].Select(r => r.TimeSec).ToArray());
        Assert.Equal([5], all[b].Select(r => r.TimeSec).ToArray());
    }

    /// <summary>
    /// 沒有索引的段不可出現在結果裡（舊逐段查的 <c>rows.Count &gt; 0</c> 條件）。
    /// 混進空清單會讓時間軸對「還沒探測」與「探測到 0 個關鍵幀」兩種狀態失去區分。
    /// </summary>
    [Fact]
    public void 一次查多段不會帶出沒有索引的段()
    {
        var day = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var indexed = BeginFinalSegment(day.AddHours(1), 60);
        var unindexed = BeginFinalSegment(day.AddHours(2), 60);
        _repo.Replace(indexed, [new SegmentKeyframe(0, 512)]);

        var all = _repo.ListForSegments([indexed, unindexed]);

        Assert.True(all.ContainsKey(indexed));
        Assert.False(all.ContainsKey(unindexed));
    }

    /// <summary>
    /// 段數超過單批上限時要跨批次合併：排在第一批與最後一批的段都必須在結果裡。
    /// 一天 10 秒一段就有 8640 段，單一 IN 查詢會撞到 SQLite 的參數上限；只顧最後一批
    /// 會讓當天早上的段在時間軸上整段沒有刻度（看起來像沒錄到）。
    /// </summary>
    [Fact]
    public void 段數超過單批上限時第一批與最後一批的段都要查得到()
    {
        var day = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var first = BeginFinalSegment(day.AddHours(1), 60);
        var last = BeginFinalSegment(day.AddHours(2), 60);
        _repo.Replace(first, [new SegmentKeyframe(0, 1024), new SegmentKeyframe(30, 4096)]);
        _repo.Replace(last, [new SegmentKeyframe(15, 512)]);

        // 40,000 個編號 = 45 批；真段各擺在最前與最後，只有把每批結果都併起來才看得到兩個。
        const int count = 40000;
        var ids = new long[count];
        for (var i = 1; i < count - 1; i++)
        {
            ids[i] = 1_000_000 + i;
        }

        ids[0] = first;
        ids[count - 1] = last;

        var all = _repo.ListForSegments(ids);

        Assert.Equal(2, all.Count);
        Assert.Equal([0, 30], all[first].Select(r => r.TimeSec).ToArray());
        Assert.Equal([15], all[last].Select(r => r.TimeSec).ToArray());
    }

    /// <summary>空清單要回空集合而不是拋例外（換頻道／換日都可能沒有段）。</summary>
    [Fact]
    public void 空清單回空結果()
    {
        var all = _repo.ListForSegments([]);

        Assert.Empty(all);
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
