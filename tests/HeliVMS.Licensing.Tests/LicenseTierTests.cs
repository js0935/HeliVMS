namespace HeliVMS.Licensing.Tests;

/// <summary>等級矩陣與端對端簽發／驗證（§19.2／§19.3）。</summary>
public class LicenseTierTests
{
    private static readonly string[] ExpectedTiers =
        ["基本版", "標準版", "專業版", "進階版", "企業版", "客製版"];

    [Fact]
    public void All_HasSixTiersInAscendingOrder()
    {
        Assert.Equal(ExpectedTiers, LicenseTiers.All.Select(t => t.Name));

        var cameras = LicenseTiers.All.Select(t => t.DefaultCameras).ToArray();
        Assert.Equal(cameras, cameras.OrderBy(c => c).ToArray());
        Assert.Equal(4, cameras[0]);
        Assert.Equal(LicenseTiers.MaxCameras, cameras[^1]);
    }

    [Fact]
    public void All_EveryTierIncludesCoreAndAi()
    {
        foreach (var tier in LicenseTiers.All)
        {
            Assert.Contains(LicenseTiers.FeatureCore, tier.Features);
            Assert.Contains(LicenseTiers.FeatureAi, tier.Features);
        }
    }

    [Fact]
    public void All_HigherTierIsSupersetOfLowerTier()
    {
        for (var i = 1; i < LicenseTiers.All.Count; i++)
        {
            var lower = LicenseTiers.All[i - 1];
            var higher = LicenseTiers.All[i];
            foreach (var feature in lower.Features)
            {
                Assert.Contains(feature, higher.Features);
            }
        }
    }

    [Fact]
    public void All_GisStartsAtAdvancedTier()
    {
        Assert.DoesNotContain(LicenseTiers.FeatureGis, LicenseTiers.All[0].Features);
        Assert.DoesNotContain(LicenseTiers.FeatureGis, LicenseTiers.All[1].Features);
        Assert.DoesNotContain(LicenseTiers.FeatureGis, LicenseTiers.All[2].Features);
        Assert.Contains(LicenseTiers.FeatureGis, LicenseTiers.All[3].Features);
    }

    [Fact]
    public void Find_ExactName_ReturnsTier()
    {
        var tier = LicenseTiers.Find("進階版");

        Assert.NotNull(tier);
        Assert.Equal(32, tier!.DefaultCameras);
    }

