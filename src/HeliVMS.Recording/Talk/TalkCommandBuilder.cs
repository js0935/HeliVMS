using HeliVMS.Shared;

namespace HeliVMS.Recording.Talk;

/// <summary>對講相關的設定鍵（app_settings）。</summary>
public static class TalkSettings
{
    /// <summary>「把主機麥克風送到攝影機」要用的錄音裝置名稱（DirectShow）。</summary>
    public const string MicrophoneKey = "talk.microphone";
}

/// <summary>對講音訊的編碼方式。</summary>
public enum TalkCodec
{
    /// <summary>ONVIF 回傳通道慣用的 G.711 A-law、8 kHz、單聲道。</summary>
    Onvif,
}

/// <summary>
/// 組出把聲音推回裝置的 ffmpeg 指令（來源可以是音訊檔或主機麥克風）。
/// </summary>
/// <remarks>
/// <para>
/// 參數刻意集中在此一處。對講要能成立，編碼（ONVIF 回傳通道用 G.711 A-law、8 kHz、單聲道）、
/// 傳輸（RTSP over TCP）與來源節奏缺一不可；散在各處遲早有人漏掉其中一項，
/// 而症狀是「有連上、沒有聲音」且日誌乾淨——最難查的那一種。
/// </para>
/// <para>
/// 檔案來源要帶 <c>-re</c>（照檔速送、不要一次爆送，攝影機的回傳通道緩衝很小）；
/// 麥克風來源<b>不能</b>帶：dshow 本身就是即時時鐘，再套 -re 會讓 ffmpeg 等待送出，
/// 聲音一路延遲越積越多。
/// </para>
/// </remarks>
public static class TalkCommandBuilder
{
    /// <summary>以預設（ONVIF）編碼、依請求的來源組出對講指令。</summary>
    public static TalkCommand Build(TalkRequest request, TalkTarget target)
        => string.IsNullOrWhiteSpace(request.MicrophoneDevice)
            ? Build(request.AudioFilePath, target, TalkCodec.Onvif)
            : BuildLiveMicrophone(request.MicrophoneDevice, target, TalkCodec.Onvif);

    /// <summary>以預設（ONVIF）編碼組出「播放音訊檔」的對講指令。</summary>
    public static TalkCommand Build(string audioFilePath, TalkTarget target)
        => Build(audioFilePath, target, TalkCodec.Onvif);

    /// <summary>以指定編碼組出「播放音訊檔」的對講指令。</summary>
    public static TalkCommand Build(string audioFilePath, TalkTarget target, TalkCodec codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(target);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-re",
            "-i", audioFilePath,
        };

        args.AddRange(EncoderArguments(codec));
        args.AddRange(TransportArguments(target));
        return new TalkCommand(FfmpegLocator.Ffmpeg, args, target.Url);
    }

    /// <summary>組出「把主機麥克風即時送到攝影機」的對講指令（喊話）。</summary>
    /// <param name="microphoneDevice">DirectShow 的音訊裝置名稱（<see cref="MicrophoneCatalog.List"/> 取得）。</param>
    /// <param name="target">對講目標（位址與傳輸方式）。</param>
    /// <param name="codec">編碼方式。</param>
    public static TalkCommand BuildLiveMicrophone(string microphoneDevice, TalkTarget target, TalkCodec codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(microphoneDevice);
        ArgumentNullException.ThrowIfNull(target);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            // dshow 的擷取佇列預設太小，麥克風稍有抖動就丟樣本（症狀：喊話斷斷續續）。
            "-thread_queue_size", "512",
            "-f", "dshow",
            "-i", $"audio={microphoneDevice}",
        };

        args.AddRange(EncoderArguments(codec));
        args.AddRange(TransportArguments(target));
        return new TalkCommand(FfmpegLocator.Ffmpeg, args, target.Url);
    }

    /// <summary>編碼參數（ONVIF 回傳通道：G.711 A-law、8 kHz、單聲道）。</summary>
    private static IReadOnlyList<string> EncoderArguments(TalkCodec codec) => codec switch
    {
        TalkCodec.Onvif => ["-ac", "1", "-ar", "8000", "-c:a", "pcm_alaw"],
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "未知的對講編碼。"),
    };

    /// <summary>傳輸參數（RTSP 回傳通道走 TCP：UDP 在回傳通道上經常整段丟掉）。</summary>
    private static IReadOnlyList<string> TransportArguments(TalkTarget target) =>
        ["-rtsp_transport", "tcp", "-f", "rtsp", target.Url];
}
