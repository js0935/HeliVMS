using System.Text.RegularExpressions;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 產品程式碼裡直接傳給 <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> 的每個
/// ffmpeg／ffprobe 選項，都必須被這台機器上實際安裝的 ffmpeg 或 ffprobe 認得。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要全 repo 掃而不是逐條驗參數清單：整個產品有十多處各自組 ffmpeg 參數（錄影、回放、
/// 匯出、去扭曲、遮蔽、對講、邊緣回補、探測），每一處的參數清單又多半是內嵌在啟動行程的
/// 程式碼裡、沒有可以單獨呼叫的組參數函式。正是這種分散讓 ffmpeg 9 移除 <c>-vsync</c> 時，
/// 只有真的有整合測試跑到的那一條（即時監看解碼）會紅，其他路徑靜靜地在本機全綠、
/// 在裝了 ffmpeg 9 的機器上一啟動就 <c>Unrecognized option</c> 結束。
/// </para>
/// <para>
/// 本測試的接縫就是「選項字面值」本身：抓到的是 <c>ArgumentList.Add("-x")</c> 這種直接寫死的
/// 單行字面值，然後問實際安裝的工具認不認得。因為掃描是原始碼文字比對，所以下面有兩個
/// 反向斷言：掃到的數量必須合理、且必須涵蓋已知檔案裡的已知選項——否則正則或路徑哪天壞掉，
/// 這條測試會空轉變成永遠綠。
/// </para>
/// <para>
/// 對照組是 <c>DecodeArgumentsFfmpegTests</c>：那一條驗的是 ffmpeg 解碼路徑的完整參數清單
/// （含位置與值），這一條驗的是所有路徑的選項存在。ffmpeg 不在 PATH 上時自我略過。
/// </para>
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class FfmpegOptionLiteralsFfmpegTests
{
    /// <summary>產品程式碼裡寫死的 ffmpeg／ffprobe 選項都要是本機工具認得的。</summary>
    [SkippableFact]
    public void 產品程式碼裡的ffmpeg選項字面值都被本機工具接受()
    {
        var literals = CollectOptionLiterals();

        // 反向斷言：掃描本身要有效。抓不到選項（正則被改壞、目錄搬走）時下面會變成空轉全綠，
        // 那比沒有這條測試更糟，所以先確定它真的看到東西。
        Assert.True(literals.Count >= 15, $"只從產品程式碼掃到 {literals.Count} 個 ffmpeg 選項，掃描本身可能壞了。");
        Assert.Contains("-rtsp_transport", literals);
        Assert.Contains("-movflags", literals);

        var helpTexts = FfmpegInstall.HelpTexts();
        Skip.If(helpTexts.Count == 0, "PATH 上沒有可啟動的 ffmpeg／ffprobe，略過選項相容性檢查。");

        foreach (var option in literals.OrderBy(o => o, StringComparer.Ordinal))
        {
            Assert.True(
                FfmpegInstall.KnowsOption(helpTexts, option),
                $"本機的 ffmpeg／ffprobe 都不認得 {option}："
                + "選項被新版本移除時，走到那條路徑的功能會直接以 Unrecognized option 結束，"
                + "而本機舊版 ffmpeg 的測試仍會全綠。");
        }
    }

    /// <summary>
    /// 走訪 <c>src/**/*.cs</c>，收集直接寫死的 <c>ArgumentList.Add("-x")</c> 字面值。
    /// </summary>
    /// <remarks>
    /// 只認單行、非插值、非逐字字串中緊接在 <c>ArgumentList.Add(</c> 後面的字面值：變數與插值
    /// 組出來的參數（例如 <c>-ss</c> 的時間值、組出來的濾鏡字串）本來就不是固定選項，掃不到是對的。
    /// </remarks>
    private static List<string> CollectOptionLiterals()
    {
        var options = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new Regex("""ArgumentList\.Add\(\s*"([^"]*)"\s*\)""");
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var literal = match.Groups[1].Value;
                if (literal.Length > 1 && literal[0] == '-' && literal[1] != '-')
                {
                    options.Add(literal);
                }
            }
        }

        return [.. options];
    }

    /// <summary>往上找出含 <c>HeliVMS.slnx</c> 的目錄（測試建置輸出在 <c>tests/*/bin/&lt;config&gt;/&lt;tfm&gt;</c>）。</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HeliVMS.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到 HeliVMS.slnx，無法定位 repo 根目錄。");
    }
}
