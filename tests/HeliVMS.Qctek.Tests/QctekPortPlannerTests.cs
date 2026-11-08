using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 「Web 服務與 QCTek 相容服務要不要共用同一個埠」的決策。
/// </summary>
/// <remarks>
/// 這個判斷同時決定啟動／停止的順序：共用時 Web 要先讓出公開埠、多工器才綁；
/// 不共用時多工器要先放掉公開埠、Web 才綁得回去。順序反了就是「連接埠已被占用」，
/// 而設定頁只會顯示一句啟動失敗。
/// </remarks>
public class QctekPortPlannerTests
{
    [Fact]
    public void 兩個服務都啟用且同埠時才共用()
    {
        Assert.True(QctekPortPlanner.ShouldSharePort(
            webEnabled: true, compatibilityEnabled: true, webPort: 80, compatibilityPort: 80));
        Assert.True(QctekPortPlanner.ShouldSharePort(
            webEnabled: true, compatibilityEnabled: true, webPort: 5080, compatibilityPort: 5080));
    }

    [Fact]
    public void 任何一個沒啟用就不共用()
    {
        // 只用 Web 時沒有 NEMO 要應答，多工器沒有存在的理由；
        // 只用相容服務時它照舊綁自己的埠（NemoServer），Web 不必讓位。
        Assert.False(QctekPortPlanner.ShouldSharePort(
            webEnabled: false, compatibilityEnabled: true, webPort: 80, compatibilityPort: 80));
        Assert.False(QctekPortPlanner.ShouldSharePort(
            webEnabled: true, compatibilityEnabled: false, webPort: 80, compatibilityPort: 80));
        Assert.False(QctekPortPlanner.ShouldSharePort(
            webEnabled: false, compatibilityEnabled: false, webPort: 80, compatibilityPort: 80));
    }

    [Fact]
    public void 埠不同就不共用且不需要多工器()
    {
        Assert.False(QctekPortPlanner.ShouldSharePort(
            webEnabled: true, compatibilityEnabled: true, webPort: 80, compatibilityPort: 5170));
    }

    [Fact]
    public void 無效的埠不會被當成共用()
    {
        // 設定頁會擋掉這些值，但索引可以直接被改壞（或從舊版升級而來）。
        // 用 0 當「共用」會讓 Kestrel 綁 0、多工器也綁 0 → 兩個隨機埠，誰都找不到。
        foreach (var port in new[] { 0, -1, 65536 })
        {
            Assert.False(QctekPortPlanner.ShouldSharePort(
                webEnabled: true, compatibilityEnabled: true, webPort: port, compatibilityPort: port));
        }
    }
}
