using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class AiEventEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _snapRoot;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public AiEventEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ai-{Guid.NewGuid():N}.db");
        _snapRoot = Path.Combine(Path.GetTempPath(), $"helivms-ai-snap-{Guid.NewGuid():N}");
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
                cmd.Parameters.AddWithValue("$n", "AI 引擎頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/ai");
            });
    }

    private static VideoFrame Frame()
    {
        var px = new byte[160 * 120 * 3];
        Array.Fill(px, (byte)120);
        return new VideoFrame
        {
            Width = 160,
            Height = 120,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    private sealed class StubEngine : IDetectionEngine
    {
        public required IReadOnlyList<Detection> Detections { get; init; }

        public IReadOnlyList<Detection> Run(VideoFrame frame) => Detections;

        public void Dispose()
        {
        }
    }

    private async Task FeedAsync(AiEventEngine engine, int frames, int gapMs = 300)
    {
        for (var i = 0; i < frames; i++)
        {
            engine.OnFrame(Frame());
            await Task.Delay(gapMs);
        }
    }

    [Fact]
    public async Task DetectedPerson_WritesCoalescedAiPersonWithSnapshot()
    {
        var stub = new StubEngine
        {
            Detections = [new Detection("person", 0.92f, 0.3f, 0.4f, 0.2f, 0.5f)],
        };

        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        await FeedAsync(engine, frames: 6);

        engine.Flush();

        var rows = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
        var row = Assert.Single(rows);
        Assert.Equal("ai_person", row.EventType);
        Assert.NotNull(row.EndUtc);
        Assert.True(row.EndUtc >= row.StartUtc);
        Assert.NotNull(row.SnapshotPath);
        Assert.True(File.Exists(row.SnapshotPath));
        Assert.Contains("person conf=0.92", row.Detail);
        Assert.True(DetectionDetail.TryParse(row.Detail, out var parsed));
        Assert.Equal("person", parsed.Class);
        Assert.Equal(0.92f, parsed.Confidence);
    }

    [Fact]
    public async Task VehicleDetection_WritesAiVehicle()
    {
        var stub = new StubEngine
        {
            Detections = [new Detection("car", 0.8f, 0.1f, 0.2f, 0.3f, 0.4f)],
        };

        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        await FeedAsync(engine, frames: 6);

        engine.Flush();

        var row = Assert.Single(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal("ai_vehicle", row.EventType);
    }

    [Fact]
    public async Task BelowConfidence_ProducesNoEvent()
    {
        var stub = new StubEngine
        {
            Detections = [new Detection("person", 0.3f, 0.3f, 0.4f, 0.2f, 0.5f)],
        };

        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        await FeedAsync(engine, frames: 6);

        engine.Flush();

        Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task UnsupportedObject_IsIgnored()
    {
        var stub = new StubEngine
        {
            Detections = [new Detection("cat", 0.95f, 0.3f, 0.4f, 0.2f, 0.5f)],
        };

        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        await FeedAsync(engine, frames: 6);

        engine.Flush();

        Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task Reset_DiscardsInFlightWindow()
    {
        var stub = new StubEngine
        {
            Detections = [new Detection("person", 0.9f, 0.3f, 0.4f, 0.2f, 0.5f)],
        };

        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        engine.OnFrame(Frame());
        await Task.Delay(500);
        engine.OnFrame(Frame());

        engine.Reset();
        engine.Flush();

        Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task DetectionsReady_PublishesEverySampleWithFullList()
    {
        var stub = new StubEngine
        {
            Detections =
            [
                new Detection("person", 0.9f, 0.3f, 0.4f, 0.2f, 0.5f),
                new Detection("cat", 0.8f, 0.1f, 0.1f, 0.1f, 0.1f),
            ],
        };

        var published = new List<DetectionsFrame>();
        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        engine.DetectionsReady += (_, f) => published.Add(f);

        await FeedAsync(engine, frames: 6);

        engine.Flush();

        Assert.NotEmpty(published);
        Assert.All(published, f =>
        {
            Assert.Equal(2, f.Items.Count);
            Assert.Equal("person", f.Items[0].Class);
            Assert.Equal("cat", f.Items[1].Class);
        });
    }

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

        try
        {
            Directory.Delete(_snapRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}