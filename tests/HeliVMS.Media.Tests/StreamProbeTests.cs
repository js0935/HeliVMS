using System.Diagnostics;
using HeliVMS.Shared;
using System.Text;
using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>驗證串流探測的逾時行為與錯誤訊息不洩漏帳密（不需真實 RTSP 來源）。</summary>
public class StreamProbeTests
{
    [Fact]
    public void Probe_Timeout_ThrowsAndTerminatesProcess()
    {
        // 以長時執行的假 ffprobe 模擬 RTSP 連線無回應：必須逾時拋出，不能永久停滯。
        var stub = CreateStub(
            """
            @echo off
            ping -n 60 127.0.0.1 >nul
            """,
            ".cmd");

        var ex = Assert.Throws<TimeoutException>(
            () => StreamProbe.Probe("rtsp://10.0.0.1/live.sdp", stub, TimeSpan.FromMilliseconds(600)));

        Assert.Contains("逾時", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rtsp://10.0.0.1/live.sdp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Failure_DoesNotLeakCredentials()
    {
        var stub = CreateStub(
            """
            @echo off
            exit 1
            """,
            ".cmd");

        var withCredentials = RtspUri.WithCredentials("rtsp://10.0.0.1/live.sdp", "root", "sup3rsecret");
        var ex = Assert.Throws<InvalidOperationException>(
            () => StreamProbe.Probe(withCredentials, stub, TimeSpan.FromSeconds(10)));

        Assert.DoesNotContain("sup3rsecret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("root", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rtsp://10.0.0.1/live.sdp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_ReportsResolution_FromProbeOutput()
    {
        // 假 ffprobe 輸出合法 JSON，驗證解析度確實被讀出（回歸 StreamProbe 解析路徑）。
        var stub = CreateStub(
            """
            @echo off
            echo {"streams":[{"codec_type":"video","codec_name":"h264","width":1280,"height":720,"r_frame_rate":"25/1"}]}
            """,
            ".cmd");

        var info = StreamProbe.Probe("rtsp://10.0.0.1/live.sdp", stub, TimeSpan.FromSeconds(10));

        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(25, info.Fps);
    }

    [Fact]
    public void Probe_PassesRwTimeout_SoHungEndpointFailsPredictably()
    {
        // 沒有 -rw_timeout 時，ffprobe 對「TCP 可連線但不再回應」的端點會反覆重試，
        // 只能靠外層逾時硬砍（實測死端點曾停滯 5 分鐘以上才被外層砍掉）。
        // 這裡驗證 ffprobe 確實收到與外層逾時一致的 I/O 邊界。
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-ffprobe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var argsFile = Path.Combine(dir, "args.txt");
        var stub = Path.Combine(dir, "ffprobe.cmd");
        File.WriteAllText(
            stub,
            $$"""
            @echo off
            echo %* > "{{argsFile}}"
            echo {"streams":[{"codec_type":"video","codec_name":"h264","width":640,"height":480,"r_frame_rate":"15/1"}]}
            """,
            Encoding.ASCII);

        _ = StreamProbe.Probe("rtsp://10.0.0.1/live.sdp", stub, TimeSpan.FromSeconds(7));

        var recorded = File.ReadAllText(argsFile);
        Assert.Contains("-rw_timeout", recorded, StringComparison.Ordinal);

        // 7 秒 = 7,000,000 微秒，必須與傳入的逾時一致。
        Assert.Contains("7000000", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_Cancellation_TerminatesProcessPromptly()
    {
        // 模擬視窗關閉／頻道停止：取消必須立刻殺掉 ffprobe，不能等 45～60 秒的探測逾時，
        // 否則孤兒進程會一直佔住攝影機的 RTSP session。
        //
        // stub 全以 cmd 內建實作（無 powershell）：一出現 `marker` 表示 stub 已啟動（測試端
        // 才開始量測取消即時性），之後持續每 ~1 秒往 `hb.txt` 追加一行（heartbeat）。若取消
        // 未把進程樹殺乾淨，hb.txt 會持續增長——以此代替「讀取 PID 再 WaitForExit」，
        // 免除對進程身份／冷啟動的一切依賴（CI 並行負載下偶發 <60s 仍拉不起 powershell）。
        var markerDir = Path.Combine(Path.GetTempPath(), $"helivms-probe-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(markerDir);
        var marker = Path.Combine(markerDir, "started.marker");
        var heartbeat = Path.Combine(markerDir, "hb.txt");
        var stub = Path.Combine(markerDir, "ffprobe.cmd");
        File.WriteAllText(
            stub,
            $$"""
            @echo off
            @echo ok> "{{marker}}"
            :loop
            @echo tick>> "{{heartbeat}}"
            ping -n 2 127.0.0.1 >nul
            goto loop
            """,
            Encoding.ASCII);

        try
        {
            // 由測試端控制取消時機：先等 marker（stub 已啟動），出現後才取消——如此
            // 「取消→立刻殺樹」的測量與 stub 啟動速度快慢完全解耦（M167 固定 5s 窗在
            // CI 負載下仍偶發不足；舊版另依賴 powershell 寫 PID，故這裡連 marker 都不靠 powershell）。
            using var cts = new CancellationTokenSource();
            Exception? outcome = null;
            var probeTask = Task.Run(
                () =>
                {
                    try
                    {
                        StreamProbe.Probe("rtsp://10.0.0.1/live.sdp", stub, TimeSpan.FromSeconds(60), cts.Token);
                    }
                    catch (Exception ex)
                    {
                        outcome = ex;
                    }
                });

            var markerWait = Stopwatch.StartNew();
            while (!File.Exists(marker) && markerWait.Elapsed < TimeSpan.FromSeconds(60) && !probeTask.IsCompleted)
            {
                Thread.Sleep(100);
            }

            Assert.True(File.Exists(marker),
                $"stub 未在 {markerWait.Elapsed.TotalSeconds:F1}s 內寫出 marker（啟動階段）");

            var cancelSw = Stopwatch.StartNew();
            cts.Cancel();
            await probeTask;
            cancelSw.Stop();

            var opEx = Assert.IsType<OperationCanceledException>(outcome);
            Assert.Equal(cts.Token, opEx.CancellationToken);
            Assert.True(cancelSw.Elapsed < TimeSpan.FromSeconds(10),
                $"取消應近即時生效（取消至擲回），實際 {cancelSw.Elapsed.TotalSeconds:F1}s");

            // 探測進程樹必須已被終止：預留在途寫入時間後，heartbeat 應停止增長（否則有孤兒進程殘留）。
            Thread.Sleep(2500);
            var lenBefore = File.Exists(heartbeat) ? new FileInfo(heartbeat).Length : 0;
            Thread.Sleep(2000);
            var lenAfter = File.Exists(heartbeat) ? new FileInfo(heartbeat).Length : 0;
            Assert.True(lenAfter == lenBefore,
                $"探測進程樹仍存活（heartbeat 持續增長 {lenBefore}→{lenAfter} bytes）");
        }
        finally
        {
            try { Directory.Delete(markerDir, recursive: true); }
            catch { /* 清理失敗不影響測試結果 */ }
        }
    }

    private static string CreateStub(string body, string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-ffprobe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ffprobe{extension}");
        File.WriteAllText(path, body, Encoding.ASCII);
        return path;
    }
}
