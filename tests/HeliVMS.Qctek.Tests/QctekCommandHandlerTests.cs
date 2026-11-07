using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>NEMO 命令派送器：命令 → 回應 payload 的純函式對照。</summary>
public class QctekCommandHandlerTests
{
    private sealed class TestContext : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";

        public int ChannelCount => 8;

        public int MaxStreams => 2;

        public bool HasVnc { get; init; }

        /// <summary>測試帳號：admin/s3cret；字面預設 guest/guest、test/test 也接受。</summary>
        public bool TryAuthenticate(string user, string password)
            => (user, password) is ("admin", "s3cret") or ("guest", "guest") or ("test", "test");
    }

    private static string? Handle(string command, bool hasVnc = false)
        => QctekCommandHandler.Handle(command, new TestContext { HasVnc = hasVnc });

    [Fact]
    public void 版本查詢回報相容產品名且尾端保留空白()
    {
        // "OK QCV8001 " 尾端空白是協定慣例（QCV8001 內建字串逐字如此），
        // 長度由 NEMO 標頭決定；被「整理」掉會讓長度對不上而被 CMS 丟棄。
        Assert.Equal("OK QCV8001 ", Handle("CHECK_SERVER_VERSION"));
        Assert.EndsWith(" ", Handle("CHECK_SERVER_VERSION"), StringComparison.Ordinal);
    }

    [Fact]
    public void 版本查詢帶CLIENT_AUTH尾綴仍回應()
    {
        // CMS 會以 "CHECK_SERVER_VERSION  CLIENT_AUTH" 探測是否需要認證。
        Assert.Equal("OK QCV8001 ", Handle("CHECK_SERVER_VERSION  CLIENT_AUTH"));
    }

    [Fact]
    public void 頻道數與串流數依裝置資訊回報()
    {
        Assert.Equal("OK 8 ", Handle("GET_NCHANNEL"));
        Assert.Equal("OK 2 ", Handle("GET_MAX_STREAM_NUM"));
    }

    [Fact]
    public void 沒有VNC時回FAIL避免CMS去連不存在的埠()
    {
        Assert.Equal("FAIL ", Handle("CHECK_VNC_VERSION"));
        Assert.Equal("OK TVNC_2.7.10 ", Handle("CHECK_VNC_VERSION", hasVnc: true));
    }

    [Fact]
    public void 警報存活查詢與取消有固定回應()
    {
        Assert.Equal("ALIVE ", Handle("ALARM_ISALIVE"));
        Assert.Equal("OK ", Handle("ALARM_CANCEL"));
    }

    [Fact]
    public void 連線帶Base64密碼通過時回QCV8001同款成功字串()
    {
        // CMS 只檢查回應有沒有 "OK" 與第一個整數 ≠ 0（NVDLL sscanf "%s%d"）。
        var pass = QctekPassword.Encode("s3cret");
        Assert.Equal("OK -1 -1 -1 ", Handle($"CONNECT admin {pass} "));
    }

    [Fact]
    public void 連線密碼錯誤回LoginFailed且不含OK()
    {
        var response = Handle($"CONNECT admin {QctekPassword.Encode("wrong")} ");
        Assert.Equal("Login Failed ", response);
        Assert.DoesNotContain("OK", response, StringComparison.Ordinal);
    }

    [Fact]
    public void 連線未帶帳號時的字面預設由實作決定()
    {
        Assert.Equal("OK -1 -1 -1 ", Handle("CONNECT guest guest "));
        Assert.Equal("Login Failed ", Handle("CONNECT guest stranger "));
    }

    [Fact]
    public void 連線參數不足不會誤判成功()
    {
        Assert.Null(Handle("CONNECT"));                       // 不是 CONNECT 命令（缺尾空格）＝不回應
        Assert.Equal("Login Failed ", Handle("CONNECT admin")); // 缺密碼欄位
    }

    [Fact]
    public void 警報訂閱帶正確憑證時回兩個識別碼()
    {
        var pass = QctekPassword.Encode("s3cret");
        Assert.Equal("OK 1 1 ", Handle($"ALARM_REQUEST 5 version7.7 user: admin {pass} =QCV0000000001"));
    }

    [Fact]
    public void 警報訂閱憑證錯誤時回FAIL()
    {
        var pass = QctekPassword.Encode("wrong");
        Assert.Equal("FAIL ", Handle($"ALARM_REQUEST 5 version7.7 user: admin {pass} =QCV0000000001"));
    }

    [Fact]
    public void 未知命令不回猜測內容()
    {
        // 未還原語意的命令（SEARCH_EVENT 等）回 null：寧可不回應，也不要回錯讓 CMS 誤判。
        Assert.Null(Handle("SEARCH_EVENT foo"));
        Assert.Null(Handle("GET_CONFIG_QCTEK"));
    }
}
