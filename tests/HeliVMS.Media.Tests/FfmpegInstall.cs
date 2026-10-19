using System.ComponentModel;
using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 查詢「這台機器上實際安裝的」ffmpeg／ffprobe 認得哪些選項：選項相容性測試共用的地方。
/// </summary>
internal static class FfmpegInstall
{
    /// <summary>
    /// 取得工具的 <c>-h full</c> 說明文字；不存在或無法啟動時回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// stdout 與 stderr 必須同時抽乾：說明文字逾 1MB，先讀完一邊會讓另一邊的管線填滿而阻塞。
    /// 不同版本把說明寫在 stdout 或 stderr，兩邊都讀才不必猜。
    /// </remarks>
    public static string? Help(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-h");
            psi.ArgumentList.Add("full");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000))
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }

            var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return text.Length > 0 ? text : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>ffmpeg 與 ffprobe 的說明文字（任一認得選項就算認得）；兩者都不可用時回空清單。</summary>
    public static IReadOnlyList<string> HelpTexts()
    {
        var texts = new List<string>();
        foreach (var tool in new[] { FfmpegLocator.Ffmpeg, FfmpegLocator.Ffprobe })
        {
            if (Help(tool) is { } text)
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    /// <summary>
    /// 說明文字是否認得某個選項。以 token 邊界比對（<c>-map</c> 不能被 <c>-map_metadata</c> 蒙混過關）。
    /// </summary>
    /// <remarks>
    /// 帶串流指定子的選項（例如 <c>-frames:v</c>）在說明文字裡寫成選項本體加可選的指定子
    /// （<c>-frames[:stream_specifier]</c>），所以比對時只用冒號前的本體。
    /// </remarks>
    public static bool KnowsOption(IReadOnlyList<string> helpTexts, string option)
    {
        var name = option.Contains(':') ? option[..option.IndexOf(':')] : option;
        var pattern = $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(name)}(?![A-Za-z0-9_])";
        return helpTexts.Any(text => System.Text.RegularExpressions.Regex.IsMatch(text, pattern));
    }
}
