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

    public void Dispose() => _store.Dispose();
}