namespace HeliVMS.Alarms.Tests;

public class DetectionDetailTests
{
    [Fact]
    public void Parse_EngineStyleDetail_ReturnsDetection()
    {
        Assert.True(DetectionDetail.TryParse(
            "person conf=0.88 bbox=(0.39,0.59,0.11,0.42)",
            out var d));
        Assert.Equal("person", d.Class);
        Assert.Equal(0.88f, d.Confidence);
        Assert.Equal(0.39f, d.X);
        Assert.Equal(0.59f, d.Y);
        Assert.Equal(0.11f, d.W);
        Assert.Equal(0.42f, d.H);
    }

    [Fact]
    public void Parse_BusDetail_ReturnsDetection()
    {
        Assert.True(DetectionDetail.TryParse(
            "bus conf=0.86 bbox=(0.51,0.46,0.71,0.50)",
            out var d));
        Assert.Equal("bus", d.Class);
        Assert.Equal(0.86f, d.Confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("motion peak=0.45 area=0.32")]
    [InlineData("person conf=0.88 bbox=(0.39)")]
    public void Parse_NonAiDetail_ReturnsFalse(string? detail)
    {
        Assert.False(DetectionDetail.TryParse(detail, out _));
    }
}