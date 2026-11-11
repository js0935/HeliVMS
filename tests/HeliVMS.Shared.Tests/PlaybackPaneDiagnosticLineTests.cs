using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 多路回放診斷行的格式。現場只說得出「不順」，這行字要能分開四種成因：落後母鐘多少、
/// 這一格有沒有在更新、重開解碼幾次、以及現在生效的容差（36 格以上是 30 秒的總覽模式）。
/// </summary>
public class PlaybackPaneDiagnosticLineTests
{
    private static PlaybackPaneDiagnosticLine.PaneSample Pane(
        int channelId = 103,
        bool keyframesOnly = false,
        double shown = 14.9,
        double? drift = 0.1,
        int restarts = 0,
        int sinceLastFrameMs = 40,
        string state = "playing",
        int width = 960,
        int height = 540,
        string stream = "main",
        string? hwAccel = null)
        => new(
            channelId,
            stream,
            width,
            height,
            keyframesOnly,
            hwAccel,
            shown,
            drift,
            restarts,
            TimeSpan.FromMilliseconds(sinceLastFrameMs),
            state);

    [Fact]
    public void 每個欄位都在同一行裡()
    {
        var line = PlaybackPaneDiagnosticLine.Format(
            new DateTime(2026, 11, 6, 8, 12, 34, 567, DateTimeKind.Utc),
            cells: 4,
            toleranceSeconds: 0.4,
            [Pane()]);

        Assert.Equal(
            "2026-11-06T08:12:34.567Z | cells=4 tol=0.4s | ch103 main 960x540 shown=14.9/s"
            + " drift=+0.1s restarts=0 frame=40ms playing",
            line);
    }

    [Fact]
    public void 總覽牆會標記只解關鍵影格且容差是三十秒()
    {
        var line = PlaybackPaneDiagnosticLine.Format(
            DateTime.UnixEpoch,
            cells: 64,
            toleranceSeconds: 30,
            [Pane(keyframesOnly: true, shown: 0.4, drift: 2.4, restarts: 2, width: 256, height: 144)]);

        Assert.Contains("cells=64 tol=30s", line);
        Assert.Contains("ch103 main 256x144 kf", line);
        Assert.Contains("drift=+2.4s", line);

        // 少了 kf 標記，讀日誌的人會以為「每格每幾秒才換一張」是壞掉——它其實是那個版面的設計。
        Assert.Contains("kf", line);
    }

    [Fact]
    public void 沒有在播就不寫漂移()
    {
        // 漂移 0 會被讀成「完全同步」，而實情是「這一格沒在播」。
        var line = PlaybackPaneDiagnosticLine.Format(
            DateTime.UnixEpoch, cells: 2, toleranceSeconds: 0.4,
            [Pane(drift: null, shown: 0, state: "gap")]);

        Assert.DoesNotContain("drift=", line);
        Assert.Contains("shown=0/s", line);
        Assert.Contains("gap", line);
    }

    [Fact]
    public void 負的漂移看得出來是哪一邊()
    {
        var line = PlaybackPaneDiagnosticLine.Format(
            DateTime.UnixEpoch, cells: 2, toleranceSeconds: 0.4, [Pane(drift: -3.25)]);

        Assert.Contains("drift=-3.3s", line);
    }

    [Fact]
    public void 硬體解碼欄位寫的是實際生效的方式()
    {
        var line = PlaybackPaneDiagnosticLine.Format(
            DateTime.UnixEpoch, cells: 4, toleranceSeconds: 0.4,
            [
                Pane(channelId: 103, hwAccel: "d3d11va"),
                Pane(channelId: 104, hwAccel: "off"),
                Pane(channelId: 105, hwAccel: "off(cooldown)"),
            ]);

        // 三種狀態的處置完全不同：設定沒開／不划算（off）、剛才失敗過（五分鐘冷卻）、真的在硬解。
        Assert.Contains("ch103 main 960x540 hw=d3d11va", line);
        Assert.Contains("hw=off ", line);
        Assert.Contains("hw=off(cooldown)", line);
    }

    [Fact]
    public void 沒有在播的格不寫硬體解碼()
    {
        // 沒在播就沒有「生效方式」可言；寫 off 會被讀成「這一格正在軟解」。
        var line = PlaybackPaneDiagnosticLine.Format(
            DateTime.UnixEpoch, cells: 2, toleranceSeconds: 0.4,
            [Pane(hwAccel: null, drift: null, shown: 0, state: "gap")]);

        Assert.DoesNotContain("hw=", line);
    }

    [Fact]
    public void 多格一起寫且沒有影格時不寫尺寸()
    {
        var line = PlaybackPaneDiagnosticLine.Format(
            DateTime.UnixEpoch, cells: 3, toleranceSeconds: 0.4,
            [Pane(), Pane(channelId: 104, width: 0, height: 0, state: "stopped")]);

        Assert.Contains("cells=3", line);
        Assert.Contains("ch103", line);
        Assert.Contains("ch104 main shown=", line);
        Assert.DoesNotContain("0x0", line);
    }
}
