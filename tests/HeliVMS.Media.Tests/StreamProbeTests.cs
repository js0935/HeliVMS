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
    public void Probe_Cancellation_TerminatesProcessPromptly()
    {
        // 模擬視窗關閉／頻道停止：取消必須立刻殺掉 ffprobe，不能等 45～60 秒的探測逾時，
        // 否則孤兒進程會一直佔住攝影機的 RTSP session。
        var markerDir = Path.Combine(Path.GetTempPath(), $"helivms-probe-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(markerDir);
        var marker = Path.Combine(markerDir, "pid.txt");
        var stub = Path.Combine(markerDir, "ffprobe.cmd");
        File.WriteAllText(
            stub,
            $$"""
            @echo off
            powershell -NoProfile -Command "Set-Content -LiteralPath '{{marker}}' -Value $PID"
            ping -n 60 127.0.0.1 >nul
            """,
            Encoding.ASCII);

        try
        {
            // 取消窗口需給 stub 足夠時間先寫出 PID（powershell -NoProfile 啟動在並行負載下可能需數秒），
            // 但仍遠短於 30s 探測逾時，確保「取消→立刻殺樹」是受測行為。
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
            var sw = Stopwatch.StartNew();

            var ex = Assert.Throws<OperationCanceledException>(
                () => StreamProbe.Probe("rtsp://10.0.0.1/live.sdp", stub, TimeSpan.FromSeconds(30), cts.Token));

            sw.Stop();
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"取消應近即時生效，實際 {sw.Elapsed.TotalSeconds:F1}s");

            // 探測進程樹必須已被終止，不能殘留孤兒進程。
            Assert.True(File.Exists(marker), "stub 未執行到寫 PID 階段");
            var pid = int.Parse(File.ReadAllText(marker).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(WaitForExit(pid, TimeSpan.FromSeconds(5)), $"探測進程 {pid} 仍存活");
        }
        finally
        {
            try { Directory.Delete(markerDir, recursive: true); }
            catch { /* 清理失敗不影響測試結果 */ }
        }
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < timeout)
        {
            try
            {
                _ = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
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
