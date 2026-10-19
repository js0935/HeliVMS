using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 解碼參數的每個選項都必須被「這台機器上實際安裝的 ffmpeg」接受。
/// </summary>
/// <remarks>
/// <para>
/// 釘住參數清單字串的單元測試擋不住「選項在新版 ffmpeg 被移除」這種版本漂移：
/// ffmpeg 9.0 移除了 <c>-vsync</c>（9.0.2 直接以 <c>Unrecognized option 'vsync'</c> 結束），
/// 而本機常見的 8.0 仍然接受。回歸因此在本機全綠、在 CI（choco 裝的正是 9）全紅，
/// 現場則是即時監看變成永無止境的重連、永遠沒有畫面，日誌只有一行 Unrecognized option。
/// </para>
/// <para>
/// 這裡不真的解一段串流，而是問實際安裝的 ffmpeg「你認得這個選項嗎」：解碼需要真實 RTSP
/// 來源（<c>-rtsp_transport</c> 對檔案輸入會被 ffmpeg 拒絕），而這條測試要驗的只是選項存在。
/// 對照組是 <c>RtspClientTests</c> 的字串斷言：那一條在本機就會紅，這一條在換 ffmpeg 版本時紅。
/// </para>
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class DecodeArgumentsFfmpegTests
{
    /// <summary>監看路徑的解碼參數不得含已移除的選項（例如 ffmpeg 9 移除的 -vsync）。</summary>
    [SkippableFact]
    public void 監看解碼參數的每個選項都被本機ffmpeg接受()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過選項相容性檢查。");

        var args = RtspClient.BuildDecodeArguments("rtsp://127.0.0.1:554/live");
        var options = args.Where(arg => arg.StartsWith('-')).Distinct().ToArray();
        Assert.NotEmpty(options);

        foreach (var option in options)
        {
            Assert.True(
                FfmpegInstall.KnowsOption([help], option),
                $"本機 ffmpeg（{FfmpegLocator.Ffmpeg}）不認得 {option}："
                + "參數被新版本移除時，即時監看會變成不斷重連且沒有任何畫面，日誌只有 Unrecognized option。");
        }
    }
}
