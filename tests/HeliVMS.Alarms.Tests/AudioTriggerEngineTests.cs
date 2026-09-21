using System.Text.RegularExpressions;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class AudioTriggerEngineTests : IDisposable
{
    private const double BurstDbExpected = -4.287;  // 20·log10(20000/32768)
    private const double SustainDbExpected = -24.288; // 20·log10(2000/32768)

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public AudioTriggerEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-audio-{Guid.NewGuid():N}.db");
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
                cmd.Parameters.AddWithValue("$n", "引擎頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/audio");
            });
    }

    [Fact]
    public void SquareWave_ClassifiesAsBurst_WithRmsDbPerFormula()
    {
        var engine = NewEngine();
        try
        {
            var blocks = engine.Feed(Square(20000, 512 * 4), DateTime.UtcNow);
            Assert.Equal(4, blocks.Count);
            Assert.All(blocks, b =>
            {
                Assert.Equal(AudioBlockKind.Burst, b.Kind);
                Assert.InRange(b.RmsDb, BurstDbExpected - 0.2, BurstDbExpected + 0.2);
            });
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void SquareWave_SustainLevel_MatchesFormulaAndNotBurst()
    {
        var engine = NewEngine();
        try
        {
            var blocks = engine.Feed(Square(2000, 512), DateTime.UtcNow);
            var block = Assert.Single(blocks);
            Assert.Equal(AudioBlockKind.Sustained, block.Kind);
            Assert.InRange(block.RmsDb, SustainDbExpected - 0.2, SustainDbExpected + 0.2);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Zeros_ClassifyAsSilent_WithFloorDb()
    {
        var engine = NewEngine();
        try
        {
            var blocks = engine.Feed(new short[512 * 2], DateTime.UtcNow);
            Assert.Equal(2, blocks.Count);
            Assert.All(blocks, b =>
            {
                Assert.Equal(AudioBlockKind.Silent, b.Kind);
                Assert.Equal(-120.0, b.RmsDb);
            });
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Burst_WritesSingleEvent_AfterCooldown()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            engine.Feed(Square(20000, 512 * 8), now);          // 8 塊爆音（256ms）
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3)); // 安靜收尾 → 冷卻結算

            var rows = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
            var row = Assert.Single(rows);
            Assert.Equal("audio_burst", row.EventType);
            Assert.Contains("kind=burst", row.Detail);
            Assert.Contains("peak_db=", row.Detail);
            Assert.Contains("duration=1500ms", row.Detail);
            Assert.NotNull(row.EndUtc);
            Assert.Null(row.SnapshotPath);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Burst_BelowConfirmStreak_NoEvent()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            engine.Feed(Square(20000, 512), now);              // 僅 1 塊爆音
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Sustained_WritesSingleEvent_AfterCooldown()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine(new AudioTriggerConfig { SustainSeconds = 0.1 });
        try
        {
            engine.Feed(Square(2000, 512 * 10), now);          // 持續 320ms ≥ SustainSeconds
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Equal("audio_sustained", row.EventType);
            Assert.Contains("kind=sustained", row.Detail);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Sustained_ShorterThanThreshold_NoEvent()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine(); // 預設 SustainSeconds=2.0
        try
        {
            engine.Feed(Square(2000, 512 * 3), now);           // 96ms 持續
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Silence_WritesBreak_WhenAudioReturns()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine(new AudioTriggerConfig { SilenceSeconds = 0.1 });
        try
        {
            engine.Feed(new short[512 * 5], now);              // 斷音 160ms ≥ SilenceSeconds
            engine.Feed(Square(20000, 512), now.AddSeconds(1)); // 爆音回歸 → 斷路立即結算

            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Equal("audio_break", row.EventType);
            Assert.Contains("kind=silence", row.Detail);
            Assert.NotNull(row.EndUtc);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void PendingSamples_AccumulateAcrossFeeds()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            Assert.Empty(engine.Feed(Square(20000, 300), now));     // 不足一整塊
            var first = engine.Feed(Square(20000, 212), now);       // 湊足 512
            Assert.Single(first);
            Assert.Equal(AudioBlockKind.Burst, first[0].Kind);

            engine.Feed(Square(20000, 512 * 7), now);              // 湊足確認序列
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));
            Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Reset_DiscardsOpenWindow()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            engine.Feed(Square(20000, 512 * 8), now);
            engine.Reset();
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Flush_FinalizesOpenWindow()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            engine.Feed(Square(20000, 512 * 8), now);
            engine.Flush();

            Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Cooldown_SeparatesRepeatedBursts()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            engine.Feed(Square(20000, 512 * 8), now);
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            var second = now.AddSeconds(10);
            engine.Feed(Square(20000, 512 * 8), second);
            engine.Feed(Square(200, 512 * 60), second.AddSeconds(3));

            var rows = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("audio_burst", r.EventType));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Detail_MatchesExpectedFormat()
    {
        var now = DateTime.UtcNow;
        var engine = NewEngine();
        try
        {
            engine.Feed(Square(20000, 512 * 8), now);
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Matches(
                new Regex(@"^kind=burst;peak_db=-?\d+(\.\d+)?;mean_db=-?\d+(\.\d+)?;duration=\d+ms$"),
                row.Detail);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void EventInserted_RaisesWithRecord()
    {
        var now = DateTime.UtcNow;
        AlarmEventRecord? raised = null;
        var engine = NewEngine();
        try
        {
            engine.EventInserted += (_, r) => raised = r;
            engine.Feed(Square(20000, 512 * 8), now);
            engine.Feed(Square(200, 512 * 60), now.AddSeconds(3));

            Assert.NotNull(raised);
            Assert.Equal("audio_burst", raised!.EventType);
            Assert.True(raised.Id > 0);
            Assert.True(raised.EndUtc > raised.StartUtc);
            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Equal(raised.Id, row.Id);
            Assert.Equal(raised.Detail, row.Detail);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void InvalidConfig_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioTriggerEngine(1, _repo, new AudioTriggerConfig { BlockSamples = 0 }));
    }

    private AudioTriggerEngine NewEngine(AudioTriggerConfig? config = null)
        => new(1, _repo, config);

    private static short[] Square(int amplitude, int samples)
    {
        var pcm = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            pcm[i] = (short)((i & 1) == 0 ? amplitude : -amplitude);
        }

        return pcm;
    }

    public void Dispose() => _store.Dispose();
}