using HeliVMS.Recording;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 測試用的 fMP4 片段（產生一次、整個測試回合共用）。
/// </summary>
/// <remarks>
/// 刻意用與 <see cref="SegmentRecorder"/> 相同的 <c>-movflags</c>
/// （<c>frag_keyframe+empty_moov+default_base_moof</c>）：解碼與關鍵幀探測都依賴這個
/// 容器佈局，用一般的 MP4 會讓測試「過得去但驗不到真實情境」。
/// </remarks>
internal static class PlaybackClipFixture
{
    /// <summary>片段幀率（與產生參數一致）。</summary>
    internal const int Fps = 25;

    /// <summary>片段長度（秒）。</summary>
    internal const int DurationSec = 10;

    /// <summary>片段總幀數（斷言的下限基準）。</summary>
    internal const int ExpectedFrames = Fps * DurationSec;

    private static readonly Lazy<string> LazyClip = new(Generate, isThreadSafe: true);

    /// <summary>片段檔案路徑（第一次取用時才產生）。</summary>
    internal static string Path => LazyClip.Value;

    private static string Generate()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "helivms-playback-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "testsrc_10s.mp4");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // 測試資源清理容錯：殘檔不該讓回合失敗。
            }
        };

        using var ffmpeg = RecordingVideoProbe.Start(
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"testsrc=duration={DurationSec}:size=320x180:rate={Fps}",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-g", Fps.ToString(),
            "-pix_fmt", "yuv420p",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-y", file);

        if (!ffmpeg.WaitForExit(60_000))
        {
            try
            {
                ffmpeg.Kill(entireProcessTree: true);
            }
            catch
            {
                // 進程可能已結束。
            }

            throw new InvalidOperationException("產生測試片段逾時。");
        }

        if (ffmpeg.ExitCode != 0 || !File.Exists(file))
        {
            throw new InvalidOperationException("產生測試片段失敗。");
        }

        return file;
    }
}
