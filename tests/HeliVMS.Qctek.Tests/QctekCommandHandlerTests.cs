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
    public void 未知命令不回猜測內容()
    {
        // 未還原語意的命令（SEARCH_EVENT 等）回 null：寧可不回應，也不要回錯讓 CMS 誤判。
        Assert.Null(Handle("SEARCH_EVENT foo"));
        Assert.Null(Handle("GET_CONFIG_QCTEK"));
    }
}
