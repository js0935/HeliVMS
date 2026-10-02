using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// ffmpeg 參數組裝的契約測試（R244）。
/// <para>
/// 這串參數<b>就是</b>「低延遲」的定義。任何一個選項被改掉或漏掉，症狀都是
/// 「延遲 3 秒」或「Safari 黑畫面」——都不是會讓測試紅的那種錯誤，所以這裡把它
/// 釘成明確斷言。
/// </para>
/// </summary>
public class LiveEncodeOptionsTests
{
    /// <summary>取得某個選項後面的值；找不到選項就让測試失敗（而非回 null 靜默通過）。</summary>
    private static string ValueOf(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], option, StringComparison.Ordinal))
            {
                Assert.True(i + 1 < args.Count, $"{option} 缺少它的值");
                return args[i + 1];
            }
        }

        Assert.Fail($"找不到選項 {option}");
        return string.Empty;
    }

    private static readonly string[] Rtsp = ["rtsp://user:secret@192.0.2.1:554/stream"];
    private static readonly string[] Target = ["rtp://127.0.0.1:5004"];

    [Fact]
    public void 送出低延遲必備的編碼選項()
    {
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);
        var text = string.Join(' ', args);

        Assert.Contains("-tune zerolatency", text, StringComparison.Ordinal);
        Assert.Contains("-preset veryfast", text, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 使用baseline以避免B影格重排()
    {
        // baseline 無 B 影格，因此不需要重排緩衝；這是延遲可控的前提。
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Contains("-profile:v", args);
        Assert.Equal("baseline", ValueOf(args, "-profile:v"));
    }

    [Fact]
    public void 關閉音軌()
    {
        // 本里程碑純視訊；帶上音軌會讓 Safari 的相容性變複雜。
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Contains("-an", args);
    }

    [Fact]
    public void 使用RTP位元流格式而非容器()
    {
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Contains("-f", args);
        Assert.Equal("rtp", ValueOf(args, "-f"));
    }

    [Fact]
    public void payload型別固定為九十六且與SDP廣告一致()
    {
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Equal(96, LiveEncodeOptions.PayloadType);
        Assert.Equal("96", ValueOf(args, "-payload_type"));
    }

    [Fact]
    public void 關鍵幀間隔以影格數鎖定且與frameRate一致()
    {
        var args = new LiveEncodeOptions(30, 2000, 1.0).BuildArguments(Rtsp[0], Target[0]);

        Assert.Equal("30", ValueOf(args, "-g"));
        Assert.Equal("30", ValueOf(args, "-keyint_min"));
    }

    [Fact]
    public void 關閉場景切換的額外關鍵幀()
    {
        // 沒有這項，最壞恢復時間是未知的——而我們不能靠瀏覽器的 PLI 救回。
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Equal("0", ValueOf(args, "-sc_threshold"));
    }

    [Fact]
    public void GOP長度至少為一不因奇怪設定變成零()
    {
        // 零長度 GOP 會讓 ffmpeg 直接拒絕啟動，必須夾住。
        var args = new LiveEncodeOptions(30, 2000, 0.0).BuildArguments(Rtsp[0], Target[0]);

        Assert.Equal("1", ValueOf(args, "-g"));
    }

    [Fact]
    public void RTSP走TCP夾帶與既有錄影路徑一致()
    {
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Equal("tcp", ValueOf(args, "-rtsp_transport"));
    }

    [Fact]
    public void 輸入位址與輸出目標都出現在參數中()
    {
        var args = LiveEncodeOptions.Default.BuildArguments(Rtsp[0], Target[0]);

        Assert.Contains(Rtsp[0], args);
        Assert.Contains(Target[0], args);
        Assert.Equal(Target[0], args[^1]);
    }

    [Fact]
    public void 空白位址直接拒絕而不是讓ffmpeg困惑()
    {
        Assert.Throws<ArgumentException>(() => LiveEncodeOptions.Default.BuildArguments("  ", Target[0]));
        Assert.Throws<ArgumentException>(() => LiveEncodeOptions.Default.BuildArguments(Rtsp[0], ""));
    }

    [Fact]
    public void 碼率同時設定上限與緩衝避免溢位()
    {
        var args = new LiveEncodeOptions(30, 1500, 1.0).BuildArguments(Rtsp[0], Target[0]);

        Assert.Equal("1500k", ValueOf(args, "-b:v"));
        Assert.Equal("1500k", ValueOf(args, "-maxrate"));
        Assert.Equal("1500k", ValueOf(args, "-bufsize"));
    }
}

