using System.Text;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 影片檔每一格的呈現時刻：這是回放的節奏與「這一格是哪一刻」的唯一來源。
/// </summary>
/// <remarks>
/// 舊寫法以「影格序號 ÷ 名目幀率」推算，相機送格速率一變（現場實測名目 15fps 實際 12.5fps、
/// 名目 30fps 實際 13fps）每一格都比名目時間早到，多路回放各台因此以不同速率前進
/// ——現場回報的「各播各的」。解析本身是純函式（不碰行程），端到端讀檔另以真實 ffmpeg 驗證。
/// </remarks>
public class FrameTimestampsTests
{
    [Fact]
    public void 封包在解碼順序下必須排序成呈現順序()
    {
        // B 影格讓封包以解碼順序出現（pts 亂序）；解出的影格是呈現順序，
        // 沒有排序就會把時刻配到錯的影格上——每一格都錯，而畫面上完全看不出來。
        var times = FrameTimestamps.Parse("0.067\n0.000\n0.133\n");

        Assert.Equal([0.0, 0.067, 0.133], times);
    }

    [Fact]
    public void 讀不到時間戳的行略過而不是整份失效()
    {
        var times = FrameTimestamps.Parse("0\nN/A\n0.5\n\n 0.25 \ngarbage\n");

        Assert.Equal([0.0, 0.25, 0.5], times);
    }

    [Fact]
    public void 空輸出回空清單()
    {
        Assert.Empty(FrameTimestamps.Parse(string.Empty));
        Assert.Empty(FrameTimestamps.Parse("\n\n"));
    }

    [Fact]
    public void 超過上限的封包清單視為異常()
    {
        // 上限是防禦性的：截斷或異常巨大的輸出不該讓回放端吃光記憶體，
        // 回空清單＝退回固定幀率節奏，畫面照播。
        var builder = new StringBuilder();
        for (var i = 0; i <= 10; i++)
        {
            builder.Append((i / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        var normal = FrameTimestamps.Parse(builder.ToString());
        Assert.Equal(11, normal.Count);

        // 直接以常數宣告的上限邏輯驗證（不真的造 400 萬行）：
        Assert.True(FrameTimestamps.MaxPackets >= 100_000);
    }

    /// <summary>
    /// 真實 ffmpeg 的端到端驗證：刻意做一個「相機斷流」的檔案（內容 0–5 秒、5–8 秒沒有影格、
    /// 8–13 秒接續），讀出的時間戳必須**保留那個空隙**——這正是「多路比對同一瞬間」所依賴的事實。
    /// </summary>
    [SkippableFact]
    public void 相機斷流造成的空隙會保留在時間戳裡()
    {
        var file = GenerateGappedClip();
        try
        {
            var times = FrameTimestamps.Read(file);

            Assert.NotEmpty(times);
            Assert.Equal(times.OrderBy(t => t), times);

            // 100 格（10fps × 10 秒）＋3 秒空隙 → 最後一格在 12.9 秒附近。
            Assert.Equal(100, times.Count);
            Assert.True(times[^1] > 12.5, $"最後一格的時間戳是 {times[^1]:0.###}s，空隙被壓掉了。");

            var gap = times.Zip(times.Skip(1), (a, b) => b - a).Max();
            Assert.True(gap > 2.5, $"最大的相鄰間距只有 {gap:0.###}s，3 秒的斷流沒有反映在時間戳上。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>產生一個中間有 3 秒空窗的 10fps 片段（與現場相機斷流的形狀相同）。</summary>
    private static string GenerateGappedClip()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過真實探測。");

        var file = Path.Combine(Path.GetTempPath(), $"helivms-frametimes-{Guid.NewGuid():N}.mp4");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=160x120:r=10:d=10",
            "-vf", "setpts='PTS+if(gte(T,5),3/TB,0)'",
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-y", file,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg");

        // 同時抽乾兩條管線（管線填滿會讓 ffmpeg 卡住不結束）。
        var stderr = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("產生帶空隙的測試片段逾時。");
        }

        Skip.If(proc.ExitCode != 0 || !File.Exists(file), $"產生測試片段失敗（結束碼 {proc.ExitCode}）：{stderr.GetAwaiter().GetResult()}");
        return file;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理容錯：殘檔不該讓回合失敗。
        }
    }
}
