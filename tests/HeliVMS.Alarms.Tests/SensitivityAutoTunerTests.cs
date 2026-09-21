using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class SensitivityAutoTunerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _repo;
    private readonly SensitivityAutoTuner _tuner;
    private readonly DateTime _now = DateTime.UtcNow;

    public SensitivityAutoTunerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-tuning-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlarmEventRepository(_store);
        _tuner = new SensitivityAutoTuner(_repo);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "調校頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/tune");
            });
    }

    [Fact]
    public void NoDispositionedEvents_NoRecommendation()
    {
        for (var i = 0; i < 5; i++)
        {
            InsertEvent("motion", _now.AddDays(-(i + 1)), null);
        }

        var s = _tuner.Suggest(1, 0.5, _now);
        Assert.False(s.ChangeRecommended);
        Assert.Equal(0.5, s.SuggestedSensitivity);
        Assert.Contains("樣本不足", s.Reason);
    }

    [Fact]
    public void FewerThanMinSamples_NoRecommendation()
    {
        for (var i = 0; i < 4; i++)
        {
            InsertEvent("motion", _now.AddDays(-(i + 1)), AlarmEventStatus.FalseAlarm);
        }

        var s = _tuner.Suggest(1, 0.5, _now);
        Assert.False(s.ChangeRecommended);
        Assert.Contains("樣本不足", s.Reason);
    }

    [Fact]
    public void HighFalsePositiveRate_RaisesSensitivity()
    {
        for (var i = 0; i < 3; i++) InsertEvent("motion", _now.AddDays(-(i + 1)), AlarmEventStatus.Actioned);
        for (var i = 0; i < 5; i++) InsertEvent("motion", _now.AddDays(-(10 + i)), AlarmEventStatus.FalseAlarm);

        var s = _tuner.Suggest(1, 0.5, _now);
        Assert.True(s.ChangeRecommended);
        Assert.Equal(0.55, s.SuggestedSensitivity);
        Assert.Equal(8, s.TruePositives + s.FalsePositives);
        Assert.Contains("誤報率", s.Reason);
    }

    [Fact]
    public void BoundaryAtMaxRate_DoesNotRaise()
    {
        for (var i = 0; i < 4; i++) InsertEvent("motion", _now.AddDays(-(i + 1)), AlarmEventStatus.FalseAlarm);
        for (var i = 0; i < 16; i++) InsertEvent("motion", _now.AddDays(-1).AddHours(-i), AlarmEventStatus.Acknowledged);

        var s = _tuner.Suggest(1, 0.5, _now);
        Assert.False(s.ChangeRecommended);
        Assert.Equal(0.5, s.SuggestedSensitivity);
        Assert.Equal(0.20, s.FalsePositiveRate, 3);
    }

    [Fact]
    public void CompliantRate_NoChange()
    {
        for (var i = 0; i < 2; i++) InsertEvent("motion", _now.AddDays(-2).AddHours(-i), AlarmEventStatus.FalseAlarm);
        for (var i = 0; i < 10; i++) InsertEvent("motion", _now.AddDays(-1).AddHours(-i), AlarmEventStatus.Actioned);

        var s = _tuner.Suggest(1, 0.5, _now);
        Assert.False(s.ChangeRecommended);
        Assert.Equal(0.5, s.SuggestedSensitivity);
        Assert.Contains("已達標", s.Reason);
    }

    [Fact]
    public void CleanHighVolume_DownshiftsSensitivity()
    {
        for (var i = 0; i < 25; i++) InsertEvent("motion", _now.AddHours(-i), AlarmEventStatus.Acknowledged);

        var s = _tuner.Suggest(1, 0.6, _now);
        Assert.True(s.ChangeRecommended);
        Assert.Equal(0.55, s.SuggestedSensitivity, 3);
        Assert.Contains("無誤報", s.Reason);
    }

    [Fact]
    public void DownshiftClampedAtMin()
    {
        for (var i = 0; i < 25; i++) InsertEvent("motion", _now.AddHours(-i), AlarmEventStatus.Actioned);

        var s = _tuner.Suggest(1, 0.12, _now);
        Assert.True(s.ChangeRecommended);
        Assert.Equal(SensitivityAutoTuner.SensitivityMin, s.SuggestedSensitivity);
    }

    [Fact]
    public void RaiseClampedAtMax()
    {
        for (var i = 0; i < 10; i++) InsertEvent("motion", _now.AddDays(-(i + 1)), AlarmEventStatus.FalseAlarm);

        var s = _tuner.Suggest(1, 0.88, _now);
        Assert.True(s.ChangeRecommended);
        Assert.Equal(SensitivityAutoTuner.SensitivityMax, s.SuggestedSensitivity);
    }

    [Fact]
    public void AlreadyAtMax_ClampedNoChange()
    {
        for (var i = 0; i < 10; i++) InsertEvent("motion", _now.AddDays(-(i + 1)), AlarmEventStatus.FalseAlarm);

        var s = _tuner.Suggest(1, SensitivityAutoTuner.SensitivityMax, _now);
        Assert.False(s.ChangeRecommended);
        Assert.Equal(SensitivityAutoTuner.SensitivityMax, s.SuggestedSensitivity);
    }

    [Fact]
    public void Summarize_CountsByDisposition()
    {
        var events = new[]
        {
            Event(AlarmEventStatus.FalseAlarm),
            Event(AlarmEventStatus.Acknowledged),
            Event(AlarmEventStatus.Actioned),
            Event(AlarmEventStatus.FalseAlarm),
            Event(AlarmEventStatus.Pending),
        };

        var w = SensitivityAutoTuner.Summarize(events);
        Assert.Equal(2, w.TruePositives);
        Assert.Equal(2, w.FalsePositives);
        Assert.Equal(0.5, w.FalsePositiveRate);
        Assert.Equal(4, w.DispositionedCount);
    }

    [Fact]
    public void LookbackWindow_IgnoresOlderEvents()
    {
        InsertEvent("motion", _now.AddDays(-15), AlarmEventStatus.FalseAlarm); // 超窗

        var s = _tuner.Suggest(1, 0.5, _now, windowDays: 14);
        Assert.False(s.ChangeRecommended);
        Assert.Contains("樣本不足", s.Reason);
    }

    [Fact]
    public void NonMotionEvents_NotCounted()
    {
        InsertEvent("motion", _now.AddDays(-1), AlarmEventStatus.FalseAlarm);
        for (var i = 0; i < 5; i++) InsertEvent("ai_person", _now.AddDays(-(2 + i)), AlarmEventStatus.FalseAlarm);

        var s = _tuner.Suggest(1, 0.5, _now);
        Assert.False(s.ChangeRecommended); // 有效樣本僅 1 < 5
        Assert.Contains("樣本不足", s.Reason);
    }

    private long InsertEvent(string type, DateTime startUtc, string? status)
    {
        var id = _repo.Insert(1, type, startUtc);
        if (status != null)
        {
            _repo.SetDisposition(id, status, null, null, startUtc.AddHours(1));
        }

        return id;
    }

    private static AlarmEventRecord Event(string status) => new() { EventType = "motion", Status = status };

    public void Dispose() => _store.Dispose();
}