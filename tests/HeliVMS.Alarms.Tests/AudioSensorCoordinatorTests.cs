using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class AudioSensorCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;

    public AudioSensorCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-asc-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlarmEventRepository(_store);
    }

    [Fact]
    public void DisabledChannel_FeedIsNoop_NoEngineNoEvents()
    {
        InsertChannel(1, audioEnabled: false);
        using var coordinator = NewCoordinator();

        var results = coordinator.Feed(1, Square(20000, 512 * 4), T0);

        Assert.Empty(results);
        Assert.DoesNotContain(1, coordinator.Channels);
        Assert.False(coordinator.IsEnabled(1));
        Assert.Empty(Events(1));
    }

    [Fact]
    public void EnabledChannel_CreatesEngineLazily_AndClassifiesBurst()
    {
        InsertChannel(1, audioEnabled: true);
        using var coordinator = NewCoordinator();

        var results = coordinator.Feed(1, Square(20000, 512 * 4), T0);

        Assert.Equal(4, results.Count);
        Assert.All(results, b => Assert.Equal(AudioBlockKind.Burst, b.Kind));
        Assert.Contains(1, coordinator.Channels);
        Assert.True(coordinator.IsEnabled(1));
    }

    [Fact]
    public void TwoChannels_AreIsolated()
    {
        InsertChannel(1, audioEnabled: true);
        InsertChannel(2, audioEnabled: true);
        using var coordinator = NewCoordinator();

        FeedAdvancing(coordinator, 1, 20000, 24); // 768ms 時長 burst → 開窗
        coordinator.Feed(2, new short[512 * 8], T0);
        coordinator.Flush();                        // 收尾寫入頻道 1 事件

        Assert.NotEmpty(Events(1));
        Assert.Empty(Events(2));
    }

    [Fact]
    public void PerChannelConfigOverride_IsApplied()
    {
        InsertChannel(1, audioEnabled: true);
        InsertChannel(2, audioEnabled: true);
        var overrides = new Dictionary<int, AudioTriggerConfig>
        {
            [1] = new() { BurstDb = -1 },
        };
        using var coordinator = NewCoordinator(overrides);

        var onA = coordinator.Feed(1, Square(20000, 512 * 2), T0); // -4.29 ≥ -1 之 BurstDb 不符 → 落至 Sustained
        var onB = coordinator.Feed(2, Square(20000, 512 * 2), T0); // 預設 → Burst

        Assert.All(onA, b => Assert.Equal(AudioBlockKind.Sustained, b.Kind));
        Assert.All(onB, b => Assert.Equal(AudioBlockKind.Burst, b.Kind));
    }

    [Fact]
    public void Refresh_PicksUpNewlyEnabledChannel()
    {
        using var coordinator = NewCoordinator();
        InsertChannel(1, audioEnabled: false);
        Assert.Empty(coordinator.Feed(1, Square(20000, 512 * 2), T0));

        SetAudioEnabled(1, true);
        coordinator.Refresh();

        var results = coordinator.Feed(1, Square(20000, 512 * 2), T0);
        Assert.NotEmpty(results);
        Assert.Contains(1, coordinator.Channels);
    }

    [Fact]
    public void Refresh_PicksUpDisabledRemoval()
    {
        InsertChannel(1, audioEnabled: true);
        using var coordinator = NewCoordinator();
        Assert.NotEmpty(coordinator.Feed(1, Square(20000, 512 * 2), T0));

        SetAudioEnabled(1, false);
        coordinator.Refresh();

        Assert.Empty(coordinator.Feed(1, Square(20000, 512 * 2), T0));
        Assert.DoesNotContain(1, coordinator.Channels);
    }

    [Fact]
    public void Flush_SettlesArmedBurst_IntoEvent()
    {
        InsertChannel(1, audioEnabled: true);
        using var coordinator = NewCoordinator();

        FeedAdvancing(coordinator, 1, 20000, 24);
        coordinator.Flush();

        var events = Events(1);
        var evt = Assert.Single(events);
        Assert.Equal("audio_burst", evt.EventType);
        Assert.True(evt.EndUtc >= evt.StartUtc);
    }

    [Fact]
    public void ResetAll_DiscardsArmedWindows()
    {
        InsertChannel(1, audioEnabled: true);
        using var coordinator = NewCoordinator();

        FeedAdvancing(coordinator, 1, 20000, 6);
        coordinator.ResetAll();
        coordinator.Flush();

        Assert.Empty(Events(1));
    }

    [Fact]
    public void Reset_SingleChannel_DoesNotAffectOthers()
    {
        InsertChannel(1, audioEnabled: true);
        InsertChannel(2, audioEnabled: true);
        using var coordinator = NewCoordinator();

        FeedAdvancing(coordinator, 1, 20000, 6);
        FeedAdvancing(coordinator, 2, 20000, 24);

        coordinator.Reset(1);
        coordinator.Flush();

        Assert.Empty(Events(1));
        Assert.NotEmpty(Events(2));
    }

    [Fact]
    public void EventInserted_ForwardedFromEngine()
    {
        InsertChannel(1, audioEnabled: true);
        using var coordinator = NewCoordinator();
        AlarmEventRecord? forwarded = null;
        coordinator.EventInserted += (_, e) => forwarded = e;

        FeedAdvancing(coordinator, 1, 20000, 24);
        coordinator.Flush();

        Assert.NotNull(forwarded);
        Assert.Equal("audio_burst", forwarded.EventType);
        Assert.Equal(1, forwarded.ChannelId);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndFeedAfterDisposeThrows()
    {
        InsertChannel(1, audioEnabled: true);
        var coordinator = NewCoordinator();
        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Throws<ObjectDisposedException>(() => coordinator.Feed(1, Square(20000, 512 * 2), T0));
    }

    private AudioSensorCoordinator NewCoordinator(IReadOnlyDictionary<int, AudioTriggerConfig>? overrides = null)
        => new(_store, _repo, overrides);

    private void InsertChannel(int id, bool audioEnabled)
    {
        _store.Execute(
            """
            INSERT INTO channels (id, device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES ($id, NULL, $n, $m, NULL, 'h264', $a, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", $"頻道{id}");
                cmd.Parameters.AddWithValue("$m", $"rtsp://127.0.0.1:8554/{id}");
                cmd.Parameters.AddWithValue("$a", audioEnabled ? 1 : 0);
            });
    }

    private void SetAudioEnabled(int id, bool audioEnabled)
        => _store.Execute("UPDATE channels SET audio_enabled = $a WHERE id = $id;", cmd =>
        {
            cmd.Parameters.AddWithValue("$a", audioEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
        });

    /// <summary>以遞增時鐘餵入（每 8 塊一批、每批 256ms），避免 duration＝0 之開窗驗收陷阱。</summary>
    private static void FeedAdvancing(AudioSensorCoordinator coordinator, int channel, short amplitude, int blockCount)
    {
        var utc = T0;
        var remaining = blockCount;
        while (remaining > 0)
        {
            var take = Math.Min(8, remaining);
            coordinator.Feed(channel, Square(amplitude, 512 * take), utc);
            remaining -= take;
            utc = utc.AddMilliseconds(take * 32); // 512/16000 = 32ms/塊
        }
    }

    private IReadOnlyList<AlarmEventRecord> Events(int channelId)
        => _repo.ListByRange(channelId, T0.AddSeconds(-2), T0.AddSeconds(20));

    private static readonly DateTime T0 = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private static short[] Square(short amplitude, int count)
    {
        var samples = new short[count];
        Array.Fill(samples, amplitude);
        return samples;
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }
}