/// <summary>環境變數設定解析的測試。</summary>
public class WhepOptionsTests
{
    private static WhepOptions Bind(Dictionary<string, string> values, List<string>? log = null)
        => WhepOptions.FromLookup(
            key => values.TryGetValue(key, out var v) ? v : null,
            log is null ? null : (_, message) => log.Add(message));

    [Fact]
    public void 未設定時使用預設值()
    {
        var options = Bind([]);

        Assert.Equal(8, options.MaxViewersPerChannel);
        Assert.Empty(options.StunServers);
        Assert.Equal(TimeSpan.FromSeconds(30), options.SessionIdleTimeout);
        Assert.Equal("ffmpeg", options.FfmpegPath);
        Assert.Null(options.PublicHost);
        Assert.Null(options.PublicPort);
    }

    [Fact]
    public void STUN清單以逗號切分並去除空白()
    {
        var options = Bind(new() { ["HELIVMS_WHEP_STUN"] = "stun:a.example:3478, stun:b.example:3478 , " });

        Assert.Equal(["stun:a.example:3478", "stun:b.example:3478"], options.StunServers);
    }

    [Fact]
    public void 格式錯誤的上限退回預設並留下記錄()
    {
        // 關鍵：不能因為上限值打錯就讓整套 NVR 開不起機，但必須記錄，
        // 否則維運會以為自己設了 100 人。
        var log = new List<string>();
        var options = Bind(new() { ["HELIVMS_WHEP_MAX_VIEWERS"] = "abc" }, log);

        Assert.Equal(8, options.MaxViewersPerChannel);
        Assert.Single(log);
        Assert.Contains("MAX_VIEWERS", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 零或負數的上限退回預設()
    {
        Assert.Equal(8, Bind(new() { ["HELIVMS_WHEP_MAX_VIEWERS"] = "0" }).MaxViewersPerChannel);
        Assert.Equal(8, Bind(new() { ["HELIVMS_WHEP_MAX_VIEWERS"] = "-5" }).MaxViewersPerChannel);
    }

    [Fact]
    public void 超過上限的觀看人數被夾住()
    {
        var log = new List<string>();
        var options = Bind(new() { ["HELIVMS_WHEP_MAX_VIEWERS"] = "5000" }, log);

        Assert.Equal(64, options.MaxViewersPerChannel);
        Assert.Contains(log, m => m.Contains("64", StringComparison.Ordinal));
    }

    [Fact]
    public void 合法上限照單全收()
    {
        var options = Bind(new() { ["HELIVMS_WHEP_MAX_VIEWERS"] = "16" });

        Assert.Equal(16, options.MaxViewersPerChannel);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("notanumber")]
    public void 埠號無效時當成未設定而非亂猜(string value)
    {
        var log = new List<string>();
        var options = Bind(new() { ["HELIVMS_WHEP_PUBLIC_PORT"] = value }, log);

        Assert.Null(options.PublicPort);
        Assert.NotEmpty(log);
    }

    [Fact]
    public void 合法埠號照收()
    {
        Assert.Equal(8555, Bind(new() { ["HELIVMS_WHEP_PUBLIC_PORT"] = "8555" }).PublicPort);
    }

    [Fact]
    public void 逾時秒數無效時退回預設()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            Bind(new() { ["HELIVMS_WHEP_SESSION_IDLE_SECONDS"] = "0" }).SessionIdleTimeout);
    }

