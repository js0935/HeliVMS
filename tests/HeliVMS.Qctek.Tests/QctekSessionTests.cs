using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HeliVMS.Qctek.Live;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 連線狀態機：以黃金抓包的位元組重播 QCC 的每種命令流，逐位元組比對回應。
/// </summary>
/// <remarks>
/// <para>
/// 狀態機刻意不碰 socket，所以這裡不需要開埠、不需要等待：把抓包裡的用戶端位元組
/// 餵給 <see cref="QctekSession.Advance"/>，再比對產生的線路位元組。抓包裡的
/// 「挑戰」是伺服端回應，測試以固定的挑戰產生器注入同一個字串，才能逐位元組重播。
/// </para>
/// <para>
/// 每條測試對應抓包裡的一條短連線（stream 編號見 <see cref="GoldenCapture"/>）。
/// 這些是 QCC 真的做過的事，不是我們對協定的想像。
/// </para>
/// </remarks>
public class QctekSessionTests
{
    private sealed class TestContext : IQctekDeviceContext
    {
        private readonly string[] _names;

        public TestContext(params string[] names) => _names = names;

        public TestContext(int channelCount)
            => _names = Enumerable.Range(1, channelCount).Select(i => $"CH{i:000}").ToArray();

        public string ServerVersion => "QCV8001";

        public int ChannelCount => _names.Length;

        public IReadOnlyList<string> ChannelNames => _names;

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password)
            => user == "admin" && password is "admin" or "s3cret";

        public bool IsKnownUser(string user) => user == "admin";

