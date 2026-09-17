using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class TamperEventEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _snapRoot;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public TamperEventEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-tamper-{Guid.NewGuid():N}.db");
        _snapRoot = Path.Combine(Path.GetTempPath(), $"helivms-tamper-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_snapRoot);
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
                cmd.Parameters.AddWithValue("$n", "遮蔽頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/tamper");
            });
    }

    private static VideoFrame Uniform(byte value)
    {
        var px = new byte[160 * 120 * 3];
        Array.Fill(px, value);
        return new VideoFrame
        {
            Width = 160,
            Height = 120,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    private static VideoFrame Checker()
    {
        const int w = 160;
        const int h = 120;
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = ((x / 8) + (y / 8)) % 2 == 0 ? (byte)200 : (byte)40;
                var i = ((y * w) + x) * 3;
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
            }
        }

        return new VideoFrame
        {
            Width = w,
            Height = h,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    [Fact]
    public async Task SustainedBlackout_WritesTamperEventWithSnapshot()
    {
        var engine = new TamperEventEngine(1, _repo, _snapRoot);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                engine.OnFrame(Uniform(0));
            }

            await Task.Delay(400);
            engine.Flush();

            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Equal("tamper", row.EventType);
            Assert.NotNull(row.EndUtc);
            Assert.Contains("kind=blackout", row.Detail);
            Assert.NotNull(row.SnapshotPath);
            Assert.True(File.Exists(row.SnapshotPath));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task BelowThreshold_ProducesNoEvent()
    {
        var engine = new TamperEventEngine(1, _repo, _snapRoot);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                engine.OnFrame(Uniform(0));
            }

            engine.OnFrame(Checker());
            await Task.Delay(400);
            engine.Flush();

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task Recovery_ClosesWindowToSingleEvent()
    {
        var engine = new TamperEventEngine(1, _repo, _snapRoot);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                engine.OnFrame(Uniform(0));
            }

            await Task.Delay(1_600);
            engine.OnFrame(Checker());   // 已過冷卻窗口 → 結算
            await Task.Delay(50);
            engine.Flush();

            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Equal("tamper", row.EventType);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task Reset_DiscardsInProgressWindow()
    {
        var engine = new TamperEventEngine(1, _repo, _snapRoot);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                engine.OnFrame(Uniform(0));
            }

            engine.Reset();
            await Task.Delay(400);
            engine.Flush();

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task Whiteout_RecordsKindWhiteout()
    {
        var engine = new TamperEventEngine(1, _repo, _snapRoot);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                engine.OnFrame(Uniform(255));
            }

            await Task.Delay(400);
            engine.Flush();

            var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
            Assert.Contains("kind=whiteout", row.Detail);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void KindText_MapsKnownKinds()
    {
        Assert.Equal("blackout", TamperEventEngine.KindText(TamperKind.Blackout));
        Assert.Equal("whiteout", TamperEventEngine.KindText(TamperKind.Whiteout));
        Assert.Equal("covered", TamperEventEngine.KindText(TamperKind.Covered));
        Assert.Equal("none", TamperEventEngine.KindText(TamperKind.None));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_snapRoot, recursive: true);
        }
        catch (IOException)
        {
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
