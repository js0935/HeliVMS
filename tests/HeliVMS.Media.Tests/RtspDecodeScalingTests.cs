using System.Text;
using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 即時監看的解碼端縮放（<see cref="RtspClient.DecodeWidth"/>）：多分割時在 ffmpeg 就縮到
/// 顯示需要的寬度，而不是解出原生解析度再讓 WPF 用軟體縮小。
/// </summary>
/// <remarks>
/// <para>
/// 這裡最貴的一種錯誤是「尺寸口徑不一致」：送給 ffmpeg 的 <c>scale=</c> 與讀取迴圈用來
/// 湊滿一幀的位元組數只要差一個像素，影格就整段錯位（畫面全黑或斜切），而日誌完全乾淨。
/// 所以除了參數清單，還要真的跑一次 stub ffmpeg 驗證 <see cref="RtspClient.FrameSize"/>。
/// </para>
/// <para>
/// 另一個必須釘住的是「切佈局時不要每格都重開 ffmpeg」：<see cref="RtspClient.SetDecodeWidthAsync"/>
/// 每次佈局變更都會被呼叫，若無條件重開，操作員切一次分割就會看到每一格閃「連線中」。
/// </para>
/// </remarks>
public class RtspDecodeScalingTests
{
    [Fact]
    public void 未指定解碼寬度時完全不帶縮放濾鏡()
    {
        // 單畫面與小格數走原生解析度：多一個 scale 濾鏡就是每一格多一次無謂的縮放成本。
        var args = RtspClient.BuildDecodeArguments("rtsp://host/live");

        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain(args, a => a.StartsWith("scale=", StringComparison.Ordinal));
    }

    [Fact]
    public void 指定解碼寬度時縮放濾鏡在輸入之後且在輸出格式之前()
    {
        var args = RtspClient.BuildDecodeArguments(
            "rtsp://host/live",
            decodeWidth: 640,
            decodeHeight: 360).ToList();

        var vf = args.IndexOf("-vf");
        Assert.True(vf >= 0, "指定解碼寬度時應帶入 -vf scale=…");

        // flags=area：swscale 預設的 bicubic 在 3:1 以上的縮小要多花約 23% CPU 卻沒有換到品質
        // （量測與 PSNR 見 VideoScaling.DownscaleFilter）。少了 flags 這一項，畫面一樣正常，
        // 只是大格數時 CPU 白花——所以必須釘在這裡。
        Assert.Equal("scale=640:360:flags=area", args[vf + 1]);

        // -vf 是輸出選項：放在 -i 之前會被 ffmpeg 當成輸入選項而報錯（或靜默套用到錯的地方）。
        Assert.True(vf > args.IndexOf("-i"), "-vf 必須在 -i 之後");
        Assert.True(vf < args.IndexOf("-f"), "-vf 必須在輸出格式 -f 之前");
        Assert.Equal("rawvideo", args[args.IndexOf("-f") + 1]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(640, 0)]
    [InlineData(0, 360)]
    public void 寬高任一為零時不帶縮放濾鏡(int width, int height)
    {
        // 只有一半的尺寸是無效的組合（高度由呼叫端算）；寧可不縮，也不要送一個
        // scale=640:0 讓 ffmpeg 直接結束。
        var args = RtspClient.BuildDecodeArguments(
            "rtsp://host/live",
            decodeWidth: width,
            decodeHeight: height);

        Assert.DoesNotContain("-vf", args);
    }

    [Theory]
    [InlineData(1920, 1080, 640, 640, 360)]
    [InlineData(1920, 1080, 480, 480, 270)]
    [InlineData(1280, 405, 320, 320, 102)]
    public void 解碼尺寸取決於探測尺寸與寬度上限(
        int probeWidth,
        int probeHeight,
        int decodeWidth,
        int expectedWidth,
        int expectedHeight)
    {
        var (width, height) = RtspClient.DecodedSize(probeWidth, probeHeight, decodeWidth);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Equal(0, height % 2);
    }

    [Theory]
    [InlineData(640, 360, 640)]
    [InlineData(640, 360, 0)]
    [InlineData(640, 360, 1920)]
    public void 上限不小於來源寬度時沿用探測尺寸(int probeWidth, int probeHeight, int decodeWidth)
    {
        // 次碼流本來就比預算窄（640 寬、16 格預算 480 才需要縮），此時必須原樣回傳，
        // 不可回 (0,0)——讀取迴圈會算出 0 位元組的影格。
        var (width, height) = RtspClient.DecodedSize(probeWidth, probeHeight, decodeWidth);

        Assert.Equal(probeWidth, width);
        Assert.Equal(probeHeight, height);
    }

    [Fact]
    public async Task 縮放後實際影格尺寸與參數一致()
    {
        // 真的跑一次 stub ffmpeg：探測回 64×36、預算 32 寬 → 32×18，一幀 1728 bytes。
        // 若參數清單與讀取迴圈的尺寸口徑漂移，這裡會讀到錯位的影格或永遠湊不滿。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 64 * 36 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EmitFrameAndWriteArgs);

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(10),
            FrameTimeout = TimeSpan.FromSeconds(10),
            MaxFramesPerSecond = 30,
        };

        await client.SetDecodeWidthAsync(32);

        var got = new TaskCompletionSource<(int Width, int Height, int Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, f) => got.TrySetResult((f.Width, f.Height, f.Pixels.Length));

        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "縮小解碼時仍應出畫");