    [Fact]
    public void Find_WithSurroundingWhitespace_ReturnsTier()
    {
        Assert.NotNull(LicenseTiers.Find("  企業版  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("basic")]
    [InlineData("旗艦版")]
    public void Find_UnknownName_ReturnsNull(string name)
    {
        Assert.Null(LicenseTiers.Find(name));
    }

    [Fact]
    public void TierSignedLicense_ValidatesThroughLicenseManager()
    {
        var tier = LicenseTiers.Find("進階版")!;
        var payload = new LicensePayload
        {
            Machine = MachineIdProvider.GetDeviceCode(),
            IssuedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(180),
            Cameras = tier.DefaultCameras,
            Features = tier.Features,
            Issuer = "禾秝公司",
        };

        var token = LicenseSerializer.Sign(payload, Crypto.RsaPem.ParsePrivateKey(TestKeys.PrivateKeyPem));
        var state = new LicenseManager().Validate(token);

        Assert.Equal(LicenseStatus.Valid, state.Status);
        Assert.Equal(tier.DefaultCameras, state.Payload!.Cameras);
        Assert.Equal(tier.Features, state.Payload.Features);
        Assert.Equal("禾秝公司", state.Payload.Issuer);
    }

    [Fact]
    public void TierSignedLicense_IsHelvmsV2Format()
    {
        // 簽發工具與產品端共用 LicenseSerializer，故格式必然一致（§19.1）。
        var tier = LicenseTiers.Find("企業版")!;
        var token = LicenseSerializer.Sign(
            new LicensePayload
            {
                Machine = string.Empty,
                IssuedUtc = DateTime.UtcNow,
                Cameras = tier.DefaultCameras,
                Features = tier.Features,
            },
            Crypto.RsaPem.ParsePrivateKey(TestKeys.PrivateKeyPem));

        Assert.StartsWith("HELVMS-v2.", token);
        Assert.Equal(3, token.Split('.').Length);
    }

    [Theory]
    [InlineData("基本版")]
    [InlineData("標準版")]
    [InlineData("專業版")]
    [InlineData("進階版")]
    [InlineData("企業版")]
    [InlineData("客製版")]
    public void Match_TierFeatures_RoundTripsToSameTier(string tierName)
    {
        var tier = LicenseTiers.Find(tierName)!;

        var matched = LicenseTiers.Match(tier.Features, tier.DefaultCameras);

        Assert.NotNull(matched);
        Assert.Equal(tier.Name, matched!.Name);
    }

    [Fact]
    public void Match_EnterpriseVsCustom_DisambiguatedByCameras()
    {
        // 兩者功能旗標相同，只有通道數能區分（§19.3）。
        var features = LicenseTiers.Find("企業版")!.Features;

        Assert.Equal("企業版", LicenseTiers.Match(features, 64)?.Name);
        Assert.Equal("客製版", LicenseTiers.Match(features, LicenseTiers.MaxCameras)?.Name);
    }

    [Fact]
    public void Match_AugmentedFeatureSet_ReturnsNull()
    {
        // 進階版 + remote：不等於任何等級包，應視為客製授權而非標示進階版。
        var tier = LicenseTiers.Find("進階版")!;
        var features = tier.Features.Concat(["remote"]).ToArray();

        Assert.Null(LicenseTiers.Match(features, tier.DefaultCameras));
    }

    [Fact]
    public void Match_PartialFeatureSet_ReturnsNull()
    {
        // 只有 core 少於基本版的 core+ai，不應被標成基本版。
        Assert.Null(LicenseTiers.Match([LicenseTiers.FeatureCore], 32));
    }

    [Fact]
    public void Match_ExactFeaturesButUnknownFlag_ReturnsNull()
    {
        Assert.Null(LicenseTiers.Match([LicenseTiers.FeatureCore, "quantum-encryption"], 8));
    }

    [Fact]
    public void Match_CamerasBelowTierMinimum_ReturnsNull()
    {
        // 企業版功能集卻只給 32 路，低於企業版建議通道數，屬客製授權。
        var features = LicenseTiers.Find("企業版")!.Features;

        Assert.Null(LicenseTiers.Match(features, 32));
    }

    [Fact]
    public void Match_ExactFeaturesWithMoreCameras_KeepsTier()
    {
        // 基本版功能集給 32 路：功能未變，通道數超發不影響等級認定。
        var features = LicenseTiers.Find("基本版")!.Features;

        Assert.Equal("基本版", LicenseTiers.Match(features, 32)?.Name);
    }

    [Fact]
    public void Match_UnknownFeature_ReturnsNull()
    {
        Assert.Null(LicenseTiers.Match(["core", "ai", "quantum-encryption"]));
    }

    [Fact]
    public void Match_MissingRequiredFeature_ReturnsNull()
    {
        // 少了 schedule 就不是標準版；矩陣無任何等級完全符合此組合。
        Assert.Null(LicenseTiers.Match([LicenseTiers.FeatureCore]));
    }

    [Fact]
    public void Match_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(LicenseTiers.Match([]));
        Assert.Null(LicenseTiers.Match(null));
    }

    [Fact]
    public void Match_OrderIndependent()
    {
        var tier = LicenseTiers.Find("專業版")!;

        Assert.Equal(
            LicenseTiers.Match(tier.Features)?.Name,
            LicenseTiers.Match(tier.Features.Reverse())?.Name);
    }
}
