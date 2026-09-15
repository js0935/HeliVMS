using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

public sealed class OfflineEventTrackerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;
    private readonly OfflineEventTracker _tracker;

    public OfflineEventTrackerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-offline-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _store.Execute(
            """
            INSERT INTO channels (id, device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (7, NULL, '離線頻道', 'rtsp://127.0.0.1:8554/off', NULL, 'h264', 1, 'copy');
            """);
        _repo = new AlarmEventRepository(_store);
        _tracker = new OfflineEventTracker(_repo);
    }

    [Fact]
    public void MarkOffline_RepeatedCalls_OnlyOneOpen()
    {
        _tracker.MarkOffline(7);
        _tracker.MarkOffline(7);
        _tracker.MarkOffline(7);

        var open = _repo.FindOpenOffline(7);
        Assert.NotNull(open);
        var items = ListByType(7, "offline");
        Assert.Single(items);
        Assert.Equal("connection lost", items[0].Detail);
        Assert.Null(items[0].EndUtc);
    }

    [Fact]
    public void MarkOnline_ClosesOfflineAndWritesOnlineWithDuration()
    {
        _tracker.MarkOffline(7);
        Assert.True(_tracker.MarkOnline(7));

        Assert.Null(_repo.FindOpenOffline(7));
        var online = ListByType(7, "online");
        var item = Assert.Single(online);
        Assert.StartsWith("offline_duration=", item.Detail);
        var suffix = item.Detail!["offline_duration=".Length..];
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", suffix);

        var offline = ListByType(7, "offline");
        Assert.NotNull(Assert.Single(offline).EndUtc);
    }

    [Fact]
    public void MarkOnline_WithoutOpenOffline_Noop()
    {
        Assert.False(_tracker.MarkOnline(7));
        Assert.Empty(ListByType(7, "online"));
    }

    [Fact]
    public void CloseOpenAtStartup_ClosesWithoutFakeOnline()
    {
        _tracker.MarkOffline(7);
        _tracker.CloseOpenAtStartup();

        Assert.Null(_repo.FindOpenOffline(7));
        Assert.Empty(ListByType(7, "online"));
        var offline = ListByType(7, "offline");
        Assert.NotNull(Assert.Single(offline).EndUtc);
    }

    private List<AlarmEventRecord> ListByType(int channelId, string eventType)
        => _repo.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            ChannelId = channelId,
            EventType = eventType,
            FromUtc = DateTime.MinValue,
            ToUtc = DateTime.MaxValue,
        }).ToList();

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }
}