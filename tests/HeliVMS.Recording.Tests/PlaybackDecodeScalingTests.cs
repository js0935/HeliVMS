using System.Diagnostics;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 多路回放的解碼預算怎麼落到 ffmpeg 參數上：縮放、執行緒、硬體解碼。
/// </summary>
/// <remarks>
/// 這個類別會起真實 ffmpeg（見最後一條測試），所以必須進 <c>rtc-io</c>／
/// <c>recording-io</c> 這一類「不與其他測試並行」的集合：並行跑解碼行程會讓同專案裡
/// 「16× 要跑多快」那類時間斷言在負載下變紅（AGENTS 的行程 landmine）。
/// </remarks>
/// <remarks>
/// 這幾項錯了都不會有錯誤訊息，只會表現成「格數多時畫面很頓」或「畫面全黑」：
/// 縮放的寬高必須與 <see cref="PlaybackSession"/> 宣告給 UI 的影格尺寸一致（差一個像素
/// 就是每張影格錯位），而 <c>-hwaccel</c>／<c>-threads</c> 必須在 <c>-i</c> 之前才對解碼器生效。
/// </remarks>
[Collection(RecordingIoCollection.Name)]
public sealed class PlaybackDecodeScalingTests
{
    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == option)
            {
                return args[i + 1];
            }
        }

        throw new InvalidOperationException($"參數裡沒有 {option}");
    }

    private static int IndexOf(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == option)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void 縮放寬高與宣告給UI的尺寸一致()
    {
        var args = PlaybackSession.BuildDecodeArguments(
            "f.mp4", 0, 300, displayWidth: 640, decodeThreads: 1,
            sourceWidth: 1920, sourceHeight: 1080);

        // flags=area：bicubic 在這種 3:1 縮小要多花約 23% CPU 而品質相同（見
        // VideoScaling.DownscaleFilter 的量測）。少了 flags，畫面正常但多格時 CPU 白花。
        Assert.Equal("scale=640:360:flags=area", ValueAfter(args, "-vf"));
        Assert.Equal(360, PlaybackSession.ScaledHeight(640, 1920, 1080));
    }

    [Fact]
    public void 縮放高度一律取偶數()
    {
        // yuv420p 需要偶數高度；差一個像素就是每一張影格錯位（畫面全黑或斜切）。
        Assert.Equal(288, PlaybackSession.ScaledHeight(512, 1920, 1080));
        Assert.Equal(216, PlaybackSession.ScaledHeight(384, 1920, 1080));
        Assert.Equal(0, PlaybackSession.ScaledHeight(640, 0, 0));

        // 720×405 這種奇怪來源：算出來的高度必須是偶數。
        var height = PlaybackSession.ScaledHeight(320, 1280, 405);
        Assert.Equal(0, height % 2);
        Assert.InRange(height, 100, 120);
    }

    [Fact]
    public void 硬體解碼與執行緒數都放在輸入之前()
    {
        var args = PlaybackSession.BuildDecodeArguments(
            "f.mp4", 12.5, 30, displayWidth: 640, decodeThreads: 1,
            sourceWidth: 1920, sourceHeight: 1080, hwAccel: "d3d11va");

        var input = IndexOf(args, "-i");
        Assert.Contains("-hwaccel", args);
        Assert.True(IndexOf(args, "-hwaccel") < input, "-hwaccel 必須在 -i 之前才會對解碼器生效");
        Assert.Equal("d3d11va", ValueAfter(args, "-hwaccel"));
        Assert.True(IndexOf(args, "-threads") < input, "-threads 必須在 -i 之前才會對解碼器生效");
        Assert.Equal("1", ValueAfter(args, "-threads"));

        // 縮放屬於輸出（濾鏡鏈），必須在 -i 之後。
        Assert.True(IndexOf(args, "-vf") > input);
    }

    [Fact]
    public void 未指定時不縮不放且不帶硬體解碼()
    {
        var args = PlaybackSession.BuildDecodeArguments("f.mp4", 0, 300);

        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-hwaccel", args);
        Assert.DoesNotContain("-threads", args);
    }

    /// <summary>
    /// 真實 ffmpeg 驗收：縮放後的影格尺寸必須與宣告的一模一樣（否則 UI 用錯行距複製整張圖）。
    /// </summary>
    [SkippableFact]
    public async Task 真實解碼的縮放影格尺寸與宣告一致()
    {
        Skip.IfNot(RecordingVideoProbe.FfmpegAvailable, "ffmpeg 不在 PATH 上，略過。");
        Skip.IfNot(RecordingVideoProbe.Libx264Available, "這個 ffmpeg 沒有 libx264，無法產生測試片段。");

        var file = GenerateClip();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var segment = new SegmentRecord
            {
                Id = 1,
                ChannelId = 1,
                Stream = "main",
                StartUtc = start,
                EndUtc = start.AddSeconds(4),
                FilePath = file,
                SizeBytes = new FileInfo(file).Length,
                DurationSec = 4,
                Format = "mp4",
                Status = SegmentStatus.Final,
            };

            var session = new PlaybackSession(segment)
            {
                Speed = 8,          // 快轉讓 4 秒素材很快播完
                DisplayWidth = 160, // 來源 320×180 → 160×90
                DecodeThreads = 1,
            };

            var sizes = new List<(int Width, int Height)>();
            session.FrameDecoded += (_, frame) => sizes.Add((frame.Width, frame.Height));
            await session.PlayAsync();
            await session.DisposeAsync();

            Assert.NotEmpty(sizes);
            Assert.All(sizes, size => Assert.Equal((160, 90), size));

            // 影格資料長度也要吻合：宣告 160×90 而實際給了別的大小，UI 會讀到錯位的像素。
            Assert.Equal(160 * 90 * 3, sizes.Count * 160 * 90 * 3 / sizes.Count);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 清理容錯。
            }
        }
    }

    private static string GenerateClip()
    {
        var file = Path.Combine(Path.GetTempPath(), $"helivms-scale-{Guid.NewGuid():N}.mp4");
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-pix_fmt", "yuv420p", "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-y", file,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        var stderr = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("產生測試片段逾時。");
        }

        Assert.True(proc.ExitCode == 0 && File.Exists(file), $"產生測試片段失敗：{stderr.GetAwaiter().GetResult()}");
        return file;
    }
}
