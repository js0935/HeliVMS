namespace HeliVMS.Recording.Talk;

/// <summary>
/// 一次對講（雙向語音，§14.1 #11）請求：把一段音訊檔送到某支攝影機的喇叭。
/// </summary>
/// <remarks>
/// 只描述「送什麼、送到哪」，不決定用哪個供應商或怎麼送——那是 <see cref="ITalkProvider"/> 的事。
/// 同一個請求因此可以在不同廠牌上以不同方式落地，呼叫端不必知道細節。
/// </remarks>
public sealed record TalkRequest(
    int ChannelId,
    string ChannelRtspUrl,
    string AudioFilePath,
    string? PreferredTalkUrl = null,
    string? DeviceModel = null);
