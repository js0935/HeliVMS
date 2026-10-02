using System.ComponentModel;
using System.Diagnostics;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 「這台機器上有沒有可用的 ffmpeg」的共用探測。
///
/// <para>
/// 抽出來的理由有兩個。其一，<c>SkippableFact</c> 的 <c>Skip.IfNot</c> 每次都會重新
/// 執行條件；不快取就等於每支測試各啟一次 <c>ffmpeg -version</c>（Windows 上接近一秒）。
/// 其二，「有 ffmpeg」與「這個 ffmpeg 有 libx264」是兩個不同的能力，混在一起寫會讓
/// 某些測試在只缺編碼器時被錯誤地略過、或反過來。
/// </para>
/// </summary>
internal static class PublishPipelineProbe
{
    private static readonly Lazy<bool> Ffmpeg = new(DetectFfmpeg, isThreadSafe: true);
    private static readonly Lazy<bool> X264 = new(DetectLibx264, isThreadSafe: true);

    /// <summary>ffmpeg 可否啟動。</summary>
    internal static bool FfmpegAvailable => Ffmpeg.Value;

    /// <summary>這個 ffmpeg 是否帶 <c>libx264</c>（H.264 編碼器的必要條件）。</summary>
    internal static bool Libx264Available => X264.Value;

    internal static Process StartProbe(params string[] probeArgs)
    {
        var info = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var arg in probeArgs) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    private static bool DetectFfmpeg()
    {
        try
        {
            using var probe = StartProbe("-hide_banner", "-loglevel", "error", "-version");
            return probe.WaitForExit(20_000) && probe.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // PATH 上沒有 ffmpeg：這是預期的「略過」，不是失敗。
            return false;
        }
    }

    private static bool DetectLibx264()
    {
        if (!FfmpegAvailable) return false;

        try
        {
            using var probe = StartProbe("-hide_banner", "-loglevel", "error", "-encoders");
            var encoders = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(20_000);
            return encoders.Contains("libx264", StringComparison.Ordinal);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}