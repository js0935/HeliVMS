using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCTek HTTP 前景：QCC 的裝置探測（實測捕獲 <c>GET /</c>＋Basic）必須認得型號，
/// 否則只會報「無法確認監控系統的型號與協定」。
/// </summary>
public class QctekHttpResponderTests
{
    private static QctekHttpResponder.Context Ctx => new("5170", 6);

    [Fact]
    public void 根路徑回QCV8001標題頁()
    {
        // QCV8001 的 GET / 是 root.html——title 就是產品名，QCC 的裝置識別讀它；
        // 用「DVR Information」當首頁時它拿不到型號，那是「無法確認型號與協定」之一。
        var response = QctekHttpResponder.BuildResponse("GET / HTTP/1.1\r\nHost: x\r\n\r\n", Ctx);

        Assert.NotNull(response);
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("<title>QCV8001</title>", response);
        Assert.Contains("Content-Type: text/html", response);
    }

    [Fact]
    public void DVR_Info頁回資訊頁且不分大小寫()
    {
        var exact = QctekHttpResponder.BuildResponse("GET /DVR_Info.htm HTTP/1.1\r\n\r\n", Ctx);
        var lower = QctekHttpResponder.BuildResponse("GET /dvr_info.htm HTTP/1.1\r\n\r\n", Ctx);

        Assert.NotNull(exact);
        Assert.NotNull(lower);
        Assert.Contains("<ProductName> QCV8001 Information </ProductName>", exact);
        Assert.Contains("<ProductVersion>", exact);
        Assert.Contains("<LoginID>", exact);
        Assert.Contains("<ProductName> QCV8001 Information </ProductName>", lower);
    }

    [Fact]
    public void 遠端控制埠端點回服務埠()
    {
        var response = QctekHttpResponder.BuildResponse("GET /getparam.cgi?RemoteConsolePort HTTP/1.1\r\n\r\n", Ctx);

        Assert.NotNull(response);
        Assert.Contains("RemoteConsolePort=5170", response);
    }

    [Fact]
    public void 地圖資訊端點回頻道數()
    {
        var response = QctekHttpResponder.BuildResponse("GET /emap/EmapInfo.txt HTTP/1.1\r\n\r\n", Ctx);

        Assert.NotNull(response);
        Assert.Contains("[Server_Number]", response);
        Assert.Contains("TotalChannel=6", response);
    }

    [Fact]
    public void 未知路徑不假裝有頁面()
    {
        // 回空頁會讓 QCC 以為這裡有網頁管理介面，後續流程撞牆——不認識就 404。
        Assert.Null(QctekHttpResponder.BuildResponse("GET /admin HTTP/1.1\r\n\r\n", Ctx));
        Assert.Null(QctekHttpResponder.BuildResponse("POST / HTTP/1.1\r\n\r\n", Ctx));
        Assert.Null(QctekHttpResponder.BuildResponse("not http at all", Ctx));
        Assert.Null(QctekHttpResponder.BuildResponse(null, Ctx));
    }

    [Fact]
    public void ContentLength等於實際內容位元組數()
    {
        // QCC 靠 Content-Length 讀 body——數字錯了它會卡在等資料。
        var response = QctekHttpResponder.BuildResponse("GET / HTTP/1.1\r\n\r\n", Ctx);
        Assert.NotNull(response);

        var marker = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerText = response[..marker];
        var body = response[(marker + 4)..];

        var lengthLine = headerText
            .Split("\r\n")
            .First(l => l.StartsWith("Content-Length:", StringComparison.Ordinal));
        var declared = int.Parse(lengthLine["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        var actual = System.Text.Encoding.UTF8.GetByteCount(body);

        Assert.Equal(declared, actual);
    }
}
