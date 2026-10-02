using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// RTP 標頭解析的契約測試（R244／RFC 3550 §5.1）。
/// <para>
/// 這條路徑上的每個位元都會變成瀏覽器裡的實際畫面：序號錯了會花屏、時間戳錯了會
/// 動不了、變長標頭長度算錯會把隨機資料當成 H.264 送出去。因此負向案例比正向更重要。
/// </para>
/// </summary>
public class RtpHeaderTests
{
    /// <summary>組出標準的 RTP 固定標頭 + payload。</summary>
    private static byte[] Packet(
        ushort seq = 0x1234,
        uint timestamp = 0xDEADBEEF,
        bool marker = true,
        byte payloadType = 96,
        uint ssrc = 0xCAFEBABE,
        int payloadLength = 5)
    {
        var packet = new byte[RtpHeader.FixedHeaderSize + payloadLength];
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = (byte)((marker ? 0x80 : 0x00) | (payloadType & 0x7F));
        packet[2] = (byte)(seq >> 8);
        packet[3] = (byte)seq;
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)timestamp;
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;

        for (var i = 0; i < payloadLength; i++)
        {
            packet[RtpHeader.FixedHeaderSize + i] = (byte)(0xA0 + i);
        }

        return packet;
    }

    [Fact]
    public void 解析出序號時間戳與標記位元()
    {
        var packet = Packet(seq: 0xBEEF, timestamp: 0x11223344, marker: true, payloadType: 96, ssrc: 0x0BADF00D);

        Assert.True(RtpHeader.TryParse(packet, out var header));
        Assert.Equal(0xBEEF, header.SequenceNumber);
        Assert.Equal(0x11223344u, header.Timestamp);
        Assert.True(header.MarkerBit);
        Assert.Equal(96, header.PayloadType);
        Assert.Equal(0x0BADF00Du, header.Ssrc);
        Assert.Equal(RtpHeader.FixedHeaderSize, header.PayloadOffset);
        Assert.Equal(5, header.PayloadLength);
    }

    [Fact]
    public void 序號零與時間戳零是合法值不該被當成無效()
    {
        // 迴歸防護：把「0」誤判為「沒資料」是最容易犯也最難查的解析錯誤。
        var packet = Packet(seq: 0, timestamp: 0, marker: false, payloadType: 0);

        Assert.True(RtpHeader.TryParse(packet, out var header));
        Assert.Equal(0, header.SequenceNumber);
        Assert.Equal(0u, header.Timestamp);
        Assert.False(header.MarkerBit);
    }

    [Fact]
    public void 版本不是二就拒絕()
    {
        var packet = Packet();
        packet[0] = 0x40; // V=1

        Assert.False(RtpHeader.TryParse(packet, out _));
    }

    [Fact]
    public void 短於固定標頭一律拒絕()
    {
        // 逐一確認邊界：11 bytes 不行，12 bytes 可以。
        Assert.False(RtpHeader.TryParse(new byte[11], out _));
        Assert.True(RtpHeader.TryParse(Packet(payloadLength: 0), out _));
    }

    [Fact]
    public void 空封包必須被拒絕()
    {
        // 邊界防護：0 長度不是「合法的空 RTP」，而是根本沒有標頭。
        Assert.False(RtpHeader.TryParse([], out _));
    }

    [Fact]
    public void 跳過CSRC清單()
    {
        // CC=2 → 12 + 2*4 = 20 bytes 才是 payload 起點。
        var packet = new byte[20 + 4];
        packet[0] = 0x80 | 0x02;
        packet[1] = 0x80 | 96;

        Assert.True(RtpHeader.TryParse(packet, out var header));
        Assert.Equal(20, header.PayloadOffset);
        Assert.Equal(4, header.PayloadLength);
    }

    [Fact]
    public void 跳過延伸標頭()
    {
        // X=1，且 extensionWords=1 → 12 + 4 + 4 = 20 bytes。
        var packet = new byte[20 + 3];
        packet[0] = 0x80 | 0x10;
        packet[1] = 0x80 | 96;
        packet[14] = 0x00;
        packet[15] = 0x01; // 1 word

        Assert.True(RtpHeader.TryParse(packet, out var header));
        Assert.Equal(20, header.PayloadOffset);
        Assert.Equal(3, header.PayloadLength);
    }

    [Fact]
    public void 延伸標頭長度超過封包時拒絕()
    {
        // 宣告 100 words 卻只有 1 word 的資料：必須拒絕，否則 payload 邊界是亂猜的。
        var packet = new byte[20];
        packet[0] = 0x80 | 0x10;
        packet[1] = 96;
        packet[14] = 0x00;
        packet[15] = 100;

        Assert.False(RtpHeader.TryParse(packet, out _));
    }

    [Fact]
    public void 依結尾padding欄位扣掉padding位元組()
    {
        var packet = new byte[RtpHeader.FixedHeaderSize + 10];
        packet[0] = 0x80 | 0x20; // P=1
        packet[1] = 96;
        packet[^1] = 4; // 最後 4 bytes 是 padding

        Assert.True(RtpHeader.TryParse(packet, out var header));
        Assert.Equal(6, header.PayloadLength);
    }

    [Fact]
    public void padding長度自稱大於封包時拒絕()
    {
        var packet = new byte[RtpHeader.FixedHeaderSize + 4];
        packet[0] = 0x80 | 0x20;
        packet[1] = 96;
        packet[^1] = 200;

        Assert.False(RtpHeader.TryParse(packet, out _));
    }

    [Fact]
    public void padding長度為零時拒絕()
    {
        var packet = new byte[RtpHeader.FixedHeaderSize + 4];
        packet[0] = 0x80 | 0x20;
        packet[1] = 96;
        packet[^1] = 0;

        Assert.False(RtpHeader.TryParse(packet, out _));
    }

    [Fact]
    public void 讀出來的payload與原始位元組完全相同()
    {
        // 這是「不解碼」的實際保證：payload 必須一位元不差地通過。
        var packet = Packet(payloadLength: 7);
        Assert.True(RtpHeader.TryParse(packet, out var header));

        var payload = packet.AsSpan(header.PayloadOffset, header.PayloadLength).ToArray();

        Assert.Equal([0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6], payload);
    }
}