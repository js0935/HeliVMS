using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>NEMO 命令派送器：命令 → 線路位元組的純函式對照。</summary>
/// <remarks>
/// 這裡釘的是<b>框法</b>，不只文字：真機的 raw／NEMO／有 NUL／無 NUL 四種型式
/// 各自出現在不同命令上（見 <see cref="QctekReply"/>），長度差 1 就會讓 QCC 的讀取器
/// 卡住或吞掉下一個回應。所以斷言用位元組，不用「字串包含」。
/// </remarks>
public class QctekCommandHandlerTests
{
    private sealed class TestContext : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";

        public int ChannelCount => ChannelNames.Count;

        public IReadOnlyList<string> ChannelNames
            => Enumerable.Range(1, 8).Select(static i => $"攝影機{i:000}").ToList();

        public int MaxStreams => 2;

        public bool HasVnc { get; init; }

        /// <summary>測試帳號：admin/s3cret；字面預設 guest/guest、test/test 也接受。</summary>
        public bool TryAuthenticate(string user, string password)
            => (user, password) is ("admin", "s3cret") or ("guest", "guest") or ("test", "test");

        public bool IsKnownUser(string user) => user is "admin" or "guest" or "test";

        public HeliVMS.Qctek.Live.IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public HeliVMS.Qctek.Live.QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc) => null;

        public HeliVMS.Qctek.Live.QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc) => null;

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public HeliVMS.Qctek.Live.IQctekLiveSource? CreatePlaybackSource(string filePath) => null;
    }

    private static byte[]? Handle(string command, bool framed = true, bool hasVnc = false)
        => QctekCommandHandler.Handle(
            framed ? QctekRequest.Nemo(command) : QctekRequest.Raw(command),
            new TestContext { HasVnc = hasVnc });

    private static string TextOf(byte[]? wire)
    {
        Assert.NotNull(wire);
        if (wire.Length >= NemoMessage.HeaderLength &&
            wire.AsSpan().StartsWith("NEMO "u8))
        {
            Assert.True(NemoMessage.TryDecode(wire, out var payload, out _));
            return Encoding.ASCII.GetString(payload).TrimEnd('\0');
        }

        return Encoding.ASCII.GetString(wire).TrimEnd('\0');
    }

    private static byte[] PayloadOf(byte[]? wire)
    {
        Assert.NotNull(wire);
        Assert.True(NemoMessage.TryDecode(wire, out var payload, out _));
        return payload;
    }

    [Fact]
    public void raw版本查詢回傳文字加結尾NUL()
    {
        // 黃金抓包（stream 0）：真機回 12 bytes：OK QCV8001 ＋NUL。
        var wire = Handle("CHECK_SERVER_VERSION CLIENT_AUTH QCTEK", framed: false);
        Assert.Equal("OK QCV8001 \0", Encoding.ASCII.GetString(wire!));
        Assert.Equal(12, wire!.Length);
    }

    [Fact]
    public void framed版本查詢的標頭少宣告一個NUL()
    {
        // 真機回應：NEMO 11（宣告 11）＋"OK QCV8001 "＋NUL＝28 bytes；標頭比實際少 1。
        var wire = Handle("CHECK_SERVER_VERSION CLIENT_AUTH QCTEK");
        Assert.StartsWith("NEMO 11         ", Encoding.ASCII.GetString(wire!, 0, 16), StringComparison.Ordinal);
        Assert.Equal(28, wire!.Length);
        Assert.Equal("OK QCV8001 ", TextOf(wire));
    }

    [Fact]
    public void 頻道數與串流數以framed文字回報且不帶NUL()
    {
        // 真機 FUN_180006110 的 GET_MAX_STREAM_NUM 只寫 strlen("OK %d ") 個位元組。
        var channels = Handle("GET_NCHANNEL");
        var streams = Handle("GET_MAX_STREAM_NUM");
        Assert.Equal("OK 8 ", TextOf(channels));
        Assert.Equal("OK 2 ", TextOf(streams));
        Assert.Equal(16 + "OK 8 ".Length, channels!.Length);
        Assert.Equal(16 + "OK 2 ".Length, streams!.Length);
    }

    [Fact]
    public void 沒有VNC時回FAIL避免CMS去連不存在的埠()
    {
        Assert.Equal("FAIL ", TextOf(Handle("CHECK_VNC_VERSION")));
        Assert.Equal("OK TVNC_2.7.10 ", TextOf(Handle("CHECK_VNC_VERSION", hasVnc: true)));
    }

    [Fact]
    public void 警報存活查詢以framed文字加NUL回應()
    {
        // 黃金抓包（stream 24）：NEMO 6＋"ALIVE "＋NUL＝23 bytes（標頭宣告 6、實際 7）。
        var wire = Handle("ALARM_ISALIVE 5122 =QCV0000000000");
        Assert.Equal("ALIVE ", TextOf(wire));
        Assert.Equal(16 + "ALIVE ".Length + 1, wire!.Length);
    }

    [Fact]
    public void 警報取消以framed文字加NUL回應()
    {
        // 真機常數：NEMO 3＋"OK "＋NUL。
        var wire = Handle("ALARM_CANCEL 1 1 ");
        Assert.Equal("OK ", TextOf(wire));
        Assert.Equal(16 + "OK ".Length + 1, wire!.Length);
    }

    [Fact]
    public void framed連線帶Base64密碼通過時回十二位元組且不帶NUL()
    {
        // 真機：NEMO 12＋12 bytes（無 NUL）。CMS 只檢查 "OK" 與第一個整數 ≠ 0。
        var pass = QctekPassword.Encode("s3cret");
        var wire = Handle($"CONNECT admin {pass} ");
        Assert.Equal("OK -1 -1 -1 ", TextOf(wire));
        Assert.Equal(16 + "OK -1 -1 -1 ".Length, wire!.Length);
    }

    [Fact]
    public void raw連線成功回傳十三位元組含NUL()
    {
        var pass = QctekPassword.Encode("s3cret");
        var wire = Handle($"CONNECT admin {pass} ", framed: false);
        Assert.Equal("OK -1 -1 -1 \0", Encoding.ASCII.GetString(wire!));
        Assert.Equal(13, wire!.Length);
    }

    [Fact]
    public void 連線密碼錯誤回LoginFailed且不含OK()
    {
        var framed = Handle($"CONNECT admin {QctekPassword.Encode("wrong")} ");
        Assert.Equal("Login Failed ", TextOf(framed));
        Assert.DoesNotContain("OK", TextOf(framed), StringComparison.Ordinal);
        Assert.Equal(16 + "Login Failed ".Length, framed!.Length); // 13 bytes，無 NUL

        var raw = Handle($"CONNECT admin {QctekPassword.Encode("wrong")} ", framed: false);
        Assert.Equal("Login Failed \0", Encoding.ASCII.GetString(raw!));
        Assert.Equal(14, raw!.Length);
    }

    [Fact]
    public void 連線未帶帳號時的字面預設由實作決定()
    {
        Assert.Equal("OK -1 -1 -1 ", TextOf(Handle("CONNECT guest guest ")));
        Assert.Equal("Login Failed ", TextOf(Handle("CONNECT guest stranger ")));
    }

    [Fact]
    public void 連線參數不足不會誤判成功()
    {
        Assert.Null(Handle("CONNECT"));                       // 不是 CONNECT 命令（缺尾空格）＝不回應
        Assert.Equal("Login Failed ", TextOf(Handle("CONNECT admin"))); // 缺密碼欄位
    }

    [Fact]
    public void raw警報訂閱回真機的兩個識別碼加NUL()
    {
        // 黃金抓包（stream 16／17）：ALARM_REQUEST 是 raw 命令，真機回 8 bytes：OK 0 1 ＋NUL。
        var pass = QctekPassword.Encode("s3cret");
        var wire = Handle(
            $"ALARM_REQUEST 5122 version7.7 user: admin {pass} =QCV0000000000", framed: false);
        Assert.Equal("OK 0 1 \0", Encoding.ASCII.GetString(wire!));
        Assert.Equal(8, wire!.Length);
    }

    [Fact]
    public void framed警報訂閱不帶NUL()
    {
        var pass = QctekPassword.Encode("s3cret");
        var wire = Handle($"ALARM_REQUEST 5122 version7.7 user: admin {pass} =QCV0000000000");
        Assert.Equal("OK 0 1 ", TextOf(wire));
        Assert.Equal(16 + "OK 0 1 ".Length, wire!.Length);
    }

    [Fact]
    public void 警報訂閱憑證錯誤時raw與framed各有真機框法()
    {
        var pass = QctekPassword.Encode("wrong");
        var raw = Handle($"ALARM_REQUEST 5 version7.7 user: admin {pass} =QCV0000000001", framed: false);
        Assert.Equal("FAIL \0", Encoding.ASCII.GetString(raw!));

        var framed = Handle($"ALARM_REQUEST 5 version7.7 user: admin {pass} =QCV0000000001");
        Assert.Equal("FAIL ", TextOf(framed));
        Assert.Equal(16 + "FAIL ".Length + 1, framed!.Length);
    }

    [Fact]
    public void 設定查詢回二進位框且長度隨頻道數成長()
    {
        var payload = PayloadOf(Handle("GET_CONFIG_QCTEK admin QCV8001_AUTH"));
        Assert.Equal(QctekPayloads.ConfigHeaderLength + (8 * QctekPayloads.ConfigRecordLength), payload.Length);
        Assert.Equal("QCTEK_RM"u8.ToArray(), payload[..8]);
        Assert.Equal(8u, BitConverter.ToUInt32(payload, 20));

        // 名稱以 UTF-16LE 寫在記錄位移 0x1C（第一筆檔位移 0x34）。這裡刻意寫死黃金的
        // 絕對位移，而不是引用 ConfigNameOffset：引用同一個常數的斷言會跟著錯誤的常數
        // 一起移動，永遠是綠的（實測：把 0x1C 改成 0x18，這種斷言不會失敗）。
        var first = payload.AsSpan(0x34, Encoding.Unicode.GetByteCount("攝影機001\0"));
        Assert.Equal(Encoding.Unicode.GetBytes("攝影機001\0"), first.ToArray());
    }

    [Fact]
    public void 使用者查詢回真機固定長度的權限位圖()
    {
        var payload = PayloadOf(Handle("GET_AUTH_USER admin QCV8001_AUTH"));
        Assert.Equal(QctekPayloads.AuthUserLength, payload.Length);

        // 位移 20 是機種容量常數（真機在 1 個頻道的裝置上也回 36），不是實際頻道數——
        // QCC 以它初始化權限表，回實際頻道數會讓即時影像在授權後立刻關線。
        Assert.Equal(36u, BitConverter.ToUInt32(payload, 20));
        Assert.All(payload.AsSpan(24).ToArray(), b => Assert.Equal((byte)0xFF, b));
    }

    [Fact]
    public void IO設定查詢回真機固定長度的零接點表()
    {
        var payload = PayloadOf(Handle("GET_IOCONFIG "));
        Assert.Equal(QctekPayloads.IoConfigLength, payload.Length);
        Assert.Equal("QCTEK_RM"u8.ToArray(), payload[..8]);
        Assert.Equal(new byte[] { 0x6D, 0x3D, 0x34, 0x01 }, payload[8..12]);
        Assert.All(payload.AsSpan(12).ToArray(), b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void 未知命令不回猜測內容()
    {
        // 未還原語意的命令（SEARCH_EVENT 等）回 null：寧可不回應，也不要回錯讓 CMS 誤判。
        // GET_MONTH_DATE 已由回放協定實作（QctekPlaybackPayloads），所以不再列在這裡。
        Assert.Null(Handle("SEARCH_EVENT foo"));
        Assert.Null(Handle("GET_SDK_RECINFO"));
        Assert.Null(Handle("ALARM_MATRIX 0"));
    }

    [Fact]
    public void 心跳命令是已知命令且不回任何位元組()
    {
        // 真機 QC_RM_Alarm 對 ALARM_HELLO 只把回應緩衝區首位寫 0（空字串），線路上就是
        // 0 bytes；QCC 端送出後只累加計數、從不讀回應。所以「補上」一個回應框會讓
        // 客戶端把它當成下一條命令的回應而全部錯位一格——比不回應更糟。這裡同時釘住
        // 「心跳」與「未知命令」必須分得出來，否則日誌會把心跳寫成缺陷。
        var heartbeat = QctekRequest.Nemo("ALARM_HELLO 1 =QCV8001");
        Assert.True(QctekCommandHandler.IsSilentHeartbeat(heartbeat.Command));
        Assert.Null(QctekCommandHandler.Handle(heartbeat, new TestContext()));
        Assert.False(QctekCommandHandler.IsSilentHeartbeat("SEARCH_EVENT 1 2 3"));
    }

    [Fact]
    public void 只有黃金抓包證明需要的命令才要求授權()
    {
        // GET_IOCONFIG 在同一次對話中沒有挑戰（stream 18）：擋下它會讓 QCC 卡在等挑戰。
        Assert.True(QctekCommandHandler.RequiresAuthorization(QctekRequest.Nemo("GET_CONFIG_QCTEK admin QCV8001_AUTH")));
        Assert.True(QctekCommandHandler.RequiresAuthorization(QctekRequest.Nemo("GET_AUTH_USER admin QCV8001_AUTH")));
        Assert.True(QctekCommandHandler.RequiresAuthorization(QctekRequest.Raw("GET_LIVE_START admin QCV8001_AUTH")));
        Assert.False(QctekCommandHandler.RequiresAuthorization(QctekRequest.Nemo("GET_IOCONFIG ")));
        Assert.False(QctekCommandHandler.RequiresAuthorization(QctekRequest.Raw("CHECK_SERVER_VERSION")));
    }
}
