using System.Diagnostics;
using System.Globalization;
using HeliVMS.Shared;

namespace HeliVMS.Media;

/// <summary>
/// 讀出一個已收尾媒體檔「自己的」長度（秒）。
/// </summary>
/// <remarks>
/// <para>
/// 錄影段的長度過去是用 ffmpeg 行程的牆鐘時間差（開始錄 → 收尾結束）記進索引的。那組數字
/// 量的不是影片，而是**這台機器被排程到多少時間**：正常情況下暖機讓它多出 0.2～1.0 秒，
/// 而只要 Windows 睡眠、時間被校時或使用者手動調過時鐘，同一段的牆鐘時間就會膨脹到幾千秒。
/// 實測現場索引：68 段裡有 15 段與檔案實際長度差超過 60 秒，最壞的兩段記著 6853 秒、
/// 6849 秒，檔案其實只有 250／260 秒；另有 4 段記著 99～200 秒，而檔案是 0.07～0.34 秒。
/// </para>
/// <para>
/// 後果全在畫面上：時間軸色帶拉出幾小時的區塊、方向鍵一跳就落到沒有影像的地方、
/// 多路回放拿一段 250 秒的檔去對一個宣稱 6853 秒的區間，每一格都在等一個不存在的時刻。
/// </para>
/// <para>
/// 容器自己的長度是唯一可信的答案（<c>ffprobe -show_entries format=duration</c>），而且
/// 錄影輸出是 fMP4（<c>frag_keyframe+empty_moov</c>），ffmpeg 正常收尾時仍會寫出
/// <c>mehd</c>／<c>mvhd</c>，實測索引與探測值的差正好是暖機那 0.2～0.6 秒。
/// </para>
/// <para>
/// 任何失敗（ffprobe 不在 PATH、檔案毀損、逾時）都回 <c>null</c>，由呼叫端退回牆鐘長度：
/// 這條路徑在錄影收尾的關鍵時刻執行，探測失敗絕不能讓一段已經寫完的錄影變成 corrupt。
/// </para>
/// </remarks>
public static class MediaDurationProbe
{
    /// <summary>
    /// 探測逾時。只讀容器索引與檔尾，正常在數十毫秒到一、兩秒內完成（300 秒的段約 1 秒）；
    /// 30 秒是「磁碟很慢、防毒正在掃這個檔」的兜底，逾時才不會讓一段收尾卡住整個錄影迴圈。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 合理的長度上限（24 小時）。超過就是探到了錯的檔案或壞掉的索引，不是一段錄影。
    /// 與 <see cref="RecordedSegmentLength.MaxPlausibleSeconds"/> 共用同一個數字。
    /// </summary>
    public const double MaxPlausibleSeconds = RecordedSegmentLength.MaxPlausibleSeconds;

    /// <summary>回傳檔案自己的秒數；探測不到、讀不懂、逾時或數值不合理時回 <c>null</c>。</summary>
    /// <param name="filePath">媒體檔路徑。</param>
    /// <param name="ffprobePath">ffprobe 路徑；null 時用 <see cref="FfmpegLocator.Ffprobe"/>。</param>
    /// <param name="timeout">探測逾時；null 時用 <see cref="DefaultTimeout"/>。</param>
    public static double? ProbeSeconds(
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
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                TryKill(proc);
                return null;
            }

            // WaitForExit 之後非同步讀取可能還差最後一段；給它一個短暫的上限才不會漏掉數字。
            if (!stdout.IsCompleted)
            {
                _ = stdout.Wait(TimeSpan.FromSeconds(2));
            }

            return proc.ExitCode == 0 && stdout.IsCompletedSuccessfully
                ? Normalize(Parse(stdout.Result))
                : null;
        }
        catch (Exception)
        {
            // ffprobe 不存在（精簡安裝）、檔案被鎖住：讓呼叫端退回牆鐘長度即可。
            return null;
        }
    }

    /// <summary>解析 <c>format=duration</c> 的單行輸出（<c>N/A</c> 或空白視為讀不到）。</summary>
    public static double? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var text = output.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Normalize(value)
            : null;
    }

    private static double? Normalize(double? value)
    {
        if (value is not { } seconds
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds)
            || seconds <= 0
            || seconds > MaxPlausibleSeconds)
        {
            return null;
        }

        return seconds;
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
            // 已經結束或無權限；逾時路徑仍回 null。
        }
    }
}
