namespace HeliVMS.Storage.Tests;

/// <summary>
/// 「已達授權上限」事件中心通知（§19.4 超限行為）——人工與排程共用的去重邏輯。
/// </summary>
public class LicenseLimitNotifierTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _events;
    private readonly LicenseLimitNotifier _notifier;
    private readonly DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public LicenseLimitNotifierTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-limit-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        // alarm_events.channel_id 有 FK，事件必須指向真實頻道；種子頻道為 1、2。
        new ChannelRepository(_store).EnsureSeedChannels();
        _events = new AlarmEventRepository(_store);
        _notifier = new LicenseLimitNotifier(_events);
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
    public void Report_OverLimit_WritesLicenseLimitEvent()
    {
        Assert.True(_notifier.Report(Gate(LicenseDecision.Valid, "已達授權上限（4 路）。"), _now));

        var evt = Assert.Single(Events());
        Assert.Equal("license_limit", evt.EventType);
        Assert.Equal(1, evt.ChannelId);
        Assert.Equal("已達授權上限（4 路）。", evt.Detail);
    }

    [Fact]
    public void Report_SameBlockRepeated_WritesOnce()
    {
        var gate = Gate(LicenseDecision.Valid, "已達授權上限（4 路）。");

        _notifier.Report(gate, _now);
        _notifier.Report(gate, _now.AddSeconds(30));
        _notifier.Report(gate, _now.AddSeconds(60));

        Assert.Single(Events());
    }

    [Fact]
    public void Report_ReasonChanged_NotifiesAgain()
    {
        _notifier.Report(Gate(LicenseDecision.Valid, "已達授權上限（4 路）。"), _now);
        _notifier.Report(Gate(LicenseDecision.Valid, "已達授權上限（8 路）。"), _now.AddMinutes(1));

        Assert.Equal(2, Events().Count);
    }

    [Fact]
    public void Report_AfterClear_NotifiesAgain()
    {
        var gate = Gate(LicenseDecision.Valid, "已達授權上限（4 路）。");
        _notifier.Report(gate, _now);

        _notifier.Clear(1);

        Assert.True(_notifier.Report(gate, _now.AddMinutes(1)));
        Assert.Equal(2, Events().Count);
    }

    [Fact]
    public void Report_ClearAll_ResetsEveryChannel()
    {
        _notifier.Report(Gate(LicenseDecision.Valid, "上限。", channelId: 1), _now);
        _notifier.Report(Gate(LicenseDecision.Valid, "上限。", channelId: 2), _now);

        _notifier.ClearAll();

        Assert.True(_notifier.Report(Gate(LicenseDecision.Valid, "上限。", channelId: 1), _now));
        Assert.True(_notifier.Report(Gate(LicenseDecision.Valid, "上限。", channelId: 2), _now));
        Assert.Equal(4, Events().Count);
    }

    [Theory]
    [InlineData(LicenseDecision.NotPresent)]
    [InlineData(LicenseDecision.Expired)]
    [InlineData(LicenseDecision.Revoked)]
    [InlineData(LicenseDecision.TimeRollback)]
    [InlineData(LicenseDecision.Invalid)]
    [InlineData(LicenseDecision.MachineMismatch)]
    public void Report_NonQuotaDecision_WritesNothing(LicenseDecision decision)
    {
        Assert.False(_notifier.Report(Gate(decision, "授權無效。"), _now));
        Assert.Empty(Events());
    }

    [Fact]
    public void Report_AllowedGate_WritesNothing()
    {
        Assert.False(_notifier.Report(new RecordingGateResult(true, LicenseDecision.Valid, 4, null, 1), _now));
        Assert.Empty(Events());
    }

    [Fact]
    public void Report_BlankReason_FallsBackToDefaultText()
    {
        _notifier.Report(Gate(LicenseDecision.Valid, null), _now);

        var evt = Assert.Single(Events());
        Assert.Equal("已達授權上限。", evt.Detail);
    }

    private IReadOnlyList<AlarmEventRecord> Events()
        => _events.ListByQuery(new AlarmEventRepository.QueryArgs
        {
            EventType = "license_limit",
            FromUtc = _now.AddDays(-1),
            ToUtc = _now.AddDays(1),
        });

    private static RecordingGateResult Gate(LicenseDecision decision, string? reason, int channelId = 1)
        => new(false, decision, 4, reason, channelId);
}
