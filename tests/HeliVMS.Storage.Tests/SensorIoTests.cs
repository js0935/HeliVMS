namespace HeliVMS.Storage.Tests;

public class SensorIoTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;

    public SensorIoTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-io-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _ports = new IoPortRepository(_store);
        _rules = new IoRuleRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, '感測器機台', 'rtsp://127.0.0.1:8554/io', NULL, 'h264', 1, 'copy');
            """);
    }

    private static DateTime Base() => new(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc);

    private long AddDi(int number = 1, string name = "門禁感測", IoPolarity polarity = IoPolarity.NormallyOpen, bool enabled = true)
        => _ports.Add(1, IoPortKind.Di, number, name, polarity, 0, enabled);

    private long AddDo(int number = 1, string name = "警報燈")
        => _ports.Add(1, IoPortKind.Do, number, name, IoPolarity.NormallyOpen);

    [Fact]
    public void IoPortRepository_AddList_RoundTripsFields()
    {
        var id = _ports.Add(1, IoPortKind.Di, 3, "紅外探測", IoPolarity.NormallyClosed, 20, enabled: true);

        var p = _ports.Get(id);
        Assert.NotNull(p);
        Assert.Equal(1, p.ChannelId);
        Assert.Equal(IoPortKind.Di, p.Kind);
        Assert.Equal(3, p.Number);
        Assert.Equal("紅外探測", p.Name);
        Assert.Equal(IoPolarity.NormallyClosed, p.Polarity);
        Assert.Equal(20, p.DebounceMs);
        Assert.True(p.Enabled);

        var all = _ports.List();
        Assert.Single(all);
        Assert.Equal(id, all[0].Id);
    }

    [Fact]
    public void IoPortRepository_DuplicatePort_Throws()
    {
        var di1 = AddDi(1);
        Assert.NotNull(_ports.Get(di1));
        Assert.Throws<ArgumentException>(() => AddDi(1, "重複"));
        var di2 = AddDi(2, "不同端子可共存");
        Assert.True(_ports.Exists(1, IoPortKind.Di, 2));
        var do1 = AddDo(1);
        Assert.True(_ports.Exists(1, IoPortKind.Do, 1));
        Assert.Throws<ArgumentException>(() => _ports.Add(1, IoPortKind.Do, 1, "佔用 DO", IoPolarity.NormallyOpen));
    }

    [Fact]
    public void IoPortRepository_Delete_RemovesPortAndItsRules()
    {
        var di = AddDi();
        var doId = AddDo();
        _rules.Add(di, IoActionKind.ToggleDo, doId, null, 30, true);

        Assert.True(_ports.Delete(di));
        Assert.Null(_ports.Get(di));
        Assert.Empty(_rules.List());
        Assert.False(_ports.Delete(di));
        Assert.Single(_ports.List());
    }

    [Fact]
    public void IoRuleRepository_AddList_Validates()
    {
        var di = AddDi();
        var doId = AddDo();
        var ruleId = _rules.Add(di, IoActionKind.Alarm, null, "motion", 45, enabled: false);

        var r = _rules.List().Single();
        Assert.Equal(ruleId, r.Id);
        Assert.Equal(di, r.InputPortId);
        Assert.Equal(IoActionKind.Alarm, r.ActionKind);
        Assert.Equal("motion", r.EventType);
        Assert.Equal(45, r.RetriggerSec);
        Assert.False(r.Enabled);

        Assert.Throws<ArgumentException>(() => _rules.Add(di, IoActionKind.Alarm, null, " "));
        Assert.Throws<ArgumentException>(() => _rules.Add(di, IoActionKind.ToggleDo, null, null));
        Assert.Single(_rules.ListByInput(di));
    }

    [Fact]
    public void IoRuleEngine_RisingEdge_TriggersAlarmAction()
    {
        var portId = AddDi();
        var ruleId = _rules.Add(portId, IoActionKind.Alarm, null, "ai_intrusion", 30, true);
        var engine = MakeEngine();

        Assert.Empty(engine.OnInput(portId, false, Base()));
        var actions = engine.OnInput(portId, true, Base().AddSeconds(1));

        var action = Assert.Single(actions);
        Assert.Equal(IoActionKind.Alarm, action.Kind);
        Assert.Equal(ruleId, action.RuleId);
        Assert.Equal(1, action.ChannelId);
        Assert.Equal(portId, action.InputPortId);
        Assert.Equal("ai_intrusion", action.EventType);
    }

    [Fact]
    public void IoRuleEngine_SameValue_NoRepeatedAction()
    {
        var portId = AddDi();
        _rules.Add(portId, IoActionKind.Alarm, null, "motion", 0, true);
        var engine = MakeEngine();

        _ = engine.OnInput(portId, true, Base());
        var second = engine.OnInput(portId, true, Base().AddSeconds(5));
        Assert.Empty(second);
    }

    [Fact]
    public void IoRuleEngine_Cooldown_BlocksThenAllows()
    {
        var portId = AddDi(polarity: IoPolarity.NormallyClosed);
        _rules.Add(portId, IoActionKind.Alarm, null, "tamper", 60, true);
        var engine = MakeEngine();
        var t = Base();

        // 常閉埠：實體 false→邏輯 true＝上昇沿（觸發）；實體 true→邏輯 false＝下降（無）
        Assert.Single(engine.OnInput(portId, false, t));
        Assert.Empty(engine.OnInput(portId, true, t.AddSeconds(1)));
        Assert.Empty(engine.OnInput(portId, false, t.AddSeconds(30)));
        Assert.Empty(engine.OnInput(portId, true, t.AddSeconds(31)));
        Assert.Single(engine.OnInput(portId, false, t.AddSeconds(61)));
    }

    [Fact]
    public void IoRuleEngine_DisabledRule_Skipped()
    {
        var portId = AddDi();
        _rules.Add(portId, IoActionKind.Alarm, null, "motion", 30, enabled: false);
        var engine = MakeEngine();

        Assert.Empty(engine.OnInput(portId, true, Base()));
    }

    [Fact]
    public void IoRuleEngine_NormallyClosed_InvertsLogic()
    {
        var portId = AddDi(polarity: IoPolarity.NormallyClosed);
        _rules.Add(portId, IoActionKind.Alarm, null, "door", 0, true);
        var engine = MakeEngine();

        var actions = engine.OnInput(portId, false, Base());
        Assert.Single(actions);
        Assert.True(engine.GetInputState(portId));

        Assert.Empty(engine.OnInput(portId, true, Base().AddSeconds(1)));
        Assert.False(engine.GetInputState(portId));
    }

    [Fact]
    public void IoRuleEngine_ToggleDo_FlipsOutputState()
    {
        var di = AddDi();
        var doId = AddDo();
        _rules.Add(di, IoActionKind.ToggleDo, doId, null, 0, true);
        var engine = MakeEngine();

        var first = engine.OnInput(di, true, Base());
        var a1 = Assert.Single(first);
        Assert.Equal(IoActionKind.ToggleDo, a1.Kind);
        Assert.Equal(doId, a1.OutputPortId);
        Assert.True(engine.GetOutputState(doId));

        engine.OnInput(di, false, Base().AddSeconds(2));
        var second = engine.OnInput(di, true, Base().AddSeconds(3));
        Assert.Single(second);
        Assert.False(engine.GetOutputState(doId));
    }

    [Fact]
    public void IoRuleEngine_UnknownPort_Throws()
    {
        var engine = MakeEngine();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.OnInput(999, true, Base()));
    }

    [Fact]
    public void IoRuleEngine_PortWithoutRules_TracksStateNoAction()
    {
        var portId = AddDi();
        var engine = MakeEngine();

        var actions = engine.OnInput(portId, true, Base());
        Assert.Empty(actions);
        Assert.True(engine.GetInputState(portId));

        engine.Reset();
        Assert.Null(engine.GetInputState(portId));
    }

    private IoRuleEngine MakeEngine()
        => new(_ports.List(), _rules.List());

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}