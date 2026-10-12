using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 法證語意融合搜尋（§14.7 #7）：FTS bm25 與 CLIP 向量餘弦經 RankFusion 合併。
/// 這裡刻意把兩路都當成可由資料庫製造的輸入，驗證融合與「向量命中補後設資料」的接線，
/// 不涉及外部模型（embedding 生成仍是外部縫）。
/// </summary>
public sealed class ForensicSemanticSearchTests : IDisposable
{
    private static readonly float[] UnitX = [1f, 0f, 0f];
    private static readonly float[] UnitY = [0f, 1f, 0f];

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _alarm;
    private readonly POSEventRepository _pos;
    private readonly EmbeddingRepository _vectors;

    private static DateTime T(int hour) => new(2026, 9, 22, hour, 0, 0, DateTimeKind.Utc);

    public ForensicSemanticSearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-sem-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();

        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'c1', 'rtsp://cam1', NULL, 'h264', 1, 'copy');
            """);
        _alarm = new AlarmEventRepository(_store);
        _pos = new POSEventRepository(_store);
        _vectors = new EmbeddingRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    private ForensicSemanticSearch Search() => new(_store);

    [Fact]
    public void SourceName_RoundTrips()
    {
        Assert.Equal("alarm", ForensicSemanticSearch.SourceName(ForensicSource.Alarm));
        Assert.Equal("pos", ForensicSemanticSearch.SourceName(ForensicSource.Pos));
        Assert.Equal(ForensicSource.EdgeSmart, ForensicSemanticSearch.SourceOf("edge"));
        Assert.Null(ForensicSemanticSearch.SourceOf("nope"));
    }

    [Fact]
    public void Search_TextOnly_DegradesToFullText()
    {
        _alarm.Insert(1, "motion", T(8), null, "alpha beta");

        var hit = Assert.Single(Search().Search("alpha"));

        Assert.Equal(ForensicSource.Alarm, hit.Source);
        Assert.Contains("alpha", hit.Text);
        Assert.InRange(hit.Score, 0.0, 1.0);
    }

    [Fact]
    public void Search_VectorOnly_ResolvesMetadataForVectorOnlyHit()
    {
        _alarm.Insert(1, "motion", T(8), null, "alpha beta");
        _vectors.Upsert("alarm", 1, "snapshot", UnitX);

        // 無文字查詢、只給向量：向量命中落在文字結果之外，仍須回查主表補齊片段與時間。
        var hit = Assert.Single(Search().Search(string.Empty, UnitX));

        Assert.Equal(ForensicSource.Alarm, hit.Source);
        Assert.Equal(1, hit.SourceId);
        Assert.Contains("alpha", hit.Text);
        Assert.Equal(T(8), hit.OccurredAtUtc);
        Assert.True(hit.Score > 0.9, $"向量完全相符應接近 1，實際 {hit.Score}");
    }

    [Fact]
    public void Search_FusesTextAndVectorHits()
    {
        _alarm.Insert(1, "motion", T(8), null, "alpha event");   // 文字命中
        _alarm.Insert(1, "motion", T(9), null, "gamma event");   // 向量命中（文字查不到）
        _vectors.Upsert("alarm", 2, null, UnitX);

        var hits = Search().Search("alpha", UnitX);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.SourceId == 1);
        Assert.Contains(hits, h => h.SourceId == 2);
    }

    [Fact]
    public void Search_VectorOnly_RespectsTimeWindow()
    {
        _alarm.Insert(1, "motion", T(8), null, "alpha");
        _vectors.Upsert("alarm", 1, null, UnitX);

        Assert.Empty(Search().Search(string.Empty, UnitX, fromUtc: T(9)));
        Assert.Single(Search().Search(string.Empty, UnitX, fromUtc: T(7)));
    }

    [Fact]
    public void Search_SourceMaskExcludesVectorOnlyOtherSources()
    {
        _pos.Insert(1, "R1", "TXN-1", 100, T(8));
        _vectors.Upsert("pos", 1, null, UnitX);

        Assert.Empty(Search().Search(string.Empty, UnitX, sourceMask: ForensicSource.Alarm));
        Assert.Single(Search().Search(string.Empty, UnitX, sourceMask: ForensicSource.Pos));
    }

    [Fact]
    public void Search_UnknownSourceTypeVectorIsIgnored()
    {
        _vectors.Upsert("weird", 1, null, UnitX);

        Assert.Empty(Search().Search(string.Empty, UnitX));
    }

    [Fact]
    public void Search_NoTextAndNoVector_ReturnsEmpty()
    {
        Assert.Empty(Search().Search(string.Empty));
        Assert.Empty(Search().Search("   "));
    }
}
