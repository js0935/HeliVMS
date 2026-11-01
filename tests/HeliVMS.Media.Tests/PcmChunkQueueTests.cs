namespace HeliVMS.Media.Tests;

/// <summary>
/// 有界 PCM 片段佇列（<see cref="PcmChunkQueue"/>）的行為契約。
/// </summary>
/// <remarks>
/// 這條佇列是「現場收音」唯一會堆積資料的地方。音效卡卡住（被獨佔、藍牙重連、USB 音效卡
/// 拔插）時若沒有上限，記憶體會一路長到當掉；而補播幾分鐘前的聲音比跳過那一段更糟——
/// 保全場景要的是現在發生的事。滿了丟最舊、並留下可數的痕跡，就是這裡要釘住的兩個保證。
/// </remarks>
public sealed class PcmChunkQueueTests
{
    private static byte[] Chunk(byte value, int length = 8) => Enumerable.Repeat(value, length).ToArray();

    [Fact]
    public void 滿了會丟掉最舊的一段並計數()
    {
        var queue = new PcmChunkQueue(capacity: 3);

        queue.Enqueue(Chunk(1));
        queue.Enqueue(Chunk(2));
        queue.Enqueue(Chunk(3));
        queue.Enqueue(Chunk(4));

        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.Dropped);

        // 留下的是最新的三段：開頭被丟掉，聲音的時間才不會愈差愈多。
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(2, first[0]);
    }

    [Fact]
    public void 取出的順序是先進先出()
    {
        var queue = new PcmChunkQueue(capacity: 4);
        queue.Enqueue(Chunk(7));
        queue.Enqueue(Chunk(8));

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(7, first[0]);
        Assert.Equal(8, second[0]);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task 等待逾時期間放入資料會立刻取回()
    {
        var queue = new PcmChunkQueue(capacity: 2);

        // 播放執行緒靠「等」而不是「輪詢」；若等待版本其實是立刻回 false，
        // 這裡就會取不到 100 ms 後才放進來的片段，聲音會變成一段一段的。
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            queue.Enqueue(Chunk(9));
        });

        Assert.True(queue.TryDequeue(out var chunk, TimeSpan.FromSeconds(5)));
        Assert.Equal(9, chunk[0]);
    }

    [Fact]
    public void 清空會歸零緩衝但不動累計計數()
    {
        var queue = new PcmChunkQueue(capacity: 4);
        queue.Enqueue(Chunk(1, 100));
        queue.Enqueue(Chunk(2, 100));

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.BufferedBytes);
        Assert.Equal(2, queue.Enqueued);   // 診斷用：清空不是「從來沒收到過」
    }

    [Fact]
    public void 緩衝位元組數跟著進出增減()
    {
        var queue = new PcmChunkQueue(capacity: 4);
        queue.Enqueue(Chunk(1, 3200));
        queue.Enqueue(Chunk(2, 1600));
        Assert.Equal(4800, queue.BufferedBytes);

        Assert.True(queue.TryDequeue(out _));
        Assert.Equal(1600, queue.BufferedBytes);
    }
}
