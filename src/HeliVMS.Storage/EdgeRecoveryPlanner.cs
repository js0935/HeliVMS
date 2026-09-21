namespace HeliVMS.Storage;

/// <summary>設備 SD 側錄段（M89，§14.7 #10）：設備回報其 SD 卡上某段時間之錄影（半開區間
/// [StartUtc, EndUtc)）。</summary>
public sealed record EdgeSegment(string SourceId, DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>長度是否為正（EndUtc 須嚴格晚於 StartUtc）。</summary>
    public bool IsPositive => EndUtc > StartUtc;
}

/// <summary>補抓任務（M89）：NVR 本機對該時窗欠缺涵蓋，需自設備 SD 抓取（FetchStep 為時窗）；L0 僅計畫不求取。</summary>
public sealed record EdgeRecoveryTask(EdgeSegment Source, DateTime FetchStartUtc, DateTime FetchEndUtc);

/// <summary>補抓計畫（M89）：依時序之任務串＋診斷計數。</summary>
public sealed record EdgeRecoveryPlan(
    IReadOnlyList<EdgeRecoveryTask> Tasks,
    int SkippedStaleSegments,
    int MergedGapCount);

/// <summary>
/// Edge Storage 雙保險 L0（M89，§14.7 #10，純 BCL、時鐘注入）：
/// 以「設備 SD manifest」與「NVR 本機既有涵蓋段」求差，產生缺失時窗的補抓排程——
/// ①重疊段正規化（設備自我重疊併段）②涵蓋求差 ③間隙 ≤ 閾值併單一補抓時窗
/// ④單任務上限 maxFetchWindow 分割 ⑤過期段略過（SD 已被覆寫，不再補）。
/// 後續里程碑再將計畫餵給實體回灌調度器/UI。
/// </summary>
public sealed class EdgeRecoveryPlanner
{
    public const int DefaultMergeThresholdSec = 60;
    public const int DefaultStaleTtlHours = 24;
    public const int DefaultMaxFetchWindowMinutes = 360;

    private readonly TimeSpan _mergeThreshold;
    private readonly TimeSpan _staleTtl;
    private readonly TimeSpan _maxFetchWindow;

    public EdgeRecoveryPlanner(
        TimeSpan? mergeThreshold = null,
        TimeSpan? staleTtl = null,
        TimeSpan? maxFetchWindow = null)
    {
        _mergeThreshold = mergeThreshold ?? TimeSpan.FromSeconds(DefaultMergeThresholdSec);
        _staleTtl = staleTtl ?? TimeSpan.FromHours(DefaultStaleTtlHours);
        _maxFetchWindow = maxFetchWindow ?? TimeSpan.FromMinutes(DefaultMaxFetchWindowMinutes);

        if (_mergeThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeThreshold), "間隙閾值不可為負。");
        }

        if (_staleTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleTtl), "過期 TTL 須為正。");
        }

        if (_maxFetchWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFetchWindow), "單任務上限須為正。");
        }
    }

    /// <summary>
    /// 計算補抓計畫。deviceManifest＝設備 SD 回報段；localCoverage＝NVR 本機已涵蓋段；
    /// 兩者皆假定與設備同頻道之時間窗。結果依時序，跨段 gap 於閾值內併單。
    /// </summary>
    public EdgeRecoveryPlan Plan(
        IReadOnlyList<EdgeSegment> deviceManifest,
        IReadOnlyList<EdgeSegment> localCoverage,
        DateTime utcNow)
    {
        var invalid = deviceManifest.FirstOrDefault(s => !s.IsPositive) ?? localCoverage.FirstOrDefault(s => !s.IsPositive);
        if (invalid is not null)
        {
            throw new ArgumentException($"段長度須為正：{invalid}。", nameof(deviceManifest));
        }

        var skipped = 0;
        var staleFloor = utcNow - _staleTtl;
        var fresh = deviceManifest
            .Where(s =>
            {
                if (s.EndUtc < staleFloor)
                {
                    skipped++;
                    return false;
                }

                return true;
            })
            .ToList();
        if (fresh.Count == 0)
        {
            return new EdgeRecoveryPlan(Array.Empty<EdgeRecoveryTask>(), skipped, 0);
        }

        var device = Normalize(fresh);
        var local = Normalize(localCoverage);

        // 求差：設備窗減去本機涵蓋，得未涵蓋 run 串。
        var runs = new List<(DateTime Start, DateTime End)>();
        var deviceIndex = 0;
        var localIndex = 0;
        while (deviceIndex < device.Count)
        {
            var dev = device[deviceIndex];
            var cursor = dev.StartUtc;

            while (cursor < dev.EndUtc)
            {
                // 略過完全在游標之前的本機段
                while (localIndex < local.Count && local[localIndex].EndUtc <= cursor)
                {
                    localIndex++;
                }

                var covering = localIndex < local.Count ? local[localIndex] : null;
                if (covering is null || covering.StartUtc >= dev.EndUtc)
                {
                    runs.Add((cursor, dev.EndUtc));
                    cursor = dev.EndUtc;
                }
                else if (covering.StartUtc > cursor)
                {
                    runs.Add((cursor, covering.StartUtc));
                    cursor = covering.EndUtc;
                }
                else
                {
                    cursor = covering.EndUtc > cursor ? covering.EndUtc : cursor;
                }

                if (cursor > dev.EndUtc)
                {
                    cursor = dev.EndUtc;
                }
            }

            deviceIndex++;
        }

        // 依 StartUtc 排序（求差輸出已與 device 同序，device 已正規化排序）
        // 併段：間隙 ≤ 閾值 → 單一補抓（含間隙）；之後依 maxFetchWindow 分割。
        var merged = new List<(DateTime Start, DateTime End)>();
        var gapMerges = 0;
        foreach (var run in runs)
        {
            if (merged.Count == 0)
            {
                merged.Add(run);
                continue;
            }

            var last = merged[^1];
            var gap = run.Start - last.End;
            if (gap <= _mergeThreshold)
            {
                merged[^1] = (last.Start, run.End > last.End ? run.End : last.End);
                gapMerges++;
            }
            else
            {
                merged.Add(run);
            }
        }

        var tasks = new List<EdgeRecoveryTask>();
        foreach (var m in merged)
        {
            foreach (var span in Chunk(m.Start, m.End))
            {
                tasks.Add(new EdgeRecoveryTask(
                    new EdgeSegment("device-sd", span.Start, span.End),
                    span.Start,
                    span.End));
            }
        }

        return new EdgeRecoveryPlan(tasks, skipped, gapMerges);
    }

    private IEnumerable<(DateTime Start, DateTime End)> Chunk(DateTime start, DateTime end)
    {
        for (var cursor = start; cursor < end;)
        {
            var stepEnd = cursor + _maxFetchWindow;
            if (stepEnd > end)
            {
                stepEnd = end;
            }

            yield return (cursor, stepEnd);
            cursor = stepEnd;
        }
    }

    private static IReadOnlyList<EdgeSegment> Normalize(IReadOnlyList<EdgeSegment> input)
    {
        var sorted = input.OrderBy(s => s.StartUtc).ThenBy(s => s.EndUtc).ToList();
        var result = new List<EdgeSegment>();
        foreach (var segment in sorted)
        {
            if (result.Count == 0 || segment.StartUtc > result[^1].EndUtc)
            {
                result.Add(segment);
                continue;
            }

            if (segment.EndUtc > result[^1].EndUtc)
            {
                result[^1] = result[^1] with { EndUtc = segment.EndUtc };
            }
        }

        return result;
    }
}