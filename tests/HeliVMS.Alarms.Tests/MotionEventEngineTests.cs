using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class MotionEventEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _snapRoot;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public MotionEventEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-eng-{Guid.NewGuid():N}.db");
        _snapRoot = Path.Combine(Path.GetTempPath(), $"helivms-snap-{Guid.NewGuid():N}");
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
                cmd.Parameters.AddWithValue("$n", "引擎頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/eng");
            });
    }

    private static VideoFrame Frame(byte value)
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

    [Fact]
    public async Task SustainedMotion_WritesSingleCoalescedEventWithSnapshot()
    {
        var engine = new MotionEventEngine(1, _repo, _snapRoot);
        try
        {
            engine.OnFrame(Frame(80));       // 預熱
            engine.OnFrame(Frame(80));

            // 連續運動約 1.2 秒（每幀皆有變動）
            for (var i = 0; i < 4; i++)
            {
                engine.OnFrame(i % 2 == 0 ? Frame(150) : Frame(80));
                await Task.Delay(300);
            }

            // 靜止超過冷卻窗口後收尾
            await Task.Delay(1_100);
            engine.Flush();

            var rows = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
            var row = Assert.Single(rows);
            Assert.Equal("motion", row.EventType);
            Assert.NotNull(row.EndUtc);
            Assert.NotNull(row.SnapshotPath);
            Assert.True(File.Exists(row.SnapshotPath));
            Assert.Contains("peak=", row.Detail);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task RepeatedMotions_ProduceSeparateEvents()
    {
        var engine = new MotionEventEngine(1, _repo, _snapRoot);
        try
        {
            engine.OnFrame(Frame(80));

            // 第一輪運動
            engine.OnFrame(Frame(150));
            await Task.Delay(300);
            await Task.Delay(1_100);
            engine.Flush();

            // 第二輪以靜態預熱後再運動（偵測器係對「相鄰幀差異」感測）
            engine.OnFrame(Frame(80));
            engine.OnFrame(Frame(150));
            await Task.Delay(300);
            await Task.Delay(1_100);
            engine.Flush();

            var rows = _repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
            Assert.Equal(2, rows.Count);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task BriefBlip_BelowMinDuration_Discarded()
    {
        var engine = new MotionEventEngine(1, _repo, _snapRoot);
        try
        {
            engine.OnFrame(Frame(80));
            engine.OnFrame(Frame(150));       // 單一瞬間運動
            await Task.Delay(150);
            engine.Flush();                   // 尚未達最短事件時長 → 捨棄

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task Reset_DiscardsOngoingMotionWindow()
    {
        var engine = new MotionEventEngine(1, _repo, _snapRoot);
        try
        {
            engine.OnFrame(Frame(80));
            engine.OnFrame(Frame(150));
            await Task.Delay(200);

            engine.Reset();                   // 重連清空
            await Task.Delay(1_100);

            Assert.Empty(_repo.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            engine.Dispose();
        }
    }

    public void Dispose() => _store.Dispose();
}