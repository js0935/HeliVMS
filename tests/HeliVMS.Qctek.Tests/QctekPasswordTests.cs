using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>CONNECT 密碼欄位：標準 Base64＋兩個字面預設的辨識。</summary>
public class QctekPasswordTests
{
    [Fact]
    public void 編碼為標準Base64()
    {
        Assert.Equal("czNjcmV0", QctekPassword.Encode("s3cret"));
    }

    [Fact]
    public void 解碼還原原文()
    {
        Assert.Equal("s3cret", QctekPassword.Decode("czNjcmV0"));
    }

    [Fact]
    public void 省略填補的Base64也能解出真機接受過的密碼()
    {
        // 黃金抓包（stream 16）：QCC 送 "YWRtaW4"（admin 的 Base64 少一個 '='），
        // 真機回 OK。要求標準填補會讓這個密碼永遠驗不過。
        Assert.Equal("admin", QctekPassword.Decode("YWRtaW4"));
    }

    [Fact]
    public void 字面預設原樣回傳而不會被當成密碼()
    {
        // 判別規則：能解成合法 UTF-8 的 Base64 才是密碼。
        // "guest" 不是合法 Base64（長度 5）；"test" 解出 B5 EB 2D ＝非法 UTF-8——
        // 兩者都落回原字串。真密碼的 Base64 一定解得回合法 UTF-8，所以不會被誤判。
        Assert.Equal("guest", QctekPassword.Decode("guest"));
        Assert.Equal("test", QctekPassword.Decode("test"));
    }

    [Fact]
    public void 非法Base64原樣回傳交由認證失敗()
    {
        Assert.Equal("not-base64!", QctekPassword.Decode("not-base64!"));
    }
}
