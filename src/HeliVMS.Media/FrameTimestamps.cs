using System.Diagnostics;
using System.Globalization;
using HeliVMS.Shared;

namespace HeliVMS.Media;

/// <summary>
/// 影片檔每一格的呈現時刻（秒；相對於檔案自己的時間原點）。
/// </summary>
/// <remarks>
/// <para>
/// 回放的節奏與「這一格是哪一刻」過去是用**影格序號 ÷ 名目幀率**（<c>r_frame_rate</c>）推算的，
/// 只有在檔案真的是定幀、而且相機真的照名目速率送格時才成立。現場的相機不是這樣：同一台
/// 錄下來的段落實測每格間距混著 67ms 與 100ms（有效 12.5fps，名目 15fps），另一台名目 30fps
/// 卻只送 13fps。於是每一格畫面都比名目時間**早**抵達，多路回放時每台以自己的速率前進
/// ——現場回報的「各播各的」——而每一格的 OSD 時刻也跟著失真。
/// </para>
/// <para>
/// 這裡改讀**檔案自己的封包時間戳**（<c>ffprobe -show_entries packet=pts_time</c>）：
/// 它同時是「這一格是哪一刻」的正確答案，也是節奏的唯一依據。相機斷流造成的空隙會如實
/// 呈現為畫面停住，而不是被壓縮成幾格快轉——那會讓兩台看似同步卻在播不同時刻。
/// </para>
/// <para>
/// 任何失敗（ffprobe 不在、檔案毀損、逾時）都回空清單：呼叫端退回固定幀率節奏，
/// 回放照播不誤。這是「時間基準」的最佳化路徑，不能讓一次探測失敗把畫面整段關掉。
/// </para>
/// </remarks>
public static class FrameTimestamps
{
    /// <summary>探測逾時（ffprobe 只讀索引與封包標頭，正常在數十毫秒內完成）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>可接受的最大封包數；超過視為異常檔案（或截斷的輸出），回空清單讓呼叫端退回舊節奏。</summary>
    public const int MaxPackets = 4_000_000;

    /// <summary>讀出每一格的呈現時刻（秒），以呈現順序排序；讀不到時回空清單。</summary>
    public static IReadOnlyList<double> Read(
        string filePath,
        string? ffprobePath = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath ?? FfmpegLocator.Ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("packet=pts_time");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return [];
            }

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            var limit = (int)(timeout ?? DefaultTimeout).TotalMilliseconds;
            if (!proc.WaitForExit(limit))
            {
                TryKill(proc);
                return [];
            }

            // WaitForExit 之後非同步讀取可能還差最後一段；給它一個短暫的上限才不會漏掉尾巴。
            if (!stdout.IsCompleted)
            {
                _ = stdout.Wait(TimeSpan.FromSeconds(2));
            }

            _ = stderr;
            return proc.ExitCode == 0 && stdout.IsCompletedSuccessfully
                ? Parse(stdout.Result)
                : [];
        }
        catch (Exception)
        {
            // 探測失敗不是回放的致命錯誤：退回名目幀率節奏即可。
            return [];
        }
    }

    /// <summary>
    /// 解析 ffprobe 的 <c>csv=p=0</c> 輸出（每行一個 <c>pts_time</c>）。
    /// </summary>
    /// <remarks>
    /// 封包是**解碼順序**（B 影格會亂序），而解出的影格是呈現順序，所以必須排序；
    /// 讀不到時間戳的封包（<c>N/A</c>）逐行略過而不是讓整份清單失效。
    /// </remarks>
    public static IReadOnlyList<double> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var times = new List<double>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value)
                || double.IsInfinity(value))
            {
                continue;
            }

            times.Add(value);
            if (times.Count > MaxPackets)
            {
                return [];
            }
        }

        times.Sort();
        return times;
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 已經結束或無權限；逾時路徑仍回空清單。
        }
    }
}
