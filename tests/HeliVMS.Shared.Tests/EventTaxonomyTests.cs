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
        Assert.Equal(5, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Sensor));
        Assert.Equal(3, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Offline));
        Assert.Equal(2, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.Manual));
        Assert.Equal(1, EventTaxonomy.ToCmsTypeCode(EventTaxonomy.System));
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
}