using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

public sealed class EventTaxonomyTests
{
    [Theory]
    [InlineData("motion", EventTaxonomy.Motion)]
    [InlineData("manual", EventTaxonomy.Manual)]
    [InlineData("schedule_start", EventTaxonomy.Schedule)]
    [InlineData("offline", EventTaxonomy.Offline)]
    [InlineData("online", EventTaxonomy.Online)]
    [InlineData("network", EventTaxonomy.Offline)]
    [InlineData("io_input", EventTaxonomy.Sensor)]
    [InlineData("io_output", EventTaxonomy.Sensor)]
    [InlineData("tamper", EventTaxonomy.Shelter)]
    [InlineData("line_cross", EventTaxonomy.Tripwire)]
    [InlineData("intrusion", EventTaxonomy.Perimeter)]
    [InlineData("ai_person", EventTaxonomy.SmartMotion)]
    [InlineData("ai_vehicle", EventTaxonomy.SmartMotion)]
    [InlineData("smart_alert", EventTaxonomy.SmartMotion)]
    [InlineData("analytics", EventTaxonomy.SmartMotion)]
    [InlineData("export", EventTaxonomy.System)]
    [InlineData("snapshot", EventTaxonomy.System)]
    [InlineData(null, EventTaxonomy.System)]
    [InlineData("", EventTaxonomy.System)]
    [InlineData("unknown_future_type", EventTaxonomy.System)]
    public void RawMapsToCategory(string? raw, string expected)
    {
        Assert.Equal(expected, EventTaxonomy.CategoryOf(raw));
    }

    [Fact]
    public void AllCategoriesRoundTrip()
    {
        foreach (var c in EventTaxonomy.All)
            Assert.Equal(c, EventTaxonomy.CategoryOf(c));
    }

    [Fact]
    public void CmsTypeCodesMatchQctekEventTypes()
    {
        Assert.Equal(4, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Motion));
        Assert.Equal(4, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.SmartMotion));
        Assert.Equal(4, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Shelter));
        Assert.Equal(5, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Sensor));
        Assert.Equal(3, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Offline));
        Assert.Equal(3, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Online));
        Assert.Equal(3, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.CategoryOf("network")));
        Assert.Equal(2, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Manual));
        Assert.Equal(1, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.System));
    }

    /// <summary>
    /// 分類與 CMS 型別碼是同一份口徑的兩個方向：每一類都要能走過去再走回來，
    /// 否則「CMS 查型別 4」與「事件中心分類＝事件」會各查到不同的集合。
    /// </summary>
    [Fact]
    public void 分類與CMS型別碼可以互相換算()
    {
        foreach (var bucket in EventTaxonomy.Buckets)
        {
            var code = EventTaxonomy.BucketToCmsTypeCode(bucket);
            Assert.InRange(code, 1, 5);
            Assert.Equal(bucket, EventTaxonomy.BucketFromCmsTypeCode(code));
        }

        // 0＝不限、200–205＝POS 標籤、其餘未知值：都不要被當成「系統」，否則
        // CMS 的「全部事件」查詢會只剩下系統事件。
        Assert.Null(EventTaxonomy.BucketFromCmsTypeCode(0));
        Assert.Null(EventTaxonomy.BucketFromCmsTypeCode(200));
        Assert.Null(EventTaxonomy.BucketFromCmsTypeCode(205));
        Assert.Null(EventTaxonomy.BucketFromCmsTypeCode(-1));
    }

    /// <summary>
    /// 事件平面分類衍生自既有資料：警報的 <c>event_type</c> 與稽核的 <c>category</c>。
    /// 分類看不懂時要落在「系統」，讓那一列仍然出現在清單裡，而不是消失。
    /// </summary>
    [Theory]
    [InlineData("motion", EventTaxonomy.BucketEvent)]
    [InlineData("line_cross", EventTaxonomy.BucketEvent)]
    [InlineData("ai_person", EventTaxonomy.BucketEvent)]
    [InlineData("tamper", EventTaxonomy.BucketEvent)]
    [InlineData("io_input", EventTaxonomy.BucketAlarm)]
    [InlineData("io_trigger", EventTaxonomy.BucketAlarm)]
    [InlineData("offline", EventTaxonomy.BucketNetwork)]
    [InlineData("online", EventTaxonomy.BucketNetwork)]
    [InlineData("network", EventTaxonomy.BucketNetwork)]
    [InlineData("manual", EventTaxonomy.BucketOperation)]
    [InlineData("schedule_start", EventTaxonomy.BucketOperation)]
    [InlineData("unknown_future_type", EventTaxonomy.BucketSystem)]
    [InlineData(null, EventTaxonomy.BucketSystem)]
    public void 警報事件會歸到事件平面分類(string? eventType, string expected)
    {
        Assert.Equal(expected, EventTaxonomy.BucketOf(eventType));
    }

    [Theory]
    [InlineData("auth", EventTaxonomy.BucketOperation)]
    [InlineData("config", EventTaxonomy.BucketOperation)]
    [InlineData("export", EventTaxonomy.BucketOperation)]
    [InlineData("network", EventTaxonomy.BucketNetwork)]
    [InlineData("recording", EventTaxonomy.BucketSystem)]
    [InlineData("storage", EventTaxonomy.BucketSystem)]
    [InlineData("", EventTaxonomy.BucketSystem)]
    [InlineData(null, EventTaxonomy.BucketSystem)]
    public void 稽核類別會歸到事件平面分類(string? category, string expected)
    {
        Assert.Equal(expected, EventTaxonomy.BucketOfAuditCategory(category));
    }

    [Fact]
    public void QccTypesMatchRecEventTypes()
    {
        Assert.Equal("motion", EventTaxonomy.ToQccEventType(EventTaxonomy.Motion));
        Assert.Equal("sensor", EventTaxonomy.ToQccEventType(EventTaxonomy.Sensor));
        Assert.Equal("tripwire", EventTaxonomy.ToQccEventType(EventTaxonomy.Tripwire));
        Assert.Equal("smart_motion", EventTaxonomy.ToQccEventType(EventTaxonomy.SmartMotion));
        Assert.Equal("shelter", EventTaxonomy.ToQccEventType(EventTaxonomy.Shelter));
    }

    /// <summary>
    /// 連線狀態事件只屬於事件平面，不能當成「偵測」去開事件錄影。
    /// 舊版把離線事件一起丟進 <c>_scheduler.RegisterEvent</c>，Event 模式的頻道因此
    /// 為了「攝影機離線」而開錄，錄到的是一段連不上的 corrupt 檔。
    /// </summary>
    [Theory]
    [InlineData("offline", true)]
    [InlineData("online", true)]
    [InlineData("disconnected", true)]
    [InlineData("reconnected", true)]
    [InlineData("OFFLINE", true)]
    [InlineData("motion", false)]
    [InlineData("ai_person", false)]
    [InlineData("io_input", false)]
    [InlineData("schedule_start", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 只有離線與上線算連線狀態(string? raw, bool expected)
    {
        Assert.Equal(expected, EventTaxonomy.IsConnectivity(raw));
    }
}