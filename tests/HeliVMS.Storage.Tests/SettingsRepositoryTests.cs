using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly SqliteStore _store;
    private readonly SettingsRepository _repo;
    private readonly string _dbPath;

    public SettingsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-settings-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SettingsRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Missing_key_returns_null()
    {
        Assert.Null(_repo.Get("no.such.key"));
    }

    [Fact]
    public void Set_then_Get_roundtrips()
    {
        _repo.Set("recording.quota_gb", "5.5");
        Assert.Equal("5.5", _repo.Get("recording.quota_gb"));
    }

    [Fact]
    public void Set_overwrites_existing_value()
    {
        _repo.Set("recording.quota_gb", "5.5");
        _repo.Set("recording.quota_gb", "8");
        Assert.Equal("8", _repo.Get("recording.quota_gb"));
    }

    [Fact]
    public void GetOrDefault_uses_default_when_missing()
    {
        Assert.Equal("10", _repo.GetOrDefault("recording.quota_gb", "10"));
    }

    [Fact]
    public void GetDoubleOrDefault_parses_invariant()
    {
        _repo.Set("recording.quota_gb", "3.25");
        Assert.Equal(3.25d, _repo.GetDoubleOrDefault("recording.quota_gb", 10));
    }

    [Fact]
    public void GetDoubleOrDefault_falls_back_on_bad_value()
    {
        _repo.Set("recording.quota_gb", "abc");
        Assert.Equal(10d, _repo.GetDoubleOrDefault("recording.quota_gb", 10));
    }

    [Fact]
    public void Initialize_is_idempotent_keeps_existing_settings()
    {
        _repo.Set("recording.quota_gb", "6");
        _store.Initialize();
        Assert.Equal("6", _repo.Get("recording.quota_gb"));
    }
}