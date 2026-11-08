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

    [Fact]
    public void 原始傾印保留可讀的請求列並轉義控制字元()
    {
        // 實測（QCC 加裝置）時唯一能回答「對方送了什麼」的手段：HTTP 探測列、
        // 別的協定、或損壞的串流都要一眼可辨。
        var http = System.Text.Encoding.ASCII.GetBytes("GET /DVR_Info.htm HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        var dump = QctekDiagnostics.DescribeBytes(http);

        Assert.Contains("GET /DVR_Info.htm HTTP/1.1\\r\\n", dump);
        Assert.Contains("Host: 127.0.0.1", dump);
    }

    [Fact]
    public void 原始傾印截斷且標示總長()
    {
        var data = new byte[200];
        Array.Fill(data, (byte)'A');

        var dump = QctekDiagnostics.DescribeBytes(data, maxBytes: 96);

        Assert.Contains("（共 200 bytes）", dump);
        Assert.Equal(96, dump.Count(c => c == 'A'));
    }

    [Fact]
    public void 空的原始資料有可讀表示()
    {
        Assert.Equal("(0 bytes)", QctekDiagnostics.DescribeBytes(Array.Empty<byte>()));
    }

    [Fact]
    public void HTTP方法被認得而NEMO與二進位不會誤判()
    {
        var a = System.Text.Encoding.ASCII;

        Assert.True(QctekDiagnostics.LooksLikeHttp(a.GetBytes("GET /DVR_Info.htm HTTP/1.1\r\n")));
        Assert.True(QctekDiagnostics.LooksLikeHttp(a.GetBytes("HEAD / HTTP/1.0\r\n")));
        Assert.True(QctekDiagnostics.LooksLikeHttp(a.GetBytes("POST /ws/login HTTP/1.1\r\n")));

        Assert.False(QctekDiagnostics.LooksLikeHttp(a.GetBytes("NEMO 11         OK QCV8001 ")));
        Assert.False(QctekDiagnostics.LooksLikeHttp(Array.Empty<byte>()));
        // N9000（0x91B）的請求頭：小端 06 00 00 01 1B 09 00 00——不是 HTTP。
        Assert.False(QctekDiagnostics.LooksLikeHttp(new byte[] { 0x06, 0x00, 0x00, 0x01, 0x1B, 0x09, 0x00, 0x00 }));
    }
}
