using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class AlarmTriageRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _events;
    private readonly AlarmTriageRepository _triage;

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public AlarmTriageRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-triage-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _events = new AlarmEventRepository(_store);
        _triage = new AlarmTriageRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "分診頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/triage");
            });
    }

    [Fact]
    public void SetTriage_RoundTrip_AndUpsert()
    {
        var id = _events.Insert(1, "motion", Now);
        var due = Now.AddHours(2);
        _triage.SetTriage(id, AlarmPriority.High, due, "alice", Now);

        var first = _triage.Get(id);
        Assert.NotNull(first);
        Assert.Equal(AlarmPriority.High, first!.Priority);
        Assert.Equal(due, first.DueUtc);
        Assert.Equal("alice", first.Owner);

        _triage.SetTriage(id, AlarmPriority.Low, null, "bob", Now.AddMinutes(5));
        var second = _triage.Get(id);
        Assert.NotNull(second);
        Assert.Equal(AlarmPriority.Low, second!.Priority);
        Assert.Null(second.DueUtc);
        Assert.Equal("bob", second.Owner);
    }

    [Fact]
    public void SetTriage_InvalidPriority_Throws()
    {
        var id = _events.Insert(1, "motion", Now);
        Assert.Throws<ArgumentException>(() => _triage.SetTriage(id, "urgent", null, null, Now));
    }

    [Fact]
    public void ListBoard_ExcludesFalseAlarm()
    {
        var keep = _events.Insert(1, "motion", Now);
        var drop = _events.Insert(1, "motion", Now.AddSeconds(1));
        _events.Insert(1, "motion", Now.AddSeconds(2));
        _events.SetDisposition(drop, AlarmEventStatus.FalseAlarm, null, null, Now);

        var rows = _triage.ListBoard(Now);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.EventId == drop);
        Assert.Contains(rows, r => r.EventId == keep);
    }

    [Fact]
    public void ListBoard_OrdersOverdueThenPriority()
    {
        var overdue = _events.Insert(1, "motion", Now.AddMinutes(-30));
        var critical = _events.Insert(1, "motion", Now.AddMinutes(-20));
        var high = _events.Insert(1, "motion", Now.AddMinutes(-10));

        _triage.SetTriage(overdue, AlarmPriority.Low, Now.AddMinutes(-5), null, Now);
        _triage.SetTriage(critical, AlarmPriority.Critical, null, null, Now);
        _triage.SetTriage(high, AlarmPriority.High, null, null, Now);

        var rows = _triage.ListBoard(Now);
        Assert.Equal(3, rows.Count);
        Assert.Equal(overdue, rows[0].EventId);
        Assert.True(rows[0].IsOverdue);
        Assert.Equal(critical, rows[1].EventId);
        Assert.Equal(high, rows[2].EventId);
    }

    [Fact]
    public void ListBoard_MarksOverdue()
    {
        var pending = _events.Insert(1, "motion", Now);
        var acked = _events.Insert(1, "motion", Now.AddSeconds(1));
        var actioned = _events.Insert(1, "motion", Now.AddSeconds(2));
        var noDue = _events.Insert(1, "motion", Now.AddSeconds(3));

        _triage.SetTriage(pending, AlarmPriority.Normal, Now.AddMinutes(-1), null, Now);
        _triage.SetTriage(acked, AlarmPriority.Normal, Now.AddMinutes(-1), null, Now);
        _triage.SetTriage(actioned, AlarmPriority.Normal, Now.AddMinutes(-1), null, Now);
        _triage.SetTriage(noDue, AlarmPriority.Normal, null, null, Now);
        _events.SetDisposition(acked, AlarmEventStatus.Acknowledged, null, null, Now);
        _events.SetDisposition(actioned, AlarmEventStatus.Actioned, null, null, Now);

        var rows = _triage.ListBoard(Now);
        Assert.True(rows.Single(r => r.EventId == pending).IsOverdue);
        Assert.True(rows.Single(r => r.EventId == acked).IsOverdue);
        Assert.False(rows.Single(r => r.EventId == actioned).IsOverdue);
        Assert.False(rows.Single(r => r.EventId == noDue).IsOverdue);
    }

    [Fact]
    public void Summarize_CountsByStatusAndOverdue()
    {
        var p1 = _events.Insert(1, "motion", Now);
        var p2 = _events.Insert(1, "motion", Now.AddSeconds(1));
        var ack = _events.Insert(1, "motion", Now.AddSeconds(2));
        var act = _events.Insert(1, "motion", Now.AddSeconds(3));
        var fal = _events.Insert(1, "motion", Now.AddSeconds(4));

        _events.SetDisposition(ack, AlarmEventStatus.Acknowledged, null, null, Now);
        _events.SetDisposition(act, AlarmEventStatus.Actioned, null, null, Now);
        _events.SetDisposition(fal, AlarmEventStatus.FalseAlarm, null, null, Now);
        _triage.SetTriage(ack, AlarmPriority.Normal, Now.AddMinutes(-1), null, Now);
        _triage.SetTriage(act, AlarmPriority.Normal, Now.AddMinutes(-1), null, Now);
        _triage.SetTriage(p2, AlarmPriority.High, Now.AddMinutes(-1), null, Now);

        var s = _triage.Summarize(Now);
        Assert.Equal(2, s.Pending);
        Assert.Equal(1, s.Acknowledged);
        Assert.Equal(1, s.Actioned);
        Assert.Equal(1, s.FalseAlarm);
        Assert.Equal(2, s.Overdue);
        Assert.Equal(AlarmPriority.High, _triage.Get(p2)!.Priority);
        Assert.Null(_triage.Get(p1));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }
}