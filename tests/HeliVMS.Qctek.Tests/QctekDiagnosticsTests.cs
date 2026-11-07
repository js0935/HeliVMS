using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 診斷摘要：QCC 實測失敗時唯一的線索——但密碼段絕不落地（日誌會被複製到郵件
/// 與工單裡）。這條安全保證由測試釘住，不是註解裡的承諾。
/// </summary>
public class QctekDiagnosticsTests
{
    [Fact]
    public void CONNECT摘要只留使用者名密碼以星號遮蔽()
    {
        var summary = QctekDiagnostics.Summarize("CONNECT admin YWRtaW4xMjM=");

        Assert.Equal("CONNECT admin ***", summary);
        Assert.DoesNotContain("YWRtaW4xMjM", summary);
    }

    [Fact]
    public void 損壞或截斷的CONNECT也不會漏出參數()
    {
        Assert.Equal("CONNECT ***", QctekDiagnostics.Summarize("CONNECT"));
        Assert.DoesNotContain("=", QctekDiagnostics.Summarize("CONNECT"));
    }

    [Fact]
    public void ALARM_REQUEST摘要只留訂閱號()
    {
        var summary = QctekDiagnostics.Summarize(
            "ALARM_REQUEST 3 version7.7 user: admin c2VjcmV0 =QCV0000000001");

        Assert.Equal("ALARM_REQUEST 3 ***", summary);
        Assert.DoesNotContain("c2VjcmV0", summary);
        Assert.DoesNotContain("admin", summary);
        Assert.DoesNotContain("QCV0000000001", summary);
    }

    [Fact]
    public void 一般命令保留全文且超長截斷()
    {
        Assert.Equal(
            "CHECK_SERVER_VERSION CLIENT_AUTH",
            QctekDiagnostics.Summarize("CHECK_SERVER_VERSION CLIENT_AUTH"));

        var summary = QctekDiagnostics.Summarize("SOMECMD " + new string('x', 500));
        Assert.Equal(QctekDiagnostics.MaxSummaryLength + 1, summary.Length); // 160 + 省略號
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void 空命令有可讀的表示()
    {
        Assert.Equal("(空)", QctekDiagnostics.Summarize(null));
        Assert.Equal("(空)", QctekDiagnostics.Summarize("   "));
    }
}
