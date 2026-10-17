using HeliVMS.Recording;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 錄影分段長度政策（<see cref="RecordingSegmentPolicy"/>）。
///
/// 這個數字直接決定單路一天要寫幾個檔：15 秒是 5,760 個、60 秒是 1,440 個。
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
}
