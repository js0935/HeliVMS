using HeliVMS.Alarms;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// <see cref="SmartAlertHost"/> 的行為測試（M54 智慧警報聚合）。
/// </summary>
/// <remarks>
/// 這裡守的是兩件事，都曾經真的壞過：
/// 一是<b>沒有規則命中的事件必須照常通知</b>（早期版本只回 <c>AlertTrigger?</c>，
/// 呼叫端寫成 <c>if (trigger is null) return;</c>，於是設定聚合規則之後
/// 所有一般通知都消失，而且沒有錯誤、事件中心還看得到每一筆）；
/// 二是<b>窗長要用規則自己的 FrameMinutes</b>（早期版本用 host 建構子的預設
/// 5 分鐘覆蓋規則值，設定頁寫 60 分鐘實際按 5 分鐘算，介面沒有任何線索）。
/// </remarks>
public sealed class SmartAlertHostTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"helivms-smartalert-{Guid.NewGuid():N}.db");
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _events;
    private readonly AlertRuleRepository _rules;
    private readonly DateTime _now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public SmartAlertHostTests()
    {
        _store = new SqliteStore(_dbPath);
        _store.Initialize();

        // alarm_events 與 alert_rules 的 channel_id 都是指向 channels 的外鍵，
        // 沒有真的插入一列頻道的話每個測試都會死在 FOREIGN KEY constraint failed。
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'smart-alert', 'rtsp://127.0.0.1:8554/main', NULL, 'h264', 1, 'copy');
            """);

        _events = new AlarmEventRepository(_store);
        _rules = new AlertRuleRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private AlarmEventRecord Insert(string eventType, DateTime startUtc, int channelId = 1, string? detail = null)
    {
        var id = _events.Insert(channelId, eventType, startUtc, snapshotPath: null, detail: detail);
        return new AlarmEventRecord
        {
            Id = id,
            ChannelId = channelId,
            EventType = eventType,
            StartUtc = startUtc,
            Detail = detail,
        };
    }

    /// <summary>沒有任何聚合規則時，這筆事件不該被抑制——否則通知會整個消失。</summary>
    [Fact]
    public void 沒有任何聚合規則時事件照常外送()
    {
        var host = new SmartAlertHost(_store);
        var record = Insert("motion", _now);

        var outcome = host.Observe(record, _now);

        Assert.False(outcome.Aggregated);
        Assert.Empty(outcome.Triggers);
    }

    /// <summary>設定了一條聚合規則之後，<b>別的事件類別</b>不該被那條規則吃掉。</summary>
    [Fact]
    public void 聚合規則只吃掉自己命中的事件類別()
    {
        _rules.Add("移動聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 3);
        var host = new SmartAlertHost(_store);
        var ioEvent = Insert("io", _now);

        var outcome = host.Observe(ioEvent, _now);

        Assert.False(outcome.Aggregated);
        Assert.Empty(outcome.Triggers);
    }

    /// <summary>窗內次數未達門檻時必須抑制，否則 8 次的門檻只會產生 8 次通知。</summary>
    [Fact]
    public void 窗內次數未達門檻時不產生通知()
    {
        _rules.Add("移動聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 3);
        var host = new SmartAlertHost(_store);

        var first = host.Observe(Insert("motion", _now), _now);
        var second = host.Observe(Insert("motion", _now.AddSeconds(10)), _now.AddSeconds(10));

        Assert.True(first.Aggregated);
        Assert.Empty(first.Triggers);
        Assert.True(second.Aggregated);
        Assert.Empty(second.Triggers);
    }

    /// <summary>跨過門檻的那一筆要產生聚合通知，而且只產生一次。</summary>
    [Fact]
    public void 跨過門檻的那一筆只產生一次聚合通知()
    {
        _rules.Add("移動聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 3);
        var host = new SmartAlertHost(_store);
        int triggers = 0;
        host.Triggered += (_, _) => triggers++;

        Assert.Empty(host.Observe(Insert("motion", _now), _now).Triggers);
        Assert.Empty(host.Observe(Insert("motion", _now.AddSeconds(10)), _now.AddSeconds(10)).Triggers);

        var crossing = host.Observe(Insert("motion", _now.AddSeconds(20)), _now.AddSeconds(20));
        Assert.True(crossing.Aggregated);
        Assert.Single(crossing.Triggers);
        Assert.Equal(3, crossing.Triggers[0].WindowCount);
        Assert.Equal(1, triggers);

        // 門檻之後的後續事件仍然被抑制；這才是這條規則存在的理由。
        var after = host.Observe(Insert("motion", _now.AddSeconds(30)), _now.AddSeconds(30));
        Assert.True(after.Aggregated);
        Assert.Empty(after.Triggers);
        Assert.Equal(1, triggers);
    }

    /// <summary>
    /// 窗長必須是規則自己的 FrameMinutes。
    /// </summary>
    /// <remarks>
    /// 這裡用 60 分鐘的窗、門檻 3 次，事件間隔 15 分鐘。
    /// 區分方向要特別小心：5 分鐘的窗是 60 分鐘的窗的<b>子集</b>，
    /// 所以錯的短窗只會「數得比較少」，不會數得比較多——
    /// 一個「事件都擠在 5 分鐘內」的測試對這個缺陷完全免疫（實測過，
    /// 把程式改成固定 5 分鐘時它照樣綠）。
    /// 必須把事件攤開到超過錯的窗長、又仍在對的窗長之內，才測得出來。
    /// 這個缺陷在現場的表現是：設定頁寫「60 分鐘內 8 次」，實際按 5 分鐘算，
    /// 所以永遠不會有第 8 次到達——操作員以為是自己算錯了。
    /// </remarks>
    [Fact]
    public void 窗長使用規則自己的FrameMinutes而不是主機預設()
    {
        _rules.Add("長窗聚合", null, 1, null, null, "motion", frameMinutes: 60, minEventsInWindow: 3);
        var host = new SmartAlertHost(_store);

        Assert.Empty(host.Observe(Insert("motion", _now), _now).Triggers);
        Assert.Empty(host.Observe(Insert("motion", _now.AddMinutes(15)), _now.AddMinutes(15)).Triggers);

        // 對的窗長（60 分鐘）看得到三筆；錯的 5 分鐘窗只看得到最後一筆，永遠不達門檻。
        var crossing = host.Observe(Insert("motion", _now.AddMinutes(30)), _now.AddMinutes(30));

        Assert.Single(crossing.Triggers);
        Assert.Equal(3, crossing.Triggers[0].WindowCount);
    }

    /// <summary>超出規則窗長的事件不該被算進次數——否則 60 分鐘的設定會變成 5 分鐘。</summary>
    [Fact]
    public void 超出窗長的事件不被計入次數()
    {
        _rules.Add("短窗聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 2);
        var host = new SmartAlertHost(_store);

        host.Observe(Insert("motion", _now), _now);
        var later = host.Observe(Insert("motion", _now.AddMinutes(30)), _now.AddMinutes(30));

        Assert.True(later.Aggregated);
        Assert.Empty(later.Triggers);
    }

    /// <summary>聚合結果要另外插成一筆事件，事件中心才看得到「這是聚合後的告警」。</summary>
    [Fact]
    public void 跨過門檻時另外插入一筆聚合事件()
    {
        _rules.Add("移動聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 1);
        var host = new SmartAlertHost(_store);

        host.Observe(Insert("motion", _now), _now);

        var summaries = _events.ListByRange(1, _now.AddMinutes(-1), _now.AddMinutes(1))
            .Where(e => e.EventType == SmartAlertHost.SmartAlertEventType)
            .ToList();

        Assert.Single(summaries);
        Assert.Contains("移動聚合", summaries[0].Detail);
    }

    /// <summary>窗長是跨午夜判斷時錨點，窗外的舊事件不能讓新窗一開始就告警。</summary>
    [Fact]
    public void 跨過門檻後新的窗會重新允許告警()
    {
        _rules.Add("移動聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 2);
        var host = new SmartAlertHost(_store);

        host.Observe(Insert("motion", _now), _now);
        var crossing = host.Observe(Insert("motion", _now.AddMinutes(1)), _now.AddMinutes(1));
        Assert.Single(crossing.Triggers);

        // 窗滿之後再累積到門檻，應該重新告警一次，而不是永遠被「已經告過」壓住。
        var next = _now.AddMinutes(10);
        host.Observe(Insert("motion", next), next);
        var rearm = host.Observe(Insert("motion", next.AddMinutes(1)), next.AddMinutes(1));

        Assert.Single(rearm.Triggers);
    }

    /// <summary>停用的規則不該生效：設定頁關掉一條規則後必須真的安靜下來。</summary>
    [Fact]
    public void 停用的規則不再抑制事件()
    {
        var id = _rules.Add("移動聚合", null, 1, null, null, "motion", frameMinutes: 5, minEventsInWindow: 3);
        _rules.SetEnabled(id, false);
        var host = new SmartAlertHost(_store);

        var outcome = host.Observe(Insert("motion", _now), _now);

        Assert.False(outcome.Aggregated);
    }

    /// <summary>FrameMinutes=0 是「不聚合」，這類規則不該吃掉事件。</summary>
    [Fact]
    public void 未設定聚合窗長的規則不抑制事件()
    {
        _rules.Add("普通規則", "motion", null, null, null);
        var host = new SmartAlertHost(_store);

        var outcome = host.Observe(Insert("motion", _now), _now);

        Assert.False(outcome.Aggregated);
    }
}
