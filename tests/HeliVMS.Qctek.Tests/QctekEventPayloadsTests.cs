using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// <c>SEARCH_EVENT</c> 回應版型：QCC 端 <c>QC_RM_Event::ResponsePageEvent</c> 逐欄位讀取的
/// 那幾個位移。
/// <para>
/// 位移錯一格，事件清單就是空白或亂碼，而且兩邊都沒有錯誤訊息可看——所以這裡直接對
/// 位元組斷言，不經過任何中介型別。
/// </para>
/// </summary>
public sealed class QctekEventPayloadsTests
{
    private static readonly DateTime Clock = new(2026, 11, 10, 18, 25, 32, DateTimeKind.Local);

    private static QctekEventEntry Entry(
        int channel = 2,
        int type = QctekEventTypes.Event,
        string text = "攝影機003 動作",
        DateTime? at = null)
        => new(channel, type, at ?? Clock, text);

    /// <summary>照 QCC 的讀法走一遍：16-byte 標頭後是 0x38 固定欄位＋可變資料。</summary>
    private static (string Header, List<(short Channel, short Type, string Clock, string Text)> Records)
        Read(byte[] payload)
    {
        var header = Encoding.ASCII.GetString(payload, 0, QctekEventPayloads.HeaderLength).TrimEnd('\0');
        var records = new List<(short, short, string, string)>();

        var offset = QctekEventPayloads.HeaderLength;
        while (offset + QctekEventPayloads.RecordFixedLength <= payload.Length)
        {
            var channel = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset));
            var type = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset + 0x02));
            var clock = Encoding.Unicode.GetString(payload, offset + 0x04, 0x2c).TrimEnd('\0');

            var len1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 0x30));
            var len2 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 0x32));
            var len3 = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 0x34));

            var cursor = offset + QctekEventPayloads.RecordFixedLength;
            cursor += len1;
            var text = len2 < 3 ? string.Empty : Encoding.Unicode.GetString(payload, cursor, len2);
            cursor += len2;
            cursor += (int)len3;

            records.Add((channel, type, clock, text.TrimEnd('\0')));
            offset = cursor;
        }

        return (header, records);
    }

    [Fact]
    public void 零筆事件只回十六位元組且整框三十二位元組()
    {
        // QCC 的 ResponsePageEvent 開頭就是 if (length < 0x21) return false：少於 0x21
        // 才是「乾淨的空清單」。多送一筆假紀錄會被畫成一筆不存在的事件。
        var payload = QctekEventPayloads.Page(QctekEventPage.Empty);

        Assert.Equal(0x10, payload.Length);
        Assert.Equal("OK 0", Encoding.ASCII.GetString(payload).TrimEnd('\0'));
        Assert.Equal(0x20, NemoMessage.Encode(payload).Length);
    }

    [Fact]
    public void 標頭寫的是符合條件的總筆數而不是本頁筆數()
    {
        // QCC 用 ceil(總數 / 每頁筆數) 算頁數。寫成本頁筆數會讓它以為只有一頁，
        // 第二頁之後永遠不會被要求——操作員看到的就是「只查到前 N 筆」。
        var page = new QctekEventPage([Entry(), Entry()], Total: 137);

        var (header, _) = Read(QctekEventPayloads.Page(page));

        Assert.Equal("OK 137", header);
    }

    [Fact]
    public void 紀錄的頻道型別時間與描述落在QCC讀取的位移上()
    {
        var page = new QctekEventPage([Entry()], Total: 1);

        var (_, records) = Read(QctekEventPayloads.Page(page));

        var record = Assert.Single(records);
        Assert.Equal(2, record.Channel);
        Assert.Equal(QctekEventTypes.Event, record.Type);

        // 時間字串是本地時間、寬字元：真機 ConvertLog 先 qc_UtcToLocal 才格式化。
        Assert.Equal(
            Clock.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
            record.Clock);
        Assert.Equal("攝影機003 動作", record.Text);
    }

    [Fact]
    public void 描述長度欄位含結束NUL且不超過五百一十二位元組()
    {
        // QCC 以 512 為界複製寬字元描述；寫超過就是讀到別人的記憶體。
        var page = new QctekEventPage([Entry(text: new string('監', 400))], Total: 1);

        var payload = QctekEventPayloads.Page(page);
        var len2 = BinaryPrimitives.ReadUInt16LittleEndian(
            payload.AsSpan(QctekEventPayloads.HeaderLength + 0x32));

        Assert.InRange(len2, 3, QctekEventPayloads.MaxTextBytes);

        // 含結束 NUL：最後兩個位元組必須是 0。
        var end = QctekEventPayloads.HeaderLength + QctekEventPayloads.RecordFixedLength + len2;
        Assert.Equal(0, payload[end - 1]);
        Assert.Equal(0, payload[end - 2]);
    }

    [Fact]
    public void 描述過長時截斷在代理對之前而不是留下半個字()
    {
        // 半個代理對解出來是 U+FFFD，QCC 清單上就是一個問號——看起來像編碼壞掉，
        // 其實只是截斷位置錯一個字元。
        var text = new string('a', QctekEventPayloads.MaxTextChars - 1) + "😀" + "尾巴";

        var page = new QctekEventPage([Entry(text: text)], Total: 1);
        var (_, records) = Read(QctekEventPayloads.Page(page));

        var shown = Assert.Single(records).Text;
        Assert.DoesNotContain('\uFFFD', shown);
        Assert.False(char.IsHighSurrogate(shown[^1]));
    }

    [Fact]
    public void 多筆紀錄依序緊接且下一筆的起點由本筆長度決定()
    {
        // QCC 是一筆一筆往前推（iVar6 = iVar6 + 0x38 + len1 + len2），所以每一筆的
        // 可變資料長度必須精確；少算或多算一個位元組，之後每一筆都會被讀到錯的位置。
        var page = new QctekEventPage(
            [Entry(text: "短"), Entry(text: new string('長', 40)), Entry(text: "尾")],
            Total: 3);

        var (_, records) = Read(QctekEventPayloads.Page(page));

        Assert.Equal(3, records.Count);
        Assert.Equal("短", records[0].Text);
        Assert.Equal(new string('長', 40), records[1].Text);
        Assert.Equal("尾", records[2].Text);
    }

    [Fact]
    public void 空描述不會讓長度欄位指向零長度區塊()
    {
        // len2 < 3 被 QCC 當成空字串，但如果我們送 len2 = 2（只有 NUL）它仍會複製 2 bytes
        // 到自己的緩衝；送 0 才是「沒有描述」。
        var page = new QctekEventPage([Entry(text: string.Empty)], Total: 1);

        var payload = QctekEventPayloads.Page(page);
        var recordAt = QctekEventPayloads.HeaderLength;

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(recordAt + 0x30)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(recordAt + 0x32)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(recordAt + 0x34)));
        Assert.Equal(recordAt + QctekEventPayloads.RecordFixedLength, payload.Length);
    }

    [Fact]
    public void 總筆數小於本頁筆數時以本頁筆數為準()
    {
        // 呼叫端忘了帶總數（Total 預設 0）時不能讓 QCC 算出 0 頁而丟掉整頁資料。
        var page = new QctekEventPage([Entry(), Entry()], Total: 0);

        var (header, records) = Read(QctekEventPayloads.Page(page));

        Assert.Equal("OK 2", header);
        Assert.Equal(2, records.Count);
    }
}
