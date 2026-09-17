using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class AlarmEventRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public AlarmEventRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ev-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlarmEventRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "事件頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/ev");
            });
    }

    [Fact]
    public void InsertAndList_RoundTrip()
    {
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 1, 8, 0, 30, DateTimeKind.Utc);

        var id = _repo.Insert(1, "motion", t0, null, "duration=800ms peak=65%");
        _repo.UpdateEnd(id, t1, null);

        var rows = _repo.ListByRange(null, t0.AddHours(-1), t1.AddHours(1));
        var row = Assert.Single(rows);
        Assert.Equal("motion", row.EventType);
        Assert.Equal(t0, row.StartUtc);
        Assert.Equal(t1, row.EndUtc);
        Assert.Equal("duration=800ms peak=65%", row.Detail);
        Assert.False(row.Acknowledged);
    }

    [Fact]
    public void ListByRange_FiltersChannelAndWindow()
    {
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "第二頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/ev2");
            });
        var t0 = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        _repo.Insert(1, "motion", t0);
        _repo.Insert(1, "offline", t0.AddMinutes(5));
        _repo.Insert(2, "motion", t0.AddMinutes(10));

        // 第二頻道
        var ch2 = _repo.ListByRange(2, DateTime.MinValue, DateTime.MaxValue);
        Assert.Single(ch2);
        Assert.Equal("motion", ch2[0].EventType);

        // 時間窗涵蓋 through pick
        var windowed = _repo.ListByRange(1, t0, t0.AddMinutes(4));
        var one = Assert.Single(windowed);
        Assert.Equal("motion", one.EventType);

        // 全頻道由新到舊
        var all = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(3, all.Count);
        Assert.True(all[0].StartUtc > all[1].StartUtc);
    }

    [Fact]
    public void Acknowledge_PersistsFlag()
    {
        var id = _repo.Insert(1, "motion", DateTime.UtcNow);
        _repo.Acknowledge(id, true);

        var rows = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
        Assert.True(Assert.Single(rows).Acknowledged);

        _repo.Acknowledge(id, false);
        var rows2 = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
        Assert.False(Assert.Single(rows2).Acknowledged);
    }

    [Fact]
    public void CountUnacknowledged_CountsOnlyUnacked()
    {
        var id1 = _repo.Insert(1, "motion", DateTime.UtcNow);
        var id2 = _repo.Insert(1, "ai_person", DateTime.UtcNow);
        var id3 = _repo.Insert(1, "offline", DateTime.UtcNow);

        _repo.Acknowledge(id3, true);

        Assert.Equal(2, _repo.CountUnacknowledged());
        _repo.Acknowledge(id1, true);
        _repo.Acknowledge(id2, true);
        Assert.Equal(0, _repo.CountUnacknowledged());
    }

    private void CreateChannel(string name, string rtsp)
    {
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$m", rtsp);
            });
    }

    [Fact]
    public void ListByQuery_FiltersAndPaginates()
    {
        CreateChannel("第二頻道", "rtsp://127.0.0.1:8554/ev2");
        var t0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            _repo.Insert(1, "motion", t0.AddSeconds(10 * i));
        }

        _repo.Insert(2, "offline", t0.AddMinutes(1));
        _repo.Insert(2, "motion", t0.AddMinutes(2));

        // 全部 motion（頻道不限）＝6
        var allMotion = _repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            EventType = "motion",
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
        });
        Assert.Equal(6, allMotion.Count);
        Assert.All(allMotion, e => Assert.Equal("motion", e.EventType));

        // 頻道＋類型
        var ch2Offline = _repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            ChannelId = 2,
            EventType = "offline",
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
        });
        Assert.Single(ch2Offline);

        // 分頁：第 1 頁 4 筆取最新的
        var page1 = _repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            EventType = "motion",
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
            Limit = 4,
            Offset = 0,
        });
        Assert.Equal(4, page1.Count);
        Assert.True(page1[0].StartUtc > page1[3].StartUtc);

        var page2 = _repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            EventType = "motion",
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
            Limit = 4,
            Offset = 4,
        });
        Assert.Equal(2, page2.Count);
    }

    [Fact]
    public void CountByQuery_MatchesFilter()
    {
        CreateChannel("第二頻道", "rtsp://127.0.0.1:8554/ev2");
        var t0 = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        _repo.Insert(1, "motion", t0);
        _repo.Insert(1, "motion", t0.AddSeconds(1));
        _repo.Insert(2, "offline", t0.AddSeconds(2));

        var motionCount = _repo.CountByQuery(new AlarmEventRepository.QueryArgs
        {
            EventType = "motion",
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
        });
        Assert.Equal(2, motionCount);

        var ch2Count = _repo.CountByQuery(new AlarmEventRepository.QueryArgs
        {
            ChannelId = 2,
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
        });
        Assert.Equal(1, ch2Count);
    }

    [Fact]
    public void FindOpenOffline_NoneThenFoundThenClosed()
    {
        Assert.Null(_repo.FindOpenOffline(1));
        var id = _repo.Insert(1, "offline", DateTime.UtcNow, null, "connection lost");
        var open = _repo.FindOpenOffline(1);
        Assert.NotNull(open);
        Assert.Equal(id, open.Id);

        _repo.UpdateEnd(id, DateTime.UtcNow.AddMinutes(1), null);
        Assert.Null(_repo.FindOpenOffline(1));
    }

    [Fact]
    public void CloseOpenEvents_ClosesAllOpen()
    {
        var t0 = DateTime.UtcNow;
        _repo.Insert(1, "offline", t0, null, "lost");
        var motionId = _repo.Insert(1, "motion", t0.AddMinutes(1));

        _repo.CloseOpenEvents(t0.AddMinutes(2));

        Assert.Null(_repo.FindOpenOffline(1));
        var row = _repo.ListByRange(1, t0, t0.AddMinutes(5)).First(e => e.Id == motionId);
        Assert.NotNull(row.EndUtc);
    }

    [Fact]
    public void ListEventTypes_ReturnsDistinctSorted()
    {
        CreateChannel("第二頻道", "rtsp://127.0.0.1:8554/ev2");
        _repo.Insert(1, "offline", DateTime.UtcNow);
        _repo.Insert(1, "motion", DateTime.UtcNow);
        _repo.Insert(1, "motion", DateTime.UtcNow);
        _repo.Insert(2, "ai_person", DateTime.UtcNow);

        var types = _repo.ListEventTypes();
        Assert.Equal(new[] { "ai_person", "motion", "offline" }, types);
    }

    [Fact]
    public void SetDisposition_DefaultsPending_ThenPersistsAndTrails()
    {
        var id = _repo.Insert(1, "motion", DateTime.UtcNow);
        var initial = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(AlarmEventStatus.Pending, initial.Status);
        Assert.Empty(_repo.ListDispositionTrail(id));

        var t1 = new DateTime(2026, 4, 1, 1, 0, 0, DateTimeKind.Utc);
        _repo.SetDisposition(id, AlarmEventStatus.Actioned, "alice", "已派工處理", t1);

        var row = Assert.Single(_repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
        }));
        Assert.Equal(AlarmEventStatus.Actioned, row.Status);
        Assert.Equal("alice", row.AssignedTo);
        Assert.Equal("已派工處理", row.Note);
        Assert.True(row.Acknowledged);

        var entry = Assert.Single(_repo.ListDispositionTrail(id));
        Assert.Equal(AlarmEventStatus.Actioned, entry.Status);
        Assert.Equal("alice", entry.AssignedTo);
        Assert.Equal(t1, entry.ChangedAt);
    }

    [Fact]
    public void SetDisposition_AppendsTrail_AndQueryFiltersByStatus()
    {
        var id1 = _repo.Insert(1, "motion", DateTime.UtcNow);
        var id2 = _repo.Insert(1, "offline", DateTime.UtcNow);
        _repo.SetDisposition(id1, AlarmEventStatus.Actioned, null, null, DateTime.UtcNow);
        _repo.SetDisposition(id1, AlarmEventStatus.FalseAlarm, "bob", "誤報", DateTime.UtcNow.AddMinutes(1));
        _repo.SetDisposition(id2, AlarmEventStatus.Acknowledged, null, null, DateTime.UtcNow);

        Assert.Equal(2, _repo.ListDispositionTrail(id1).Count);
        Assert.Single(_repo.ListDispositionTrail(id2));
        Assert.Equal(AlarmEventStatus.Actioned, _repo.ListDispositionTrail(id1)[0].Status);
        Assert.Equal(AlarmEventStatus.FalseAlarm, _repo.ListDispositionTrail(id1)[1].Status);

        var q = new AlarmEventRepository.QueryArgs
        {
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
            Status = AlarmEventStatus.FalseAlarm,
        };
        var falseAlarms = Assert.Single(_repo.ListByQuery(q));
        Assert.Equal(id1, falseAlarms.Id);
        Assert.Equal(1, _repo.CountByQuery(q));

        var ack = Assert.Single(_repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
            Status = AlarmEventStatus.Acknowledged,
        }));
        Assert.Equal(id2, ack.Id);
    }

    [Fact]
    public void SetDisposition_PendingClearsAcknowledged_AndRejectsInvalidStatus()
    {
        var id = _repo.Insert(1, "motion", DateTime.UtcNow);
        _repo.SetDisposition(id, AlarmEventStatus.Actioned, "a", "n", DateTime.UtcNow);
        _repo.SetDisposition(id, AlarmEventStatus.Pending, null, null, DateTime.UtcNow.AddMinutes(1));

        var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(AlarmEventStatus.Pending, row.Status);
        Assert.False(row.Acknowledged);
        Assert.Null(row.AssignedTo);
        Assert.Null(row.Note);
        Assert.Equal(2, _repo.ListDispositionTrail(id).Count);

        Assert.Throws<ArgumentException>(() =>
            _repo.SetDisposition(id, "bogus", null, null, DateTime.UtcNow));
    }

    [Fact]
    public void Acknowledge_SyncsStatusAndTrail_AndKeepsAssignment()
    {
        var id = _repo.Insert(1, "motion", DateTime.UtcNow);
        _repo.SetDisposition(id, AlarmEventStatus.Actioned, "alice", "處理中", DateTime.UtcNow);

        _repo.Acknowledge(id, true);
        var acked = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(AlarmEventStatus.Acknowledged, acked.Status);
        Assert.Equal("alice", acked.AssignedTo);
        Assert.True(acked.Acknowledged);
        Assert.Equal(2, _repo.ListDispositionTrail(id).Count);

        _repo.Acknowledge(id, false);
        var pending = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(AlarmEventStatus.Pending, pending.Status);
        Assert.False(pending.Acknowledged);
    }

    public void Dispose() => _store.Dispose();
}