namespace HeliVMS.Recording.Talk;

/// <summary>
/// 對講供應商的優先序清單：<see cref="Resolve"/> 取第一個 <see cref="ITalkProvider.CanHandle"/> 為真者。
/// </summary>
/// <remarks>
/// 順序即優先序。內建清單把「操作者明確指定」排在「一般 RTSP 回傳通道」之前，
/// 所以現場若填了專用對講網址就會蓋掉通用推測；沒填才退回事先猜的位址。
/// </remarks>
public sealed class TalkProviderRegistry
{
    private readonly IReadOnlyList<ITalkProvider> _providers;

    /// <summary>以給定供應商建立登錄表；未提供時使用 <see cref="BuiltInTalkProviders.All"/>。</summary>
    public TalkProviderRegistry(IEnumerable<ITalkProvider>? providers = null)
        => _providers = (providers ?? BuiltInTalkProviders.All).ToList();

    /// <summary>目前登記的供應商（依優先序）。</summary>
    public IReadOnlyList<ITalkProvider> Providers => _providers;

    /// <summary>回傳第一個能處理 <paramref name="request"/> 的供應商；都不行時回 <c>null</c>。</summary>
    public ITalkProvider? Resolve(TalkRequest request)
        => _providers.FirstOrDefault(p => p.CanHandle(request));
}
