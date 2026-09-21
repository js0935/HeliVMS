using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>M14：全域 AI 併發佇列＋每格策略（啟用/節流間隔）之行為驗證。</summary>
public class AiConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _snapRoot;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public AiConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-aics-{Guid.NewGuid():N}.db");
        _snapRoot = Path.Combine(Path.GetTempPath(), $"helivms-aics-snap-{Guid.NewGuid():N}");
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
                cmd.Parameters.AddWithValue("$n", "AI 策略頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/pa");
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
            Directory.Delete(_snapRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static VideoFrame Frame() => Frame(120);

    private static VideoFrame Frame(byte gray)
    {
        var px = new byte[160 * 120 * 3];
        Array.Fill(px, gray);
        return new VideoFrame
        {
            Width = 160,
            Height = 120,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    private sealed class SlowCountingEngine : IDetectionEngine
    {
        public int Runs;

        public IReadOnlyList<Detection> Run(VideoFrame frame)
        {
            Interlocked.Increment(ref Runs);
            Thread.Sleep(50);   // 模擬推理耗時，讓併發上限可直接觀察
            return [];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>時序斷言用輪詢等待：避免 xUnit 平行載入下固定 Sleep 偶發不足。</summary>
    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task 全域限併_同時啟動之推理不超過指定上限()
    {
        using var sched = new AiConcurrencyScheduler(2);
        var concurrent = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            sched.RunSync(() =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedAddMax(ref peak, now);
                Thread.Sleep(50);
                Interlocked.Decrement(ref concurrent);
            })));

        await Task.WhenAll(tasks);

        Assert.True(peak <= 2, $"peak={peak}");
        Assert.Equal(0, concurrent);
        return;

        static void InterlockedAddMax(ref int target, int value)
        {
            var cur = Volatile.Read(ref target);
            while (cur < value && Interlocked.CompareExchange(ref target, value, cur) != cur)
            {
                cur = Volatile.Read(ref target);
            }
        }
    }

    [Fact]
    public async Task 引擎節流_取樣間隔門檻內只收一幀()
    {
        var stub = new SlowCountingEngine();
        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        engine.MinSampleIntervalMs = 1000;

        engine.OnFrame(Frame(100));
        engine.OnFrame(Frame(101));
        engine.OnFrame(Frame(102));
        await Task.Delay(350);

        // 1000ms 內多幀只應取樣 1 幀、推理 1 次
        await WaitUntil(() => stub.Runs >= 1 && engine.FramesInferred >= 1);
        Assert.Equal(1, engine.FramesInferred);
        Assert.Equal(1, stub.Runs);

        // 降低間隔後 取下一幀
        engine.MinSampleIntervalMs = 100;
        await Task.Delay(300);
        engine.OnFrame(Frame(103));
        await WaitUntil(() => stub.Runs >= 2 && engine.FramesInferred >= 2);
        Assert.Equal(2, engine.FramesInferred);
        Assert.Equal(2, stub.Runs);
    }

    [Fact]
    public async Task 引擎停用_不取樣不推理_啟用後恢復()
    {
        var stub = new SlowCountingEngine();
        using var engine = new AiEventEngine(1, _repo, _snapRoot, stub);
        engine.MinSampleIntervalMs = 100;
        engine.Enabled = false;

        engine.OnFrame(Frame(100));
        engine.OnFrame(Frame(101));
        engine.OnFrame(Frame(102));
        await Task.Delay(350);
        Assert.Equal(0, engine.FramesFed);
        Assert.Equal(0, stub.Runs);

        engine.Enabled = true;
        engine.OnFrame(Frame(103));
        await WaitUntil(() => stub.Runs >= 1 && engine.FramesFed >= 1);
        Assert.Equal(1, engine.FramesFed);
        Assert.Equal(1, stub.Runs);
    }

    [Fact]
    public async Task 引擎停用_清空待決佇列避免堆積()
    {
        var stub = new SlowCountingEngine();
        var engine = new AiEventEngine(1, _repo, _snapRoot, stub, minConfidence: 0.5f);
        engine.MinSampleIntervalMs = 100;
        engine.OnFrame(Frame(100));
        await Task.Delay(250);   // 確保取樣已入佇列（或正在推理）
        engine.Enabled = false;  // 停用應 Reset 清佇列

        await Task.Delay(300);
        Assert.Equal(0, engine.PendingCount);
        engine.Dispose();
    }
}