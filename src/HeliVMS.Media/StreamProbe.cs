using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared;

namespace HeliVMS.Media;

/// <summary>串流探測結果。</summary>
public sealed record StreamProbeInfo(
    int Width,
    int Height,
    string VideoCodec,
    string? AudioCodec,
    double Fps = 25);

/// <summary>
/// 以 ffprobe 探測 RTSP 串流資訊（供監看與錄影共用）。
/// </summary>
public static class StreamProbe
{
    /// <summary>探測逾時上限。RTSP 連線若無回應，ffprobe 可能永久停滯，故必須逾時並終止進程。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

/// <summary>探測主流視訊解析度與音訊編碼。</summary>
    /// <param name="rtspUrl">RTSP 位址；錯誤訊息會先遮蔽帳密。</param>
    /// <param name="ffprobePath">ffprobe 執行檔路徑（預設自 PATH 取得）。</param>
    /// <param name="timeout">逾時上限；逾時會終止進程並擲出 <see cref="TimeoutException"/>。</param>
    /// <param name="cancellationToken">
    /// 取消時立刻終止 ffprobe 進程並擲出 <see cref="OperationCanceledException"/>；
    /// 用於視窗關閉／頻道停止時避免殘留孤兒進程佔住 RTSP session 直到逾時。
    /// </param>
    public static StreamProbeInfo Probe(
        string rtspUrl,
        string? ffprobePath = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // 位址可能含帳密，錯誤訊息一律使用遮蔽後的形式。
        var safeUrl = RtspUri.Redact(rtspUrl);

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath ?? "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        var limit = timeout ?? DefaultTimeout;

        // 加上 -rw_timeout 讓 ffmpeg 內部的 I/O 等待有明確邊界。
        // 沒有它時，ffprobe 對「已連線但不再回應」的端點會反覆重試，
        // 每次都只能靠外層逾時硬砍，既慢又不準確。
        var rwTimeoutUs = (long)Math.Clamp(limit.TotalMilliseconds * 1000, 1_000, int.MaxValue);
        psi.ArgumentList.Add("-rw_timeout");
        psi.ArgumentList.Add(rwTimeoutUs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        psi.ArgumentList.Add("-rtsp_transport");
        psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_type,codec_name,width,height,r_frame_rate");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(rtspUrl);

using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffprobe");

        // 同時抽乾 stdout/stderr，避免任一管線填滿而使進程阻塞。
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        // 同時等待「進程結束／逾時／取消」三者之一：取消必須能立刻終止 ffprobe，
        // 否則視窗關閉或頻道停止時，孤兒進程會佔住攝影機的 RTSP session 直到逾時。
        var deadline = DateTime.UtcNow + limit;
        while (!proc.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(proc);
                throw new OperationCanceledException(cancellationToken);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                TryKill(proc);
                throw new TimeoutException($"ffprobe 探測逾時（{limit.TotalSeconds:0.#} 秒）：{safeUrl}");
            }

            proc.WaitForExit((int)Math.Min(remaining.TotalMilliseconds, 100));
        }

        // WaitForExit(int) 回傳後再等待非同步讀取收尾，確保輸出完整。
        var output = stdout.GetAwaiter().GetResult();
        _ = stderr;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 探測失敗：{safeUrl}");
        }

        using var doc = JsonDocument.Parse(output);
        var width = 0;
        var height = 0;
        var videoCodec = string.Empty;
        string? audioCodec = null;
        var fps = 25.0;

        foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var type))
            {
                continue;
            }

            var t = type.GetString();
            if (t == "video")
            {
                if (stream.TryGetProperty("width", out var w))
                {
                    width = w.GetInt32();
                }

                if (stream.TryGetProperty("height", out var h))
                {
                    height = h.GetInt32();
                }

                videoCodec = stream.TryGetProperty("codec_name", out var vc) ? vc.GetString() ?? string.Empty : string.Empty;
                if (stream.TryGetProperty("r_frame_rate", out var rate))
                {
                    fps = ParseFrameRate(rate.GetString());
                }
            }
            else if (t == "audio")
            {
                audioCodec = stream.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null;
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"無法取得解析度：{safeUrl}");
        }

        return new StreamProbeInfo(width, height, videoCodec, audioCodec, fps);
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
            // 進程已結束或無權限；逾時例外仍會往上拋。
        }
    }

    /// <summary>解析 ffprobe 之 "15/1"、「30/1」等幀率字串。</summary>
    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 25;
        }

        var parts = value.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var num))
        {
            return 25;
        }

        return num;
    }
}