        var frame = await got.Task;
        Assert.Equal(32, frame.Width);
        Assert.Equal(18, frame.Height);
        Assert.Equal(32 * 18 * 3, frame.Bytes);
        Assert.Equal((32, 18), client.FrameSize);

        var args = await File.ReadAllTextAsync(Path.Combine(dir, "args.txt"));
        Assert.Contains("scale=32:18", args);
    }

    [Fact]
    public async Task 解碼寬度沒有實際改變時不會重開解碼行程()
    {
        // 佈局變更時這條路每次都會被呼叫。無條件重開行程的症狀是：操作員切一次分割，
        // 每一格都閃一下「連線中」，而且每次切換都重新協商 RTSP。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 64 * 36 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EmitFramesContinuously);

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(10),
            FrameTimeout = TimeSpan.FromSeconds(10),
            MaxFramesPerSecond = 30,
        };

        await client.StartAsync();
        await WaitUntilAsync(() => client.FramesEmitted >= 1);
        Assert.Equal(RtspState.Streaming, client.State);

        var states = new List<RtspState>();
        client.StateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };

        var before = client.FramesEmitted;

        // 上限比來源寬 → 實際生效寬度不變（都是 64），不得重開。
        await client.SetDecodeWidthAsync(0);
        await client.SetDecodeWidthAsync(64);
        await client.SetDecodeWidthAsync(1920);

        lock (states)
        {
            Assert.Empty(states);
        }

        await WaitUntilAsync(() => client.FramesEmitted > before);
        Assert.True(client.FramesEmitted > before, "沒有重開時影格應該持續進來");
        Assert.Equal((64, 36), client.FrameSize);
    }

    [Fact]
    public async Task 解碼寬度真的改變時會重開並以新尺寸出畫()
    {
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 64 * 36 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EmitFramesContinuously);

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(10),
            FrameTimeout = TimeSpan.FromSeconds(10),
            MaxFramesPerSecond = 30,
        };

        await client.StartAsync();
        await WaitUntilAsync(() => client.FramesEmitted >= 1);
        Assert.Equal((64, 36), client.FrameSize);

        // 由 64 縮到 32：必須換一輪 ffmpeg，新一輪的影格尺寸才是 32×18。
        await client.SetDecodeWidthAsync(32);

        await WaitUntilAsync(() => client.FrameSize == (32, 18));
        Assert.Equal((32, 18), client.FrameSize);
        Assert.Equal(RtspState.Streaming, client.State);
    }

    private const string EchoResolution64x36 =
        """
        @echo off
        echo {"streams":[{"codec_type":"video","codec_name":"h264","width":64,"height":36,"r_frame_rate":"1/1"}]}
        """;

    // 把實際命令列寫進 args.txt 供斷言，再輸出一幀（大小由呼叫端的 frame.bin 決定）。
    private const string EmitFrameAndWriteArgs =
        """
        @echo off
        if "%2"=="-hwaccels" (
          echo Hardware acceleration methods:
          exit /b 0
        )
        echo %* > "%~dp0args.txt"
        type "%~dp0frame.bin"
        ping -n 30 127.0.0.1 >nul
        """;

    private const string EmitFramesContinuously =
        """
        @echo off
        if "%2"=="-hwaccels" (
          echo Hardware acceleration methods:
          exit /b 0
        )
        echo %* > "%~dp0args.txt"
        for /l %%i in (1,1,300) do @type "%~dp0frame.bin"
        ping -n 30 127.0.0.1 >nul
        """;

    private static void WriteFrame(string dir, int bytes)
    {
        // 內容不重要（尺寸才是被驗的），但不能全零位元組以外什麼都沒有：
        // RtspClient 只按 frameSize 讀，所以這裡給剛好一幀的長度。
        File.WriteAllBytes(Path.Combine(dir, "frame.bin"), new byte[bytes]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-rtsp-scale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteStub(string dir, string name, string body)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, body, Encoding.ASCII);
        return path;
    }
}
