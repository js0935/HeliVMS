namespace HeliVMS.Recording;

/// <summary>一個受治理頻道的現況（M67，§14.7 #12）。Priority＝1..5（越高越優先）。</summary>
public sealed record GoChannel(int ChannelId, long CurrentBps, int Priority, bool InEvent = false);

/// <summary>分配建議：該頻道可用的目標碼率（bps）。</summary>
public sealed record GoSuggestion(int ChannelId, long TargetBps);

/// <summary>一次配額重分配的結果。</summary>
public sealed record ReallocateResult(
    IReadOnlyList<GoSuggestion> Targets,
    long AllocatedBps,
    double ThrottleRatio);

/// <summary>
/// 依頻寬負載動態調整碼率（M67，§14.7 #12 "SVR/自適應品質" L0 純演算法）。
/// 在總可用頻寬（預算）下，按通道優先權與「事件中」狀態分配建議碼率；事件通道優先保全。
/// 保證 ΣTarget == Budget（Budget&gt;0 且有通道時），不超總頻寬。
/// </summary>
public sealed class BitrateGovernor
{
    private const int EventBoostMultiplier = 4;
    private const int MinPriority = 1;
    private const int MaxPriority = 5;

    public ReallocateResult Adapt(long budgetBps, IReadOnlyList<GoChannel> channels)
    {
        var channelsArray = channels.Select(c => new GoChannel(
                c.ChannelId,
                Math.Max(0, c.CurrentBps),
                Math.Clamp(c.Priority, MinPriority, MaxPriority),
                c.InEvent))
            .ToArray();

        if (budgetBps <= 0 || channelsArray.Length == 0)
        {
            return new ReallocateResult(
                channelsArray.Select(c => new GoSuggestion(c.ChannelId, 0)).ToArray(),
                0,
                0);
        }

        var weights = channelsArray
            .Select(c => (long)c.Priority * (c.InEvent ? EventBoostMultiplier : 1))
            .ToArray();
        var totalWeight = weights.Aggregate(0L, (s, w) => s + w);
        if (totalWeight <= 0)
        {
            return new ReallocateResult(
                channelsArray.Select(c => new GoSuggestion(c.ChannelId, 0)).ToArray(),
                0,
                0);
        }

        var targets = new long[channelsArray.Length];
        long allocated = 0;
        for (var i = 0; i < channelsArray.Length; i++)
        {
            targets[i] = (budgetBps * weights[i]) / totalWeight;
            allocated += targets[i];
        }

        long remainder = budgetBps - allocated;
        while (remainder > 0)
        {
            var best = MaxWeightIndex(weights, targets, budgetBps);
            if (best < 0)
            {
                break;
            }

            targets[best]++;
            allocated++;
            remainder--;
        }

        var currentTotal = channelsArray.Aggregate(0L, (s, c) => s + c.CurrentBps);
        var ratio = currentTotal > 0 ? (double)allocated / currentTotal : 0;

        var suggestions = new GoSuggestion[channelsArray.Length];
        for (var i = 0; i < channelsArray.Length; i++)
        {
            suggestions[i] = new GoSuggestion(channelsArray[i].ChannelId, targets[i]);
        }

        return new ReallocateResult(suggestions, allocated, ratio);
    }

    /// <summary>選出尚未達自身權重比例上限的「最大權重/已配額比」通道（largest remainder 落點）。</summary>
    private static int MaxWeightIndex(long[] weights, long[] targets, long budgetBps)
    {
        var best = -1;
        double bestScore = double.NegativeInfinity;
        for (var i = 0; i < weights.Length; i++)
        {
            if (targets[i] >= budgetBps)
            {
                continue;
            }

            var score = weights[i] > 0 ? (double)targets[i] / weights[i] : double.PositiveInfinity;
            if (weights[i] == 0)
            {
                continue;
            }

            if (best < 0 || score < bestScore)
            {
                best = i;
                bestScore = score;
            }
        }

        return best;
    }
}