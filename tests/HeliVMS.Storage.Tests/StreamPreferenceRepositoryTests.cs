namespace HeliVMS.Storage.Tests;

/// <summary>
/// 目前碼流的持久化（M76，§15.2）。切流視窗重開時要恢復「目前在哪一流」與維持期起點；
/// 若每次重開都回主碼流並把維持期歸零，畫面會與實際連線不一致，且每次開窗都能再抖動切一次。
/// </summary>
public sealed class StreamPreferenceRepositoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly StreamPreferenceRepository _prefs;

    public StreamPreferenceRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-stream-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _prefs = new StreamPreferenceRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 未設定過的頻道預設為主碼流且無切換時間()
    {
        Assert.Equal(StreamKind.Main, _prefs.GetKind(1));
        Assert.Null(_prefs.GetLastSwitchUtc(1));
    }

    [Fact]
    public void 儲存次碼流後可讀回碼流與切換時間()
    {
        _prefs.Save(1, StreamKind.Sub, T0);

        Assert.Equal(StreamKind.Sub, _prefs.GetKind(1));
        Assert.Equal(T0, _prefs.GetLastSwitchUtc(1));
    }

    [Fact]
    public void 再次儲存會覆寫而非累積()
    {
        _prefs.Save(1, StreamKind.Sub, T0);
        _prefs.Save(1, StreamKind.Main, T0.AddMinutes(5));

        Assert.Equal(StreamKind.Main, _prefs.GetKind(1));
        Assert.Equal(T0.AddMinutes(5), _prefs.GetLastSwitchUtc(1));
    }

    [Fact]
    public void 不同頻道的狀態互不干擾()
    {
        _prefs.Save(1, StreamKind.Sub, T0);

        Assert.Equal(StreamKind.Main, _prefs.GetKind(2));
        Assert.Null(_prefs.GetLastSwitchUtc(2));
    }
}
