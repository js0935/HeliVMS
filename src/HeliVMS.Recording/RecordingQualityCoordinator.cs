using System.Globalization;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>錄影要採用的串流。攝影機同時提供主碼流（高畫質）與子碼流（低碼率）。</summary>
public enum RecordingStream
{
    /// <summary>主碼流。</summary>
    Main,

    /// <summary>子碼流。</summary>
    Sub,
}

/// <summary>跨設備協調的輸入：一台攝影機目前的錄影負載。</summary>
/// <param name="ChannelId">頻道 ID。</param>
/// <param name="ObservedBps">主碼流的實測碼率（bps）；沒有可用樣本時為 0。</param>
/// <param name="Priority">優先權 1..5（越高越優先）。</param>
/// <param name="InEvent">是否正處於事件中（事件通道優先保全）。</param>
/// <param name="HasSubStream">是否有可切換的子碼流。</param>
public sealed record QualityChannel(
    int ChannelId,
    long ObservedBps,
    int Priority,
    bool InEvent,
    bool HasSubStream);

/// <summary>跨設備協調的輸出：該頻道該用哪條串流錄、以及配到的目標碼率。</summary>
public sealed record QualityChoice(int ChannelId, RecordingStream Stream, long TargetBps, long ObservedBps);

/// <summary>一次跨設備協調的完整結果。</summary>
public sealed record QualityPlan(
    IReadOnlyList<QualityChoice> Choices,
    long AllocatedBps,
    long BudgetBps,
    double ThrottleRatio)
{
    /// <summary>查某一頻道的決策；沒有該頻道時回 null。</summary>
    public QualityChoice? For(int channelId)
    {
        foreach (var choice in Choices)
        {
            if (choice.ChannelId == channelId)
            {
                return choice;
            }
        }

        return null;
    }
}

/// <summary>
/// 跨設備錄影品質協調（M285，§14.7 #12「SVR／跨設備協調」）。
///
/// <para>
/// 到 M67 為止，<see cref="BitrateGovernor"/> 與 <see cref="ObservedBitrateEstimator"/>
/// 都是「算得出來、但沒有人在用」的純演算法（沒有測試以外的呼叫端）：治理器需要
/// 「現況碼率」當輸入，推估器需要「已完成的區段」當輸入，而把兩者接起來的最後一段
/// ——跨設備的決策——始終缺席。這裡就是那一段。</para>
///
/// <para>
/// 為什麼決策是「換主／子碼流」而不是「改碼率」：錄影管線是 <c>ffmpeg … -c:v copy</c>，
/// 不做重編碼，所以執行中無法改變輸出碼率。要做到「同一台 ffmpeg 續錄、但少佔頻寬」，
/// 唯一可行的做法是改用攝影機自己提供的低碼率子碼流。</para>
///
/// <para>
/// 為什麼預設關閉（預算 0 ＝不限制）：這是 P3 的最佳化，不該改變未設定者的既有行為；
/// 沒有設 <see cref="BudgetSettingKey"/> 的站台，排程一律照舊用主碼流。</para>
///
/// <para>
/// 為何用「主碼流實測碼率」而不用「目前正在錄的那條串流」當負載：若改用現行串流，
/// 降級之後實測碼率會跟著掉到子碼流水準，配額又立刻足夠，於是升回主碼流、碼率再漲，
/// 形成每 30 秒一次的來回切換（每切一次就斷一段錄影）。以主碼流需求當基準，
/// 決策只隨預算與主碼率變動，是穩定且可預測的。</para>
/// </summary>
public static class RecordingQualityCoordinator
{
    /// <summary>錄影總頻寬上限（bps）的設定鍵；未設定或非正數代表不限制。</summary>
    public const string BudgetSettingKey = "recording.bandwidth_budget_bps";

    /// <summary>沒有更高優先權來源時採用的預設優先權（1..5）。</summary>
    public const int DefaultPriority = 3;

    /// <summary>
    /// 配額至少要到主碼流需求的這個比例，才值得繼續用主碼流錄影。
    /// 低於此比例就降到子碼流——硬撐主碼流只會在頻寬不足時持續掉格，
    /// 留下看起來正常、實際破損的錄影。
    /// </summary>
    public const double MainRetentionRatio = 0.6;

    /// <summary>
    /// 在總預算 <paramref name="budgetBps"/> 下協調各頻道。結果依頻道 ID 排序，確保可重現。
    /// </summary>
    /// <remarks>
    /// 配額本身交給 <see cref="BitrateGovernor"/> 依優先權與「事件中」狀態分配；
    /// 這裡只負責把配額翻譯成「主碼流或子碼流」的操作決策。兩者刻意分開：
    /// 分配是已經被測過的純函式，翻譯規則是這一層唯一的新邏輯。
    /// </remarks>
    public static QualityPlan Coordinate(long budgetBps, IReadOnlyList<QualityChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var ordered = channels.OrderBy(c => c.ChannelId).ToArray();

        // 預算關閉（0＝不限制）時一律用主碼流。若不特別處理，治理器會回傳全 0 配額，
        // 而 0 配額會被下面的規則判成「該降級」——等於關閉的站台反而全部降級。
        if (budgetBps <= 0)
        {
            return new QualityPlan(
                ordered.Select(c => new QualityChoice(c.ChannelId, RecordingStream.Main, 0, c.ObservedBps)).ToArray(),
                0,
                0,
                0);
        }

        var allocation = new BitrateGovernor().Adapt(
            budgetBps,
            ordered.Select(c => new GoChannel(c.ChannelId, c.ObservedBps, c.Priority, c.InEvent)).ToArray());
        var targetById = allocation.Targets.ToDictionary(t => t.ChannelId, t => t.TargetBps);

        var choices = new QualityChoice[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var channel = ordered[i];
            var target = targetById.TryGetValue(channel.ChannelId, out var allocated) ? allocated : 0;

            // 沒有子碼流就絕不降級（降無可降）；碼率未知（樣本不足）時也不降級，
            // 因為在不知道主碼流需求前貿然降級會讓畫質白白變差。
            var demote = channel.HasSubStream
                && channel.ObservedBps > 0
                && target < (long)(channel.ObservedBps * MainRetentionRatio);

            choices[i] = new QualityChoice(
                channel.ChannelId,
                demote ? RecordingStream.Sub : RecordingStream.Main,
                target,
                channel.ObservedBps);
        }

        return new QualityPlan(choices, allocation.AllocatedBps, budgetBps, allocation.ThrottleRatio);
    }

    /// <summary>從共用設定讀取錄影總頻寬上限（bps）；0 代表不限制。</summary>
    public static long ReadBudget(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return ParseBudget(new SettingsRepository(store).Get(BudgetSettingKey));
    }

    /// <summary>解析設定的預算字串；無法解析或非正數回 0（不限制）。</summary>
    public static long ParseBudget(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    /// <summary>該頻道應採用的串流；沒有計畫（未設預算）時為主碼流。</summary>
    public static RecordingStream StreamFor(QualityPlan? plan, int channelId)
        => plan?.For(channelId)?.Stream ?? RecordingStream.Main;
}
