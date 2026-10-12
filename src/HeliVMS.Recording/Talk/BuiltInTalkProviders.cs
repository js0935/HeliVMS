namespace HeliVMS.Recording.Talk;

/// <summary>
/// 內建對講供應商。順序即優先序（見 <see cref="TalkProviderRegistry"/>）。
/// </summary>
/// <remarks>
/// 刻意不內建各廠牌的推測網址：不同型號的對講通道位址差異很大，一份沒有實機驗證的清單
/// 只會讓現場「連上了卻沒有聲音」。需要專用位址的場合，由操作者透過
/// <see cref="TalkRequest.PreferredTalkUrl"/> 明確指定，這條路徑永遠優先。
/// </remarks>
public static class BuiltInTalkProviders
{
    /// <summary>預設供應商清單（優先序：明確指定 → 一般 RTSP 回傳通道）。</summary>
    public static IReadOnlyList<ITalkProvider> All { get; } =
    [
        new ExplicitTalkUrlProvider(),
        new GenericRtspTalkProvider(),
    ];
}

/// <summary>操作者明確指定的對講位址（最高優先，用於標準 ONVIF 之外的廠牌）。</summary>
public sealed class ExplicitTalkUrlProvider : ITalkProvider
{
    /// <inheritdoc />
    public string Name => "explicit-url";

    /// <inheritdoc />
    public bool CanHandle(TalkRequest request) => !string.IsNullOrWhiteSpace(request.PreferredTalkUrl);

    /// <inheritdoc />
    public TalkTarget Resolve(TalkRequest request)
        => new(request.PreferredTalkUrl!.Trim(), TalkTransport.RtspBackchannel, Name);

    /// <inheritdoc />
    public TalkCommand BuildCommand(TalkRequest request, TalkTarget target)
        => TalkCommandBuilder.Build(request.AudioFilePath, target);
}

/// <summary>一般 ONVIF／RTSP 回傳通道：沿用頻道自身的 RTSP 位址（最後的退路）。</summary>
public sealed class GenericRtspTalkProvider : ITalkProvider
{
    /// <inheritdoc />
    public string Name => "rtsp-backchannel";

    /// <inheritdoc />
    public bool CanHandle(TalkRequest request) => !string.IsNullOrWhiteSpace(request.ChannelRtspUrl);

    /// <inheritdoc />
    public TalkTarget Resolve(TalkRequest request)
        => new(request.ChannelRtspUrl.Trim(), TalkTransport.RtspBackchannel, Name);

    /// <inheritdoc />
    public TalkCommand BuildCommand(TalkRequest request, TalkTarget target)
        => TalkCommandBuilder.Build(request.AudioFilePath, target);
}
