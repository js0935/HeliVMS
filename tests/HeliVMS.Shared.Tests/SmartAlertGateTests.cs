using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 智慧警報「每窗只告警一次」閘門（<see cref="SmartAlertGate"/>）的行為契約。
///
/// 現場症狀：保全收到一則「某頻道 5 分鐘內 8 次移動」的告警，接著同一個窗裡
/// 剩下的每一次移動都各送一則。結果同一件事洗版十幾則，真正的緊急事件反而被埋掉——
/// 而操作員設定這條規則的目的正是避免這件事。
/// </summary>
public sealed class SmartAlertGateTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 門檻未滿時不告警()
    {
        var gate = new SmartAlertGate();

        var d = gate.Observe(ruleId: 1, windowCount: 3, minEvents: 8, T0, windowMinutes: 5);

        Assert.False(d.Reached);
        Assert.False(d.FirstCrossing);
    }

    [Fact]
    public void 首次跨過門檻時會告警()
    {
        var gate = new SmartAlertGate();

        var d = gate.Observe(ruleId: 1, windowCount: 8, minEvents: 8, T0, windowMinutes: 5);

        Assert.True(d.Reached);
        Assert.True(d.FirstCrossing);
        Assert.Equal(8, d.WindowCount);
    }

    [Fact]
    public void 門檻跨過之後同一個窗不再重複告警()
    {
        // 這是整個功能的核心：8 次的門檻在跨過之後，每一筆都送等於沒有聚合。
        var gate = new SmartAlertGate();
        Assert.True(gate.Observe(1, 8, 8, T0, 5).FirstCrossing);

        for (var i = 9; i <= 15; i++)
        {
            var d = gate.Observe(1, i, 8, T0.AddMinutes(1), 5);

            Assert.True(d.Reached, $"第 {i} 次仍達門檻，但不该再告警。");
            Assert.False(d.FirstCrossing);
        }
    }

    [Fact]
    public void 新的窗開始後會再次告警()
    {
        // 門檻滿足後過了完整窗長，下一個窗的第一筆就該能再告一次，
        // 否則持續性的異常只會在第一次通知，永遠不再提醒。
        var gate = new SmartAlertGate();
        Assert.True(gate.Observe(1, 8, 8, T0, 5).FirstCrossing);

        Assert.False(gate.Observe(1, 9, 8, T0.AddMinutes(2), 5).FirstCrossing);

        var nextWindow = gate.Observe(1, 10, 8, T0.AddMinutes(7), 5);
        Assert.True(nextWindow.FirstCrossing);
    }

    [Fact]
    public void 不同規則各自獨立告警()
    {
        // 兩條規則都是移動告警但門檻不同，共用一個標記會讓其中一條永遠靜默。
        var gate = new SmartAlertGate();

        Assert.True(gate.Observe(ruleId: 1, windowCount: 5, minEvents: 5, T0, 5).FirstCrossing);
        Assert.True(gate.Observe(ruleId: 2, windowCount: 5, minEvents: 5, T0, 5).FirstCrossing);
    }

    [Fact]
    public void 規則被重設後可以立即重新告警()
    {
        // 操作員把門檻從 8 改成 4 後，不該還要再等一個窗才能看到效果。
        var gate = new SmartAlertGate();
        Assert.True(gate.Observe(1, 8, 8, T0, 5).FirstCrossing);

        gate.Reset(1);

        Assert.True(gate.Observe(1, 8, 4, T0.AddMinutes(1), 5).FirstCrossing);
    }
}
