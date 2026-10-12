namespace HeliVMS.Recording.Talk;

/// <summary>對講音訊的傳輸方式。</summary>
public enum TalkTransport
{
    /// <summary>ONVIF／RTSP 回傳通道（backchannel）：以 RTSP 把音訊推回裝置喇叭。</summary>
    RtspBackchannel,
}

/// <summary>解析後的對講目標：音訊要送到哪個位址、用哪種傳輸、由哪個供應商決定。</summary>
public sealed record TalkTarget(string Url, TalkTransport Transport, string Provider);
