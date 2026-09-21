namespace HeliVMS.Storage.Tests;

public class PatrolRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly PatrolRepository _repo;
    private readonly DateTime _base;

    public PatrolRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-patrol-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new PatrolRepository(_store);
        var now = DateTime.UtcNow;
        _base = new DateTime(now.AddDays(-10).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, '巡航機台', 'rtsp://127.0.0.1:8554/patrol', NULL, 'h264', 1, 'copy');
            """);
    }

    private static PatrolStepRow[] SampleSteps()
    {
        return
        [
            new("大門A", 30),
            new("大門B", 15),
            new("倉庫", 60),
        ];
    }

    [Fact]
    public void Save_New_Then_GetByChannel_RoundTripsAllFieldsAndStepOrder()
    {
        var id = _repo.Save(null, "早班巡航", 1, true, "08:00", "18:00", _base, SampleSteps());

        Assert.True(id > 0);
        var plan = _repo.GetByChannel(1);
        Assert.NotNull(plan);
        Assert.Equal(id, plan.Id);
        Assert.Equal("早班巡航", plan.Name);
        Assert.True(plan.Enabled);
        Assert.Equal("08:00", plan.WindowStart);
        Assert.Equal("18:00", plan.WindowEnd);
        Assert.Equal(_base, plan.UpdatedAtUtc);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal("大門A", plan.Steps[0].PresetName);
        Assert.Equal(30, plan.Steps[0].DwellSeconds);
        Assert.Equal("大門B", plan.Steps[1].PresetName);
        Assert.Equal(15, plan.Steps[1].DwellSeconds);
        Assert.Equal("倉庫", plan.Steps[2].PresetName);
        Assert.Equal(60, plan.Steps[2].DwellSeconds);
    }

    [Fact]
    public void Save_NullId_OnSameChannel_OverwritesRatherThanDuplicating()
    {
        var first = _repo.Save(null, "第一版", 1, false, "00:00", "23:59", _base, SampleSteps());
        var second = _repo.Save(null, "第二版", 1, true, "09:00", "17:00", _base.AddMinutes(1), new[] { new PatrolStepRow("大門A", 10) });

        Assert.Equal(first, second);
        var plan = _repo.GetByChannel(1);
        Assert.NotNull(plan);
        Assert.Equal("第二版", plan.Name);
        Assert.True(plan.Enabled);
        Assert.Single(plan.Steps);
        Assert.Single(_repo.ListAll());
    }

    [Fact]
    public void Save_WithExplicitId_ReplacesStepsAndKeepsPlan()
    {
        var id = _repo.Save(null, "計畫", 1, true, "00:00", "23:59", _base, SampleSteps());
        var sameId = _repo.Save(id, "計畫改名", 1, false, "10:00", "20:00", _base.AddMinutes(5), new[] { new PatrolStepRow("倉庫", 90) });

        Assert.Equal(id, sameId);
        var plan = _repo.GetByChannel(1);
        Assert.NotNull(plan);
        Assert.Equal("計畫改名", plan.Name);
        Assert.False(plan.Enabled);
        Assert.Equal("10:00", plan.WindowStart);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("倉庫", step.PresetName);
        Assert.Equal(90, step.DwellSeconds);
    }

    [Fact]
    public void EmptySteps_ArePersistedAsPlanWithoutPresets()
    {
        var id = _repo.Save(null, "空持續", 1, true, "00:00", "23:59", _base, Array.Empty<PatrolStepRow>());

        var plan = _repo.GetByChannel(1);
        Assert.NotNull(plan);
        Assert.Equal(id, plan.Id);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Delete_RemovesPlanAndSteps()
    {
        var id = _repo.Save(null, "待刪", 1, true, "00:00", "23:59", _base, SampleSteps());

        Assert.True(_repo.Delete(id));
        Assert.Null(_repo.GetByChannel(1));
        Assert.Empty(_repo.ListAll());
        Assert.False(_repo.Delete(id));
    }

    [Fact]
    public void ListAll_OrdersByChannel_AndSeparatesPlans()
    {
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, '二號機', 'rtsp://127.0.0.1:8554/x2', NULL, 'h264', 1, 'copy');
            """);
        var id1 = _repo.Save(null, "一號", 1, true, "00:00", "12:00", _base, new[] { new PatrolStepRow("A", 5) });
        var id2 = _repo.Save(null, "二號", 2, false, "13:00", "23:00", _base, new[] { new PatrolStepRow("B", 7) });

        var all = _repo.ListAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(id1, all[0].Id);
        Assert.Equal(1, all[0].ChannelId);
        Assert.Equal(id2, all[1].Id);
        Assert.Equal(2, all[1].ChannelId);
        Assert.Single(all[0].Steps);
        Assert.Single(all[1].Steps);
    }

    [Fact]
    public void Save_ValidatesBadInputs()
    {
        Assert.Throws<ArgumentException>(() => _repo.Save(null, "  ", 1, true, "00:00", "23:59", _base, SampleSteps()));
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.Save(null, "ok", 0, true, "00:00", "23:59", _base, SampleSteps()));
        Assert.Throws<ArgumentException>(() => _repo.Save(null, "ok", 1, true, "00:00", "23:59", _base, new[] { new PatrolStepRow(" ", 5) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.Save(null, "ok", 1, true, "00:00", "23:59", _base, new[] { new PatrolStepRow("A", -1) }));
    }

    [Fact]
    public void BlankWindow_DefaultsToAllDay()
    {
        _repo.Save(null, "全天", 1, true, "  ", "  ", _base, Array.Empty<PatrolStepRow>());

        var plan = _repo.GetByChannel(1);
        Assert.NotNull(plan);
        Assert.Equal("00:00", plan.WindowStart);
        Assert.Equal("23:59", plan.WindowEnd);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}