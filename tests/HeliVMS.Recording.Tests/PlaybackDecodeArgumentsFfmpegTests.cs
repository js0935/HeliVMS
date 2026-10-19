using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HeliVMS.Shared;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放解碼參數的每個選項都必須被「這台機器上實際安裝的 ffmpeg」接受。
/// </summary>
/// <remarks>
/// <para>
/// 這是 Media 端 <c>DecodeArgumentsFfmpegTests</c> 的回放對應版本：兩條解碼路徑各有自己的選項清單，
/// 只驗一邊會讓另一邊的版本漂移（下一個被移除的選項）沒人看著。同樣地，釘住字串的斷言
/// （<c>PlaybackSessionTests</c> 的 <c>-fps_mode</c>／沒有 <c>-vsync</c>）在本機永遠綠，
/// 只有這條問真 ffmpeg 的測試會在換版本時紅。
/// </para>
/// <para>
/// 說明文字讀取器在此重寫一份而不是共用：xunit 的 CollectionDefinition 與測試 helper 都無法跨
/// assembly 共用（<see cref="RecordingIoCollection"/> 也是同一個理由），而兩邊要驗的參數清單不同。
/// </para>
/// </remarks>
[Collection(RecordingIoCollection.Name)]
public sealed class PlaybackDecodeArgumentsFfmpegTests
{
    /// <summary>回放解碼參數不得含已移除的選項（例如 ffmpeg 9 移除的 -vsync）。</summary>
    [SkippableFact]
    public void 回放解碼參數的每個選項都被本機ffmpeg接受()
    {
        var help = FfmpegHelpText();
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過選項相容性檢查。");

        var args = PlaybackSession.BuildDecodeArguments(@"C:\does\not\matter.mp4", 12.5, 30);
        var options = args.Where(arg => arg.StartsWith('-')).Distinct().ToArray();
        Assert.NotEmpty(options);

        foreach (var option in options)
        {
            Assert.True(
                Regex.IsMatch(help, $@"(?<![A-Za-z0-9_]){Regex.Escape(option)}(?![A-Za-z0-9_])"),
                $"本機 ffmpeg（{FfmpegLocator.Ffmpeg}）不認得 {option}："
                + "參數被新版本移除時，拖拽跳轉會變成 0 幀且日誌看起來很乾淨，只有 Unrecognized option 一行。");
        }
    }

    /// <summary>
    /// 秒數必須以不變文化格式化：在德文／法文 Windows 上逗號小數點會讓 ffmpeg 把 <c>-ss 12,5</c>
    /// 讀成別的秒數甚至直接失敗（本機英文全綠，現場只有那台機器出事）。抽成純函式後能直接在
    /// 逗號小數點的文化下驗。
    /// </summary>
    [Fact]
    public void 起點與長度的秒數用不變文化格式化()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("12,5", (12.5).ToString()); // 先確認這個文化真的會產生逗號，否則這條測試沒意義

            var args = PlaybackSession.BuildDecodeArguments("a.mp4", 12.5, 30.25);

            Assert.Equal("12.5", ValueAfter(args, "-ss"));
            Assert.Equal("30.25", ValueAfter(args, "-t"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// 取得 <c>ffmpeg -h full</c> 的說明文字；ffmpeg 不存在或無法啟動時回 <c>null</c>（＝略過）。
    /// </summary>
    /// <remarks>
    /// stdout 與 stderr 必須同時抽乾：說明文字逾 1MB，先讀完一邊會讓另一邊的管線填滿而阻塞。
    /// 不同版本把說明寫在 stdout 或 stderr，兩邊都讀才不必猜。
    /// </remarks>
    private static string? FfmpegHelpText()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg,
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

    private static string? ValueAfter(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == option)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
