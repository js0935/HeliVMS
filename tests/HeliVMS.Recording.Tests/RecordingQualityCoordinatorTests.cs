using HeliVMS.Recording;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 跨設備錄影品質協調（M285，§14.7 #12）。
///
/// <see cref="BitrateGovernor"/> 與 <see cref="ObservedBitrateEstimator"/> 在這之前
/// 都是「算得出來、但沒有生產端在用」的純演算法；這裡驗的是把它們接起來的那層決策：
/// 在總頻寬上限下，哪些頻道該改用子碼流錄影。
/// </summary>
public sealed class RecordingQualityCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly HeliVMS.Storage.SqliteStore _store;

    public RecordingQualityCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-qos-{Guid.NewGuid():N}.db");
        _store = new HeliVMS.Storage.SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 未設上限（0＝不限制）時一律用主碼流。
    ///
    /// 這是預設路徑：治理器在預算 0 時回傳全 0 配額，若不特別處理就會被判成「該降級」，
    /// 於是「關閉這個功能」反而讓每一路都掉到子碼流——畫質全站變差卻沒有任何設定被改。
    /// </summary>
    [Fact]
    public void 未設上限時一律使用主碼流()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            0,
            new[] { new QualityChannel(1, 5_000_000, 3, InEvent: false, HasSubStream: true) });

        Assert.Equal(RecordingStream.Main, plan.For(1)!.Stream);
        Assert.Equal(0, plan.BudgetBps);
    }

    /// <summary>主碼流需求遠低於配額時，維持主碼流。</summary>
    [Fact]
    public void 配額足以負擔主碼流時維持主碼流()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            10_000_000,
            new[] { new QualityChannel(1, 2_000_000, 3, InEvent: false, HasSubStream: true) });

        Assert.Equal(RecordingStream.Main, plan.For(1)!.Stream);
    }

    /// <summary>配額遠低於主碼流需求時，降到子碼流——硬撐只會持續掉格。</summary>
    [Fact]
    public void 主碼流需求超過配額時降到子碼流()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            1_000_000,
            new[] { new QualityChannel(1, 5_000_000, 3, InEvent: false, HasSubStream: true) });

        Assert.Equal(RecordingStream.Sub, plan.For(1)!.Stream);
    }

    /// <summary>沒有子碼流的頻道不降級：降無可降，硬判降級會讓它被指向空位址。</summary>
    [Fact]
    public void 沒有子碼流的頻道不會被降級()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            1_000_000,
            new[] { new QualityChannel(1, 5_000_000, 3, InEvent: false, HasSubStream: false) });

        Assert.Equal(RecordingStream.Main, plan.For(1)!.Stream);
    }

    /// <summary>碼率未知（樣本不足）時不降級：在不知道需求前降級只會白白犧牲畫質。</summary>
    [Fact]
    public void 碼率未知的頻道不會被降級()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            1_000_000,
            new[] { new QualityChannel(1, 0, 3, InEvent: false, HasSubStream: true) });

        Assert.Equal(RecordingStream.Main, plan.For(1)!.Stream);
    }

    /// <summary>
    /// 事件中的頻道優先保留主碼流：權重加倍讓它在同預算下拿到足以續用主碼流的配額，
    /// 而同一批非事件頻道則被降級。
    /// </summary>
    [Fact]
    public void 事件中的頻道優先保留主碼流()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            2_000_000,
            new[]
            {
                new QualityChannel(1, 2_000_000, 3, InEvent: true, HasSubStream: true),
                new QualityChannel(2, 2_000_000, 3, InEvent: false, HasSubStream: true),
            });

        Assert.Equal(RecordingStream.Main, plan.For(1)!.Stream);
        Assert.Equal(RecordingStream.Sub, plan.For(2)!.Stream);
    }

    /// <summary>配額總和必須等於預算：這是治理器的不變式，協調層不得破壞它。</summary>
    [Fact]
    public void 配額總和等於預算()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            3_000_000,
            new[]
            {
                new QualityChannel(3, 1_000_000, 3, InEvent: false, HasSubStream: true),
                new QualityChannel(1, 1_000_000, 3, InEvent: false, HasSubStream: true),
                new QualityChannel(2, 1_000_000, 3, InEvent: false, HasSubStream: true),
            });

        Assert.Equal(3_000_000, plan.Choices.Sum(c => c.TargetBps));
    }

    /// <summary>結果依頻道 ID 排序，讓相同輸入在不同呼叫順序下產生相同（可重現）的計畫。</summary>
    [Fact]
    public void 結果依頻道ID排序()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            3_000_000,
            new[]
            {
                new QualityChannel(30, 1_000_000, 3, InEvent: false, HasSubStream: true),
                new QualityChannel(10, 1_000_000, 3, InEvent: false, HasSubStream: true),
                new QualityChannel(20, 1_000_000, 3, InEvent: false, HasSubStream: true),
            });

        Assert.Equal(new[] { 10, 20, 30 }, plan.Choices.Select(c => c.ChannelId));
    }

    /// <summary>沒有計畫（未設上限）時，任何頻道都視為主碼流。</summary>
    [Fact]
    public void 沒有計畫時視為主碼流()
    {
        Assert.Equal(RecordingStream.Main, RecordingQualityCoordinator.StreamFor(null, 7));
    }

    /// <summary>查不到的頻道回 null，呼叫端才不會誤用預設。</summary>
    [Fact]
    public void 查不到的頻道回null()
    {
        var plan = RecordingQualityCoordinator.Coordinate(
            1_000_000,
            new[] { new QualityChannel(1, 1_000_000, 3, InEvent: false, HasSubStream: true) });

        Assert.Null(plan.For(999));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("0", 0)]
    [InlineData("-5", 0)]
    [InlineData("8000000", 8_000_000)]
    public void 解析預算非正數或無效值視為不限制(string? raw, long expected)
    {
        Assert.Equal(expected, RecordingQualityCoordinator.ParseBudget(raw));
    }

    /// <summary>設定值以 bps 儲存，協調器直接讀該鍵；未設定時為 0（不限制）。</summary>
    [Fact]
    public void 從設定讀取預算()
    {
        Assert.Equal(0, RecordingQualityCoordinator.ReadBudget(_store));

        new HeliVMS.Storage.SettingsRepository(_store)
            .Set(RecordingQualityCoordinator.BudgetSettingKey, "5000000");

        Assert.Equal(5_000_000, RecordingQualityCoordinator.ReadBudget(_store));
    }
}
