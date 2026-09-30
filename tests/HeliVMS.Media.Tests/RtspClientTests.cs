using HeliVMS.Shared;
using System.Text;
using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 驗證 <see cref="RtspClient"/> 的逾時與重連行為。
/// 以假 ffmpeg／ffprobe 模擬真實攝影機的病態情形，不需 RTSP 來源。
/// </summary>
public class RtspClientTests
{
    [Fact]
    public async Task Start_WhenNoFrameArrives_TransitionsToReconnecting_NotStuckConnecting()
    {
        // 攝影機接受連線卻永不送影格：ffprobe 回解析度後 ffmpeg 靜默不輸出。
        // 舊行為會永久停在 Connecting；必須在首幀逾時後轉為 Reconnecting。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", Silent);

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromMilliseconds(600),
            FrameTimeout = TimeSpan.FromMilliseconds(600),
        };

        var states = new List<RtspState>();
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };

        client.Reconnecting += (_, ex) => reconnected.TrySetResult();

        await client.StartAsync();

        var done = await Task.WhenAny(reconnected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(done == reconnected.Task, "首幀逾時後應觸發重連，而非卡在 Connecting");

        lock (states)
        {
            Assert.Contains(RtspState.Connecting, states);
        }

        await client.StopAsync();
    }

[Fact]
    public async Task Start_WhenStreamProducesFrames_EmitsFrameAndStreams()
    {
        // 正常情形：ffprobe 回 2x2 解析度，ffmpeg 持續輸出剛好一幀（2*2*3 bytes）。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution);
        var frame = WriteFrame(dir);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EmitOneFrame(frame));

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(10),
            FrameTimeout = TimeSpan.FromSeconds(10),
            MaxFramesPerSecond = 30,
        };

        var got = new TaskCompletionSource<(int Width, int Height, int Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, f) => got.TrySetResult((f.Width, f.Height, f.Pixels.Length));

        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "應收到解碼後的影格");

        var frameResult = await got.Task;
        Assert.Equal(2, frameResult.Width);
        Assert.Equal(2, frameResult.Height);
        Assert.Equal(2 * 2 * 3, frameResult.Bytes);
        Assert.Equal(RtspState.Streaming, client.State);

        await client.StopAsync();
    }

