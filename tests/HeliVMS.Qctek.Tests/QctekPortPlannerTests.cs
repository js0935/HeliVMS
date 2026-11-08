using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 「Web 服務與 QCTek 相容服務要怎麼分配連接埠」的決策。
/// </summary>
/// <remarks>
/// 這個判斷同時決定 listener 的組態與啟動／停止的順序：共用時 Web 要先讓出公開埠、
/// 多工器才綁；不共用時多工器要先放掉公開埠、Web 才綁得回去。順序反了就是
/// 「連接埠已被占用」，而設定頁只會顯示一句啟動失敗。
/// </remarks>
public class QctekPortPlannerTests
{
    [Fact]
    public void 相容埠與Web埠不同時QCC走的Web埠仍由多工器接手()
    {
        // 現場實測的失敗形狀：web.port=80、qctek.compat.port=5170（出廠預設值）。
        // 舊規則要求「兩個埠相同才共用」，於是 80 上只有 Kestrel，QCC 送出的 NEMO
        // 命令換到一句 400 Bad Request：裝置加得進去，畫面永遠「連線中斷」，日誌乾淨。
        // QCC 沒有地方可以填 NEMO 埠——它固定連 Web 埠。
        var plan = QctekPortPlanner.Plan(
            webEnabled: true, compatibilityEnabled: true, webPort: 80, compatibilityPort: 5170);

        Assert.True(plan.ShareWebPort);
        Assert.Equal(80, plan.PublicPort);
        Assert.Equal(5170, plan.StandalonePort); // 遠端 CMS 若明確填了 5170 仍然連得上
        Assert.True(plan.Anything);
    }

    [Fact]
    public void 相容埠與Web埠相同時只留多工器不開第二個listener()
    {
        // 同一個埠上開兩個 listener 一定有一個失敗，而失敗的那一個會讓整組套用
        // 被當成「啟動失敗」——明明 80 已經有東西在聽。
        var plan = QctekPortPlanner.Plan(
            webEnabled: true, compatibilityEnabled: true, webPort: 80, compatibilityPort: 80);

        Assert.True(plan.ShareWebPort);
        Assert.Equal(80, plan.PublicPort);
        Assert.Equal(0, plan.StandalonePort);
        Assert.True(plan.Anything);
    }

    [Fact]
    public void Web沒啟用時只剩獨立埠()
    {
        // 沒有公開埠可以搶，多工器沒有存在的理由；相容服務照舊綁自己的埠。
        var plan = QctekPortPlanner.Plan(
            webEnabled: false, compatibilityEnabled: true, webPort: 80, compatibilityPort: 5170);

        Assert.False(plan.ShareWebPort);
        Assert.Equal(0, plan.PublicPort);
        Assert.Equal(5170, plan.StandalonePort);
    }

    [Fact]
    public void 相容服務沒啟用時不開任何listener()
    {
        var plan = QctekPortPlanner.Plan(
            webEnabled: true, compatibilityEnabled: false, webPort: 80, compatibilityPort: 5170);

        Assert.False(plan.Anything);
        Assert.False(plan.ShareWebPort);
        Assert.Equal(0, plan.StandalonePort);
    }

    [Fact]
    public void 無效的埠不會被當成共用()
    {
        // 設定頁會擋掉這些值，但索引可以直接被改壞（或從舊版升級而來）。
        // 用 0 當「共用」會讓 Kestrel 綁 0、多工器也綁 0 → 兩個隨機埠，誰都找不到。
        foreach (var port in new[] { 0, -1, 65536 })
        {
            Assert.False(QctekPortPlanner
                .Plan(webEnabled: true, compatibilityEnabled: true, webPort: port, compatibilityPort: port)
                .Anything);

            // Web 埠壞掉時仍可退回獨立埠；否則整個相容服務就無聲消失。
            var standalone = QctekPortPlanner.Plan(
                webEnabled: true, compatibilityEnabled: true, webPort: port, compatibilityPort: 5170);
            Assert.False(standalone.ShareWebPort);
            Assert.Equal(5170, standalone.StandalonePort);
        }
    }

    [Fact]
    public void 沒有協調者時只開獨立埠()
    {
        // 直接呼叫 QctekCompatHost 的舊路徑不知道 Web 服務在哪，不能假裝自己擁有公開埠。
        var plan = QctekPortPlanner.Standalone(compatibilityEnabled: true, compatibilityPort: 5170);

        Assert.False(plan.ShareWebPort);
        Assert.Equal(5170, plan.StandalonePort);
        Assert.False(QctekPortPlanner.Standalone(compatibilityEnabled: false, compatibilityPort: 5170).Anything);
    }
}
