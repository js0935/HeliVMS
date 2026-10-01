namespace HeliVMS.Storage.Tests;

public class EmbeddingRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly EmbeddingRepository _repo;

    public EmbeddingRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-clip-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new EmbeddingRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    private static float[] Normalized(params float[] values)
    {
        double sum = 0;
        foreach (var v in values)
        {
            sum += (double)v * v;
        }

        var norm = Math.Sqrt(sum);
        return values.Select(v => (float)(v / norm)).ToArray();
    }

    [Fact]
    public void Search_RanksByCosineSimilarity()
    {
        _repo.Upsert("event", 1, "motion", Normalized(0.9f, 0.1f));
        _repo.Upsert("event", 2, "tamper", Normalized(0.1f, 0.9f));

        var hits = _repo.Search(Normalized(0.8f, 0.2f));

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].RefId);
        Assert.Equal("motion", hits[0].Label);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Upsert_SameRefReplacesVector()
    {
        _repo.Upsert("event", 7, "dup", Normalized(1f, 0f));
        _repo.Upsert("event", 7, "dup-new", Normalized(0f, 1f));

        var hits = _repo.Search(Normalized(0.1f, 0.9f));

        Assert.Single(hits);
        Assert.Equal("dup-new", hits[0].Label);
    }

    [Fact]
    public void Search_EmptyVectorThrows()
    {
        Assert.Throws<ArgumentException>(() => _repo.Search(Array.Empty<float>()));
        Assert.Throws<ArgumentException>(() => _repo.Upsert("event", 1, "x", Array.Empty<float>()));
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        _repo.Upsert("event", 1, "a", Normalized(1f, 0f));
        _repo.Clear();

        Assert.Empty(_repo.Search(Normalized(1f, 0f)));
    }

    [Fact]
    public void ByRef_ReturnsStoredVector_AndDeleteRemoves()
    {
        _repo.Upsert("event", 5, "tamper", Normalized(0.2f, 0.8f));

        var v = _repo.ByRef("event", 5);

        Assert.NotNull(v);
        Assert.Equal(2, v.Count);
        Assert.Equal(Normalized(0.2f, 0.8f)[1], v[1], 4);

        var hit = _repo.Delete("event", 5);
        Assert.True(hit);
        Assert.Null(_repo.ByRef("event", 5));
        Assert.False(_repo.Delete("event", 5));
    }

    [Fact]
    public void Search_SkipsVectorsOfADifferentDimension_InsteadOfThrowing()
    {
        // M242：模型換版（512→768）是很正常的事件，不能變成一個看不懂的越界例外。
        _repo.Upsert("event", 1, "old-model", new float[] { 0.5f, 0.5f, 0.5f, 0.5f });
        _repo.Upsert("event", 2, "new-model", new float[] { 0.9f, 0.1f });

        var hits = _repo.Search(new float[] { 0.8f, 0.2f });

        Assert.Single(hits);
        Assert.Equal(2, hits[0].RefId);
        Assert.True(hits[0].Score > 0.9f);
    }

    [Fact]
    public void Search_WithNoMatchingDimension_ReturnsEmptyRatherThanFailing()
    {
        _repo.Upsert("event", 1, "old-model", new float[] { 0.5f, 0.5f, 0.5f });

        Assert.Empty(_repo.Search(new float[] { 1f, 0f }));
    }

    [Fact]
    public void StoredDimension_ReportsTheIndexedVectorWidth()
    {
        Assert.Null(_repo.StoredDimension());

        _repo.Upsert("event", 1, "a", new float[] { 0.1f, 0.2f, 0.3f });
        Assert.Equal(3, _repo.StoredDimension());

        _repo.Clear();
        Assert.Null(_repo.StoredDimension());
    }

    [Fact]
    public void RoundTrip_PreservesNegativeAndFractionalValues()
    {
        var vector = new float[] { -0.375f, 0.125f, 1f, -1f, float.Epsilon };
        _repo.Upsert("event", 11, "precision", vector);

        var stored = _repo.ByRef("event", 11);
        Assert.NotNull(stored);
        for (var i = 0; i < vector.Length; i++)
        {
            Assert.Equal(vector[i], stored[i]);
        }
    }

    [Fact]
    public void Search_RanksIdenticalVectorsAboveOrthogonalOnes()
    {
        var vector = new float[] { 0.6f, 0.8f };
        _repo.Upsert("event", 1, "same", vector);
        _repo.Upsert("event", 2, "orthogonal", new float[] { -0.8f, 0.6f });
        _repo.Upsert("event", 3, "zero", new float[] { 0f, 0f });

        var hits = _repo.Search(vector);

        Assert.Equal(1, hits[0].RefId);
        Assert.Equal(1.0, hits[0].Score, 5);
        // 零向量分母為 0，不得變成 NaN——NaN 會讓整個排序結果失去意義。
        Assert.All(hits, h => Assert.False(double.IsNaN(h.Score)));
        Assert.Contains(hits, h => h.RefId == 3 && h.Score == 0);
    }

    [Fact]
    public void TopK_IsRespected_AndNonPositiveFallsBackToDefault()
    {
        for (var i = 1; i <= 5; i++)
        {
            _repo.Upsert("event", i, $"l{i}", new float[] { i / 5f, 1 });
        }

        Assert.Equal(2, _repo.Search(new float[] { 1f, 1f }, topK: 2).Count);
        Assert.Equal(5, _repo.Search(new float[] { 1f, 1f }, topK: 0).Count);
        Assert.Equal(5, _repo.Search(new float[] { 1f, 1f }, topK: -3).Count);
    }
    [Fact]
    public void VectorsArePersistedAsLittleEndianRegardlessOfHostByteOrder()
    {
        // 讀取端一律以小端解讀，所以寫入端也必須固定小端；
        // 否則同一份資料在大小端機器上會算出不同的相似度（ByRef 與 Search 都走小端解讀）。
        _repo.Upsert("event", 9, "endianness", new float[] { 1f, 0f, -1f, 0.5f });

        var stored = _repo.ByRef("event", 9);
        Assert.NotNull(stored);
        Assert.Equal(new float[] { 1f, 0f, -1f, 0.5f }, stored);

        // 相似度也要一致：自己跟自己應該是滿分。
        var hits = _repo.Search(new float[] { 1f, 0f, -1f, 0.5f });
        Assert.Equal(9, hits[0].RefId);
        Assert.Equal(1.0, hits[0].Score, 5);
    }

}
