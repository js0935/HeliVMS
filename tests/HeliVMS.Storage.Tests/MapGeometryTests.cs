namespace HeliVMS.Storage.Tests;

public class MapGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(405, 45)]
    [InlineData(-450, 270)]
    public void NormalizeAngle_WrapsIntoRange(double input, double expected)
        => Assert.Equal(expected, MapGeometry.NormalizeAngle(input));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NormalizeAngle_NonFiniteThrows(double input)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MapGeometry.NormalizeAngle(input));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(90, 90)]
    [InlineData(400, 360)]
    public void ClampFov_ClampsToBounds(double input, double expected)
        => Assert.Equal(expected, MapGeometry.ClampFov(input));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(3, 3)]
    [InlineData(1000, 1000)]
    [InlineData(2000, 1000)]
    public void ClampDepth_ClampsToBounds(double input, double expected)
        => Assert.Equal(expected, MapGeometry.ClampDepth(input));

    [Theory]
    [InlineData(0, "東")]
    [InlineData(45, "東南")]
    [InlineData(90, "南")]
    [InlineData(135, "西南")]
    [InlineData(180, "西")]
    [InlineData(225, "西北")]
    [InlineData(270, "北")]
    [InlineData(315, "東北")]
    [InlineData(-90, "北")]
    [InlineData(400, "東南")]
    public void Bearing_ReturnsEightWayName(double angle, string expected)
        => Assert.Equal(expected, MapGeometry.Bearing(angle));

    [Fact]
    public void SectorRadiusPixels_UsesDepthOverScale()
    {
        Assert.Equal(100, MapGeometry.SectorRadiusPixels(fovDepthMeters: 5, scaleMPerPx: 0.05));
        Assert.Equal(50, MapGeometry.SectorRadiusPixels(fovDepthMeters: 30, scaleMPerPx: 0.6));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(0, 0.05)]
    [InlineData(-5, 0.05)]
    [InlineData(5, -1)]
    public void SectorRadiusPixels_FallsBackWhenUnscaled(double depth, double scale)
        => Assert.Equal(MapGeometry.DefaultSectorRadiusPixels, MapGeometry.SectorRadiusPixels(depth, scale));

    [Fact]
    public void SectorRadiusPixels_ClampsExtremes()
    {
        Assert.Equal(MapGeometry.MaxSectorRadiusPixels, MapGeometry.SectorRadiusPixels(1000, 0.0001));
        Assert.Equal(MapGeometry.MinSectorRadiusPixels, MapGeometry.SectorRadiusPixels(0.0001, 1));
    }

    [Fact]
    public void ScaleLabel_FormatsScaleOrUncalibrated()
    {
        Assert.Equal("0.05 m/px", MapGeometry.ScaleLabel(0.05));
        Assert.Equal("未標定", MapGeometry.ScaleLabel(0));
        Assert.Equal("未標定", MapGeometry.ScaleLabel(-1));
    }

    [Fact]
    public void ValidateGeometry_NormalizesAndThrowsOutOfRange()
    {
        Assert.Equal(45, MapGeometry.ValidateGeometry(405, 120, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapGeometry.ValidateGeometry(0, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapGeometry.ValidateGeometry(0, 90, 1001));
    }
}
