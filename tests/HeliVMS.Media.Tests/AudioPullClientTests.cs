using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 音訊拉取（<see cref="AudioPullClient"/>）的參數與切片契約。
/// </summary>
/// <remarks>
/// 這條路徑是「攝影機的麥克風從來沒被聽過」的補洞：畫面解碼刻意帶了 <c>-an</c>，聲音必須
/// 另外拉。參數錯掉的症狀很安靜——畫面完全正常，只是沒有聲音，而 ffmpeg 的錯誤只停在
/// 管線裡。單聲道 16 bit 無容器（s16le）是與播放端約好的格式，任一項不符就是雜音或無聲。
/// </remarks>
public sealed class AudioPullClientTests
{
    private static readonly string[] ExpectedTail =
        ["-vn", "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", "-f", "s16le", "pipe:1"];

    [Fact]
    public void 輸出固定是單聲道16位元無容器PCM()
    {
        var args = AudioPullClient.BuildArguments("rtsp://cam/live");

        Assert.Equal(ExpectedTail, args.TakeLast(ExpectedTail.Length));
        Assert.DoesNotContain("-an", args);
    }

    [Fact]
    public void 即時來源走TCP且不預先緩衝()
    {
        var args = AudioPullClient.BuildArguments("rtsp://cam/live", live: true);

        // UDP 在多網卡／NAT 機器上經常只掉音訊，症狀是「有畫面沒聲音」；
        // 而為了量測而緩衝整段的 ffmpeg 會讓現場聲音慢好幾秒。
        Assert.Equal(["tcp"], After(args, "-rtsp_transport"));
        Assert.Equal(["nobuffer"], After(args, "-fflags"));
        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-t", args);
    }

    [Fact]
    public void 檔案來源不帶RTSP選項且起始偏移在輸入之前()
    {
        var args = AudioPullClient.BuildArguments(
            @"C:\rec\seg.mp4", live: false, seekSeconds: 12.5, durationSeconds: 30);

        Assert.DoesNotContain("-rtsp_transport", args);
        Assert.Equal(["12.5"], After(args, "-ss"));
        Assert.Equal(["30"], After(args, "-t"));

        // -ss 必須排在 -i 前面才是快速跳轉（輸入端 seek）；放到後面會先解碼整段再丟掉，
        // 回放拉音訊就得多等好幾秒。
        var list = args.ToList();
        Assert.True(list.IndexOf("-ss") < list.IndexOf("-i"));
    }

    [Fact]
    public void 檔案來源未指定長度時不加上限()
    {
        var args = AudioPullClient.BuildArguments(@"C:\rec\seg.mp4", live: false, durationSeconds: 0);

        Assert.DoesNotContain("-t", args);
    }

    [Fact]
    public async Task 片段大小隨取樣率換算成100毫秒()
    {
        await using var client16 = new AudioPullClient("rtsp://cam/live", sampleRate: 16000);
        await using var client48 = new AudioPullClient("rtsp://cam/live", sampleRate: 48000);

        Assert.Equal(3200, client16.ChunkBytes);
        Assert.Equal(9600, client48.ChunkBytes);
        Assert.False(client16.IsRunning);
    }

    [Fact]
    public async Task 連續位元組流會切成固定大小且最後一段較短()
    {
        var data = new byte[7000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        using var stream = new MemoryStream(data);
        var chunks = new List<byte[]>();
        await foreach (var chunk in AudioPullClient.ReadChunksAsync(stream, 3200))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal(3200, chunks[0].Length);
        Assert.Equal(3200, chunks[1].Length);
        Assert.Equal(600, chunks[2].Length);
        Assert.Equal(data, chunks.SelectMany(c => c).ToArray());
    }

    private static string[] After(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == option)
            {
                return [args[i + 1]];
            }
        }

        return [];
    }
}
