using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證多廠牌 profile 命名之主/子碼流判斷、解析度規則、挑選與排序。</summary>
public class OnvifProfileSelectionTests
{
    [Theory]
    [InlineData("MainStream", "MainProfile")]
    [InlineData("主碼流", "Profile_1")]
    [InlineData("Main", "Main")]
    [InlineData("Stream1", "high")]
    public void HasSubStreamKeyword_ForMainStreamNaming_IsFalse(string name, string token)
    {
        var profile = Profile(name, token, 1920, 1080);

        Assert.False(OnvifProfileSelection.HasSubStreamKeyword(profile));
        Assert.True(OnvifProfileSelection.HasMainStreamKeyword(profile));
        Assert.False(profile.IsSubStream);
    }

    [Theory]
    [InlineData("SubStream", "SubProfile")]
    [InlineData("子碼流", "Profile_2")]
    [InlineData("Sub", "Sub")]
    [InlineData("Stream2", "low")]
    public void HasSubStreamKeyword_ForSubStreamNaming_IsTrue(string name, string token)
    {
        var profile = Profile(name, token, 640, 360);

        Assert.True(OnvifProfileSelection.HasSubStreamKeyword(profile));
        Assert.True(profile.IsSubStream);
    }

    [Theory]
    [InlineData("Profile100")]
    [InlineData("Profile101")]
    [InlineData("Profile102")]
    [InlineData("101")]
    [InlineData("102")]
    public void 純編號的token不會被關鍵字判成主或子碼流(string token)
    {
        // 關鍵字表裡的 stream1／stream2／profile1／profile2 指的是「第一路／第二路」。
        // 少了數字邊界，profile1 會命中 Profile100 與 Profile102，於是 Profile101
        // （串流2，真正的子碼流）被當成主碼流、Profile102（串流3）成為唯一的子碼流候選。
        // 純編號（101／102）本身也不該被直接判主或判子——第幾路要看同一組的其他 profile。
        var profile = Profile(token, token, 1920, 1080);

        Assert.False(OnvifProfileSelection.HasSubStreamKeyword(profile));
        Assert.False(OnvifProfileSelection.HasMainStreamKeyword(profile));
    }

