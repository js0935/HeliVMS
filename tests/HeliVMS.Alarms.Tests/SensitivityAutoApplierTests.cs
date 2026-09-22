using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class SensitivityAutoApplierTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SensitivityAutoApplier _applier;
    private readonly AuditLogRepository _audit;
    private readonly ChannelRepository _channels;

    public SensitivityAutoApplierTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-applier-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _applier = new SensitivityAutoApplier(_store);
        _audit = new AuditLogRepository(_store);
        _channels = new ChannelRepository(_store);
    }

    private void InsertChannel(int id, bool motionEnabled, double sensitivity)
    {
        _store.Execute(
            """
            INSERT INTO channels (id, device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder,
                                  motion_enabled, motion_sensitivity)
            VALUES ($id, NULL, $n, $m, NULL, 'h264', 1, 'copy', $me, $ms);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", "channel");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/ch");
                cmd.Parameters.AddWithValue("$me", motionEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$ms", sensitivity);
            });
    }

    private static SensitivitySuggestion Suggest(int channelId, double current, bool change, double? suggested = null)
        => new(channelId, current, suggested ?? current, change, 0.0, 0, 0, "t");

    [Fact]
    public void ApplySuggestion_WritesSensitivityAndAudits()
    {
        InsertChannel(1, true, 0.5);
        var result = _applier.Apply(Suggest(1, 0.5, true, 0.55));

        Assert.True(result.Applied);
        Assert.Equal(0.5, result.Before);
        Assert.Equal(0.55, result.After);
        Assert.Equal(0.55, _channels.Get(1)!.MotionSensitivity);
        var entry = Assert.Single(_audit.List(new AuditLogQuery { Action = "vmd.sensitivity.apply" }));
        Assert.Equal("channel", entry.TargetType);
        Assert.Equal(1, entry.TargetId);
        Assert.Equal("0.50->0.55", entry.Detail);
    }

    [Fact]
    public void Apply_ClampsSuggestedAboveMax()
    {
        InsertChannel(1, true, 0.5);
        var result = _applier.Apply(Suggest(1, 0.5, true, 0.99));

        Assert.True(result.Applied);
        Assert.Equal(SensitivityAutoTuner.SensitivityMax, result.After);
        Assert.Equal(SensitivityAutoTuner.SensitivityMax, _channels.Get(1)!.MotionSensitivity);
    }

    [Fact]
    public void Apply_WhenVmdDisabled_Skips()
    {
        InsertChannel(1, false, 0.5);
        var result = _applier.Apply(Suggest(1, 0.5, true, 0.55));

        Assert.False(result.Applied);
        Assert.Equal(0.5, _channels.Get(1)!.MotionSensitivity);
        Assert.Equal(0, _audit.Count(new AuditLogQuery { Action = "vmd.sensitivity.apply" }));
    }

    [Fact]
    public void Apply_WhenChannelMissing_Skips()
    {
        var result = _applier.Apply(Suggest(99, 0.5, true, 0.55));

        Assert.False(result.Applied);
        Assert.Contains("不存在", result.Reason);
    }

    [Fact]
    public void Apply_WhenNoChangeRecommended_Skips()
    {
        InsertChannel(1, true, 0.5);
        var result = _applier.Apply(Suggest(1, 0.5, false));

        Assert.False(result.Applied);
        Assert.Equal(0.5, _channels.Get(1)!.MotionSensitivity);
        Assert.Equal(0, _audit.Count(new AuditLogQuery { Action = "vmd.sensitivity.apply" }));
    }

    public void Dispose() => _store.Dispose();
}