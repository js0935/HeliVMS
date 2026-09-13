using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HeliVMS.Media;

/// <summary>串流探測結果。</summary>
public sealed record StreamProbeInfo(int Width, int Height, string VideoCodec, string? AudioCodec);

/// <summary>
/// 以 ffprobe 探測 RTSP 串流資訊（供監看與錄影共用）。
/// </summary>
public static class StreamProbe
{
    /// <summary>探測主流視訊解析度與音訊編碼。</summary>
    public static StreamProbeInfo Probe(string rtspUrl, string? ffprobePath = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath ?? "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-rtsp_transport");
        psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_type,codec_name,width,height");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(rtspUrl);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffprobe");
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 探測失敗：{rtspUrl}");
        }

        using var doc = JsonDocument.Parse(output);
        var width = 0;
        var height = 0;
        var videoCodec = string.Empty;
        string? audioCodec = null;

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
            }
            else if (t == "audio")
            {
                audioCodec = stream.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null;
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"無法取得解析度：{rtspUrl}");
        }

        return new StreamProbeInfo(width, height, videoCodec, audioCodec);
    }
}