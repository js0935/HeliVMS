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
}