using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 頻道編輯／刪除的資料庫不變式（M60 設定頁「頻道」）。
///
/// <para>
/// 這兩個方法原本只被 WPF 設定頁的「新增」用到，編輯與刪除長期沒有入口，
/// 也就沒有人發現它們其實沒有測試。刪除尤其危險：<c>channels</c> 是
/// <c>recording_schedule</c>／<c>detections</c>／<c>segments</c>／<c>alarm_events</c>／
/// <c>export_jobs</c> 的外鍵父表且全部 <c>ON DELETE CASCADE</c>。若 <c>PRAGMA foreign_keys</c>
/// 沒開，刪除會留下指向不存在頻道的孤兒列——回放清單、事件中心與匯出都會把它們當成真實資料，
/// 且不會有任何錯誤訊息。
/// </para>
/// </summary>
public sealed class ChannelRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;

    public ChannelRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-chan-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _channels = new ChannelRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 更新頻道會覆寫名稱串流與錄影設定()
    {
        var id = _channels.Add("Cam1", "rtsp://10.0.0.5/1", subRtsp: "rtsp://10.0.0.5/1s");

        _channels.Update(new ChannelInfo
        {
            Id = id,
            Name = "Cam1-renamed",
            MainStreamUrl = "rtsp://10.0.0.5/9",
            SubStreamUrl = "rtsp://10.0.0.5/9s",
            Codec = "h265",
            AudioEnabled = false,
            AudioEncoder = "aac",
            MotionEnabled = true,
            MotionSensitivity = 0.9,
            RecordingMode = RecordingMode.Event,
        });

        var got = _channels.Get(id);
        Assert.NotNull(got);
        Assert.Equal("Cam1-renamed", got.Name);
        Assert.Equal("rtsp://10.0.0.5/9", got.MainStreamUrl);
        Assert.Equal("rtsp://10.0.0.5/9s", got.SubStreamUrl);
        Assert.Equal("h265", got.Codec);
        Assert.False(got.AudioEnabled);
        Assert.Equal("aac", got.AudioEncoder);
        Assert.True(got.MotionEnabled);
        Assert.Equal(0.9, got.MotionSensitivity, 3);
        Assert.Equal(RecordingMode.Event, got.RecordingMode);
    }

    [Fact]
    public void 更新不存在的頻道不會新增資料列()
    {
        var before = _channels.List().Count;

        _channels.Update(new ChannelInfo
        {
            Id = 987654,
            Name = "ghost",
            MainStreamUrl = "rtsp://no/such",
        });

        Assert.Equal(before, _channels.List().Count);
        Assert.Null(_channels.Get(987654));
    }

    [Fact]
    public void 刪除頻道會移除頻道本身()
    {
        var id = _channels.Add("Doomed", "rtsp://10.0.0.5/1");

        _channels.Delete(id);

        Assert.Null(_channels.Get(id));
    }

    [Fact]
    public void 刪除頻道會串聯清除所有外鍵子表()
    {
        var id = _channels.Add("Doomed", "rtsp://10.0.0.5/1");

        // 各建一列，覆蓋每一張宣告 ON DELETE CASCADE 的子表。
        new RecordingScheduleRepository(_store).Upsert(new RecordingScheduleRecord
        {
            ChannelId = id,
            DaysMask = 127,
            StartMinute = 0,
            EndMinute = 60,
            Enabled = true,
        });
        new SegmentRepository(_store).BeginSegment(id, "main", "C:/seg/1.mp4", DateTime.UtcNow);
        new AlarmEventRepository(_store).Insert(id, "motion", DateTime.UtcNow);

        Assert.True(Count("recording_schedule", id) > 0);
        Assert.True(Count("segments", id) > 0);
        Assert.True(Count("alarm_events", id) > 0);

        _channels.Delete(id);

        // 孤兒列比刪除失敗更糟：它們會被回放／事件／匯出當成真實資料，且沒有錯誤可循。
        Assert.Equal(0, Count("recording_schedule", id));
        Assert.Equal(0, Count("segments", id));
        Assert.Equal(0, Count("alarm_events", id));
    }

    private int Count(string table, int channelId) =>
        _store.Query(
            $"SELECT COUNT(*) FROM {table} WHERE channel_id = $id;",
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd => cmd.Parameters.AddWithValue("$id", channelId));
}
