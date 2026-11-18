using HeliVMS.Recording;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 錄影分段長度政策（<see cref="RecordingSegmentPolicy"/>）。
///
/// 這個數字直接決定單路一天要寫幾個檔：15 秒是 5,760 個、60 秒是 1,440 個、300 秒是 288 個。
/// 解析若悄悄把越界值當成合法（或把設定值吞掉退回預設），操作員調了設定卻看到
/// 檔數沒變，只會往磁碟或排程方向找錯地方，所以邊界行為要逐條釘住。
/// </summary>
public sealed class RecordingSegmentPolicyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public RecordingSegmentPolicyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-segpolicy-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
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

    [Fact]
    public void 未設定時回預設秒數()
        => Assert.Equal(RecordingSegmentPolicy.DefaultSeconds, RecordingSegmentPolicy.Parse(null));

    /// <summary>
    /// 預設必須是 300 秒，且不得悄悄退回 60 秒。60 秒 × 16 路 × 365 天約 840 萬筆
    /// <c>segments</c>（<c>SegmentRepository</c> 自註「一年可達千萬列」），會讓索引、
    /// 保留清理與備份在一年級儲存下被列數壓垮。300 秒把年列數砍到約 168 萬，
    /// 且仍是 <see cref="RecordingSegmentPolicy.MaxSeconds"/>＝600 的一半，HLS 粒度可接受。
    /// 這條釘住的是容量決策，不是實作細節——改回 60 必須先面對這個試算。
    /// </summary>
    [Fact]
    public void 預設分段是五分鐘以因應一年級儲存列數()
    {
        Assert.Equal(300, RecordingSegmentPolicy.DefaultSeconds);
        Assert.True(RecordingSegmentPolicy.DefaultSeconds * 2 <= RecordingSegmentPolicy.MaxSeconds);
    }

    [Fact]
    public void 合法值直接採用()
        => Assert.Equal(120, RecordingSegmentPolicy.Parse("120"));

    [Fact]
    public void 低於下限夾到下限而非退回預設()
    {
        // 設 1 秒代表「想要越短越好」；悄悄跳回 60 會讓操作員以為設定沒生效。
        Assert.Equal(RecordingSegmentPolicy.MinSeconds, RecordingSegmentPolicy.Parse("1"));
    }

    [Fact]
    public void 高於上限夾到上限()
        => Assert.Equal(RecordingSegmentPolicy.MaxSeconds, RecordingSegmentPolicy.Parse("99999"));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public void 無法解析的字串回預設(string raw)
        => Assert.Equal(RecordingSegmentPolicy.DefaultSeconds, RecordingSegmentPolicy.Parse(raw));

    [Fact]
    public void 越界值TryParse回false並帶出預設()
    {
        Assert.False(RecordingSegmentPolicy.TryParse("0", out var seconds));
        Assert.Equal(RecordingSegmentPolicy.DefaultSeconds, seconds);
    }

    [Fact]
    public void 設定值會被Read讀回()
    {
        new SettingsRepository(_store).Set(
            RecordingSegmentPolicy.SettingKey,
            300.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(300, RecordingSegmentPolicy.Read(_store));
    }

    [Fact]
    public void 未寫入設定時Read回預設()
        => Assert.Equal(RecordingSegmentPolicy.DefaultSeconds, RecordingSegmentPolicy.Read(_store));

    /// <summary>
    /// 段檔資料夾是「起點所屬的那個小時」，而每小時合併以資料夾為單位重新封裝。固定長度
    /// 會讓跨過整點的那一段把上一小時的內容帶進下一小時的資料夾，合併檔因此永遠不是
    /// 01:00–02:00；這裡釘住「只有逼近整點的那一段會縮短，其餘仍是設定值」。
    /// </summary>
    [Fact]
    public void 遠離整點時沿用設定的分段長度()
    {
        Assert.Equal(300, RecordingSegmentPolicy.PlanSeconds(300, At(10, 0, 7)));
        Assert.Equal(300, RecordingSegmentPolicy.PlanSeconds(300, At(10, 50, 7)));
    }

    /// <summary>10:58:20 起錄、設定 300 秒：到下一個整點只剩 100 秒。</summary>
    [Fact]
    public void 逼近整點的段會縮短到下一個整點()
        => Assert.Equal(100, RecordingSegmentPolicy.PlanSeconds(300, At(10, 58, 20)));

    /// <summary>設定值剛好等於到整點的距離時，整段錄到整點為止（不跨點也不提前收）。</summary>
    [Fact]
    public void 設定值剛好等於到整點的距離時在整點收檔()
        => Assert.Equal(600, RecordingSegmentPolicy.PlanSeconds(600, At(10, 50, 0)));

    /// <summary>
    /// 子秒起點要向下取整：10:57:20.500 到整點是 159.5 秒，取 159 才不會把內容推進下一個小時。
    /// </summary>
    [Fact]
    public void 子秒起點向下取整不會超過整點()
        => Assert.Equal(159, RecordingSegmentPolicy.PlanSeconds(300, At(10, 57, 20).AddMilliseconds(500)));

    /// <summary>
    /// 整點前不到一秒才開錄時，往下取整會是 0——<c>-t 0</c> 產不出任何影格。至少要給一秒，
    /// 寧可跨過整點零點幾秒，也不要產生一筆沒有內容的損壞段。
    /// </summary>
    [Fact]
    public void 整點前不到一秒仍會錄一秒而不是零秒()
    {
        var planned = RecordingSegmentPolicy.PlanSeconds(
            300,
            At(10, 59, 59).AddMilliseconds(700));

        Assert.Equal(RecordingSegmentPolicy.MinimumAlignedSeconds, planned);
    }

    /// <summary>整天的每一個起點（含跨午夜的 23 時）都不會讓分段跨過整點。</summary>
    [Fact]
    public void 任何起點的分段都不會跨過整點()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            var hourStart = At(hour, 0, 0);
            for (var offset = 0; offset < 3600; offset += 7)
            {
                var start = hourStart.AddSeconds(offset);
                var planned = RecordingSegmentPolicy.PlanSeconds(600, start);
                Assert.True(
                    start.AddSeconds(planned) <= hourStart.AddHours(1),
                    $"{start:HH:mm:ss} 起錄的分段會跨過整點");
            }
        }
    }

    private static DateTime At(int hour, int minute, int second)
        => new(2026, 1, 1, hour, minute, second, DateTimeKind.Utc);
}
