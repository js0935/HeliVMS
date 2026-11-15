using HeliVMS.Shared;
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

    /// <summary>
    /// 同一個離線期只回報一次。解碼端每輪重試都會發一次 Reconnecting，若每一輪都回報，
    /// 事件中心與推播會在幾分鐘內被同一次斷線洗版（而且牆上事件面板只有 20 列）。
    /// </summary>
    [Fact]
    public void 同一個離線期只回報一次()
    {
        var first = _tracker.MarkOffline(7);
        Assert.NotNull(first);
        Assert.Null(_tracker.MarkOffline(7));
        Assert.Null(_tracker.MarkOffline(7));

        Assert.NotNull(_repo.FindOpenOffline(7));
        var items = ListByType(7, EventTaxonomy.Offline);
        Assert.Single(items);
        Assert.Equal("connection lost", items[0].Detail);
        Assert.Null(items[0].EndUtc);
    }

    /// <summary>
    /// 回報出去的那一筆必須就是資料庫裡的那一筆：呼叫端拿它去推播、送 CMS 回報，
    /// 若 id／時間／明細與資料列不一致，事件中心點那一列會查到另一個事件。
    /// </summary>
    [Fact]
    public void 回報的事件內容與資料列一致()
    {
        var offline = _tracker.MarkOffline(7);
        Assert.NotNull(offline);

        var row = Assert.Single(ListByType(7, EventTaxonomy.Offline));
        Assert.Equal(row.Id, offline!.Id);
        Assert.Equal(row.ChannelId, offline.ChannelId);
        Assert.Equal(row.EventType, offline.EventType);
        Assert.Equal(row.StartUtc, offline.StartUtc);
        Assert.Equal(row.Detail, offline.Detail);

        var online = _tracker.MarkOnline(7);
        Assert.NotNull(online);

        var onlineRow = Assert.Single(ListByType(7, EventTaxonomy.Online));
        Assert.Equal(onlineRow.Id, online!.Id);
        Assert.Equal(onlineRow.EventType, online.EventType);
        Assert.Equal(onlineRow.Detail, online.Detail);
        Assert.StartsWith("offline_duration=", online.Detail);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", online.Detail!["offline_duration=".Length..]);

        // 離線那一筆已被收斂：時間窗有結束時刻，才畫得出「離線持續多久」。
        Assert.NotNull(Assert.Single(ListByType(7, EventTaxonomy.Offline)).EndUtc);
    }

    /// <summary>
    /// 沒有開窗離線事件時復連不寫任何東西、也不回報。啟動後第一次連上攝影機就是這個情形；
    /// 舊版會補一筆 duration=00:00:00 的假上線，等於每次開機都記一次沒發生過的斷線。
    /// </summary>
    [Fact]
    public void 沒有開窗時復連不回報也不寫列()
    {
        Assert.Null(_tracker.MarkOnline(7));
        Assert.Empty(ListByType(7, EventTaxonomy.Online));
        Assert.Empty(ListByType(7, EventTaxonomy.Offline));
    }

    [Fact]
    public void 啟動收斂開窗事件但不補上線()
    {
        _tracker.MarkOffline(7);
        _tracker.CloseOpenAtStartup();

        Assert.Null(_repo.FindOpenOffline(7));
        Assert.Empty(ListByType(7, EventTaxonomy.Online));
        var offline = ListByType(7, EventTaxonomy.Offline);
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
