namespace HeliVMS.Recording.Talk;

/// <summary>
/// 一次對講（雙向語音，§14.1 #11）請求：把聲音送到某支攝影機的喇叭。
/// </summary>
/// <remarks>
/// <para>
/// 來源有兩種，擇一：<see cref="AudioFilePath"/>（播一段既有的音訊檔，例如廣播稿）或
/// <see cref="MicrophoneDevice"/>（把主機麥克風的聲音即時送過去，也就是「喊話」）。
/// 兩者都給時以麥克風優先——喊話是即時需求，檔案是排程需求。
/// </para>
/// <para>
/// 只描述「送什麼、送到哪」，不決定用哪個供應商或怎麼送——那是 <see cref="ITalkProvider"/> 的事。
/// 同一個請求因此可以在不同廠牌上以不同方式落地，呼叫端不必知道細節。
/// </para>
/// </remarks>
public sealed record TalkRequest(
    int ChannelId,
    string ChannelRtspUrl,
    string AudioFilePath,
    string? PreferredTalkUrl = null,
    string? DeviceModel = null,
    string? MicrophoneDevice = null);