    [Fact]
    public void ffmpeg路徑可被覆寫()
    {
        var options = Bind(new() { ["HELIVMS_WHEP_FFMPEG"] = @"C:\tools\ffmpeg.exe" });

        Assert.Equal(@"C:\tools\ffmpeg.exe", options.FfmpegPath);
    }

[Fact]
    public void Turn清單支援逗號切分與內嵌認證()
    {
        var options = Bind(new()
        {
            ["HELIVMS_WHEP_TURN"] = "turn:a.example:3478;user1;pass1, turns:b.example:5349",
        });

        Assert.Equal(2, options.TurnServers.Count);
        Assert.Equal(new WhepIceServer("turn:a.example:3478", "user1", "pass1"), options.TurnServers[0]);
        Assert.Equal(new WhepIceServer("turns:b.example:5349", null, null), options.TurnServers[1]);
    }

    [Fact]
    public void Turn沒寫認證時套用全域帳密()
    {
        // 單一 TURN 是最常見的部署，讓維運只設一次帳密就好。
        var options = Bind(new()
        {
            ["HELIVMS_WHEP_TURN"] = "turn:turn.example.com:3478",
            ["HELIVMS_WHEP_TURN_USERNAME"] = "helivms",
            ["HELIVMS_WHEP_TURN_CREDENTIAL"] = "secret",
        });

        var turn = Assert.Single(options.TurnServers);
        Assert.Equal("helivms", turn.Username);
        Assert.Equal("secret", turn.Credential);
    }

    [Fact]
    public void Turn項目自帶的認證優先於全域值()
    {
        // TURN REST API 會動態產生短期密碼，這時必須以項目上的為準。
        var options = Bind(new()
        {
            ["HELIVMS_WHEP_TURN"] = "turn:a:3478;rotating;ephemeral",
            ["HELIVMS_WHEP_TURN_USERNAME"] = "static",
            ["HELIVMS_WHEP_TURN_CREDENTIAL"] = "static-secret",
        });

        var turn = Assert.Single(options.TurnServers);
        Assert.Equal("rotating", turn.Username);
        Assert.Equal("ephemeral", turn.Credential);
    }

    [Theory]
    [InlineData("stun:notaturn.example:3478")]
    [InlineData("http:notaturn.example")]
    public void Turn清單裡的非TURN項目被忽略並留下記錄(string value)
    {
        // 把 STUN 網址放進 TURN 清單是常見手誤；照樣送出只會得到「有 STUN 沒 relay」的
        // 半套設定，比直接回報清楚得多。
        var log = new List<string>();
        var options = Bind(new() { ["HELIVMS_WHEP_TURN"] = value }, log);

        Assert.Empty(options.TurnServers);
        Assert.Single(log);
        Assert.Contains("TURN", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Turn有帳號卻沒有密碼時被忽略()
    {
        // 沒有密碼的 TURN 會在 allocating 時被拒，症狀是「連不上 relay」而不是「設定錯了」。
        var log = new List<string>();
        var options = Bind(new()
        {
            ["HELIVMS_WHEP_TURN"] = "turn:turn.example.com:3478;user1",
        }, log);

        Assert.Empty(options.TurnServers);
        Assert.Single(log);
    }

    [Fact]
    public void PublicHost接受IP位址()
    {
        Assert.Equal("203.0.113.50", Bind(new() { ["HELIVMS_WHEP_PUBLIC_HOST"] = "203.0.113.50" }).PublicHost);
        Assert.Equal("2001:db8::1", Bind(new() { ["HELIVMS_WHEP_PUBLIC_HOST"] = "2001:db8::1" }).PublicHost);
    }

    [Fact]
    public void PublicHost不是IP時忽略並留下記錄()
    {
        // ICE 候選欄位只能放位址：接受 DNS 名稱等於設定一個不會生效的值，
        // 維運會得到「明明有設定卻還是不行」且沒有任何錯誤。
        var log = new List<string>();
        var options = Bind(new() { ["HELIVMS_WHEP_PUBLIC_HOST"] = "vms.example.com" }, log);

        Assert.Null(options.PublicHost);
        Assert.Single(log);
        Assert.Contains("PUBLIC_HOST", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 空白值等同未設定()
    {
        var options = Bind(new() { ["HELIVMS_WHEP_MAX_VIEWERS"] = "   " });

        Assert.Equal(8, options.MaxViewersPerChannel);
    }
}
