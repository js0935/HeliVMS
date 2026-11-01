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

    // ── 攝影機端子綁定（設定中心 IO 頁）──────────────────────────────────
    // 為什麼要「明確綁定」：ONVIF 的端子權杖是不透明字串，先前只能靠端子號猜對應；
    // 猜錯就是驅動錯的閘門，或讓 A 端子的訊號觸發 B 端子的規則。這裡釘住綁定的
    // 往返、查詢與冪等性——設定頁的按鈕會被連按，也可能對同一端子重複按。

    [Fact]
    public void 建立埠時可一併記下攝影機端子權杖並讀回()
    {
        var id = _ports.Add(
            1, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen, terminalToken: "RelayOutputToken_7");

        Assert.Equal("RelayOutputToken_7", _ports.Get(id)!.TerminalToken);
        Assert.Equal("RelayOutputToken_7", Assert.Single(_ports.List()).TerminalToken);
        Assert.Equal("RelayOutputToken_7", Assert.Single(_ports.ListByChannel(1)).TerminalToken);
    }

    [Fact]
    public void 未指定端子權杖時存成空字串而不是null()
    {
        var id = AddDo();

        // 空字串＝未綁定；若存成 null，記錄型別的非空字串欄位會在讀取時擲例外
        // （SQLite 的 DBNull 沒有字串可給），播放／IO 頁會整頁空白。
        Assert.Equal(string.Empty, _ports.Get(id)!.TerminalToken);
    }

    [Fact]
    public void 端子綁定可依權杖找回同一個埠()
    {
        var id = _ports.Add(1, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen, terminalToken: "DigitalInputToken_1");

        Assert.Equal(id, _ports.FindByTerminal(1, IoPortKind.Di, "DigitalInputToken_1")!.Id);

        // 不同頻道／不同方向／空白權杖都不該命中：權杖只在同一台攝影機的同一種端子上有意義。
        Assert.Null(_ports.FindByTerminal(2, IoPortKind.Di, "DigitalInputToken_1"));
        Assert.Null(_ports.FindByTerminal(1, IoPortKind.Do, "DigitalInputToken_1"));
        Assert.Null(_ports.FindByTerminal(1, IoPortKind.Di, "  "));
    }

    [Fact]
    public void 同一個端子重複綁定不會多出第二個埠()
    {
        var (firstId, firstCreated) = _ports.EnsureForTerminal(1, IoPortKind.Di, 1, "門磁", "DigitalInputToken_1");
        var (secondId, secondCreated) = _ports.EnsureForTerminal(1, IoPortKind.Di, 1, "門磁", "DigitalInputToken_1");

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(firstId, secondId);
        Assert.Single(_ports.List());
    }

    [Fact]
    public void 端子號已被別的埠佔用時綁定會沿用那一個而不是新增()
    {
        // 操作員可能先在「IO 通道」頁手動建了端子 1，之後才到攝影機區塊綁定；
        // (channel, kind, number) 有唯一鍵，硬新增會擲例外並讓整個綁定看起來失敗。
        var existing = AddDi(1, "手動建立的門磁");

        var (id, created) = _ports.EnsureForTerminal(1, IoPortKind.Di, 1, "裝置回報的名稱", "DigitalInputToken_1");

        Assert.False(created);
        Assert.Equal(existing, id);
        Assert.Equal("DigitalInputToken_1", _ports.Get(existing)!.TerminalToken);
        Assert.Equal("手動建立的門磁", _ports.Get(existing)!.Name);   // 名稱不覆寫操作員取的
    }

    [Fact]
    public void 解除綁定不會刪掉埠與它的規則()
    {
        var di = _ports.Add(1, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen, terminalToken: "DigitalInputToken_1");
        var doPort = AddDo();
        _rules.Add(di, IoActionKind.ToggleDo, doPort, null, 0, true);

        _ports.SetTerminalToken(di, null);

        Assert.Equal(string.Empty, _ports.Get(di)!.TerminalToken);
        Assert.Null(_ports.FindByTerminal(1, IoPortKind.Di, "DigitalInputToken_1"));
        Assert.Single(_rules.List());
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