namespace HeliVMS.Storage.Tests;

public class AlarmWorkflowTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmWorkflowRepository _repo;
    private static DateTime T0() => new(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);

    public AlarmWorkflowTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-alarmflow-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlarmWorkflowRepository(_store);
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
    public void AddNote_NotesByEvent_Ordered()
    {
        _repo.AddNote(1, "alice", "確認錄影正常", T0().AddMinutes(1));
        var id2 = _repo.AddNote(1, "bob", "已派工處理", T0().AddMinutes(2));

        var notes = _repo.NotesByEvent(1);

        Assert.Equal(2, notes.Count);
        Assert.Equal("確認錄影正常", notes[0].Note);
        Assert.Equal(id2, notes[1].Id);
        Assert.Equal("bob", notes[1].Author);
    }

    [Fact]
    public void AddNote_EmptyNote_Throws()
    {
        Assert.Throws<ArgumentException>(() => _repo.AddNote(1, "alice", "   ", T0()));
    }

    [Fact]
    public void NoteCount_And_Remove()
    {
        var a = _repo.AddNote(3, "alice", "第一次", T0());
        _repo.AddNote(3, "bob", "第二次", T0());

        Assert.Equal(2, _repo.NoteCountByEvent(3));
        Assert.True(_repo.RemoveNote(a));
        Assert.Equal(1, _repo.NoteCountByEvent(3));
        Assert.False(_repo.RemoveNote(a));
    }

    [Fact]
    public void Notes_AreScopedPerEvent()
    {
        _repo.AddNote(5, "alice", "事件五", T0());
        _repo.AddNote(6, "alice", "事件六", T0());

        var five = Assert.Single(_repo.NotesByEvent(5));
        Assert.Equal("事件五", five.Note);
        Assert.Single(_repo.NotesByEvent(6));
    }

    [Fact]
    public void Escalation_LevelsIncrement_AndList()
    {
        var d1 = new AlarmSlaDecision(true, 1, "normal", "high", T0().AddHours(1));
        var d2 = new AlarmSlaDecision(true, 1, "high", "critical", T0().AddMinutes(30));

        _repo.RecordEscalation(2, d1, T0());
        _repo.RecordEscalation(2, d2, T0().AddMinutes(10));

        var rows = _repo.EscalationsByEvent(2);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Level).ToArray());
        Assert.Equal("normal", rows[0].FromPriority);
        Assert.Equal("critical", rows[1].ToPriority);
        Assert.Equal(T0().AddMinutes(30), rows[1].DueUtc);
    }

    [Fact]
    public void Escalations_AreScopedPerEvent()
    {
        var d = new AlarmSlaDecision(true, 1, "high", "critical", T0().AddMinutes(10));
        _repo.RecordEscalation(9, d, T0());
        _repo.RecordEscalation(10, d, T0());

        Assert.Single(_repo.EscalationsByEvent(9));
        Assert.Equal(1, Assert.Single(_repo.EscalationsByEvent(10)).Level);
    }

    [Fact]
    public void Sla_ResponseSeconds_ByPriority()
    {
        Assert.Equal(300, AlarmSla.ResponseSeconds(AlarmPriority.Critical));
        Assert.Equal(900, AlarmSla.ResponseSeconds(AlarmPriority.High));
        Assert.Equal(3600, AlarmSla.ResponseSeconds(AlarmPriority.Normal));
        Assert.Equal(7200, AlarmSla.ResponseSeconds(AlarmPriority.Low));
    }

    [Fact]
    public void EscalationPolicy_NotOverdue_NoEscalation()
    {
        var decision = AlarmEscalationPolicy.Decide(AlarmPriority.High, T0().AddMinutes(10), T0(), open: true);
        Assert.False(decision.ShouldEscalate);
    }

    [Fact]
    public void EscalationPolicy_Overdue_PromotesPriority()
    {
        var decision = AlarmEscalationPolicy.Decide(AlarmPriority.Normal, T0().AddMinutes(-5), T0(), open: true);

        Assert.True(decision.ShouldEscalate);
        Assert.Equal("normal", decision.FromPriority);
        Assert.Equal("high", decision.ToPriority);
        Assert.Equal(T0().AddSeconds(AlarmSla.ResponseSeconds("high")), decision.NewDueUtc);
    }

    [Fact]
    public void EscalationPolicy_CriticalOverdue_StaysCritical()
    {
        var decision = AlarmEscalationPolicy.Decide(AlarmPriority.Critical, T0().AddMinutes(-1), T0(), open: true);

        Assert.True(decision.ShouldEscalate);
        Assert.Equal("critical", decision.ToPriority);
        Assert.Equal(T0().AddMinutes(5), decision.NewDueUtc);
    }

    [Fact]
    public void EscalationPolicy_ClosedEvent_NoEscalation()
    {
        var decision = AlarmEscalationPolicy.Decide(AlarmPriority.High, T0().AddMinutes(-5), T0(), open: false);
        Assert.False(decision.ShouldEscalate);
    }
}