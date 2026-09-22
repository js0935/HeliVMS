namespace HeliVMS.Storage.Tests;

public class PosReconciliationTests : IDisposable
{
    private readonly POSEventRepository _repo;
    private readonly SqliteStore _store;
    private readonly string _dbPath;

    public PosReconciliationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-posrecon-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new POSEventRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static DateTime T0() => new(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    [Fact]
    public void InsertDedupe_FirstInserts_SecondRejectsAsDuplicate()
    {
        var t = T0();
        var (id1, ins1) = _repo.InsertDedupe(3, "R1", "TXN-100", 500, t, Window);
        var (id2, ins2) = _repo.InsertDedupe(3, "R1", "TXN-100", 500, t.AddSeconds(20), Window);

        Assert.True(ins1);
        Assert.False(ins2);
        Assert.Equal(id1, id2);
        Assert.Single(_repo.QueryByRegister(3, "R1", t.AddHours(-1), t.AddHours(1)));
    }

    [Fact]
    public void InsertDedupe_SameTxn_BeyondWindow_Inserts()
    {
        var t = T0();
        _repo.InsertDedupe(3, "R1", "TXN-100", 500, t, Window);
        var (_, ins2) = _repo.InsertDedupe(3, "R1", "TXN-100", 500, t.AddMinutes(5), Window);

        Assert.True(ins2);
        Assert.Equal(2, _repo.QueryByRegister(3, "R1", t.AddHours(-1), t.AddHours(1)).Count);
    }

    [Fact]
    public void InsertDedupe_DifferentRegister_NotDuplicate()
    {
        var t = T0();
        _repo.InsertDedupe(3, "R1", "TXN-100", 500, t, Window);
        var (_, ins2) = _repo.InsertDedupe(3, "R2", "TXN-100", 500, t, Window);

        Assert.True(ins2);
    }

    [Fact]
    public void InsertDedupe_DifferentAmount_NotDuplicate()
    {
        var t = T0();
        _repo.InsertDedupe(3, "R1", "TXN-100", 500, t, Window);
        var (_, ins2) = _repo.InsertDedupe(3, "R1", "TXN-100", 501, t, Window);

        Assert.True(ins2);
    }

    [Fact]
    public void QueryByRegister_FiltersDeviceRegisterTime()
    {
        var t = T0();
        _repo.Insert(1, "R1", "A", 10, t);
        _repo.Insert(1, "R2", "B", 20, t);
        _repo.Insert(2, "R1", "C", 30, t);
        _repo.Insert(1, "R1", "D", 40, t.AddHours(3));

        var rows = _repo.QueryByRegister(1, "R1", t.AddHours(-1), t.AddHours(2));

        Assert.Single(rows);
        Assert.Equal("A", rows[0].TransactionNo);
    }

    [Fact]
    public void Reconcile_AllMatched_NoDuplicates()
    {
        var t = T0();
        var pos = new List<POSEvent>
        {
            new(1, 3, "R1", "T1", 100, t),
            new(2, 3, "R1", "T2", 200, t.AddSeconds(5)),
        };
        var doors = new List<DateTime> { t.AddSeconds(1), t.AddSeconds(6) };

        var summary = PosReconciliation.Compute(pos, doors, d => d, Window);

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Matched);
        Assert.Equal(0, summary.Unmatched);
        Assert.Equal(0, summary.Duplicates);
    }

    [Fact]
    public void Reconcile_PartialMatch_CountsUnmatched()
    {
        var t = T0();
        var pos = new List<POSEvent>
        {
            new(1, 3, "R1", "T1", 100, t),
            new(2, 3, "R1", "T2", 200, t.AddMinutes(10)),
        };
        var doors = new List<DateTime> { t.AddSeconds(1) };

        var summary = PosReconciliation.Compute(pos, doors, d => d, Window);

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Matched);
        Assert.Equal(1, summary.Unmatched);
    }

    [Fact]
    public void Reconcile_Duplicates_CountedNotMatchedTwice()
    {
        var t = T0();
        var pos = new List<POSEvent>
        {
            new(1, 3, "R1", "T1", 100, t),
            new(2, 3, "R1", "T1", 100, t.AddSeconds(10)),
        };
        var doors = new List<DateTime> { t.AddSeconds(1) };

        var summary = PosReconciliation.Compute(pos, doors, d => d, Window);

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Matched);
        Assert.Equal(1, summary.Duplicates);
    }

    [Fact]
    public void Reconcile_NoCandidates_AllUnmatched()
    {
        var t = T0();
        var pos = new List<POSEvent> { new(1, 3, "R1", "T1", 100, t) };

        var summary = PosReconciliation.Compute(pos, Array.Empty<DateTime>(), d => d, Window);

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.Matched);
        Assert.Equal(1, summary.Unmatched);
    }

    [Fact]
    public void Reconcile_EmptyTransactions_None()
    {
        var summary = PosReconciliation.Compute(Array.Empty<POSEvent>(), Array.Empty<DateTime>(), d => d, Window);
        Assert.Equal(0, summary.Total);
    }
}