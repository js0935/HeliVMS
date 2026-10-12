using System.Collections.Concurrent;
using System.Diagnostics;

namespace HeliVMS.Media;

/// <summary>
/// 硬體解碼能力偵測（§13-1 / §3.3 D4）。
/// <see cref="RtspClient"/> 在啟用 <c>-hwaccel</c> 前，先問本機 ffmpeg 支援哪些加速方式；
/// 若設定的值不在清單內就當作未啟用——避免「有對應 GPU/DLL 才有畫面，否則連得上卻沒畫面
/// 且日誌乾淨」這個最難診斷的失效模式。
/// </summary>
public static class FfmpegHwAccel
{
    // 同一台機器上所有頻道共用同一份 ffmpeg，探測結果也共用：避免 32 路各自
    // 開一次 ffmpeg 只為了問同一件事。值為空集合代表「探測失敗／無法得知」。
    private static readonly ConcurrentDictionary<string, IReadOnlyCollection<string>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 <c>ffmpeg -hwaccels</c> 的輸出：第一行為標題（以冒號結尾）「Hardware acceleration methods:」，
    /// 其後每行一個方法名。忽略空行、標題與含空白的行；結果轉小寫（呼叫端比對不分大小寫）。
    /// </summary>
    public static IReadOnlyCollection<string> ParseHwAccels(string? output)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return set;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.EndsWith(':'))
            {
                continue;
            }

            // 方法名是不含空白的單詞；含空白的行（例如警告訊息）一律略過。
            if (line.IndexOf(' ') >= 0 || line.IndexOf('\t') >= 0)
            {
                continue;
            }

            set.Add(line.ToLowerInvariant());
        }

        return set;
    }

    /// <summary>
    /// 是否應套用 <paramref name="requested"/> 加速值（已由
    /// <see cref="RtspClient.NormalizeHwAccel"/> 正規化，<c>null</c>＝停用）。
    /// <c>auto</c> 一律允許（交給 ffmpeg 自行挑選）；清單為空（探測失敗／無法得知）時也允許，
    /// 因為此時無從判斷，交由執行期 fallback 兜底——寧可試了再退，也不要因探測失敗而靜默停用。
    /// </summary>
    public static bool ShouldUse(string? requested, IReadOnlyCollection<string> available)
    {
        if (requested is null)
        {
            return false;
        }

        if (requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (available is null || available.Count == 0)
        {
            return true;
        }

        // 不倚賴呼叫端集合的比較子（可能是不分大小寫的 HashSet，也可能不是）。
        foreach (var method in available)
        {
            if (method.Equals(requested, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>執行 <c>ffmpeg -hwaccels</c> 取得本機支援的加速方式；逾時或任何失敗回傳空集合。</summary>
    public static IReadOnlyCollection<string> Probe(string ffmpegPath, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 第一優先參數固定為 -hide_banner，讓探測呼叫的 %2 為 -hwaccels，
            // 測試 stub 可據此區分「探測」與「解碼」兩種呼叫。
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-hwaccels");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return Array.Empty<string>();
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已結束
                }

                return Array.Empty<string>();
            }

            return ParseHwAccels(stdout);
        }
        catch (Exception)
        {
            // ffmpeg 不存在／無法啟動：視為無法得知。
            return Array.Empty<string>();
        }
    }

    /// <summary>同上但依 ffmpeg 路徑快取；同一路徑只探測一次。</summary>
    public static IReadOnlyCollection<string> ProbeCached(string ffmpegPath, TimeSpan timeout) =>
        Cache.GetOrAdd(ffmpegPath, p => Probe(p, timeout));
}
