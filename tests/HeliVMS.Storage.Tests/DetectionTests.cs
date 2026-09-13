using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class DetectionTests : IDisposable
{
    private readonly SqliteStore _store;
    private readonly DetectionRepository _repo;
    private readonly string _dbPath;

    public DetectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-det-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new DetectionRepository(_store);
        var ch = new ChannelRepository(_store);
        Ch1 = ch.Add("CAM1", "rtsp://x/1");
        Ch2 = ch.Add("CAM2", "rtsp://x/2");
    }

    private readonly int Ch1;
    private readonly int Ch2;
    private DateTime BaseUtc => new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

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

    private DetectionRecord D(int ch, string cls, float conf, int minuteOffset, float x = 0.5f, float y = 0.5f)
        => new()
        {
            ChannelId = ch,
            Class = cls,
            Confidence = conf,
            X = x,
            Y = y,
            W = 0.2f,
            H = 0.4f,
            DetectedUtc = BaseUtc.AddMinutes(minuteOffset),
        };

    [Fact]
    public void Upsert查詢_新增多筆偵測可按條件取得()
    {
        _repo.AddBatch(new[] { D(Ch1, "person", 0.91f, 0), D(Ch1, "car", 0.62f, 1), D(Ch2, "person", 0.55f, 2) });

        var all = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Equal(3, all.Count);

        var onlyCh1 = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            ChannelId = Ch1,
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Equal(2, onlyCh1.Count);
        Assert.All(onlyCh1, r => Assert.Equal(Ch1, r.ChannelId));

        var persons = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            Class = "person",
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Equal(2, persons.Count);

        var high = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            MinConfidence = 0.9f,
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Single(high);
        Assert.Equal("person", high[0].Class);
    }

    [Fact]
    public void 查詢按時間範圍與降冪排序()
    {
        _repo.AddBatch(new[] { D(Ch1, "car", 0.8f, 0), D(Ch1, "car", 0.8f, 5), D(Ch1, "car", 0.8f, 10) });

        var inRange = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(4),
            ToUtc = BaseUtc.AddMinutes(8),
        });
        Assert.Single(inRange);
        Assert.Equal(BaseUtc.AddMinutes(5), inRange[0].DetectedUtc);

        var desc = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(20),
        });
        Assert.Equal(10, desc[0].DetectedUtc.Minute);
    }

    [Fact]
    public void 統計_依類別分組計數()
    {
        _repo.AddBatch(new[]
        {
            D(Ch1, "person", 0.9f, 0),
            D(Ch1, "person", 0.8f, 1),
            D(Ch1, "car", 0.7f, 2),
            D(Ch2, "person", 0.6f, 3),
        });

        var stats = _repo.CountByClass(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Equal(2, stats.Count);
        Assert.Equal(("person", 3), (stats[0].Class, stats[0].Count));
        Assert.Equal(("car", 1), (stats[1].Class, stats[1].Count));

        var ch2Stats = _repo.CountByClass(new DetectionRepository.QueryArgs
        {
            ChannelId = Ch2,
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Equal(("person", 1), (ch2Stats.Single().Class, ch2Stats.Single().Count));
    }

    [Fact]
    public void 刪除_按時間清掉舊偵測()
    {
        _repo.AddBatch(new[] { D(Ch1, "car", 0.8f, 0), D(Ch1, "car", 0.8f, 5) });

        var deleted = _repo.DeleteBefore(BaseUtc.AddMinutes(3));
        Assert.Equal(1, deleted);

        var rest = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-10),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Single(rest);
    }

    [Fact]
    public void 批次上限_單筆交易不超過指定大小()
    {
        var many = Enumerable.Range(0, 1000).Select(i => D(Ch1, "person", 0.8f, i % 50, x: i % 10 / 10f)).ToList();
        _repo.AddBatch(many);

        var all = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(60),
            Limit = 2000,
        });
        Assert.Equal(1000, all.Count);
    }

    [Fact]
    public void 寫入管線_佇列批次刷入資料庫()
    {
        using var writer = new DetectionWriter(_store, flushInterval: TimeSpan.FromHours(1), batchSize: 16);
        for (var i = 0; i < 40; i++)
        {
            writer.Enqueue(D(Ch1, "person", 0.75f, i));
        }

        Assert.Equal(40, writer.PendingCount);
        writer.Flush();
        Assert.Equal(0, writer.PendingCount);

        var all = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(60),
        });
        Assert.Equal(40, all.Count);
    }

    [Fact]
    public void 寫入管線_Dispose前強制刷空()
    {
        var writer = new DetectionWriter(_store, flushInterval: TimeSpan.FromHours(1));
        writer.Enqueue(D(Ch1, "car", 0.7f, 0));
        writer.Dispose();

        var all = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        });
        Assert.Single(all);
    }

    [Fact]
    public void 顯示欄位_座標信心往返正確()
    {
        _repo.AddBatch(new[] { D(Ch1, "dog", 0.987f, 0, x: 0.123f, y: 0.456f) });
        var row = _repo.ListByQuery(new DetectionRepository.QueryArgs
        {
            FromUtc = BaseUtc.AddMinutes(-1),
            ToUtc = BaseUtc.AddMinutes(10),
        }).Single();
        Assert.Equal(0.987f, row.Confidence);
        Assert.Equal(0.123f, row.X);
        Assert.Equal(0.456f, row.Y);
        Assert.Equal("dog", row.Class);
        Assert.Equal(BaseUtc, row.DetectedUtc);
    }
}