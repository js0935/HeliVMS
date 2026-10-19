using System.ComponentModel;
using System.Diagnostics;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 「這台機器上有沒有可用的 ffmpeg／libx264」的共用探測。
/// </summary>
/// <remarks>
/// 與 Rtc.Tests 的 <c>PublishPipelineProbe</c> 同一個理由：<c>Skip.IfNot</c> 每次都重算條件，
/// 不快取就等於每支測試各啟一次 <c>ffmpeg -version</c>；而「有 ffmpeg」與「這個 ffmpeg 有
/// libx264」是兩種不同能力，混在一起會讓測試在只缺編碼器時被錯誤地略過（或反過來）。
/// 這一份是 Recording 專案的副本：xunit 的測試輔助型別無法跨 assembly 共用。
/// </remarks>
internal static class RecordingVideoProbe
{
    private static readonly Lazy<bool> Ffmpeg = new(DetectFfmpeg, isThreadSafe: true);
    private static readonly Lazy<bool> X264 = new(DetectLibx264, isThreadSafe: true);

    /// <summary>ffmpeg 可否啟動。</summary>
    internal static bool FfmpegAvailable => Ffmpeg.Value;

    /// <summary>這個 ffmpeg 是否帶 libx264（產生測試片段與解碼驗收的必要條件）。</summary>
    internal static bool Libx264Available => X264.Value;

    internal static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info)!;
    }

    private static bool DetectFfmpeg()
    {
        try
        {
            using var probe = Start("-hide_banner", "-loglevel", "error", "-version");
            return probe.WaitForExit(20_000) && probe.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // PATH 上沒有 ffmpeg：預期的「略過」，不是失敗。
            return false;
        }
    }

    private static bool DetectLibx264()
    {
        if (!FfmpegAvailable)
        {
            return false;
        }

        try
        {
            using var probe = Start("-hide_banner", "-loglevel", "error", "-encoders");
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
