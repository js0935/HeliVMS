using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 音訊拉取參數的每個選項都必須被「這台機器上實際安裝的 ffmpeg」接受。
/// </summary>
/// <remarks>
/// 與 <see cref="DecodeArgumentsFfmpegTests"/> 同一個理由：字串斷言擋不住版本漂移，
/// 被移除的選項會讓 ffmpeg 立刻結束，而症狀是「按了沒聲音」、日誌只有一行
/// <c>Unrecognized option</c>。這裡問實際安裝的 ffmpeg「你認得這些選項嗎」。
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class AudioArgumentsFfmpegTests
{
    /// <summary>即時與檔案兩種來源的選項都要被接受（檔案路徑帶 -ss／-t，即時帶 -rtsp_transport）。</summary>
    [SkippableFact]
    public void 音訊拉取參數的每個選項都被本機ffmpeg接受()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過選項相容性檢查。");

        var sets = new[]
        {
            AudioPullClient.BuildArguments("rtsp://127.0.0.1:554/live", live: true),
            AudioPullClient.BuildArguments("seg.mp4", live: false, seekSeconds: 5, durationSeconds: 10),
        };

        foreach (var args in sets)
        {
            foreach (var option in args.Where(arg => arg.StartsWith('-')).Distinct())
            {
                Assert.True(
                    FfmpegInstall.KnowsOption([help], option),
                    $"本機 ffmpeg（{FfmpegLocator.Ffmpeg}）不認得 {option}："
                    + "現場收音會變成『按了沒聲音』，日誌只有 Unrecognized option。");
            }
        }
    }
}
