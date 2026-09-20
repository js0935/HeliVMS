using HeliVMS.Shared.Models;

namespace HeliVMS.Storage.Tests;

/// <summary>M60（§14.7 #9）：統圖報表唯讀查詢。</summary>
public class ReportRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly int _ch1;
    private readonly int _ch2;

    public ReportRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-report-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'cam1', 'rtsp://127.0.0.1:8554/a', NULL, 'h264', 1, 'copy'),
                   (NULL, 'cam2', 'rtsp://127.0.0.1:8554/b', NULL, 'h264', 1, 'copy');
            """);
        _ch1 = _store.Query("SELECT id FROM channels WHERE name='cam1';", static r => r.Read() ? r.GetInt32(0) : 0);
        _ch2 = _store.Query("SELECT id FROM channels WHERE name='cam2';", static r => r.Read() ? r.GetInt32(0) : 0);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static void InsertSegment(SqliteStore store, int channelId, string startIso, string endIso, long bytes, double sec, string status = "final")
    {
        store.Execute(
            """
            INSERT INTO segments (channel_id, stream, start_time, end_time, file_path, size_bytes, duration_sec, status)
            VALUES ($c, 'main', $s, $e, $f, $b, $d, $st);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", startIso);
                cmd.Parameters.AddWithValue("$e", endIso);
                cmd.Parameters.AddWithValue("$f", $"C:/seg-{Guid.NewGuid():N}.mp4");
                cmd.Parameters.AddWithValue("$b", bytes);
                cmd.Parameters.AddWithValue("$d", sec);
                cmd.Parameters.AddWithValue("$st", status);
            });
    }

    private static void InsertEvent(SqliteStore store, int channelId, string eventType, string startIso)
    {
        store.Execute(
            """
            INSERT INTO alarm_events (channel_id, event_type, start_time)
            VALUES ($c, $t, $s);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$t", eventType);
                cmd.Parameters.AddWithValue("$s", startIso);
            });
    }

    [Fact]
    public void RecordingSummary_AggregatesPerChannel()
    {
        InsertSegment(_store, _ch1, "2026-09-01T10:00:00Z", "2026-09-01T10:30:00Z", 900, 1800);   // 0.5h
        InsertSegment(_store, _ch1, "2026-09-01T11:00:00Z", "2026-09-01T12:00:00Z", 1800, 3600);  // 1.0h
        InsertSegment(_store, _ch2, "2026-09-01T09:00:00Z", "2026-09-01T10:00:00Z", 1200, 3600);  // 1.0h
        InsertSegment(_store, _ch2, "2026-09-02T09:00:00Z", "2026-09-02T10:00:00Z", 1200, 3600, status: "tmp"); // 排除

        var rows = new ReportRepository(_store)
            .ListRecordingSummary(DateTime.Parse("2026-09-01T00:00:00Z").ToUniversalTime(), DateTime.Parse("2026-09-02T00:00:00Z").ToUniversalTime());

        Assert.Equal(2, rows.Count);
        Assert.Equal(1.5, rows[0].Hours, 2);
        Assert.Equal(2700, rows[0].Bytes);
        Assert.Equal("cam2", rows[1].ChannelName);
        Assert.Equal(1.0, rows[1].Hours, 2);
    }

    [Fact]
    public void RecordingSummary_OutsideRange_Excluded()
    {
        InsertSegment(_store, _ch1, "2026-08-31T23:59:59Z", "2026-09-01T00:00:00Z", 100, 10);
        InsertSegment(_store, _ch1, "2026-09-02T00:00:01Z", "2026-09-02T00:10:00Z", 100, 600);

        var rows = new ReportRepository(_store)
            .ListRecordingSummary(DateTime.Parse("2026-09-01T00:00:00Z").ToUniversalTime(), DateTime.Parse("2026-09-02T00:00:00Z").ToUniversalTime());

        Assert.All(rows, r => Assert.Equal(0, r.Hours));
        Assert.All(rows, r => Assert.Equal(0, r.Bytes));
    }

    [Fact]
    public void RecordingSummary_NoSegments_ZeroPerChannel()
    {
        var rows = new ReportRepository(_store)
            .ListRecordingSummary(DateTime.Parse("2026-01-01T00:00:00Z").ToUniversalTime(), DateTime.Parse("2026-12-31T00:00:00Z").ToUniversalTime());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Hours));
        Assert.All(rows, r => Assert.Equal(0, r.Bytes));
    }

    [Fact]
    public void CapacityTrend_GroupsByDay()
    {
        InsertSegment(_store, _ch1, "2026-09-01T10:00:00Z", "2026-09-01T10:30:00Z", 1000, 1800);
        InsertSegment(_store, _ch2, "2026-09-01T11:00:00Z", "2026-09-01T11:10:00Z", 2000, 600);
        InsertSegment(_store, _ch1, "2026-09-02T10:00:00Z", "2026-09-02T11:00:00Z", 3000, 3600);
        InsertSegment(_store, _ch1, "2026-09-03T10:00:00Z", "2026-09-03T10:30:00Z", 4000, 1800, status: "tmp");

        var rows = new ReportRepository(_store)
            .ListCapacityTrend(DateTime.Parse("2026-09-01T00:00:00Z").ToUniversalTime(), DateTime.Parse("2026-09-03T00:00:00Z").ToUniversalTime());

        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-09-01", rows[0].Day);
        Assert.Equal(3000, rows[0].Bytes);
        Assert.Equal(0.6667, rows[0].Hours, 3);
        Assert.Equal("2026-09-02", rows[1].Day);
        Assert.Equal(3000, rows[1].Bytes);
    }

    [Fact]
    public void DisconnectCount_CountsOnlyOfflineInRange()
    {
        InsertEvent(_store, _ch1, "offline", "2026-09-01T08:00:00Z");
        InsertEvent(_store, _ch1, "offline", "2026-09-01T09:00:00Z");
        InsertEvent(_store, _ch1, "online", "2026-09-01T09:30:00Z");     // 不算
        InsertEvent(_store, _ch1, "offline", "2026-08-31T23:00:00Z");    // 窗外不算

        var count = new ReportRepository(_store)
            .GetDisconnectCount(DateTime.Parse("2026-09-01T00:00:00Z").ToUniversalTime(), DateTime.Parse("2026-09-02T00:00:00Z").ToUniversalTime());

        Assert.Equal(2, count);
    }

    [Fact]
    public void AiEventSummary_GroupsByType_OrderedByCount()
    {
        InsertEvent(_store, _ch1, "motion", "2026-09-01T08:00:00Z");
        InsertEvent(_store, _ch1, "motion", "2026-09-01T09:00:00Z");
        InsertEvent(_store, _ch2, "ai_intrusion", "2026-09-01T09:30:00Z");
        InsertEvent(_store, _ch1, "ai_line_cross", "2026-09-01T10:00:00Z");
        InsertEvent(_store, _ch1, "motion", "2026-08-31T23:59:00Z");     // 窗外不算

        var rows = new ReportRepository(_store)
            .ListAiEventSummary(DateTime.Parse("2026-09-01T00:00:00Z").ToUniversalTime(), DateTime.Parse("2026-09-02T00:00:00Z").ToUniversalTime());

        Assert.Equal(3, rows.Count);
        Assert.Equal("motion", rows[0].EventType);
        Assert.Equal(2, rows[0].Count);
        Assert.Contains(rows, r => r.EventType == "ai_intrusion" && r.Count == 1);
        Assert.Contains(rows, r => r.EventType == "ai_line_cross" && r.Count == 1);
    }
}