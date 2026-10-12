using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// POS 異常偵測（§14.7 #8，M282）：金額離群與短時連發皆以收銀機分組、純統計。
/// 這裡刻意不放影像，驗證偵測器本身對「同一收銀機內的離群」的判斷。
/// </summary>
public sealed class PosAnomalyDetectorTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc);

    private static POSEvent Txn(long id, string register, long amountCents, TimeSpan after, int device = 1) =>
        new(id, device, register, $"TXN-{id}", amountCents, T0 + after);

    [Fact]
    public void AmountOutlier_FlagsFarAmountWithinRegister()
    {
        var txns = new[]
        {
            Txn(1, "R1", 1000, TimeSpan.Zero),
            Txn(2, "R1", 1100, TimeSpan.FromHours(1)),
            Txn(3, "R1", 1200, TimeSpan.FromHours(2)),
            Txn(4, "R1", 1300, TimeSpan.FromHours(3)),
            Txn(5, "R1", 1400, TimeSpan.FromHours(4)),
            Txn(6, "R1", 50000, TimeSpan.FromHours(5)),
        };

        var report = PosAnomalyDetector.Analyze(txns);

        var outlier = Assert.Single(report.Anomalies);
        Assert.Equal(PosAnomalyKind.AmountOutlier, outlier.Kind);
        Assert.Equal(6, outlier.Id);
        Assert.Equal(1250, outlier.PeerMedianCents);
        Assert.Equal(1, report.AmountOutliers);
        Assert.Equal(0, report.RapidRepeats);
    }

    [Fact]
    public void AmountOutlier_RequiresDispersion_MadZeroDoesNotFlag()
    {
        // 同額交易完全沒有離散時，任何一筆都不該被標紅——否則固定金額的正常收銀全數誤報。
        var txns = Enumerable.Range(1, 6)
            .Select(i => Txn(i, "R1", 1000, TimeSpan.FromHours(i)))
            .ToArray();

        var report = PosAnomalyDetector.Analyze(txns);

        Assert.Empty(report.Anomalies);
    }

    [Fact]
    public void AmountOutlier_SmallSampleNotJudged()
    {
        var txns = new[]
        {
            Txn(1, "R1", 1000, TimeSpan.Zero),
            Txn(2, "R1", 50000, TimeSpan.FromHours(1)),
        };

        Assert.Empty(PosAnomalyDetector.Analyze(txns).Anomalies);
    }

    [Fact]
    public void RapidRepeat_FlagsClusterWithinWindow()
    {
        var txns = new[]
        {
            Txn(1, "R1", 1000, TimeSpan.Zero),
            Txn(2, "R1", 1000, TimeSpan.FromSeconds(5)),
            Txn(3, "R1", 1000, TimeSpan.FromSeconds(10)),
            Txn(4, "R1", 1000, TimeSpan.FromSeconds(15)),
        };

        var report = PosAnomalyDetector.Analyze(txns);

        Assert.Equal(4, report.RapidRepeats);
        Assert.Equal(0, report.AmountOutliers);
        Assert.All(report.Anomalies, a => Assert.Equal(PosAnomalyKind.RapidRepeat, a.Kind));
    }

    [Fact]
    public void RapidRepeat_SeparatedTransactionsNotFlagged()
    {
        var txns = Enumerable.Range(1, 4)
            .Select(i => Txn(i, "R1", 1000, TimeSpan.FromMinutes(10 * i)))
            .ToArray();

        Assert.Empty(PosAnomalyDetector.Analyze(txns).Anomalies);
    }

    [Fact]
    public void GroupsByRegister_OtherRegisterUnaffected()
    {
        var txns = new[]
        {
            Txn(1, "R1", 1000, TimeSpan.Zero),
            Txn(2, "R1", 1000, TimeSpan.FromSeconds(5)),
            Txn(3, "R1", 1000, TimeSpan.FromSeconds(10)),
            Txn(4, "R2", 1000, TimeSpan.Zero),
            Txn(5, "R2", 1000, TimeSpan.FromHours(1)),
            Txn(6, "R2", 1000, TimeSpan.FromHours(2)),
        };

        var report = PosAnomalyDetector.Analyze(txns);

        Assert.Equal(3, report.Total);
        Assert.All(report.Anomalies, a => Assert.Equal("R1", a.RegisterId));
    }

    [Fact]
    public void OutlierFactor_RaisesThreshold()
    {
        var txns = new[]
        {
            Txn(1, "R1", 1000, TimeSpan.Zero),
            Txn(2, "R1", 1100, TimeSpan.FromHours(1)),
            Txn(3, "R1", 1200, TimeSpan.FromHours(2)),
            Txn(4, "R1", 1300, TimeSpan.FromHours(3)),
            Txn(5, "R1", 1400, TimeSpan.FromHours(4)),
            Txn(6, "R1", 50000, TimeSpan.FromHours(5)),
        };

        var report = PosAnomalyDetector.Analyze(txns, outlierFactor: 1000);

        Assert.Empty(report.Anomalies);
    }

    [Fact]
    public void Score_WithinZeroToOne()
    {
        var txns = new[]
        {
            Txn(1, "R1", 1000, TimeSpan.Zero),
            Txn(2, "R1", 1100, TimeSpan.FromHours(1)),
            Txn(3, "R1", 1200, TimeSpan.FromHours(2)),
            Txn(4, "R1", 1300, TimeSpan.FromHours(3)),
            Txn(5, "R1", 1400, TimeSpan.FromHours(4)),
            Txn(6, "R1", 50000, TimeSpan.FromHours(5)),
        };

        Assert.All(PosAnomalyDetector.Analyze(txns).Anomalies, a => Assert.InRange(a.Score, 0.0, 1.0));
    }

    [Fact]
    public void Empty_ReturnsNone()
    {
        var report = PosAnomalyDetector.Analyze([]);

        Assert.Equal(0, report.Total);
        Assert.Empty(report.Anomalies);
    }
}