    [Fact]
    public void 三路編號的次碼流是第二路而不是第三路()
    {
        // 實機：rtsp://…/media/stream.sdp?profile=Profile100/101/102。新增攝影機時
        // 次碼流被自動帶成串流3（Profile102），正確是串流2（Profile101）。
        const string sp = "rtsp://211.72.229.97:554/media/stream.sdp?profile=";
        var profiles = new[]
        {
            Profile("Profile100", "Profile100", 1920, 1080, "VideoSource0", "H264").WithStreamUri(sp + "Profile100"),
            Profile("Profile101", "Profile101", 1280, 720, "VideoSource0", "H264").WithStreamUri(sp + "Profile101"),
            Profile("Profile102", "Profile102", 640, 360, "VideoSource0", "H264").WithStreamUri(sp + "Profile102"),
        };

        var classified = OnvifProfileSelection.Classify(profiles);
        var main = OnvifProfileSelection.SelectMain(classified);
        var sub = OnvifProfileSelection.SelectSub(classified);

        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "Profile100").Role);
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "Profile101").Role);
        Assert.Equal(sp + "Profile100", main!.StreamUri);
        Assert.Equal(sp + "Profile101", sub!.StreamUri);
    }

    [Fact]
    public void 兩路純編號仍以第一路為主第二路為子()
    {
        // 同一家族的兩路機（101／102）：舊行為必須維持，否則既有安裝的次碼流會消失。
        var profiles = new[]
        {
            Profile("101", "101", 1920, 1080, "VideoSource0", "H264"),
            Profile("102", "102", 640, 360, "VideoSource0", "H264"),
        };

        var classified = OnvifProfileSelection.Classify(profiles);

        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "101").Role);
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "102").Role);
    }

    [Fact]
    public void 編號前綴不同時退回關鍵字與解析度判斷()
    {
        // 混用命名（Profile100 與 Stream3）時編號無法排序，不得硬把兩個不同系列配成一組。
        var profiles = new[]
        {
            Profile("MainStream", "Profile100", 1280, 720, "VideoSource0"),
            Profile("SubStream", "Stream3", 1920, 1080, "VideoSource0"),
        };

        var classified = OnvifProfileSelection.Classify(profiles);

        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "Profile100").Role);
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "Stream3").Role);
    }

    [Fact]
    public void Classify_WithoutKeywords_UsesLowestResolutionWithinVideoSource()
    {
        var profiles = new[]
        {
            Profile("A", "A", 1920, 1080, "VideoSource0"),
            Profile("B", "B", 640, 360, "VideoSource0"),
        };

        var classified = OnvifProfileSelection.Classify(profiles);

        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "A").Role);
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "B").Role);
        Assert.True(classified.Single(p => p.Token == "B").IsSubStream);
    }

    [Fact]
    public void Classify_DoesNotCrossClassifyDifferentVideoSources()
    {
        var profiles = new[]
        {
            Profile("A", "A", 1920, 1080, "VideoSource0"),
            Profile("B", "B", 1920, 1080, "VideoSource1"),
            Profile("C", "C", 704, 576, "VideoSource1"),
        };

        var classified = OnvifProfileSelection.Classify(profiles);

        // VideoSource1 的 C 為該來源最低解析度 → 子碼流；VideoSource0 僅一個 profile → 主碼流
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "C").Role);
        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "A").Role);
    }

    [Fact]
    public void Classify_KeepsKeywordPriority_OverResolution()
    {
        var profiles = new[]
        {
            Profile("MainStream", "MainStream", 1280, 720, "VideoSource0"),
            Profile("SubStream", "SubStream", 1920, 1080, "VideoSource0"),
        };

        var classified = OnvifProfileSelection.Classify(profiles);

        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "MainStream").Role);
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "SubStream").Role);
    }

    [Fact]
    public void Classify_DoesNotMutateInput()
    {
        var profile = Profile("B", "B", 640, 360, "VideoSource0");

        _ = OnvifProfileSelection.Classify([Profile("A", "A", 1920, 1080, "VideoSource0"), profile]);

        Assert.Equal(OnvifStreamRole.Unknown, profile.Role);
    }

    [Fact]
    public void SelectMain_PrefersHighestResolutionMainStream()
    {
        var profiles = new[]
        {
            Profile("MainStream", "MainStream", 1920, 1080, "VideoSource0"),
            Profile("SubStream", "SubStream", 640, 360, "VideoSource0"),
            Profile("Extra", "Extra", 2560, 1440, "VideoSource1"),
        };

        var main = OnvifProfileSelection.SelectMain(profiles);
        var sub = OnvifProfileSelection.SelectSub(profiles);

        Assert.Equal("Extra", main!.Token);
        Assert.Equal("SubStream", sub!.Token);
    }

    [Fact]
    public void SelectMain_WithoutVideoProfiles_FallsBackToFirst()
    {
        var profiles = new[] { Profile("Audio1", "Audio1", 0, 0, string.Empty) };

        Assert.Equal("Audio1", OnvifProfileSelection.SelectMain(profiles)!.Token);
        Assert.Null(OnvifProfileSelection.SelectSub(profiles));
    }

    [Fact]
    public void Order_PlacesMainStreamsFirstByResolution()
    {
        var profiles = new[]
        {
            Profile("SubStream", "SubStream", 640, 360, "VideoSource0"),
            Profile("MainStream", "MainStream", 1920, 1080, "VideoSource0"),
            Profile("MidStream", "MidStream", 1280, 720, "VideoSource0"),
        };

        var ordered = OnvifProfileSelection.Order(profiles).Select(p => p.Token).ToList();

        Assert.Equal(["MainStream", "MidStream", "SubStream"], ordered);
    }

    [Fact]
    public void DisplayLabel_IncludesNameResolutionEncodingAndRole()
    {
        var main = Profile("MainStream", "MainStream", 1920, 1080, "VideoSource0", "H264");
        main = main.WithRole(OnvifStreamRole.Main);

        Assert.Equal("MainStream · 1920×1080 · H264 · 主碼流", main.DisplayLabel);

        var sub = Profile("SubStream", "SubStream", 640, 360, "VideoSource0", "H265")
            .WithRole(OnvifStreamRole.Sub);
        Assert.Equal("SubStream · 640×360 · H265 · 子碼流", sub.DisplayLabel);
    }

    [Fact]
    public void ResolutionLabel_FallsBackWhenDeviceOmitsDimensions()
    {
        Assert.Equal(string.Empty, Profile("A", "A", 0, 0).ResolutionLabel);
        Assert.Equal("1080", Profile("A", "A", 0, 1080).ResolutionLabel);
    }

    [Fact]
    public void WithStreamUri_PreservesOtherFields()
    {
        var profile = Profile("MainStream", "MainStream", 1920, 1080, "VideoSource0", "H264")
            .WithRole(OnvifStreamRole.Main)
            .WithStreamUri("rtsp://192.168.1.64/stream");

        Assert.Equal("rtsp://192.168.1.64/stream", profile.StreamUri);
        Assert.Equal(1920, profile.Width);
        Assert.Equal("VideoSource0", profile.VideoSourceToken);
        Assert.Equal(OnvifStreamRole.Main, profile.Role);
    }

    [Fact]
    public void WithProbedResolution_ReplacesDeclaredSize_AndMarksProbed()
    {
        // 實機情形：ONVIF 宣告 1920×1080，實際輸出 640×360。
        var declared = Profile("Profile101", "Profile101", 1920, 1080, "VideoSource0", "H264")
            .WithStreamUri("rtsp://192.168.1.64/stream");

        Assert.False(declared.ResolutionIsProbed);
        Assert.Equal("1920×1080", declared.ResolutionLabel);

        var probed = declared.WithProbedResolution(640, 360);

        Assert.Equal(640, probed.Width);
        Assert.Equal(360, probed.Height);
        Assert.Equal("640×360", probed.ResolutionLabel);
        Assert.True(probed.ResolutionIsProbed);
        Assert.Contains("640×360", probed.DisplayLabel, StringComparison.Ordinal);

        // 不可變性：原物件不受影響，其餘欄位需保留。
        Assert.Equal(1920, declared.Width);
        Assert.Equal("rtsp://192.168.1.64/stream", probed.StreamUri);
        Assert.Equal("H264", probed.Encoding);
        Assert.Equal("VideoSource0", probed.VideoSourceToken);

        // 複製方法必須保留探測旗標，否則分類／補取位址後會遺失。
        Assert.True(probed.WithRole(OnvifStreamRole.Sub).ResolutionIsProbed);
        Assert.True(probed.WithStreamUri("rtsp://192.168.1.64/other").ResolutionIsProbed);
    }

    [Fact]
    public void Classify_And_SelectMain_UseProbedResolution_NotDeclaredResolution()
    {
        // ONVIF 宣告 ProfileA 為 1920×1080（像素最多），實際只有 640×360；
        // ProfileB 宣告 1280×720，實際 1920×1080。以探測值覆寫後應以 ProfileB 為主碼流。
        var a = Profile("ProfileA", "ProfileA", 640, 360, "VideoSource0", "H264")
            .WithProbedResolution(640, 360);
        var b = Profile("ProfileB", "ProfileB", 1920, 1080, "VideoSource0", "H264")
            .WithProbedResolution(1920, 1080);

        var classified = OnvifProfileSelection.Classify([a, b]);
        var ordered = OnvifProfileSelection.Order(classified);

        Assert.Equal("ProfileB", OnvifProfileSelection.SelectMain([a, b])?.Token);
        Assert.Equal("ProfileB", ordered[0].Token);
        Assert.Equal(OnvifStreamRole.Main, classified.Single(p => p.Token == "ProfileB").Role);
        Assert.Equal(OnvifStreamRole.Sub, classified.Single(p => p.Token == "ProfileA").Role);
    }

    [Fact]
    public void SelectMain_PrefersProbedResolution_OverUnverifiedDeclaredResolution()
    {
        // 探測逾時的 profile 仍留著 ONVIF 宣告值（1920×1080），但實際只有 1280×720。
        // 已探測的 ProfileB 為 1280×720，應勝過未經驗證的宣告值，避免預選到錯誤的主碼流。
        var unprobed = Profile("Profile3", "Profile3", 1920, 1080, "VideoSource0", "H264");
        var probed = Profile("Profile1", "Profile1", 1280, 720, "VideoSource0", "H264")
            .WithProbedResolution(1280, 720);

        Assert.False(unprobed.ResolutionIsProbed);
        Assert.Equal("Profile1", OnvifProfileSelection.SelectMain([unprobed, probed])?.Token);
    }

    [Fact]
    public void SelectMain_WhenNoneProbed_FallsBackToDeclaredResolution()
    {
        // 全部探測失敗時維持原本以宣告值挑選最大解析度的行為。
        var a = Profile("ProfileA", "ProfileA", 1280, 720, "VideoSource0", "H264");
        var b = Profile("ProfileB", "ProfileB", 1920, 1080, "VideoSource0", "H264");

        Assert.Equal("ProfileB", OnvifProfileSelection.SelectMain([a, b])?.Token);
    }

    [Fact]
    public void SelectMain_PrefersProbedSubStream_OverUnprobedDeclaredMain()
    {
        // 實機：主碼流宣告 1920×1080 但探測失敗（RTSP 回 503），只有子碼流可拉。
        // 依 ONVIF 宣告值挑選主碼流會預選到壞掉的 profile；已驗證可連的子碼流必須勝出。
        var brokenMain = Profile("Profile1", "Profile1", 1920, 1080, "VideoSource0", "H264")
            .WithRole(OnvifStreamRole.Main); // 探測失敗，保留宣告值，ResolutionIsProbed=false
        var workingSub = Profile("Profile2", "Profile2", 640, 360, "VideoSource0", "H264")
            .WithProbedResolution(640, 360)
            .WithRole(OnvifStreamRole.Sub);

        var selected = OnvifProfileSelection.SelectMain([brokenMain, workingSub]);

        Assert.Equal("Profile2", selected!.Token);
    }

    [Fact]
    public void SelectMain_HealthyCamera_StillPrefersProbedMain_OverProbedSub()
    {
        // 相機健康時主碼流也探測成功：此時仍以主碼流優先，不因降級權重而選錯碼流。
        var main = Profile("Profile1", "Profile1", 1920, 1080, "VideoSource0", "H264")
            .WithProbedResolution(1920, 1080)
            .WithRole(OnvifStreamRole.Main);
        var sub = Profile("Profile2", "Profile2", 640, 360, "VideoSource0", "H264")
            .WithProbedResolution(640, 360)
            .WithRole(OnvifStreamRole.Sub);

        Assert.Equal("Profile1", OnvifProfileSelection.SelectMain([main, sub])?.Token);
    }

    [Fact]
    public void SelectSub_PrefersNamedSubStream_OverLowerResolutionThirdStream()
    {
        // 實機症狀：三碼流相機（串流1 主／串流2 子／串流3 更低）。若只取解析度最低者就會
        // 誤選串流3，但即時監看要用的其實是串流2。現在 Classify 依同一前綴的編號順序
        // 已把串流2 標成子碼流、串流3 留 Unknown，SelectSub 不會多一個更低的候選。
        var profiles = OnvifProfileSelection.Classify(
        [
            Profile("Stream1", "Stream1", 1920, 1080, "VideoSource0"),
            Profile("Stream2", "Stream2", 704, 576, "VideoSource0"),
            Profile("Stream3", "Stream3", 352, 288, "VideoSource0"),
        ]);

        Assert.Equal("Stream2", OnvifProfileSelection.SelectSub(profiles)!.Token);

        // 第三路不能再被標成子碼流：否則 SelectSub 會多一個更低的候選，下一次又挑錯。
        Assert.False(profiles.Single(p => p.Token == "Stream3").IsSubStream);
    }

    [Fact]
    public void SelectSub_AmongNamedSubStreams_PicksClosestToMain()
    {
        // 多個具名子碼流時取最接近主碼流（解析度最高）者，而非最小的那個。
        var profiles = OnvifProfileSelection.Classify(
        [
            Profile("MainStream", "MainStream", 1920, 1080, "VideoSource0"),
            Profile("SubStream", "SubStream", 704, 576, "VideoSource0"),
            Profile("LowStream", "LowStream", 352, 288, "VideoSource0"),
        ]);

        Assert.Equal("SubStream", OnvifProfileSelection.SelectSub(profiles)!.Token);
    }

    [Fact]
    public void SelectSub_WithoutNamedSubStream_FallsBackToLowestResolution()
    {
        // 無具名子碼流時維持既有行為：Classify 只把同來源最低解析度者標為子碼流。
        var profiles = OnvifProfileSelection.Classify(
        [
            Profile("Alpha", "Alpha", 1920, 1080, "VideoSource0"),
            Profile("Beta", "Beta", 1280, 720, "VideoSource0"),
            Profile("Gamma", "Gamma", 640, 360, "VideoSource0"),
        ]);

        Assert.Equal("Gamma", OnvifProfileSelection.SelectSub(profiles)!.Token);
    }

    private static OnvifProfile Profile(
        string name,
        string token,
        int width,
        int height,
        string source = "",
        string? encoding = null) => new()
        {
            Token = token,
            Name = name,
            Width = width,
            Height = height,
            Encoding = encoding ?? (width > 0 ? "H264" : string.Empty),
            VideoSourceToken = source,
        };
}
