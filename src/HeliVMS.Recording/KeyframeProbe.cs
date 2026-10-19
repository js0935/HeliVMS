using System.Diagnostics;
using System.Globalization;
using System.Text;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 以 ffprobe 探測 fMP4 區段的關鍵幀位置（§3.4「播放頭為關鍵幀吸附」）。
/// </summary>
/// <remarks>
/// <para>
/// 資料來源是 <c>-show_entries packet=pts_time,flags,pos</c>：每行 <c>pts_time,flags,pos</c>，
/// <c>flags</c> 含 <c>K</c> 即為關鍵幀。fMP4 的每個 moof 都從關鍵幀開始
/// （SegmentRecorder 以 <c>frag_keyframe</c> 輸出），所以封包的 <c>pos</c>（檔案內位元組位移）
/// 就是該關鍵幀的起始位元組——這是「跳轉前先 seek 到位元組」的地基。
/// </para>
/// <para>
/// 解析與行程本體分開（<see cref="Parse"/>）：單元測試不必真的跑 ffprobe。
/// 探測失敗（檔案損壞、無影片軌）一律回<b>空清單</b>而非丟例外——索引缺了只是時間軸少
/// 一層刻度，不該讓整個回放日載入失敗或整天看不到色帶。
/// </para>
/// </remarks>
public static class KeyframeProbe
{
    /// <summary>單一段探測逾時上限（本地檔案，正常在百毫秒內完成；30 秒是壞檔的兜底）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 探測一個區段檔案的關鍵幀；失敗或逾時回空清單（見類別說明）。
    /// </summary>
    /// <param name="filePath">區段 MP4 路徑。</param>
    /// <param name="ffprobePath">ffprobe 執行檔路徑（預設由 <see cref="FfmpegLocator.Ffprobe"/> 解析）。</param>
    /// <param name="timeout">逾時上限；逾時會終止進程。</param>
    public static IReadOnlyList<SegmentKeyframe> Probe(
        string filePath,
        string? ffprobePath = null,
        TimeSpan? timeout = null)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath ?? FfmpegLocator.Ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("packet=pts_time,flags,pos");
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

            // 同時抽乾 stdout/stderr，避免任一管線填滿而使進程阻塞（同 StreamProbe）。
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            var limit = timeout ?? DefaultTimeout;
            if (!proc.WaitForExit((int)limit.TotalMilliseconds))
            {
                TryKill(proc);
                return [];
            }

            var output = stdout.GetAwaiter().GetResult();
            _ = stderr;
            if (proc.ExitCode != 0)
            {
                return [];
            }

            return Parse(output);
        }
        catch (Exception)
        {
            // ffprobe 不在、被防毒擋下等啟動失敗：一律視為「探不到」，語意同探測失敗。
            return [];
        }
    }

    /// <summary>
    /// 解析 ffprobe 的 CSV 輸出（<c>pts_time,flags,pos</c> 每行一個封包）：
    /// 只取 <c>flags</c> 含 <c>K</c> 的列，依 <c>TimeSec</c> 由小到大並去除同一時刻的重複列
    /// （<c>segment_keyframes</c> 表對 <c>(segment_id, time_sec)</c> 有 UNIQUE 約束，重複列會讓整批
    /// 取代交易失敗）。無法解析的列（<c>N/A</c> 等）靜默跳過：一列壞掉不該讓整段索引失效。
    /// </summary>
    public static IReadOnlyList<SegmentKeyframe> Parse(string stdout)
    {
        var rows = new List<SegmentKeyframe>();
        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < 3
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var timeSec)
                || timeSec < 0 || double.IsNaN(timeSec)
                || !parts[1].Contains('K')
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos))
            {
                continue;
            }

            rows.Add(new SegmentKeyframe(timeSec, pos));
        }

        rows.Sort(static (a, b) => a.TimeSec.CompareTo(b.TimeSec));

        var result = new List<SegmentKeyframe>(rows.Count);
        foreach (var row in rows)
        {
            if (result.Count == 0 || result[^1].TimeSec != row.TimeSec)
            {
                result.Add(row);
            }
        }

        return result;
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
            // 進程已結束或無權限；逾時仍視為探不到。
        }
    }
}
