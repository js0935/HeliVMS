using HeliVMS.Shared;

namespace HeliVMS.Recording.Talk;

/// <summary>對講音訊的編碼方式。</summary>
public enum TalkCodec
{
    /// <summary>ONVIF 回傳通道慣用的 G.711 A-law、8 kHz、單聲道。</summary>
    Onvif,
}

/// <summary>
/// 組出把音訊檔推回裝置的 ffmpeg 指令。
/// </summary>
/// <remarks>
/// 參數刻意集中在此一處。對講要能成立，編碼（ONVIF 回傳通道用 G.711 A-law、8 kHz、單聲道）、
/// 傳輸（RTSP over TCP）與 <c>-re</c>（照檔速送、不要一次爆送）缺一不可；散在各處遲早有人漏掉其中一項，
/// 而症狀是「有連上、沒有聲音」且日誌乾淨——最難查的那一種。
/// </remarks>
public static class TalkCommandBuilder
{
    /// <summary>以預設（ONVIF）編碼組出對講指令。</summary>
    public static TalkCommand Build(string audioFilePath, TalkTarget target)
        => Build(audioFilePath, target, TalkCodec.Onvif);

    /// <summary>以指定編碼組出對講指令。</summary>
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

        switch (codec)
        {
            case TalkCodec.Onvif:
                args.Add("-ac");
                args.Add("1");
                args.Add("-ar");
                args.Add("8000");
                args.Add("-c:a");
                args.Add("pcm_alaw");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(codec), codec, "未知的對講編碼。");
        }

        args.Add("-rtsp_transport");
        args.Add("tcp");
        args.Add("-f");
        args.Add("rtsp");
        args.Add(target.Url);

        return new TalkCommand(FfmpegLocator.Ffmpeg, args, target.Url);
    }
}