[Fact]
    public async Task Dispose_StopsLoop_AndLeavesStateStopped()
    {
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution);
        var frame = WriteFrame(dir);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EmitOneFrame(frame));

        var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(5),
        };

        await client.StartAsync();
        await client.DisposeAsync();

        Assert.Equal(RtspState.Stopped, client.State);
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task Start_WhenFfmpegFloodsStderr_StillDeliversFrames()
    {
        // 回歸測試：ffmpeg 遇連線異常會反覆寫錯誤到 stderr。若 RtspClient 不排乾 stderr，
        // 管線填滿（Windows 約 64KB）後 ffmpeg 會阻塞在寫入而不再產出影格，
        // 導致正常串流被誤判為停滯並陷入重連迴圈。
        // 本 stub 先寫 320KB stderr（緩衝的 5 倍）再寫一幀，模擬該情境。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution);
        var frame = WriteFrame(dir);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", FloodStderrThenEmitFrame(frame));

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(20),
            FrameTimeout = TimeSpan.FromSeconds(2),
            MaxFramesPerSecond = 30,
        };

        var got = new TaskCompletionSource<(int Width, int Height, int Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, f) => got.TrySetResult((f.Width, f.Height, f.Pixels.Length));

        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "stderr 被塞滿時仍應送出影格（stderr 必須被排乾）");

        var frameResult = await got.Task;
        Assert.Equal(2 * 2 * 3, frameResult.Bytes);
    }

    [Fact]
    public async Task StopAsync_WhenStderrFlooded_ExposesRedactedDiagnostic()
    {
        // ffmpeg 會回顯含帳密的輸入網址；診斷內容對外可用，但不得含明文密碼。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EchoCredentialInStderr);

        await using var client = new RtspClient("rtsp://root:sup3rsecret@10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(2),
        };

        await client.StartAsync();

        // StartAsync 為背景啟動（回傳時 ffmpeg 尚未啟動），需等 ffmpeg 真的跑起來再給它時間輸出。
        await WaitUntilAsync(() => client.IsRunning);
        Assert.True(client.IsRunning, "ffmpeg 應已啟動");

        // 讓 stub 有時間把錯誤寫進 stderr（排乾任務需讀到）。
        await Task.Delay(TimeSpan.FromSeconds(2));

        await client.StopAsync();

        Assert.NotEmpty(client.LastDiagnostic);
        Assert.DoesNotContain("sup3rsecret", client.LastDiagnostic, StringComparison.Ordinal);
        Assert.Contains("401 Unauthorized", client.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_AfterStop_RestartsAndDeliversFrames()
    {
        // 回歸測試：ChannelManager.RestartAsync 會在同一個 RtspClient 實例上 Stop 後再 Start。
        // 若 StartAsync 僅因 _cts 非 null 就返回，重啟會被靜默忽略，頻道永久無法恢復，
        // 且 health timer 會誤標為上線，之後 State 已非 Streaming 而永不再重試。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution);
        var frame = WriteFrame(dir);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", EmitFramesContinuously(frame));

        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp", ffmpeg, probe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(10),
            FrameTimeout = TimeSpan.FromSeconds(10),
            MaxFramesPerSecond = 30,
        };

        var frames = 0;
        client.FrameDecoded += (_, _) => Interlocked.Increment(ref frames);

        await client.StartAsync();
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 1);
        Assert.Equal(RtspState.Streaming, client.State);

        // 第一次停止後必須回到可再次啟動的狀態
        await client.StopAsync();
        Assert.Equal(RtspState.Stopped, client.State);
        Assert.False(client.IsRunning);

        var afterStop = Volatile.Read(ref frames);
        await client.StartAsync();
        await WaitUntilAsync(() => Volatile.Read(ref frames) > afterStop);

        Assert.Equal(RtspState.Streaming, client.State);
        Assert.True(
            Volatile.Read(ref frames) > afterStop,
            "Stop 後再次 Start 應重新拉流（重啟不得被靜默忽略）");
    }

    [Fact]
    public async Task DefaultTimeouts_CoverSlowestMeasuredCamera()
    {
        // 校正測試（依 7 台實機量測值，非憑空設定）。
        // 220.130.205.226：ffprobe 需 11.6～55.3 秒、原生 ffmpeg 首幀 19.3 秒且耗時波動大。
        // 預設值若被調回 10/20 秒，該攝影機會永遠停在 Connecting／Reconnecting（實測已發生）。
        await using var client = new RtspClient("rtsp://10.0.0.1/live.sdp");

        Assert.True(
            client.ProbeTimeout >= TimeSpan.FromSeconds(60),
            $"ProbeTimeout 至少需 60 秒（實測最慢探測 55.3 秒），目前為 {client.ProbeTimeout}");
        Assert.True(
            client.FirstFrameTimeout >= TimeSpan.FromSeconds(45),
            $"FirstFrameTimeout 至少需 45 秒（實測最慢首幀 43 秒），目前為 {client.FirstFrameTimeout}");
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

            await Task.Delay(100);
        }
    }

    // 單一影格的原始位元組（2*2*3 RGB）。以 cmd `type` 輸出預先寫好的 frame.bin，產位元組精確
    // 且不需 powershell 冷啟動（CI 並行負載下 powershell 可能數秒～數十秒才起來）。
    private static readonly byte[] FrameBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private static string WriteFrame(string dir)
    {
        var path = Path.Combine(dir, "frame.bin");
        File.WriteAllBytes(path, FrameBytes);
        return path;
    }

    // 一次送出剛好 12 bytes（2*2*3），之後靜默等待，讓 RtspClient 成功湊滿一幀。
    private static string EmitOneFrame(string framePath) =>
        $$"""
        @echo off
        type "{{framePath}}"
        ping -n 30 127.0.0.1 >nul
        """;

    // 持續輸出多幀，讓「可重啟」與「Streaming 狀態」可被觀察。
    private static string EmitFramesContinuously(string framePath) =>
        $$"""
        @echo off
        for /l %%i in (1,1,300) do @type "{{framePath}}"
        ping -n 30 127.0.0.1 >nul
        """;

    // 先寫 320KB stderr（緩衝的 5 倍）再寫一幀，模擬 stderr 填滿阻塞。
    private static string FloodStderrThenEmitFrame(string framePath) =>
        $$"""
        @echo off
        for /l %%i in (1,1,4000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2
        type "{{framePath}}"
        ping -n 30 127.0.0.1 >nul
        """;

    // 輸出 5 行錯誤，其中一行回顯含帳密的輸入網址（ffmpeg 的真實行為）。
    // cmd 的 echo 預設寫 stdout，必須用 1>&2 導向 stderr。
    private const string EchoCredentialInStderr =
        """
        @echo off
        echo [rtsp @ 0] method DESCRIBE failed: 401 Unauthorized 1>&2
        echo Error opening input file rtsp://root:sup3rsecret@10.0.0.1:554/live.sdp 1>&2
        echo Error opening input file rtsp://root:sup3rsecret@10.0.0.1:554/live.sdp 1>&2
        echo [in#0 @ 0] Error opening input: Server returned 401 Unauthorized 1>&2
        echo Error opening input files: Server returned 401 Unauthorized 1>&2
        ping -n 30 127.0.0.1 >nul
        """;

    private const string EchoResolution =
        """
        @echo off
        echo {"streams":[{"codec_type":"video","codec_name":"h264","width":2,"height":2,"r_frame_rate":"1/1"}]}
        """;

private const string Silent =
        """
        @echo off
        ping -n 60 127.0.0.1 >nul
        """;

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-rtsp-{Guid.NewGuid():N}");
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
