using System.Buffers.Binary;
using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// FRAM 封包的線上位元組：真機 <c>FUN_18001cb50</c>（發送）與客戶端
/// <c>readQCV8001frame</c>（解析）雙向核對的欄位表，這裡逐位元組釘住。
/// <para>
/// 這些欄位錯任何一個，QCC 的症狀都只是「連著但沒畫面」：frame 長度算錯會讓
/// 客戶端把下一個 FRAM 的標頭吃進 media，<c>Put</c> 直接丟包；時戳錯位會讓
/// DecProc 的調速把非關鍵幀整包丟棄。
/// </para>
/// </summary>
public class QctekLivePacketsTests
{
    [Fact]
    public void 資料幀的標頭與媒體標頭逐欄位與真機一致()
    {
        byte[] block1 = [0x00, 0x00, 0x00, 0x01, 0x67, 0x42];
        byte[] block2 = [0x00, 0x00, 0x00, 0x01, 0x68, 0xce];
        byte[] video = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84];

        var packet = QctekLivePackets.Data(7, block1, block2, video, unixSeconds: 1_799_000_000, microSeconds: 123_456);

        // 幀總長 = 8 標頭 + 0x38 media + block1 + block2 + video。
        Assert.Equal(8 + 0x38 + block1.Length + block2.Length + video.Length, packet.Length);

        // FRAM 標頭："FRAM" + u16 channel + u16 status（0 = 資料幀）。
        Assert.Equal("FRAM"u8.ToArray(), packet[..4]);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)));

        var media = packet.AsSpan(8, 0x38);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(media[0x00..]));  // type ＝ 視訊
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(media[0x04..]));  // codec ＝ H.264
        Assert.Equal(video.Length, BinaryPrimitives.ReadInt32LittleEndian(media[0x08..]));
        Assert.Equal(block1.Length, BinaryPrimitives.ReadInt32LittleEndian(media[0x0c..]));
        Assert.Equal(block2.Length, BinaryPrimitives.ReadInt32LittleEndian(media[0x10..]));
        // qc_timeval 的線上順序：+0x14 微秒、+0x18 Unix 秒（客戶端 DecProc 對牆鐘調速用）。
        Assert.Equal(123_456u, BinaryPrimitives.ReadUInt32LittleEndian(media[0x14..]));
        Assert.Equal(1_799_000_000L, BinaryPrimitives.ReadInt64LittleEndian(media[0x18..]));

        // payload 順序固定 block1 → block2 → video。
        var payload = packet.AsSpan(8 + 0x38);
        Assert.Equal(block1, payload[..block1.Length].ToArray());
        Assert.Equal(block2, payload[block1.Length..(block1.Length + block2.Length)].ToArray());
        Assert.Equal(video, payload[(block1.Length + block2.Length)..].ToArray());
    }

    [Fact]
    public void 指標欄位一律為零而不是把緩衝位址或FILETIME寫上線()
    {
        // 真機在關鍵影格上把**自己行程裡**的緩衝位址抄進 +0x20/+0x28/+0x30
        // （黃金抓包 114/114 個關鍵影格：+0x30 − +0x28 恰等於 block1 長度 118，
        // 三個高位 u32 全等）——那些位址跨機器沒有意義，客戶端以三個 size 重算。
        // 舊版把高位 0x172 讀成 FILETIME 的日期欄位，於是每一幀都多寫了真機沒有
        // 的一份時間；這裡釘住「一律 0」，也順便擋住「把裝置指標照抄上線」。
        byte[] block1 = [0x00, 0x00, 0x00, 0x01, 0x67, 0x42];
        byte[] block2 = [0x00, 0x00, 0x00, 0x01, 0x68, 0xce];
        byte[] video = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88];

        var packet = QctekLivePackets.Data(0, block1, block2, video, 1_799_000_000, 123_456);
        var media = packet.AsSpan(8, 0x38);

        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(media[0x20..]));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(media[0x28..]));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(media[0x30..]));
    }

    [Fact]
    public void 狀態幀只有八個位元組()
    {
        var packet = QctekLivePackets.Status(3, QctekLivePackets.StatusNoFrameYet);

        Assert.Equal(8, packet.Length);
        Assert.Equal("FRAM"u8.ToArray(), packet[..4]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)));
        Assert.Equal(0x8001, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)));
    }

    [Fact]
    public void 型別二的幀是音訊而不是參數幀()
    {
        // 客戶端 dvrProxy_live::GetFrameQCTekAudio 明寫 *(u32*)media = 2、codec 取
        // +0x20（實測 200）、大小取 +8；真機 906 個 type=2 幀的 payload 全是 1000
        // bytes，整段 180 秒只有 0x7e/0xff/0xfe（G.711 µ-law 的近靜音）。把這一幀
        // 當成「叫醒影像的握手」會讓下一個人把它寫成 codec=1／b1=SPS，客戶端就會
        // 拿 SPS 去解音訊。
        var frame = QctekLivePackets.AudioFrame(5, unixSeconds: 1_799_000_000, microSeconds: 525_000);
        var media = frame.AsSpan(8, 0x38);

        Assert.Equal(8 + 0x38 + 1000, frame.Length);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(media[0x00..]));    // type ＝ 音訊
        Assert.Equal(200, BinaryPrimitives.ReadInt32LittleEndian(media[0x04..]));  // codec ＝ µ-law
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(media[0x08..])); // payload 長度
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(media[0x0c..]));    // block1（音訊沒有 SPS）
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(media[0x10..]));    // block2
        Assert.Equal(525_000u, BinaryPrimitives.ReadUInt32LittleEndian(media[0x14..]));
        Assert.Equal(1_799_000_000L, BinaryPrimitives.ReadInt64LittleEndian(media[0x18..]));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(media[0x20..]));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(media[0x28..]));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(media[0x30..]));

        // payload 是照抄真機的近靜音 µ-law 樣本：只會出現 0x7e/0xff/0xfe。
        var payload = frame[(8 + 0x38)..];
        Assert.Equal(1000, payload.Length);
        Assert.All(payload, b => Assert.Contains(b, new byte[] { 0x7e, 0xff, 0xfe }));
        Assert.Equal(QctekLivePackets.SilentAudioPayload.ToArray(), payload);
    }

    [Fact]
    public void 時戳的微秒與秒欄位各自獨立不會互相蓋寫()
    {
        // 時戳蓋章的兩個欄位彼此獨立：微秒在 +0x14（4 bytes），秒在 +0x18（8 bytes）。
        // 一個錯誤的實作會把秒當 4 bytes 寫（溢出後蓋掉微秒欄位），這裡用大值抓。
        var packet = QctekLivePackets.Data(0, [], [], [0xAA], unixSeconds: 1_812_345_678, microSeconds: 999_999);

        var media = packet.AsSpan(8, 0x38);
        Assert.Equal(999_999u, BinaryPrimitives.ReadUInt32LittleEndian(media[0x14..]));
        Assert.Equal(1_812_345_678L, BinaryPrimitives.ReadInt64LittleEndian(media[0x18..]));
    }
}
