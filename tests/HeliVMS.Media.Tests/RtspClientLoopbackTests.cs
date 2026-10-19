using System.ComponentModel;
using System.Diagnostics;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 端對端驗收：<see cref="RtspClient"/> 對一條真實的 RTSP 來源（測試自己起的 loopback 伺服器）
/// 能不能完成探測、解碼並進入串流狀態。
/// </summary>
/// <remarks>
/// <para>
/// 其他 RtspClient 測試用假 ffmpeg 腳本模擬逾時、stderr 爆量等病態情形——那些驗的是狀態機，
/// 這條驗的是整條鏈真的會動：真實 ffmpeg 的 RTSP 客戶端協商、RTP H.264 解包、rawvideo 輸出、
/// 加上 <see cref="StreamProbe"/> 的 ffprobe 解析度回報。改壞 SDP 或 RTP 打包（例如把
/// packetization-mode 或 payload type 換掉）時，前面的測試全綠、這裡會紅。
/// </para>
/// <para>
/// ffmpeg 不在 PATH 上時自我略過（<see cref="SkippableFactAttribute"/>），與其他需要 ffmpeg 的測試一致。
/// </para>
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class RtspClientLoopbackTests
{
    private const int Width = 320;
    private const int Height = 240;

    [SkippableFact]
    public async Task 真實RTSP來源能解出影格並進入Streaming狀態()
    {
        var h264 = TryGenerateH264();
        Skip.If(h264 is null, "PATH 上沒有可產生測試串流的 ffmpeg，略過 loopback 驗收。");

        using var server = new RtspLoopbackServer(h264!);
        server.Start();

        var frames = new List<VideoFrame>();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new RtspClient(server.Url, FfmpegLocator.Ffmpeg, FfmpegLocator.Ffprobe)
        {
            ProbeTimeout = TimeSpan.FromSeconds(30),
            FirstFrameTimeout = TimeSpan.FromSeconds(30),
            FrameTimeout = TimeSpan.FromSeconds(20),
        };

        client.FrameDecoded += (_, frame) =>
        {
            lock (frames)
            {
                frames.Add(frame);
                if (frames.Count >= 3)
                {
                    got.TrySetResult();
                }
            }
        };

        await client.StartAsync();
        var completed = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        await client.StopAsync();

        Assert.True(
            completed == got.Task,
            $"loopback 來源沒有解出影格（請求過程：{string.Join(", ", server.Requests)}）");

        lock (frames)
        {
            Assert.True(frames.Count >= 3, $"只解出 {frames.Count} 幀");
            foreach (var frame in frames)
            {
                Assert.Equal(Width, frame.Width);
                Assert.Equal(Height, frame.Height);
                Assert.Equal(Width * Height * 3, frame.Pixels.Length);
            }
        }
    }

    /// <summary>
    /// 用本機 ffmpeg 產生一段 H.264 Annex-B 測試串流；ffmpeg 不存在或編碼失敗時回 <c>null</c>（＝略過）。
    /// </summary>
    /// <remarks>
    /// 刻意在執行時產生而不是把位元流塞進原始碼：硬編碼的位元流會隨編碼器版本過期，而這條測試
    /// 本來就需要本機 ffmpeg，多跑一次 x264 的邊際成本只是幾百毫秒。
    /// </remarks>
    private static byte[]? TryGenerateH264()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", $"testsrc2=size={Width}x{Height}:rate=10",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                "-g", "5", "-keyint_min", "5", "-pix_fmt", "yuv420p",
                "-frames:v", "15", "-f", "h264", "pipe:1",
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            // stdout（位元流）與 stderr 同時抽乾，避免任一管線填滿而互相阻塞。
            using var output = new MemoryStream();
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000))
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }

            copy.GetAwaiter().GetResult();
            _ = errors.GetAwaiter().GetResult();
            var bytes = output.ToArray();
            return proc.ExitCode == 0 && bytes.Length > 0 ? bytes : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
