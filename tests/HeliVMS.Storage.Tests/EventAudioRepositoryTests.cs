using System;
using System.Linq;
using System.Security.Cryptography;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>事件錄音資料面（M157，§14.7 事件錄音資料側）。</summary>
public sealed class EventAudioRepositoryTests
{
    private static readonly string[] Mimes = { "audio/ogg", "audio/wav", "application/octet-stream" };

    private static (SqliteStore Store, EventAudioRepository Repo) NewFixture()
    {
        var store = new SqliteStore(Path.Combine(Path.GetTempPath(), $"hv-eaa-{Guid.NewGuid():N}.db"));
        store.Initialize();
        return (store, new EventAudioRepository(store));
    }

    private static byte[] Bytes(int seed)
        => new byte[] { (byte)(seed % 256), (byte)(seed * 7 % 256), 0xAA, 0x55, 0x00, 0xFF, 0x01, 0x02 };

    [Fact]
    public void Save_GetByEvent_Roundtrips()
    {
        var (_, repo) = NewFixture();
        var when = new DateTime(2026, 2, 1, 3, 4, 5, DateTimeKind.Utc);
        var payload = Bytes(3);

        repo.Save(11, 2, when, 3500, "audio/ogg", payload);

        var got = repo.GetByEvent(11);
        Assert.NotNull(got);
        Assert.Equal(11, got.EventId);
        Assert.Equal(2, got.ChannelId);
        Assert.Equal(when, got.StartedAtUtc);
        Assert.Equal(3500, got.DurationMs);
        Assert.Equal("audio/ogg", got.Mime);
        Assert.Equal(payload, got.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), got.Sha256);
    }

    [Fact]
    public void Save_UpsertsOnSameEvent()
    {
        var (store, repo) = NewFixture();
        var when = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.Save(5, 1, when, 1000, "audio/wav", Bytes(1));
        repo.Save(5, 2, when.AddMinutes(1), 2000, "audio/ogg", Bytes(2));

        var got = repo.GetByEvent(5);
        Assert.NotNull(got);
        Assert.Equal(2000, got.DurationMs);
        Assert.Equal(2, got.ChannelId);
        Assert.Equal(Bytes(2), got.Bytes);
        Assert.Equal(1, store.Query<long>("SELECT COUNT(*) FROM event_audio;", r => r.Read() ? r.GetInt64(0) : 0));
    }

    [Fact]
    public void QueryByTime_FiltersAndOrders()
    {
        var (_, repo) = NewFixture();
        var baseTime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.Save(1, 1, baseTime.AddHours(2), 1000, "audio/ogg", Bytes(1));
        repo.Save(2, 1, baseTime.AddHours(3), 1000, "audio/ogg", Bytes(2));
        repo.Save(3, 1, baseTime.AddHours(5), 1000, "audio/ogg", Bytes(3));

        var rows = repo.QueryByTime(baseTime.AddHours(2), baseTime.AddHours(4));

        Assert.Equal(2, rows.Count);
        Assert.Equal(new long[] { 1, 2 }, rows.Select(r => r.EventId).ToArray());
        Assert.StartsWith("2026-02-01T02:00", SqliteStore.Iso(rows[0].StartedAtUtc));
    }

    [Fact]
    public void PruneOlderThan_RemovesOnlyOld()
    {
        var (_, repo) = NewFixture();
        var cut = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.Save(1, 1, cut.AddDays(-2), 500, "audio/ogg", Bytes(1));
        repo.Save(2, 1, cut.AddDays(1), 500, "audio/ogg", Bytes(2));

        var removed = repo.PruneOlderThan(cut);

        Assert.Equal(1, removed);
        Assert.Null(repo.GetByEvent(1));
        Assert.NotNull(repo.GetByEvent(2));
    }

    [Fact]
    public void InvalidInput_Throws()
    {
        var (_, repo) = NewFixture();
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.ThrowsAny<ArgumentException>(() => repo.Save(1, 1, when, 0, "audio/ogg", Bytes(1)));
        Assert.ThrowsAny<ArgumentException>(() => repo.Save(0, 1, when, 100, "audio/ogg", Bytes(1)));
        Assert.ThrowsAny<ArgumentException>(() => repo.Save(1, 1, when, 100, "audio/ogg", Array.Empty<byte>()));
        Assert.ThrowsAny<ArgumentException>(() => repo.Save(1, 1, when, 100, "", Bytes(1)));
    }
}