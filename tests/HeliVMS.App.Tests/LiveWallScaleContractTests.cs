namespace HeliVMS.App.Tests;

/// <summary>
/// 即時監看牆的接線：格數 → 每一格的解碼預算（<c>LiveWallBudget</c>）→ ffmpeg 的解碼寬度。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端沒有 WPF 執行期測試，所以這裡掃原始碼；預算表本身是行為測試
/// （<c>HeliVMS.Shared.Tests.LiveWallBudgetTests</c>），尺寸口徑是
/// <c>HeliVMS.Shared.Tests.VideoScalingTests</c> 與 <c>HeliVMS.Media.Tests.RtspDecodeScalingTests</c>。
/// </para>
/// <para>
/// 這裡守的是「有沒有接上去」：預算表寫得再對，只要 <c>ApplyDisplayPolicyAsync</c> 沒有
/// 把它交給工作階段，多分割時仍然每一格都用原生解析度解碼——而症狀只是「大格數時有點頓」，
/// 沒有任何錯誤訊息。
/// </para>
/// </remarks>
public sealed class LiveWallScaleContractTests
{
    private const string Manager = "src/HeliVMS.App/Services/ChannelManager.cs";
    private const string Session = "src/HeliVMS.App/Services/ChannelSession.cs";

    [Fact]
    public void 佈局政策會依格數套用解碼預算()
    {
        var policy = SourceContract.BodyOf(SourceContract.Read(Manager), "ApplyDisplayPolicyAsync");

        Assert.Contains("LiveWallBudget.For(cellCount", policy);
        Assert.Contains("budget.DecodeWidth", policy);
        Assert.Contains("budget.MaxFramesPerSecond", policy);
        Assert.Contains("SetLiveBudgetAsync(", policy);

        // 順序：單畫面時主流是「切流」才建立的，先設預算就會漏掉它，
        // 於是單畫面仍然用原生解析度解碼（成本最高的那一格）。
        var switchAt = policy.IndexOf("SwitchDisplayStreamAsync(", StringComparison.Ordinal);
        var budgetAt = policy.IndexOf("SetLiveBudgetAsync(", StringComparison.Ordinal);
        Assert.True(switchAt >= 0 && budgetAt > switchAt, "套用預算必須排在切流之後");
    }

    [Fact]
    public void 解碼預算同時套用到次流與主流()
    {
        var budget = SourceContract.BodyOf(SourceContract.Read(Session), "SetLiveBudgetAsync");
        var apply = SourceContract.BodyOf(SourceContract.Read(Session), "ApplyDecodeWidthAsync");

        Assert.Contains("Client.MaxFramesPerSecond", budget);
        Assert.Contains("_mainClient", budget);
        Assert.Contains("ApplyDecodeWidthAsync(", budget);

        Assert.Contains("Client.SetDecodeWidthAsync(", apply);
        Assert.Contains("_mainClient", apply);
        Assert.Contains("SetDecodeWidthAsync(", apply);
    }

    [Fact]
    public void 解碼寬度一律經過AI例外換算()
    {
        // 只在一條路徑上套用 ForDetection 是不夠的：主流有「單畫面」與「手動切流」兩條
        // 建立路徑，任何一條漏掉，那條路徑上的 AI 頻道就會被縮小解碼——畫面正常，
        // 只有模型看不到遠處的人車。
        var session = SourceContract.Read(Session);
        var effective = SourceContract.BodyOf(session, "EffectiveDecodeWidth");
        var apply = SourceContract.BodyOf(session, "ApplyDecodeWidthAsync");
        var ensure = SourceContract.BodyOf(session, "EnsureMainAsync");

        Assert.Contains("ApplyDetectionRule(", effective);
        Assert.Contains("_ai", effective);
        Assert.Contains("EffectiveDecodeWidth(", apply);
        Assert.Contains("EffectiveDecodeWidth(", ensure);
    }

    [Fact]
    public void AI開關變動時會立即重新套用解碼寬度()
    {
        // 設定頁勾選 AI 之後若只在下一次切換佈局才生效，同一台攝影機的偵測精度會
        // 取決於「先勾 AI 還是先切版面」，這種不一致沒有人查得出來。
        var configure = SourceContract.BodyOf(SourceContract.Read(Session), "ConfigureAi");

        Assert.Contains("wasEnabled", configure);
        Assert.Contains("ReapplyDecodeWidthAsync(", configure);
        Assert.Contains("IsMonitoring", configure);
    }

    [Fact]
    public void 建立主流時沿用目前的解碼預算()
    {
        // 主流會在「單畫面」與「手動切流」兩條路徑上被建立。只設次流的預算，
        // 切回單畫面就恢復成原生 1920 解碼——切換看起來正常，成本卻無聲地回來。
        var ensure = SourceContract.BodyOf(SourceContract.Read(Session), "EnsureMainAsync");

        Assert.Contains("_budgetMaxFramesPerSecond", ensure);
        Assert.Contains("_budgetDecodeWidth", ensure);
        Assert.DoesNotContain("MaxFramesPerSecond = 15", ensure);
    }

    [Fact]
    public void 錄影不受顯示預算影響()
    {
        // 錄影走獨立的主流 SegmentRecorder，顯示端縮小解碼只是為了畫縮圖；
        // 若有人把預算接到錄影路徑，錄下來的檔案會變成 480 寬，而且完全沒有錯誤訊息。
        var budget = SourceContract.BodyOf(SourceContract.Read(Session), "SetLiveBudgetAsync");
        var apply = SourceContract.BodyOf(SourceContract.Read(Session), "ApplyDecodeWidthAsync");

        Assert.DoesNotContain("_recorder", budget);
        Assert.DoesNotContain("RecordingUrl", budget);
        Assert.DoesNotContain("_recorder", apply);
        Assert.DoesNotContain("RecordingUrl", apply);
    }

    [Fact]
    public void 硬體解碼設定與回放牆共用同一把尺()
    {
        var manager = SourceContract.Read(Manager);

        Assert.Contains("decode.hwaccel", manager);
        Assert.Contains("FfmpegHwAccel.ShouldUse(", manager);
        Assert.Contains("FfmpegHwAccel.ProbeCached(", manager);
    }
}
