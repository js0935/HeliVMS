using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Recording.Talk;

/// <summary>
/// 列出主機的錄音裝置（DirectShow），供「把主機麥克風送到攝影機」選擇來源。
/// </summary>
/// <remarks>
/// <para>
/// 裝置清單只能靠 ffmpeg 自己問（<c>-list_devices</c>），結果寫在 stderr；解析與行程分開，
/// 解析才是會壞的地方（ffmpeg 換版之後措辭變了，清單就空了——空清單會被誤認成「這台機器
/// 沒有麥克風」）。沒有裝置時回空清單並不丟例外：遠端桌面與無麥克風的主機是常態。
/// </para>
/// </remarks>
public static class MicrophoneCatalog
{
    /// <summary>stderr 裡「音訊裝置區段」的開頭；之後的帶引號行才是裝置名稱。</summary>
    public const string AudioSectionMarker = "DirectShow audio devices";

    /// <summary>列出主機的錄音裝置名稱；無法取得（沒有 ffmpeg、沒有裝置）時回空清單。</summary>
    public static IReadOnlyList<string> List(string? ffmpegPath = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath ?? FfmpegLocator.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-list_devices");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("dshow");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("dummy");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            // 兩邊都要抽乾：清單不長，但只讀一邊仍可能在罕見情況下塞滿管線。
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            return Parse(stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            // 沒有 ffmpeg 或裝置列舉失敗：空清單，呼叫端顯示「沒有可用麥克風」。
            return [];
        }
    }

    /// <summary>從 ffmpeg 的輸出解析音訊裝置名稱（純函式，供測試釘住格式）。</summary>
    /// <remarks>
    /// 只認「音訊區段之後」的帶引號行：影片裝置也常標成 both，照單全收會把攝影機的
    /// 名稱混進麥克風清單，選了它對講就是整段無聲。
    /// </remarks>
    public static IReadOnlyList<string> Parse(string output)
    {
        var devices = new List<string>();
        var inAudioSection = false;

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Contains(AudioSectionMarker, StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }

            // 換了新的區段標題（例如「DirectShow video devices」出現在後面）就離開音訊區段。
            if (inAudioSection && text.Contains("DirectShow", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }

            if (!inAudioSection)
            {
                continue;
            }

            var name = ExtractQuoted(text);
            if (!string.IsNullOrWhiteSpace(name) && !devices.Contains(name))
            {
                devices.Add(name);
            }
        }

        return devices;
    }

    /// <summary>取一行裡第一組雙引號內容（dshow 的裝置行格式：<c>"名稱"</c>）。</summary>
    private static string? ExtractQuoted(string line)
    {
        var open = line.IndexOf('"');
        if (open < 0)
        {
            return null;
        }

        var close = line.IndexOf('"', open + 1);
        return close < 0 ? null : line[(open + 1)..close];
    }
}
