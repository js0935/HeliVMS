namespace HeliVMS.Storage;

/// <summary>POS 異常種類（M282，§14.7 #8）：由交易後設資料（金額、時間、收銀機）推得，不需影像。</summary>
public enum PosAnomalyKind
{
    /// <summary>金額遠離同收銀機中位數（穩健 z 分數超標）——疑似錯帳／重複刷退／人為改單。</summary>
    AmountOutlier,

    /// <summary>同一收銀機在極短時間窗內連續多筆交易——疑似重複結帳／刷卡側錄。</summary>
    RapidRepeat,
}

/// <summary>單筆 POS 異常（M282）：Kind 供 UI 以字串鍵呈現，數值欄位保留供排序與比對。</summary>
public sealed record PosAnomaly(
    PosAnomalyKind Kind,
    long Id,
    string RegisterId,
    long AmountCents,
    DateTime OccurredAtUtc,
    double Score,
    long? PeerMedianCents = null);

/// <summary>異常彙總（M282）：Total 與逐類計數（計數用於面板標題，清單用於視覺化）。</summary>
public sealed record PosAnomalyReport(
    int Total,
    int AmountOutliers,
    int RapidRepeats,
    IReadOnlyList<PosAnomaly> Anomalies)
{
    public static PosAnomalyReport None() => new(0, 0, 0, []);
}

/// <summary>
/// POS 異常偵測器（M282，§14.7 #8，純 BCL，無 IO）：把門禁／POS 對帳之外的「單看交易本身就不對勁」
/// 補上——對帳（M104 <see cref="PosReconciliation"/>）回答「有沒有一筆對應事件」，本偵測器回答
/// 「同一收銀機的交易裡哪些明顯離群」。①金額離群：以同收銀機金額的**中位數 ± 穩健 z 分數**
/// （MAD 為尺度，0.6745×|偏差|/MAD）判定，MAD 為 0（完全無離散）時不判——否則單一固定金額的
/// 正常收銀會被全標紅。②短時連發：同收銀機相鄰交易間隔 ≤ window 串成叢集，叢集大小 ≥ threshold
/// 即標記整組。兩者皆以收銀機分組，故一通勤 Coffee shop 的高頻結帳不會把另一個收銀機的正常交易誤判。
/// </summary>
public static class PosAnomalyDetector
{
    /// <summary>穩健 z 分數門檻：0.6745×|x−median|/MAD 超過此值才算金額離群（預設 3.5，約常態的 0.05%）。</summary>
    public const double DefaultOutlierFactor = 3.5;

    /// <summary>短時連發叢集門檻（同一收銀機、窗內筆數）。</summary>
    public const int DefaultRepeatThreshold = 3;

    /// <summary>短時連發的預設時間窗。</summary>
    public static readonly TimeSpan DefaultRepeatWindow = TimeSpan.FromMinutes(2);

    public static PosAnomalyReport Analyze(
        IReadOnlyList<POSEvent> transactions,
        double outlierFactor = DefaultOutlierFactor,
        int repeatThreshold = DefaultRepeatThreshold,
        TimeSpan? repeatWindow = null)
    {
        if (transactions.Count == 0)
        {
            return PosAnomalyReport.None();
        }

        if (repeatThreshold < 2)
        {
            repeatThreshold = 2;
        }

        var window = repeatWindow ?? DefaultRepeatWindow;
        var anomalies = new List<PosAnomaly>();
        var amountOutliers = 0;

        foreach (var group in transactions.GroupBy(t => t.RegisterId, StringComparer.Ordinal))
        {
            var list = group.OrderBy(t => t.OccurredAtUtc).ThenBy(t => t.Id).ToList();

            foreach (var outlier in AmountOutliers(list, outlierFactor))
            {
                anomalies.Add(outlier);
                amountOutliers++;
            }

            anomalies.AddRange(RapidRepeats(list, window, repeatThreshold));
        }

        anomalies.Sort((a, b) =>
        {
            var c = a.OccurredAtUtc.CompareTo(b.OccurredAtUtc);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        return new PosAnomalyReport(
            anomalies.Count,
            amountOutliers,
            anomalies.Count - amountOutliers,
            anomalies);
    }

    /// <summary>回傳同收銀機中金額離群的交易（附該收銀機金額中位數）。</summary>
    private static IEnumerable<PosAnomaly> AmountOutliers(
        IReadOnlyList<POSEvent> registerTransactions,
        double outlierFactor)
    {
        if (registerTransactions.Count < 4 || outlierFactor <= 0)
        {
            yield break;
        }

        var amounts = registerTransactions.Select(t => t.AmountCents).ToList();
        var median = Median(amounts);
        var deviations = amounts.Select(a => Math.Abs(a - median)).ToList();
        var mad = Median(deviations);
        if (mad <= 0)
        {
            yield break;
        }

        foreach (var txn in registerTransactions)
        {
            var robustZ = 0.6745 * Math.Abs(txn.AmountCents - median) / mad;
            if (robustZ <= outlierFactor)
            {
                continue;
            }

            var score = Math.Min(1.0, robustZ / (outlierFactor * 2));
            yield return new PosAnomaly(
                PosAnomalyKind.AmountOutlier,
                txn.Id,
                txn.RegisterId,
                txn.AmountCents,
                txn.OccurredAtUtc,
                score,
                median);
        }
    }

    /// <summary>把相鄰間隔 ≤ window 的交易串成叢集，叢集大小 ≥ threshold 者整組標為短時連發。</summary>
    private static IEnumerable<PosAnomaly> RapidRepeats(
        IReadOnlyList<POSEvent> registerTransactions,
        TimeSpan window,
        int threshold)
    {
        var cluster = new List<POSEvent>();

        IEnumerable<PosAnomaly> Drain()
        {
            if (cluster.Count >= threshold)
            {
                var score = Math.Min(1.0, (cluster.Count - threshold + 1) / (double)threshold);
                foreach (var txn in cluster)
                {
                    yield return new PosAnomaly(
                        PosAnomalyKind.RapidRepeat,
                        txn.Id,
                        txn.RegisterId,
                        txn.AmountCents,
                        txn.OccurredAtUtc,
                        score);
                }
            }

            cluster.Clear();
        }

        foreach (var txn in registerTransactions)
        {
            if (cluster.Count > 0 && txn.OccurredAtUtc - cluster[^1].OccurredAtUtc > window)
            {
                foreach (var a in Drain())
                {
                    yield return a;
                }
            }

            cluster.Add(txn);
        }

        foreach (var a in Drain())
        {
            yield return a;
        }
    }

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
