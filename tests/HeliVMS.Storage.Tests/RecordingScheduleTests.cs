using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class RecordingScheduleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly RecordingScheduleRepository _repo;

    public RecordingScheduleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-sched-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new RecordingScheduleRepository(_store);
        SeedChannels();
    }

    private void SeedChannels()
    {
        var channels = new ChannelRepository(_store);
        if (channels.List().Count == 0)
        {
            channels.Add("SchedA", "rtsp://localhost/sched");
            channels.Add("SchedB", "rtsp://localhost/sched2");
        }
    }

    [Fact]
    public void Upsert_InsertsAndUpdates()
    {
        var row = _repo.Upsert(new RecordingScheduleRecord
        {
            ChannelId = 1,
            DaysMask = 2,
            StartMinute = 60,
            EndMinute = 120,
            Enabled = true,
        });

        Assert.True(row.Id > 0);
        var list = _repo.ListByChannel(1);
        Assert.Single(list);
        Assert.Equal(2, list[0].DaysMask);
        Assert.Equal(60, list[0].StartMinute);
        Assert.Equal(120, list[0].EndMinute);
        Assert.True(list[0].Enabled);

        _repo.Upsert(new RecordingScheduleRecord
        {
            Id = row.Id,
            ChannelId = 1,
            DaysMask = 4,
            StartMinute = 300,
            EndMinute = 400,
            Enabled = false,
        });

        var updated = Assert.Single(_repo.ListByChannel(1));
        Assert.Equal(4, updated.DaysMask);
        Assert.Equal(300, updated.StartMinute);
        Assert.False(updated.Enabled);
        Assert.Equal(row.Id, updated.Id);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var row = _repo.Upsert(new RecordingScheduleRecord
        {
            ChannelId = 1,
            DaysMask = 1,
            StartMinute = 0,
            EndMinute = 10,
        });

        _repo.Delete(row.Id);
        Assert.Empty(_repo.ListByChannel(1));
    }

    [Fact]
    public void DeleteByChannel_RemovesGroup()
    {
        var channelId = new ChannelRepository(_store).Add("ToDelete", "rtsp://x/y");
        _repo.Upsert(new RecordingScheduleRecord { ChannelId = channelId, DaysMask = 127, StartMinute = 0, EndMinute = 60 });

        _repo.DeleteByChannel(channelId);
        Assert.Empty(_repo.ListByChannel(channelId));
    }

    [Theory]
    [InlineData("2026-09-13T10:00:00", 127, 600, 660, true)]   // 週日 10:00 全週遮罩 10:00-11:00
    [InlineData("2026-09-13T12:00:00", 127, 600, 660, false)]  // 11:00 後
    [InlineData("2026-09-13T09:59:00", 127, 600, 660, false)]  // 10:00 前
    public void IsActiveAt_WithinWindow(string isoLocal, int mask, int startMin, int endMin, bool expected)
    {
        var local = DateTime.Parse(isoLocal);
        var rec = new RecordingScheduleRecord
        {
            ChannelId = 1,
            DaysMask = mask,
            StartMinute = startMin,
            EndMinute = endMin,
            Enabled = true,
        };

        Assert.Equal(expected, rec.IsActiveAt(local));
    }

    [Fact]
    public void IsActiveAt_CrossMidnight_UsesPreviousDayBit()
    {
        // 22:00-06:00 跨午夜
        var rec = new RecordingScheduleRecord
        {
            ChannelId = 1,
            DaysMask = 1 << (int)DayOfWeek.Saturday,   // 僅週六
            StartMinute = 22 * 60,
            EndMinute = 6 * 60,
            Enabled = true,
        };

        // 週六 23:00（0278）→ 合（週六 bit）
        Assert.True(rec.IsActiveAt(new DateTime(2026, 9, 12, 23, 0, 0))); // 2026-09-12 為週六
        // 週日 03:00 → 承接週六 bit，合
        Assert.True(rec.IsActiveAt(new DateTime(2026, 9, 13, 3, 0, 0)));
        // 週日 12:00 → 不在時段
        Assert.False(rec.IsActiveAt(new DateTime(2026, 9, 13, 12, 0, 0)));
        // 週日 05:00 → 週日非週六 bit，且昨天（週六）有 bit → 合；但週日 05:00 之昨天為週六
        Assert.True(rec.IsActiveAt(new DateTime(2026, 9, 13, 5, 0, 0)));
    }

    [Fact]
    public void IsActiveAt_Disabled_AlwaysFalse()
    {
        var rec = new RecordingScheduleRecord
        {
            ChannelId = 1,
            DaysMask = 127,
            StartMinute = 0,
            EndMinute = 1439,
            Enabled = false,
        };

        Assert.False(rec.IsActiveAt(new DateTime(2026, 9, 13, 12, 0, 0)));
    }

    [Fact]
    public void IsActiveAt_DayMask_GatesByDay()
    {
        var rec = new RecordingScheduleRecord
        {
            ChannelId = 1,
            DaysMask = 1 << (int)DayOfWeek.Monday,
            StartMinute = 0,
            EndMinute = 1439,
            Enabled = true,
        };

        // 2026-09-14 為週一
        Assert.True(rec.IsActiveAt(new DateTime(2026, 9, 14, 8, 0, 0)));
        // 2026-09-13 為週日
        Assert.False(rec.IsActiveAt(new DateTime(2026, 9, 13, 8, 0, 0)));
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
        File.Delete(_dbPath + "-wal");
        File.Delete(_dbPath + "-shm");
    }
}