using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// <c>SEARCH_EVENT</c> 請求本體（<c>__EVENT_SEARCH_PARAM</c>）的解析，以及 HeliVMS 事件型別
/// 對到 CMS 型別碼的規則。
/// </summary>
/// <remarks>
/// 本體位移來自 <c>QC_RM_Alarm.dll</c> 的送出端與 <c>QC_Event::SearchEvent</c> 的接收端：
/// 本體就是結構去掉前面 4 bytes，所以本體 <c>+0x00</c> 是起始 <c>tm</c>、<c>+0x24</c> 是
/// 結束 <c>tm</c>、<c>+0x48</c> 是型別、<c>+0x4c</c> 是頻道（i16，負值＝全部）。
/// </remarks>
public sealed class QctekEventSearchRequestTests
{
    private static readonly DateTime From = new(2026, 11, 10, 8, 0, 0);
    private static readonly DateTime To = new(2026, 11, 10, 18, 30, 15);

    /// <summary>組一段完整的 NEMO payload（16-byte 命令名＋長度＋頁寬＋336-byte 本體）。</summary>
    private static byte[] Payload(
        DateTime? from = null,
        DateTime? to = null,
        int type = 0,
        int channel = -1,
        int pageSize = 100,
        bool includeBody = true)
    {
        var payload = new byte[0x18 + QctekEventSearchRequest.BodyLength];

        Encoding.ASCII.GetBytes(QctekEventSearchRequest.CommandName).CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10), QctekEventSearchRequest.BodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x14), (uint)pageSize);

        if (!includeBody)
        {
            // 只留到頻道欄位之前：模擬一個「本體被截斷」的請求。
            return payload[..(0x18 + 0x48)];
        }

        var body = payload.AsSpan(0x18);
        WriteTm(body, 0x00, from ?? From);
        WriteTm(body, 0x24, to ?? To);
        BinaryPrimitives.WriteInt32LittleEndian(body[0x48..], type);
        BinaryPrimitives.WriteInt16LittleEndian(body[0x4c..], (short)channel);

        return payload;
    }

    /// <summary>照 <c>struct tm</c> 寫：tm_year 距 1900、tm_mon 0-based。</summary>
    private static void WriteTm(Span<byte> body, int offset, DateTime at)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], at.Second);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 4)..], at.Minute);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 8)..], at.Hour);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 12)..], at.Day);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 16)..], at.Month - 1);
        BinaryPrimitives.WriteInt32LittleEndian(body[(offset + 20)..], at.Year - 1900);
    }

    [Fact]
    public void 本體的時間型別頻道與頁寬都會解析出來()
    {
        var query = QctekEventSearchRequest.TryParse(
            Payload(from: From, to: To, type: QctekEventTypes.Alarm, channel: 3, pageSize: 50));

        Assert.NotNull(query);
        Assert.Equal(From, query.FromLocal);
        Assert.Equal(To, query.ToLocal);
        Assert.Equal(QctekEventTypes.Alarm, query.TypeCode);
        Assert.Equal(3, query.ChannelIndex);
        Assert.Equal(50, query.PageSize);
    }

    [Fact]
    public void 頻道負值代表全部頻道()
    {
        var query = QctekEventSearchRequest.TryParse(Payload(channel: -1));

        Assert.NotNull(query);
        Assert.Equal(-1, query.ChannelIndex);
    }

    [Fact]
    public void 月份超出範圍的時刻會讓整筆查詢被拒絕()
    {
        // 默默改成 12 月或放寬成整天，是匯出精靈上吃過的虧：操作員要 5 秒卻拿到
        // 50 分鐘的資料。寧可回 0 筆。
        var payload = Payload();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x18 + 0x10), 99);

        Assert.Null(QctekEventSearchRequest.TryParse(payload));
    }

    [Fact]
    public void 欄位合法但日期不存在的時刻會讓查詢被拒絕而不是拋例外()
    {
        // 2 月 31 日：每個欄位都在範圍內，DateTime 建構子卻會丟例外。例外會把整條
        // NEMO 連線帶走，QCC 只看到裝置斷線。
        var payload = Payload();
        var body = payload.AsSpan(0x18);
        BinaryPrimitives.WriteInt32LittleEndian(body[(0x00 + 12)..], 31);
        BinaryPrimitives.WriteInt32LittleEndian(body[(0x00 + 16)..], 1); // 2 月

        Assert.Null(QctekEventSearchRequest.TryParse(payload));
    }

    [Fact]
    public void 每頁筆數為零或超過上限時查詢會被拒絕()
    {
        // 0 頁寬會讓「總數 / 頁寬」除以零；0x7fffffff 則是一次組出整年份的記憶體炸彈。
        Assert.Null(QctekEventSearchRequest.TryParse(Payload(pageSize: 0)));
        Assert.Null(QctekEventSearchRequest.TryParse(Payload(pageSize: QctekEventSearchRequest.MaxPageSize + 1)));
        Assert.NotNull(QctekEventSearchRequest.TryParse(Payload(pageSize: QctekEventSearchRequest.MaxPageSize)));
    }

    [Fact]
    public void 命令名不符或本體被截斷時回空而不是猜()
    {
        var payload = Payload();
        payload[0] = (byte)'X';
        Assert.Null(QctekEventSearchRequest.TryParse(payload));

        Assert.Null(QctekEventSearchRequest.TryParse(Payload(includeBody: false)));
    }

    [Fact]
    public void 全部事件會接受一到五型別但不含POS標籤()
    {
        // QCC 的「全部事件」送型別 0。若把 AI 事件對到 205（Object）看起來更像，
        // 但 205 不屬於事件日誌，型別 0 的查詢就整批漏掉它們。
        Assert.True(QctekEventTypes.Matches(0, QctekEventTypes.System));
        Assert.True(QctekEventTypes.Matches(0, QctekEventTypes.Event));
        Assert.True(QctekEventTypes.Matches(0, QctekEventTypes.Alarm));
        Assert.False(QctekEventTypes.Matches(0, 200));
        Assert.False(QctekEventTypes.Matches(0, 205));
        Assert.False(QctekEventTypes.Matches(0, 0));
    }

    [Fact]
    public void 指定型別只收那一個型別()
    {
        Assert.True(QctekEventTypes.Matches(QctekEventTypes.Alarm, QctekEventTypes.Alarm));
        Assert.False(QctekEventTypes.Matches(QctekEventTypes.Alarm, QctekEventTypes.Event));
        Assert.True(QctekEventTypes.Matches(205, 205));
        Assert.False(QctekEventTypes.Matches(205, QctekEventTypes.Alarm));
    }

    [Theory]
    [InlineData("motion", QctekEventTypes.Event)]
    [InlineData("ai_person", QctekEventTypes.Event)]
    [InlineData("line_cross", QctekEventTypes.Event)]
    [InlineData("intrusion", QctekEventTypes.Event)]
    [InlineData("offline", QctekEventTypes.Network)]
    [InlineData("online", QctekEventTypes.Network)]
    [InlineData("io_input", QctekEventTypes.Alarm)]
    [InlineData("io_trigger", QctekEventTypes.Alarm)]
    [InlineData("schedule_start", QctekEventTypes.Operation)]
    [InlineData("", QctekEventTypes.System)]
    [InlineData("unknown_thing", QctekEventTypes.System)]
    public void 事件字串對到CMS認得的型別碼(string eventType, int expected)
    {
        Assert.Equal(expected, QctekEventTypes.FromAlarmEventType(eventType));
    }

    [Fact]
    public void 對映結果一定落在CMS認得的事件日誌型別裡()
    {
        // 落在 200–205 的話 QCC 會用 POS 標籤那條路徑解析，事件清單變空白。
        string[] all =
        [
            "motion", "tamper", "smart_alert", "analytics", "audio", "offline", "online",
            "network", "io_input", "io_trigger", "schedule_start", "manual", "export",
            "snapshot", "license_limit", "ai_person", "line_cross", "",
        ];

        foreach (var name in all)
        {
            var type = QctekEventTypes.FromAlarmEventType(name);
            Assert.True(QctekEventTypes.IsEventLog(type), $"{name} → {type} 不在事件日誌型別內");
            Assert.False(QctekEventTypes.IsPosTag(type));
        }
    }
}
