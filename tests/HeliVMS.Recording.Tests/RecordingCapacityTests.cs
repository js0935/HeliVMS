using HeliVMS.Recording;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 剩餘可錄時間的估算（<see cref="RecordingCapacity"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這是現場問「這顆碟還能錄多久」時唯一的答案來源，而它最容易錯的地方不是算術，是
/// <b>把「不知道」講成「0」或「無限大」</b>：前者會讓人以為磁碟滿了（其實只是這一小時
/// 沒有寫入），後者會讓人在還有 12 天的時候以為還有 30 天。所以三態被逐一釘住。
/// </para>
/// <para>
/// 第二個易錯點是「用名目碼率」：本專案已多次量到攝影機不照名目速率送（同一台不同段
/// 差兩倍），所以輸入必須是實測寫入量，這一點由服務層（<c>RecordingCapacityService</c>）
/// 負責，這裡只釘換算與邊界。
/// </para>
/// </remarks>
public class RecordingCapacityTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void 沒有寫入樣本時回未知而不是零或無限大()
    {
        // 剛裝機、全部停錄、事件模式還沒觸發：寫入量 0。
        var estimate = RecordingCapacity.Estimate(
            freeBytes: 100L * 1024 * 1024 * 1024,
            reserveBytes: 2L * 1024 * 1024 * 1024,
            writtenBytes: 0,
            basis: Hour);

        Assert.Equal(RecordingCapacityState.Unknown, estimate.State);
        Assert.Equal(TimeSpan.Zero, estimate.Remaining);
        Assert.Equal(0, estimate.BytesPerHour);
        Assert.True(estimate.FreeBytesAboveReserve > 0, "未知時仍要保留可用空間的實況供畫面顯示");
    }

    [Fact]
    public void 低於保留水位時回可錄零小時且優先於未知()
    {
        // 這一小時剛好沒有寫入（事件模式），但可用空間已經在保留水位以下——
        // 「接下來就會被汰除」是事實，不能因為沒有樣本就回未知。
        var estimate = RecordingCapacity.Estimate(freeBytes: 1_000, reserveBytes: 2_000, writtenBytes: 0, basis: Hour);

        Assert.Equal(RecordingCapacityState.AtReserve, estimate.State);
        Assert.Equal(TimeSpan.Zero, estimate.Remaining);
        Assert.Equal(0, estimate.FreeBytesAboveReserve);
    }

    [Fact]
    public void 以實測寫入量推算剩餘時間()
    {
        // 高於保留水位 100 GB，寫入量 10 GB/小時 → 10 小時。
        var estimate = RecordingCapacity.Estimate(
            freeBytes: 102L * 1024 * 1024 * 1024,
            reserveBytes: 2L * 1024 * 1024 * 1024,
            writtenBytes: 10L * 1024 * 1024 * 1024,
            basis: Hour);

        Assert.Equal(RecordingCapacityState.Known, estimate.State);
        Assert.Equal(10, estimate.Remaining.TotalHours, precision: 3);
        Assert.Equal(10d * 1024 * 1024 * 1024, estimate.BytesPerHour, precision: 0);
    }

    [Fact]
    public void 只有超過保留水位的部分才拿來估算()
    {
        // 可用 100 GB、保留 40 GB → 只有 60 GB 可以寫；4 GB/小時 → 15 小時。
        var estimate = RecordingCapacity.Estimate(
            freeBytes: 100L * 1024 * 1024 * 1024,
            reserveBytes: 40L * 1024 * 1024 * 1024,
            writtenBytes: 4L * 1024 * 1024 * 1024,
            basis: Hour);

        Assert.Equal(60L * 1024 * 1024 * 1024, estimate.FreeBytesAboveReserve);
        Assert.Equal(15, estimate.Remaining.TotalHours, precision: 3);
    }

    [Fact]
    public void 窗長無效或寫入量為零時速率是零()
    {
        // 除以 0 的窗長會得到無限大速率（畫面顯示「可錄 0 秒」或「無限」都是錯的）。
        Assert.Equal(0, RecordingCapacity.BytesPerHour(1000, TimeSpan.Zero));
        Assert.Equal(0, RecordingCapacity.BytesPerHour(1000, TimeSpan.FromMinutes(-5)));
        Assert.Equal(0, RecordingCapacity.BytesPerHour(0, Hour));
        Assert.Equal(0, RecordingCapacity.BytesPerHour(-1, Hour));
    }

    [Fact]
    public void 窗長不是一小時時按比例換算()
    {
        // 30 分鐘寫 5 GB → 10 GB/小時。
        var rate = RecordingCapacity.BytesPerHour(5L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(30));

        Assert.Equal(10d * 1024 * 1024 * 1024, rate, precision: 0);
    }

    [Fact]
    public void 多顆磁碟合計會加總可用空間與逐顆速率()
    {
        // 兩顆各 30 GB 可用（保留各扣 2 GB → 28+28=56 GB），速率 3+4＝7 GB/小時 → 8 小時。
        var total = RecordingCapacity.EstimateAll(
            drives: [(30L * GiB, 3L * GiB, Hour), (30L * GiB, 4L * GiB, Hour)],
            reserveBytes: 2L * GiB);

        Assert.Equal(RecordingCapacityState.Known, total.State);
        Assert.Equal(56L * GiB, total.FreeBytesAboveReserve);
        Assert.Equal(8, total.Remaining.TotalHours, precision: 3);
    }

    [Fact]
    public void 合計會用每一顆自己的基準換算速率()
    {
        // 第一顆錄滿一小時 4 GB（4 GB/h）；第二顆半小時前才加入、寫了 4 GB（8 GB/h）。
        // 合計速率是 12 GB/h，不是把 8 GB 除以一小時的 8 GB/h。
        var total = RecordingCapacity.EstimateAll(
            drives: [(30L * GiB, 4L * GiB, Hour), (30L * GiB, 4L * GiB, TimeSpan.FromMinutes(30))],
            reserveBytes: 2L * GiB);

        Assert.Equal(12d * GiB, total.BytesPerHour, precision: 0);
        Assert.Equal(56d / 12, total.Remaining.TotalHours, precision: 3);
    }

    [Fact]
    public void 全部磁碟都在保留水位以下時合計回零()
    {
        var total = RecordingCapacity.EstimateAll(
            drives: [(1_000, 500, Hour), (500, 100, Hour)],
            reserveBytes: 2_000);

        Assert.Equal(RecordingCapacityState.AtReserve, total.State);
        Assert.Equal(TimeSpan.Zero, total.Remaining);
    }

    [Fact]
    public void 一顆有樣本一顆沒有樣本時合計仍可估算()
    {
        // 第二顆沒寫入（例如剛加入的新碟），合計仍然算得出來：可用 (30−2)×2＝56 GB，
        // 速率 8 GB/小時 → 7 小時；畫面仍必須另外列出逐顆數字，
        // 否則「合計還有 7 小時」會蓋掉「其中一顆已經在保留水位」。
        var total = RecordingCapacity.EstimateAll(
            drives: [(30L * GiB, 8L * GiB, Hour), (30L * GiB, 0, Hour)],
            reserveBytes: 2L * GiB);

        Assert.Equal(RecordingCapacityState.Known, total.State);
        Assert.Equal(7, total.Remaining.TotalHours, precision: 3);
    }

    [Fact]
    public void 沒有任何磁碟時合計回未知()
    {
        var total = RecordingCapacity.EstimateAll([], reserveBytes: 2_000);

        Assert.Equal(RecordingCapacityState.Unknown, total.State);
    }

    [Fact]
    public void 基準是從最早的段起點到現在且不超過量測窗()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        // 錄滿一小時 → 基準就是量測窗（與除以窗相同）。
        Assert.Equal(Hour, RecordingCapacity.MeasurementBasis(now.AddHours(-3), now, Hour));

        // 半小時前才開始錄 → 基準半小時；除以一小時會把速率算成一半。
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            RecordingCapacity.MeasurementBasis(now.AddMinutes(-30), now, Hour));

        // 只有一段剛寫完的事件錄影（5 分鐘前）→ 夾到最短基準，避免分母趨近 0。
        Assert.Equal(
            RecordingCapacity.MinimumBasis,
            RecordingCapacity.MeasurementBasis(now.AddMinutes(-5), now, Hour));

        // 沒有樣本 → 回量測窗（速率會是 0，這個值不影響結果）。
        Assert.Equal(Hour, RecordingCapacity.MeasurementBasis(null, now, Hour));

        // 窗長無效 → 最短基準（不能回 0，否則速率除 0）。
        Assert.Equal(RecordingCapacity.MinimumBasis, RecordingCapacity.MeasurementBasis(now.AddHours(-2), now, TimeSpan.Zero));
    }

    [Fact]
    public void 剛開始錄影時不會把速率算成一半()
    {
        // 半小時寫 5 GB、基準半小時 → 10 GB/小時（除以一小時只會得到 5 GB/小時，
        // 剩餘時間就會被報成兩倍——「還能錄 8 小時」在 4 小時後變成真的沒空間）。
        var basis = RecordingCapacity.MeasurementBasis(
            new DateTime(2026, 11, 11, 11, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc),
            Hour);

        var estimate = RecordingCapacity.Estimate(
            freeBytes: 102L * GiB,
            reserveBytes: 2L * GiB,
            writtenBytes: 5L * GiB,
            basis);

        Assert.Equal(10, estimate.Remaining.TotalHours, precision: 3);
    }

    [Fact]
    public void 時長拆解一律向下取整()
    {
        // 「23 小時 40 分」不能顯示成 2 天：操作員會據此決定今天要不要去擴充磁碟。
        var (days, hours, minutes) = RecordingCapacity.Parts(
            TimeSpan.FromHours(23) + TimeSpan.FromMinutes(40) + TimeSpan.FromSeconds(59));

        Assert.Equal(0, days);
        Assert.Equal(23, hours);
        Assert.Equal(40, minutes);

        var two = RecordingCapacity.Parts(TimeSpan.FromHours(49.9));
        Assert.Equal(2, two.Days);
        Assert.Equal(1, two.Hours);

        var negative = RecordingCapacity.Parts(TimeSpan.FromMinutes(-5));
        Assert.Equal((0, 0, 0), negative);
    }
}
