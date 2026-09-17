using HeliVMS.Recording;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class DewarpFilterTests
{
    [Fact]
    public void Build_FisheyeToFlat_ContainsV360()
    {
        var filter = DewarpFilter.Build(DewarpSettings.Default);

        Assert.Equal(
            "v360=input=fisheye:output=flat:ih_fov=180:iv_fov=180:h_fov=90:v_fov=90:yaw=0:pitch=0:roll=0:w=1280:h=720",
            filter);
    }

    [Fact]
    public void Build_DualFisheyeToEquirect_FormatsAngles()
    {
        var filter = DewarpFilter.Build(new DewarpSettings
        {
            Input = DewarpProjection.DualFisheye,
            Output = DewarpProjection.Equirect,
            HFov = 360,
            VFov = 180,
            Yaw = 10,
            Pitch = -5,
            Roll = 2.5,
            Width = 1920,
            Height = 960,
        });

        Assert.Equal(
            "v360=input=dfisheye:output=equirect:ih_fov=180:iv_fov=180:h_fov=360:v_fov=180:yaw=10:pitch=-5:roll=2.5:w=1920:h=960",
            filter);
    }

    [Fact]
    public void Build_ZeroSize_OmitsWidthHeight()
    {
        var filter = DewarpFilter.Build(new DewarpSettings
        {
            Input = DewarpProjection.Equirect,
            Output = DewarpProjection.Cubemap3x2,
            Width = 0,
            Height = 0,
        });

        Assert.Equal("c3x2", filter[(filter.IndexOf("output=", StringComparison.Ordinal) + 7)..].Split(':')[0]);
        Assert.EndsWith(":roll=0", filter);
        Assert.DoesNotContain(":w=", filter);
        Assert.DoesNotContain(":h=", filter);
    }

    [Fact]
    public void Build_FovOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { HFov = 361 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { InputVFov = -1 }));
    }

    [Fact]
    public void Build_AngleOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { Yaw = 181 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { Pitch = -180.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { Roll = 200 }));
    }

    [Fact]
    public void Build_FlatWithZeroFov_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DewarpFilter.Build(new DewarpSettings { Output = DewarpProjection.Flat, HFov = 0 }));
        Assert.Throws<InvalidOperationException>(() =>
            DewarpFilter.Build(new DewarpSettings { Output = DewarpProjection.Flat, VFov = 0 }));
    }

    [Fact]
    public void Build_NegativeSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { Width = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DewarpFilter.Build(new DewarpSettings { Height = -1 }));
    }

    [Fact]
    public void Build_InvalidProjection_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DewarpFilter.Build(new DewarpSettings { Input = DewarpProjection.Flat }));
        Assert.Throws<ArgumentException>(() =>
            DewarpFilter.Build(new DewarpSettings { Output = DewarpProjection.Fisheye }));
    }

    [Fact]
    public void ToToken_MapsAllProjections()
    {
        Assert.Equal("fisheye", DewarpFilter.ToToken(DewarpProjection.Fisheye));
        Assert.Equal("dfisheye", DewarpFilter.ToToken(DewarpProjection.DualFisheye));
        Assert.Equal("equirect", DewarpFilter.ToToken(DewarpProjection.Equirect));
        Assert.Equal("flat", DewarpFilter.ToToken(DewarpProjection.Flat));
        Assert.Equal("c3x2", DewarpFilter.ToToken(DewarpProjection.Cubemap3x2));
    }
}