        public IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc) => null;

        public QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc) => null;

        /// <summary>測試可注入事件頁；預設 0 筆。</summary>
        public Func<QctekEventSearchRequest, int, int, QctekEventPage>? EventSearch { get; init; }

        /// <summary>最近一次查詢（測試用來確認本體解析的欄位）。</summary>
        public QctekEventSearchRequest? LastEventQuery { get; private set; }

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
        {
            LastEventQuery = query;
            return EventSearch?.Invoke(query, offset, limit) ?? QctekEventPage.Empty;
        }

        public IQctekLiveSource? CreatePlaybackSource(string filePath) => null;
    }

    [Fact]
    public void 黃金流0的raw版本查詢回傳文字加結尾NUL()
    {
        var session = new QctekSession(new TestContext(1));
        var request = GoldenCapture.Hex(GoldenCapture.CheckServerVersionRequest);

        var result = session.Advance(request);

        Assert.Equal(request.Length, result.Consumed);
        Assert.False(result.Close);
        Assert.Equal(GoldenCapture.Hex(GoldenCapture.CheckServerVersionReply), result.Reply);
    }

    [Fact]
    public void 黃金流2的設定查詢先挑戰再回真機的三百一十二位元組()
    {
        var session = new QctekSession(
            new TestContext("攝影機001"),
            () => GoldenCapture.ConfigChallenge);

        var pending = session.Advance(GoldenCapture.Hex(GoldenCapture.ConfigRequest));

        // 挑戰是 raw 27 bytes、沒有結尾 NUL（真機如此）；此時命令尚未執行。
        Assert.Equal(51, pending.Consumed);
        Assert.True(session.AwaitingAuthorization);
        Assert.Equal(Encoding.ASCII.GetBytes(GoldenCapture.ConfigChallenge), pending.Reply);

        var done = session.Advance(GoldenCapture.Hex(GoldenCapture.ConfigSendCode));

        Assert.Equal(55, done.Consumed); // 認證碼＋NUL
        Assert.False(session.AwaitingAuthorization);
        Assert.Equal(QctekReply.FramedBinary(GoldenCapture.ConfigPayload), done.Reply);
        Assert.Equal(
            Encoding.ASCII.GetBytes("NEMO 312" + new string(' ', 8)),
            done.Reply![..16]);
    }

    [Fact]
    public void 黃金流4的使用者查詢回傳一百五十二位元組()
    {
        var session = new QctekSession(
            new TestContext(36),
            () => GoldenCapture.AuthUserChallenge);

        var pending = session.Advance(GoldenCapture.Hex(GoldenCapture.AuthUserRequest));
        Assert.Equal(Encoding.ASCII.GetBytes(GoldenCapture.AuthUserChallenge), pending.Reply);

        var done = session.Advance(GoldenCapture.Hex(GoldenCapture.AuthUserSendCode));

        Assert.Equal(QctekReply.FramedBinary(GoldenCapture.AuthUserPayload), done.Reply);
    }

    [Fact]
    public void 黃金流6的raw即時監看啟動先挑戰再回OK()
    {
        var session = new QctekSession(new TestContext(1), () => GoldenCapture.LiveStartChallenge);

        var pending = session.Advance(GoldenCapture.Hex(GoldenCapture.LiveStartRequest));

        // raw 命令的挑戰同樣是 raw：沒有 NEMO 標頭、沒有 NUL。
        Assert.Equal(34, pending.Consumed);
        Assert.Equal(Encoding.ASCII.GetBytes(GoldenCapture.LiveStartChallenge), pending.Reply);

        var done = session.Advance(GoldenCapture.Hex(GoldenCapture.LiveStartSendCode));

        Assert.Equal(GoldenCapture.Hex(GoldenCapture.LiveStartReply), done.Reply);
    }

    [Fact]
    public void 黃金流18的IO設定查詢不經挑戰直接回二進位()
    {
        // 這是抓包裡唯一「沒有挑戰」的查詢。把它一併擋下，QCC 會停在等挑戰，
        // 裝置看起來就是加入失敗——而其他查詢都成功，最難查的一種。
        var session = new QctekSession(new TestContext(1));

        var result = session.Advance(GoldenCapture.Hex(GoldenCapture.IoConfigRequest));

        Assert.False(session.AwaitingAuthorization);
        Assert.Equal(QctekReply.FramedBinary(GoldenCapture.IoConfigPayload), result.Reply);
    }

    [Fact]
    public void 黃金流16的raw警報訂閱回真機的兩個識別碼()
    {
        var session = new QctekSession(new TestContext(1));

        var result = session.Advance(GoldenCapture.Hex(GoldenCapture.AlarmRequest));

        Assert.False(result.Close);
        Assert.Equal(GoldenCapture.Hex(GoldenCapture.AlarmRequestReply), result.Reply);
    }

    [Fact]
    public void 黃金流24的警報存活查詢回ALIVE且吃掉payload內的NUL()
    {
        var session = new QctekSession(new TestContext(1));
        var request = GoldenCapture.Hex(GoldenCapture.AlarmIsAliveSerial0Request);

        var result = session.Advance(request);

        // 標頭宣告 34、payload 最後一個位元組就是命令的 NUL；要整個吃掉，
        // 否則下一個命令的解析會從一個孤立 NUL 開始。
        Assert.Equal(request.Length, result.Consumed);
        Assert.Equal(GoldenCapture.Hex(GoldenCapture.AlarmIsAliveSerial0Reply), result.Reply);
    }

    [Fact]
    public void 第二個序號一律回ALIVE而不是真機的NOT_ALIVE()
    {
        // 真機（stream 25）對第二個序號回 NOT ALIVE；HeliVMS 沒有「這個序號的訂閱已失效」
        // 的概念，一律接受。刻意不重現 NOT ALIVE：那會讓 QCC 認為訂閱掉了而重送
        // ALARM_REQUEST，形成沒有意義的重連迴圈。
        var session = new QctekSession(new TestContext(1));

        var result = session.Advance(GoldenCapture.Hex(GoldenCapture.AlarmIsAliveSerial1Request));

        Assert.Equal(QctekReply.FramedWithNul("ALIVE "), result.Reply);
        Assert.NotEqual(GoldenCapture.Hex(GoldenCapture.AlarmIsAliveSerial1ReplyOnDevice), result.Reply);
    }

    [Fact]
    public void 不存在的使用者即使digest形狀正確也會被拒絕()
    {
        // digest 本身無法驗證（QCTek 的專有轉換密碼不存在於 HeliVMS）；能驗的只有
        // 「帳號存在且可用」。這裡確保那一關真的在跑，而不是任何 32 hex 都放行。
        var session = new QctekSession(new TestContext(1), () => "challenge");
        var request = NemoMessage.EncodeText("GET_CONFIG_QCTEK intruder QCV8001_AUTH");

        session.Advance(request);
        var result = session.Advance(GoldenCapture.Hex(GoldenCapture.ConfigSendCode));

        Assert.Equal(QctekReply.Framed("Login Failed"), result.Reply);
        Assert.Equal(16 + "Login Failed".Length, result.Reply!.Length);
    }

    [Fact]
    public void 形狀不對的認證碼會被拒絕且不會執行原本的命令()
    {
        var session = new QctekSession(new TestContext("攝影機001"), () => "challenge");
        session.Advance(GoldenCapture.Hex(GoldenCapture.ConfigRequest));

        // 31 個 hex：長度差 1；QCC 真的送過壞掉的 digest 時，不能讓它變成「剛好通過」。
        var shortCode = Encoding.ASCII.GetBytes("QCV8001_AUTH_SENDCODE f1aa3ce87497593a803bb183079f455\0");
        var result = session.Advance(shortCode);

        Assert.Equal(QctekReply.Framed("Login Failed"), result.Reply);
        Assert.False(session.AwaitingAuthorization);

        // 沒有授權成功，下一個保護命令必須重新取得挑戰。
        var again = session.Advance(NemoMessage.EncodeText("GET_NCHANNEL"));
        Assert.Equal(QctekReply.Framed("OK 1 "), again.Reply);
    }

    [Fact]
    public void 沒有挑戰在前時多送的認證碼不會被當成命令()
    {
        var session = new QctekSession(new TestContext(1));
        var stray = Encoding.ASCII.GetBytes(
            "QCV8001_AUTH_SENDCODE 0123456789abcdef0123456789abcdef\0");

        var result = session.Advance(stray);

        Assert.Equal(stray.Length, result.Consumed);
        Assert.Null(result.Reply);
        Assert.False(session.AwaitingAuthorization);
    }

    [Fact]
    public void 半個訊框不回應也不耗用任何位元組()
    {
        var session = new QctekSession(new TestContext("攝影機001"), () => "challenge");
        var request = GoldenCapture.Hex(GoldenCapture.ConfigRequest);

        var partial = session.Advance(request.AsSpan(0, 8));

        Assert.Equal(0, partial.Consumed);
        Assert.Null(partial.Reply);

        // 呼叫端會把新讀到的位元組接到同一條緩衝後面；補齊後才解析。
        var complete = session.Advance(request);
        Assert.Equal(Encoding.ASCII.GetBytes("challenge"), complete.Reply);
    }

    [Fact]
    public void 連線開頭的孤立NUL會被跳過而不是關閉連線()
    {
        // 上一條命令若在框外留了 NUL，下一輪緩衝就會以 NUL 開頭。把它當成「非文字
        // 資料」會直接關線，QCC 只看到裝置斷線；跳過它則一切照常。
        var session = new QctekSession(new TestContext(1));
        var request = GoldenCapture.Hex(GoldenCapture.CheckServerVersionRequest);
        var framed = new byte[request.Length + 1];
        request.CopyTo(framed, 1);

        var result = session.Advance(framed);

        Assert.False(result.Close);
        Assert.Equal(framed.Length, result.Consumed);
        Assert.Equal(GoldenCapture.Hex(GoldenCapture.CheckServerVersionReply), result.Reply);
    }

    [Fact]
    public void 授權成功後同一條連線的後續查詢不再挑戰()
    {
        // QCC 實測每條命令一條短連線，但協定本身允許續用；授權狀態不該每條命令重問。
        var session = new QctekSession(new TestContext(36), () => GoldenCapture.AuthUserChallenge);
        session.Advance(GoldenCapture.Hex(GoldenCapture.AuthUserRequest));
        session.Advance(GoldenCapture.Hex(GoldenCapture.AuthUserSendCode));

        var result = session.Advance(GoldenCapture.Hex(GoldenCapture.IoConfigRequest));

        Assert.Equal(QctekReply.FramedBinary(GoldenCapture.IoConfigPayload), result.Reply);
    }

    [Fact]
    public void 挑戰途中來的新命令會丟掉過期的待辦而不是之後補送()
    {
        // 若保留待辦狀態，之後任何一條命令的認證碼都會拿去執行「舊的」設定查詢，
        // 回應與請求錯位：QCC 收到 312 bytes 卻以為那是頻道數，畫面直接壞掉。
        var session = new QctekSession(new TestContext("攝影機001"), () => "challenge");
        session.Advance(GoldenCapture.Hex(GoldenCapture.ConfigRequest));
        Assert.True(session.AwaitingAuthorization);

        var other = session.Advance(NemoMessage.EncodeText("GET_NCHANNEL"));

        Assert.Equal(QctekReply.Framed("OK 1 "), other.Reply);
        Assert.False(session.AwaitingAuthorization);

        var late = session.Advance(GoldenCapture.Hex(GoldenCapture.ConfigSendCode));
        Assert.Null(late.Reply);
    }

    [Fact]
    public void 無法辨識的資料會要求關閉連線()
    {
        var session = new QctekSession(new TestContext(1));

        // NEMO 標頭缺長度：不是可重新同步的串流。
        Assert.True(session.Advance(Encoding.ASCII.GetBytes("NEMO xxxxxxxxxxx")).Close);

        // 非文字的 raw 資料。
        Assert.True(session.Advance(new byte[] { 0x01, 0x02, 0x03, 0x04 }).Close);
    }

    // ─────────────────────── 事件查詢（SEARCH_EVENT）分頁 ───────────────────────

    /// <summary>
    /// 組一段 <c>SEARCH_EVENT</c> 請求框：NEMO 16-byte 命令名＋u32 本體長度＋u32 頁寬＋
    /// 336-byte 本體（兩個 <c>struct tm</c>、型別、頻道）。
    /// </summary>
    private static byte[] SearchEventFrame(int pageSize = 10, int type = 0, int channel = -1)
    {
        var payload = new byte[0x18 + QctekEventSearchRequest.BodyLength];
        Encoding.ASCII.GetBytes(QctekEventSearchRequest.CommandName).CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0x10), QctekEventSearchRequest.BodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x14), (uint)pageSize);

        var body = payload.AsSpan(0x18);
        WriteTm(body, 0x00, new DateTime(2026, 11, 10, 8, 0, 0));
        WriteTm(body, 0x24, new DateTime(2026, 11, 10, 18, 30, 0));
        BinaryPrimitives.WriteInt32LittleEndian(body[0x48..], type);
        BinaryPrimitives.WriteInt16LittleEndian(body[0x4c..], (short)channel);

        return NemoMessage.Encode(payload);
    }

    private static void WriteTm(Span<byte> body, int offset, DateTime at)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], at.Second);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 4)..], at.Minute);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 8)..], at.Hour);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 12)..], at.Day);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 16)..], at.Month - 1);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 20)..], at.Year - 1900);
    }

    /// <summary>照 QCC 的讀法把一頁拆回來：標頭總筆數＋每筆描述。</summary>
    private static (int Total, List<string> Texts) ReadEventPage(byte[] reply)
    {
        Assert.True(NemoMessage.TryDecode(reply, out var payload, out _));

        var total = int.Parse(
            Encoding.ASCII.GetString(payload, 3, 13).TrimEnd('\0'),
            CultureInfo.InvariantCulture);

        var texts = new List<string>();
        var offset = QctekEventPayloads.HeaderLength;
        while (offset + QctekEventPayloads.RecordFixedLength <= payload.Length)
        {
            var len1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 0x30));
            var len2 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 0x32));
            var len3 = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 0x34));

            var cursor = offset + QctekEventPayloads.RecordFixedLength + len1;
            if (len2 >= 3)
            {
                texts.Add(Encoding.Unicode.GetString(payload, cursor, len2 - 2));
            }

            offset = cursor + len2 + (int)len3;
        }

        return (total, texts);
    }

    /// <summary>
    /// 有 <paramref name="total"/> 筆事件的裝置；可指定「非第一頁」回報的總數，用來驗證
    /// 總數以第一頁為準。
    /// </summary>
    private static TestContext ContextWithEvents(int total, Func<int, int>? totalForOffset = null)
    {
        var entries = Enumerable.Range(0, total)
            .Select(i => new QctekEventEntry(
                0,
                QctekEventTypes.Event,
                new DateTime(2026, 11, 10, 9, 0, 0).AddMinutes(i),
                $"事件{i:000}"))
            .ToArray();

        return new TestContext(3)
        {
            EventSearch = (_, offset, limit) => new QctekEventPage(
                entries.Skip(offset).Take(limit).ToArray(),
                totalForOffset?.Invoke(offset) ?? total),
        };
    }

    [Fact]
    public void 事件查詢直接回第一頁且不經認證挑戰()
    {
        // QC_RM_Event::SearchEvent 直接送框，不經 QCV8001_AUTH。把它加進
        // RequiresAuthorization 會讓 QCC 停在等挑戰，事件視窗永遠是空的。
        var context = ContextWithEvents(total: 25);
        var session = new QctekSession(context);
        var frame = SearchEventFrame(pageSize: 10);

        var result = session.Advance(frame);

        Assert.False(session.AwaitingAuthorization);
        Assert.False(result.Close);
        Assert.Equal(frame.Length, result.Consumed);
        Assert.NotNull(result.Reply);

        var (total, texts) = ReadEventPage(result.Reply!);
        Assert.Equal(25, total);
        Assert.Equal(10, texts.Count);
        Assert.Equal("事件000", texts[0]);

        // 本體要真的解析出來（下達給裝置的是本地時間、型別 0＝全部、頻道 −1＝全部）。
        Assert.NotNull(context.LastEventQuery);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), context.LastEventQuery!.FromLocal);
        Assert.Equal(0, context.LastEventQuery.TypeCode);
        Assert.Equal(-1, context.LastEventQuery.ChannelIndex);
        Assert.Equal(10, context.LastEventQuery.PageSize);
    }

    [Fact]
    public void 四byte的NEXT與PREV會在同一條連線上換頁()
    {
        // QCC 換頁送的是 4 個位元組、沒有結尾 NUL（DAT_18001cf10 的 "NEXT"）。
        // 一般 raw 命令要等 NUL，照那樣解析永遠等不到，QCC 10 秒後放棄、清單停在第一頁。
        var session = new QctekSession(ContextWithEvents(total: 25));
        session.Advance(SearchEventFrame(pageSize: 10));

        var next = session.Advance("NEXT"u8);
        Assert.Equal(4, next.Consumed);
        var page1 = ReadEventPage(next.Reply!);
        Assert.Equal(25, page1.Total);
        Assert.Equal("事件010", page1.Texts[0]);

        var prev = session.Advance("PREV"u8);
        Assert.Equal(4, prev.Consumed);
        var page0 = ReadEventPage(prev.Reply!);
        Assert.Equal("事件000", page0.Texts[0]);
    }

    [Fact]
    public void 最後一頁再按下一頁會停在最後一頁而不是回空頁()
    {
        // QCC 只在 isLastPage()==false 時送 NEXT，但頁數是它自己用總數算的；夾在
        // 最後一頁是安全的，回空頁反而會讓它把清單清掉。
        var session = new QctekSession(ContextWithEvents(total: 25));
        session.Advance(SearchEventFrame(pageSize: 10));
        session.Advance("NEXT"u8);
        var last = ReadEventPage(session.Advance("NEXT"u8).Reply!);
        Assert.Equal(5, last.Texts.Count);

        var again = ReadEventPage(session.Advance("NEXT"u8).Reply!);
        Assert.Equal(last.Texts, again.Texts);
    }

    [Fact]
    public void 沒有查詢在前時NEXT回乾淨的空頁而不是卡住()
    {
        // 對端若重用連線狀態（或重開後直接送 NEXT），等一個永遠不會來的查詢就是
        // 10 秒的空白視窗。回「OK 0」讓它立刻畫出空清單。
        var session = new QctekSession(new TestContext(1));

        var result = session.Advance("NEXT"u8);

        Assert.Equal(4, result.Consumed);
        var (total, texts) = ReadEventPage(result.Reply!);
        Assert.Equal(0, total);
        Assert.Empty(texts);
    }

    [Fact]
    public void 後續頁的總筆數沿用第一頁()
    {
        // 期間若有新事件，第二頁回報的總數會不同。QCC 用第一頁的總數算頁數，
        // 兩個數字不一致時翻頁會停在錯的位置。
        var session = new QctekSession(
            ContextWithEvents(total: 25, totalForOffset: offset => offset == 0 ? 25 : 40));
        var first = ReadEventPage(session.Advance(SearchEventFrame(pageSize: 10)).Reply!);
        Assert.Equal(25, first.Total);

        var (total, _) = ReadEventPage(session.Advance("NEXT"u8).Reply!);
        Assert.Equal(25, total);
    }

    [Fact]
    public void 讀不懂的查詢本體回零筆而不是替對端猜時間範圍()
    {
        // 同匯出時刻的教訓：默默放寬成整天比報錯更難發現。這裡送頁寬 0（會除以零）
        // 應該得到乾淨的「OK 0」，而且完全不去查資料庫。
        var context = ContextWithEvents(total: 25);
        var session = new QctekSession(context);

        var result = session.Advance(SearchEventFrame(pageSize: 0));

        var (total, texts) = ReadEventPage(result.Reply!);
        Assert.Equal(0, total);
        Assert.Empty(texts);
        Assert.Null(context.LastEventQuery);
    }

    [Fact]
    public void 事件查詢的框不會被當成等待結尾NUL的殘缺命令()
    {
        // 命令名後面就是二進位本體。若沿用舊的「整段解 UTF-8 再削尾端 NUL」，命令字串
        // 會混進本體，Verb 就不再等於 SEARCH_EVENT，這個框會被當成未知命令回空。
        var session = new QctekSession(ContextWithEvents(total: 3));
        var frame = SearchEventFrame(pageSize: 10);

        var half = session.Advance(frame.AsSpan(0, frame.Length / 2));
        Assert.Equal(0, half.Consumed);
        Assert.Null(half.Reply);

        var result = session.Advance(frame);
        Assert.Equal(frame.Length, result.Consumed);
        Assert.Equal(QctekEventSearchRequest.CommandName, result.Command);
        Assert.Equal(3, ReadEventPage(result.Reply!).Total);
    }

    [Fact]
    public void 分頁命令後面的補零不會變成下一條命令()
    {
        // QCC 的常數是 8 bytes（"NEXT\0\0\0\0"）。真的送 8 bytes 時多出來的 NUL 必須
        // 被吃掉，否則下一次解析會從資料中間開始。
        var session = new QctekSession(ContextWithEvents(total: 25));
        session.Advance(SearchEventFrame(pageSize: 10));

        session.Advance("NEXT\0\0\0\0"u8);

        var stray = session.Advance(new byte[] { 0, 0, 0, 0 });
        Assert.False(stray.Close);
        Assert.Equal(4, stray.Consumed);
        Assert.Null(stray.Reply);
    }
}
