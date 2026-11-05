using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// ONVIF 精靈的帳密預填。現場的機隊幾乎共用同一組帳密（實測 7 台 VIVOTEK 全是 <c>root</c>），
/// 每次新增攝影機都重打一次正是把操作員推向短密碼的原因；但填錯的帳密會讓「搜尋不到設備」，
/// 最容易被歸咎於網路——所以每一條規則都要釘死。
/// </summary>
public class OnvifCredentialPlannerTests
{
    private static OnvifCredentialPlanner.Endpoint Device(string host, string? user, string? password)
        => new(host, user, password);

    [Fact]
    public void 位址指向已知設備時勝過機隊常用的那一組()
    {
        // 那台的帳密是「少數」的那一組（機隊其他三台是 admin／fleet）：位址是操作員剛打的、
        // 最特定的訊號，一定要蓋過「最常用」。否則在跨品牌混用的機房裡會拿去搜錯帳號。
        var prefill = OnvifCredentialPlanner.Resolve(
            "192.168.1.64",
            rememberedUsername: null,
            [
                Device("192.168.1.64", "root", "s3cret"),
                Device("10.0.0.7", "admin", "fleet"),
                Device("10.0.0.8", "admin", "fleet"),
                Device("10.0.0.9", "admin", "fleet"),
            ]);

        Assert.Equal("root", prefill.Username);
        Assert.Equal("s3cret", prefill.Password);
    }

    [Theory]
    [InlineData("rtsp://192.168.1.64/live")]
    [InlineData("http://192.168.1.64:8080/onvif/device_service")]
    [InlineData("192.168.1.64:554")]
    [InlineData(" 192.168.1.64 ")]
    public void 位址寫法不同也認得出同一台(string address)
    {
        // 機隊多數是 admin／fleet：位址認得出來才拿得到 root／s3cret，認不出來就會填錯那一組。
        var prefill = OnvifCredentialPlanner.Resolve(
            address,
            rememberedUsername: null,
            [
                Device("192.168.1.64", "root", "s3cret"),
                Device("10.0.0.7", "admin", "fleet"),
                Device("10.0.0.8", "admin", "fleet"),
                Device("10.0.0.9", "admin", "fleet"),
            ]);

        Assert.Equal("root", prefill.Username);
        Assert.Equal("s3cret", prefill.Password);
    }

    [Fact]
    public void 主機名的大小寫不影響比對()
    {
        var prefill = OnvifCredentialPlanner.Resolve(
            "vivotek-01.local",
            rememberedUsername: null,
            [
                Device("VIVOTEK-01.local", "root", "s3cret"),
                Device("10.0.0.7", "admin", "fleet"),
                Device("10.0.0.8", "admin", "fleet"),
            ]);

        Assert.Equal("root", prefill.Username);
        Assert.Equal("s3cret", prefill.Password);
    }

    [Fact]
    public void 網段運算式不當成某一台()
    {
        // 192.168.1.0/24 的 .64 剛好是已知設備，但操作員要搜的是整個網段：
        // 拿它去認那台會讓「用哪組帳密」取決於巧合。
        foreach (var expression in new[] { "192.168.1.0/24", "192.168.1.1-50", "192.168.1.1, 192.168.1.2", "192.168.1.5 192.168.1.6" })
        {
            var prefill = OnvifCredentialPlanner.Resolve(
                expression,
                rememberedUsername: null,
                [
                    Device("192.168.1.64", "root", "s3cret"),
                    Device("10.0.0.7", "admin", "fleet"),
                    Device("10.0.0.8", "admin", "fleet"),
                ]);

            Assert.Equal("admin", prefill.Username);
            Assert.Equal("fleet", prefill.Password);
        }
    }

    [Fact]
    public void 沒有指定時用全機隊最常用的一組()
    {
        var prefill = OnvifCredentialPlanner.Resolve(
            "220.130.208.103",
            rememberedUsername: null,
            [
                Device("192.168.1.5", "admin", "test"),
                Device("60.249.37.202", "root", "fleet"),
                Device("125.227.64.37", "root", "fleet"),
                Device("61.221.88.235", "root", "fleet"),
            ]);

        Assert.Equal("root", prefill.Username);
        Assert.Equal("fleet", prefill.Password);
    }

    [Fact]
    public void 記住的帳號與機隊一致時連密碼一起填()
    {
        var prefill = OnvifCredentialPlanner.Resolve(
            addressText: null,
            rememberedUsername: "ROOT",
            [Device("60.249.37.202", "root", "fleet")]);

        // 大小寫不計視為同一個帳號，但填回去的是系統裡那組的原始大小寫（有些機型對大小寫敏感）。
        Assert.Equal("root", prefill.Username);
        Assert.Equal("fleet", prefill.Password);
    }

    [Fact]
    public void 記住的帳號與機隊不同時不填密碼()
    {
        // 臨時測過一台 admin 的機器之後，下次開窗帳號是 admin、密碼留空：
        // 拿機隊的密碼去配別的帳號，錯的密碼是遮罩的、看不出來，比留空更糟。
        var prefill = OnvifCredentialPlanner.Resolve(
            addressText: null,
            rememberedUsername: "admin",
            [Device("60.249.37.202", "root", "fleet")]);

        Assert.Equal("admin", prefill.Username);
        Assert.Null(prefill.Password);
    }

    [Fact]
    public void 沒有裝置時只填記住的帳號()
    {
        var prefill = OnvifCredentialPlanner.Resolve(null, "operator", []);

        Assert.Equal("operator", prefill.Username);
        Assert.Null(prefill.Password);
        Assert.Null(OnvifCredentialPlanner.Resolve(null, null, []).Username);
    }

    [Fact]
    public void 最常用的組合才被選中且帳號大小寫不影響計數()
    {
        var preferred = OnvifCredentialPlanner.Preferred(
        [
            ("root", "a"),
            ("Root", "a"),
            ("admin", "b"),
        ]);

        Assert.Equal(("root", "a"), preferred);
    }

    [Fact]
    public void 同票時取先出現的那一組()
    {
        // 呼叫端以「最新在前」排序：同票時要沿用最近用過的那組，而不是隨機。
        var preferred = OnvifCredentialPlanner.Preferred([("admin", "new"), ("root", "old")]);

        Assert.Equal(("admin", "new"), preferred);
    }

    [Fact]
    public void 空帳號不算一組可沿用的帳密()
    {
        Assert.Null(OnvifCredentialPlanner.Preferred([("", ""), (null, null), ("   ", "x")]));

        var preferred = OnvifCredentialPlanner.Preferred([("", ""), ("root", "fleet")]);
        Assert.Equal(("root", "fleet"), preferred);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.0/24")]
    [InlineData("192.168.1.1-50")]
    [InlineData("a, b")]
    public void 取不出主機時回空(string? address)
        => Assert.Null(OnvifCredentialPlanner.HostOf(address));
}
