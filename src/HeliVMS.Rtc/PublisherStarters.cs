namespace HeliVMS.Rtc;

/// <summary>
/// 正式環境的 publisher 啟動器。
///
/// <para>
/// 放在 <c>HeliVMS.Rtc</c> 而不是 WebApi，是為了讓「啟動失敗要變成可回應的例外」這條
/// 規則<b>只有一份</b>：先前它住在 <c>LiveStreamMaintenance.LivePublishers</c>，
/// 於是任何新增的呼叫點都得記得自己再轉一次例外，而漏掉時症狀就是 500 而非 502。
/// </para>
/// </summary>
public static class PublisherStarters
{
    /// <summary>
    /// 啟動 ffmpeg；失敗時回傳帶原因（已遮蔽憑證）的例外物件。
    /// </summary>
    /// <remarks>
    /// 這裡只負責「啟動」，不負責「等畫面」——後者是
    /// <see cref="ChannelRuntime.EnsurePublisherAsync"/> 的工作。兩者混在一起會讓
    /// 「ffmpeg 有沒有起來」與「有沒有出畫面」分不清楚，而這正是 M244 首次實作時
    /// 回 200 卻沒畫面的原因。
    /// </remarks>
    public static Task<PublisherStartException?> StartFfmpeg(
        LivePublisher publisher,
        System.Net.IPEndPoint rtpTarget,
        ChannelSource source,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(rtpTarget);

        try
        {
            publisher.Start(source.RtspUrl, rtpTarget);
            return Task.FromResult<PublisherStartException?>(null);
        }
        catch (PublisherStartException ex)
        {
            // LivePublisher 已把「找不到執行檔」等啟動失敗轉成這個例外（含環境變數名稱），
            // 這裡只是把它交給端點層：回傳 null 代表成功，非 null 代表 502。
            return Task.FromResult<PublisherStartException?>(ex);
        }
    }
}