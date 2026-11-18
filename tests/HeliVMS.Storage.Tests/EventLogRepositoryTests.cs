using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 事件平面（警報事件 ∪ 稽核日誌）的查詢契約。
/// </summary>
/// <remarks>
/// 事件中心要對齊參考機的單一「事件紀錄」清單，但資料本來就分兩張表。這裡釘住的回歸是：
/// 分類篩選與「精確總數」不一致（清單看得到、選那個分類卻查不到）、
/// 「僅未確認」混進永遠不會被確認的稽核列、頻道篩選把稽核列整批濾掉（或整批留下），
/// 以及兩個來源的 id 撞號。
/// </remarks>
public sealed class EventLogRepositoryTests : IDisposable
{
    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _alarms;
    private readonly AuditLogRepository _audit;
    private readonly EventLogRepository _plane;
    private readonly int _channelA;
    private readonly int _channelB;

    public EventLogRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-evtplane-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _alarms = new AlarmEventRepository(_store);
        _audit = new AuditLogRepository(_store);
        _plane = new EventLogRepository(_store);

        _channelA = AddChannel("攝影機A");
        _channelB = AddChannel("攝影機B");

        // 警報：動作事件（Event 分類）＋離線事件（Network 分類）。
        AddAlarm(_channelA, "motion", new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc), "moving");
        AddAlarm(_channelA, "offline", new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc), "camera lost");
        AddAlarm(_channelB, "ai_person", new DateTime(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc), "person conf=0.9");

        // 稽核：操作（auth）與系統（recording），其中一筆指向頻道 B。
        _audit.Record("admin", "login", "auth", occurredAtUtc: new DateTime(2026, 9, 10, 7, 0, 0, DateTimeKind.Utc));
        _audit.Record(
            "admin", "channel.update", "config", "channel", _channelB, "rename",
            new DateTime(2026, 9, 10, 11, 0, 0, DateTimeKind.Utc));
        _audit.Record(
            "system", "recording.stalled", "recording", "segment", 42, "ffmpeg exit 1",
            new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private int AddChannel(string name)
    {
        _store.Execute(
            "INSERT INTO channels (name, main_rtsp) VALUES ($n, $u);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$u", $"rtsp://{name}");
            });
        return _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });
    }

    private void AddAlarm(int channelId, string eventType, DateTime startUtc, string detail)
    {
        var id = _alarms.Insert(channelId, eventType, startUtc, null, detail);
        _alarms.UpdateEnd(id, startUtc.AddSeconds(30), null);
    }

    private EventLogQuery Wide(
        string? bucket = null,
        long? channelId = null,
        string? eventType = null,
        string? status = null,
        string? keyword = null,
        IReadOnlyList<long>? eventIds = null,
        int limit = 200,
        int offset = 0) => new()
    {
        Bucket = bucket,
        ChannelId = channelId,
        EventType = eventType,
        Status = status,
        Keyword = keyword,
        EventIds = eventIds,
        FromUtc = From,
        ToUtc = To,
        Limit = limit,
        Offset = offset,
    };

    [Fact]
    public void 聯集會同時帶出警報與稽核且由新到舊()
    {
        var list = _plane.List(Wide());

        Assert.Equal(6, list.Count);
        Assert.Equal(6, _plane.Count(Wide()));
        Assert.Equal(3, list.Count(e => e.Origin == EventLogOrigin.Alarm));
        Assert.Equal(3, list.Count(e => e.Origin == EventLogOrigin.Audit));
        Assert.Equal(
            list.Select(e => e.OccurredAtUtc).OrderByDescending(t => t),
            list.Select(e => e.OccurredAtUtc));
    }

    [Fact]
    public void 稽核列帶出操作者與動作而警報列操作者為空()
    {
        var list = _plane.List(Wide());

        var login = Assert.Single(list, e => e.Code == "login");
        Assert.Equal("admin", login.Actor);
        Assert.True(login.IsAudit);
        Assert.Null(login.Status);
        Assert.False(login.Acknowledged);
        Assert.Null(login.DurationSeconds);

        var motion = Assert.Single(list, e => e.Code == "motion");
        Assert.Null(motion.Actor);
        Assert.False(motion.IsAudit);
        Assert.Equal(AlarmEventStatus.Pending, motion.Status);
        Assert.Equal(30d, motion.DurationSeconds!.Value, 3);
    }

    [Fact]
    public void 分類欄取自共用口徑且橫跨兩個來源()
    {
        var list = _plane.List(Wide());

        Assert.Equal(EventTaxonomy.BucketEvent, Assert.Single(list, e => e.Code == "motion").Bucket);
        Assert.Equal(EventTaxonomy.BucketEvent, Assert.Single(list, e => e.Code == "ai_person").Bucket);
        Assert.Equal(EventTaxonomy.BucketNetwork, Assert.Single(list, e => e.Code == "offline").Bucket);
        Assert.Equal(EventTaxonomy.BucketOperation, Assert.Single(list, e => e.Code == "login").Bucket);
        Assert.Equal(EventTaxonomy.BucketSystem, Assert.Single(list, e => e.Code == "recording.stalled").Bucket);
    }

    [Theory]
    [InlineData(EventTaxonomy.BucketEvent, 2)]
    [InlineData(EventTaxonomy.BucketNetwork, 1)]
    [InlineData(EventTaxonomy.BucketOperation, 2)]
    [InlineData(EventTaxonomy.BucketSystem, 1)]
    public void 分類篩選的筆數與精確總數一致(string bucket, int expected)
    {
        var q = Wide(bucket);
        var list = _plane.List(q);

        Assert.Equal(expected, list.Count);
        Assert.Equal(expected, _plane.Count(q));
        Assert.All(list, e => Assert.Equal(bucket, e.Bucket));
    }

    [Fact]
    public void 不存在的分類回空而不是全部()
    {
        var q = Wide("not-a-bucket");

        Assert.Empty(_plane.List(q));
        Assert.Equal(0, _plane.Count(q));
    }

    [Fact]
    public void 僅未確認的篩選不會帶出稽核列()
    {
        var q = Wide(status: AlarmEventStatus.Pending);
        var list = _plane.List(q);

        Assert.Equal(3, list.Count);
        Assert.All(list, e => Assert.False(e.IsAudit));
    }

    [Fact]
    public void 指定事件類型時不會帶出稽核列()
    {
        var list = _plane.List(Wide(eventType: "motion"));

        var row = Assert.Single(list);
        Assert.Equal("motion", row.Code);
        Assert.False(row.IsAudit);
    }

    [Fact]
    public void 頻道篩選對稽核列走目標頻道()
    {
        var q = Wide(channelId: _channelB);
        var list = _plane.List(q);

        // 頻道 B：ai_person 警報＋目標為頻道 B 的 config 稽核；
        // 目標是 segment 的 recording 稽核與登入稽核都不屬於任何頻道。
        Assert.Equal(2, list.Count);
        Assert.Equal(2, _plane.Count(q));
        Assert.Contains(list, e => e.Code == "ai_person");
        Assert.Contains(list, e => e.Code == "channel.update");
        Assert.DoesNotContain(list, e => e.Code == "login");
        Assert.DoesNotContain(list, e => e.Code == "recording.stalled");
    }

    [Fact]
    public void 關鍵字同時比對稽核動作與詳情()
    {
        Assert.Equal("login", Assert.Single(_plane.List(Wide(keyword: "login"))).Code);
        Assert.Equal("recording.stalled", Assert.Single(_plane.List(Wide(keyword: "ffmpeg"))).Code);
        Assert.Equal("channel.update", Assert.Single(_plane.List(Wide(keyword: "rename"))).Code);
    }

    [Fact]
    public void 事件id白名單只影響警報而不會帶進稽核列()
    {
        var motion = Assert.Single(_plane.List(Wide(keyword: "moving")));
        Assert.False(motion.IsAudit);

        // 用一個「同時會撞到稽核 id」的白名單：兩個來源的 id 各自從 1 起算，
        // 若白名單套到稽核表，就會多回一列不是被命中的事件。
        var q = Wide(eventIds: [motion.Id]);
        var list = _plane.List(q);

        Assert.Equal("motion", Assert.Single(list).Code);
        Assert.Equal(1, _plane.Count(q));
    }

    [Fact]
    public void 分頁的總數是聯集後的精確筆數()
    {
        var first = _plane.List(Wide(limit: 2, offset: 0));
        var last = _plane.List(Wide(limit: 2, offset: 4));

        Assert.Equal(2, first.Count);
        Assert.Equal(2, last.Count);
        Assert.Equal(6, _plane.Count(Wide()));
        Assert.Empty(first.Select(e => (e.Origin, e.Id)).Intersect(last.Select(e => (e.Origin, e.Id))));
    }

    [Fact]
    public void 時間窗兩端都是含的且同時套到兩個來源()
    {
        var q = new EventLogQuery
        {
            FromUtc = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc),
        };

        var list = _plane.List(q);

        // 8:00 motion、9:00 offline、10:00 ai_person；稽核全部落在窗外。
        Assert.Equal(3, list.Count);
        Assert.All(list, e => Assert.False(e.IsAudit));
    }

    [Fact]
    public void 分類篩選與頻道篩選可以並用()
    {
        var q = Wide(EventTaxonomy.BucketEvent, _channelA);

        var row = Assert.Single(_plane.List(q));
        Assert.Equal("motion", row.Code);
        Assert.Equal(1, _plane.Count(q));
    }

    /// <summary>
    /// 事件中心的「指派」與「備註」兩欄來自 <c>event_dispositions</c>，而稽核列沒有處置。
    /// </summary>
    /// <remarks>
    /// 兩張表的 id 各自從 1 起算，所以稽核列的 <c>id</c> 幾乎一定撞到某個警報事件。
    /// 若 SELECT 把處置表的 join 寫成「以 id 對應」而沒有綁在警報那一半，稽核列就會借用
    /// 別的警報的處置——畫面上是「系統登入」那列寫著被指派給某個人，看起來只是資料怪，
    /// 但事件紀錄是要拿來追責任的。
    /// </remarks>
    [Fact]
    public void 處置的指派與備註會帶進事件平面而稽核列為空()
    {
        var motion = Assert.Single(_plane.List(Wide(eventType: "motion")));
        _alarms.SetDisposition(
            motion.Id,
            AlarmEventStatus.Actioned,
            "小明",
            "已通知現場",
            motion.OccurredAtUtc.AddHours(1));

        var row = Assert.Single(_plane.List(Wide(eventType: "motion")));
        Assert.Equal("小明", row.AssignedTo);
        Assert.Equal("已通知現場", row.Note);
        Assert.Equal(AlarmEventStatus.Actioned, row.Status);

        var audit = Assert.Single(_plane.List(Wide(keyword: "login")));
        Assert.True(audit.IsAudit);
        Assert.Null(audit.AssignedTo);
        Assert.Null(audit.Note);
    }
}
