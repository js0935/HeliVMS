using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 二進位 payload 產生器：與真機 QCV8001 的黃金抓包逐位元組相同。
/// </summary>
/// <remarks>
/// QCC 不對 payload 做版本協商；它直接照位移解讀。任何一個位元組偏移錯了（名稱起點、
/// 記錄長度、格式碼），QCC 只會把裝置畫成「沒有頻道」或連線後空白，沒有任何錯誤訊息。
/// 所以這裡的斷言是整段位元組相等，不抽樣。
/// </remarks>
public class QctekPayloadsTests
{
    [Fact]
    public void 單頻道設定payload與真機回應逐位元組相同()
    {
        var actual = QctekPayloads.Config(["攝影機001"]);

        Assert.Equal(312, actual.Length);
        Assert.Equal(GoldenCapture.ConfigPayload, actual);
    }

    [Fact]
    public void 使用者payload與真機的一百五十二位元組相同()
    {
        var actual = QctekPayloads.AuthUser(36);

        // 真機在同一台只有 1 個頻道記錄的裝置上回報 36（位移 20）；照抄這個值。
        Assert.Equal(QctekPayloads.AuthUserLength, actual.Length);
        Assert.Equal(GoldenCapture.AuthUserPayload, actual);
    }

    [Fact]
    public void IO設定payload與真機的二百八十位元組相同()
    {
        var actual = QctekPayloads.IoConfig();

        Assert.Equal(QctekPayloads.IoConfigLength, actual.Length);
        Assert.Equal(GoldenCapture.IoConfigPayload, actual);
    }

    [Fact]
    public void 設定payload長度是標頭加每頻道一筆記錄()
    {
        // 真機：0x18 + n * 0x120（單頻道 312）。QCC 依這個長度決定讀幾筆記錄，
        // 多一截少一截都會讓它把後面的位元組當成下一筆。
        foreach (var n in new[] { 0, 1, 2, 36 })
        {
            var names = Enumerable.Range(1, n).Select(i => $"CH{i:000}").ToArray();
            Assert.Equal(0x18 + (n * 0x120), QctekPayloads.Config(names).Length);
        }
    }

    [Fact]
    public void 設定payload的計數與頻道名寫在真機的位移上()
    {
        var payload = QctekPayloads.Config(["A", "攝影機002"]);

        Assert.Equal("QCTEK_RM"u8.ToArray(), payload[..8]);
        Assert.Equal(new byte[] { 0x1C, 0x3B, 0x34, 0x01 }, payload[8..12]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20)));

        // 第一筆記錄的能力位元組照抄真機：01 00 01 01 01 01 00 01。
        Assert.Equal(
            new byte[] { 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x00, 0x01 },
            payload[0x18..0x20]);

        // 名稱在記錄位移 0x1C（絕對 0x34），純 UTF-16LE、無 BOM。位移一律寫死黃金的
        // 絕對值：引用 ConfigNameOffset 會讓斷言跟著錯誤的常數移動（永遠是綠的）。
        var first = payload.AsSpan(0x34);
        Assert.Equal(Encoding.Unicode.GetBytes("A\0"), first[..4].ToArray());

        var second = payload.AsSpan(0x34 + QctekPayloads.ConfigRecordLength);
        Assert.Equal(Encoding.Unicode.GetBytes("攝影機002\0"), second[..(7 * 2)].ToArray());
    }

    [Fact]
    public void 過長的頻道名會截斷在欄位容量內而不是寫出記錄()
    {
        // 容量 0x41 wchar（含結尾 NUL）＝最多 64 字。超長名稱若寫出去，會蓋掉下一筆
        // 記錄的狀態位元組——那筆頻道就從 QCC 清單裡消失，而且沒有任何錯誤。
        var name = new string('X', 70);
        var payload = QctekPayloads.Config([name, "下一台"]);

        var record = payload.AsSpan(0x18, QctekPayloads.ConfigRecordLength);
        var field = record.Slice(QctekPayloads.ConfigNameOffset, QctekPayloads.ConfigNameCapacity * 2);
        var expected = Encoding.Unicode.GetBytes(new string('X', 64) + "\0");
        Assert.Equal(expected, field[..expected.Length].ToArray());
        Assert.All(field[expected.Length..].ToArray(), b => Assert.Equal((byte)0, b));

        // 下一筆記錄仍然完好，名稱在它自己的位移上。
        var next = payload.AsSpan(0x18 + QctekPayloads.ConfigRecordLength + QctekPayloads.ConfigNameOffset);
        Assert.Equal(Encoding.Unicode.GetBytes("下一台\0"), next[..8].ToArray());
    }

    [Fact]
    public void 使用者payload的權限位圖全開且偏移十六是常數()
    {
        var payload = QctekPayloads.AuthUser(4);

        Assert.Equal("QCTEK_RM"u8.ToArray(), payload[..8]);
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20)));
        Assert.All(payload.AsSpan(24).ToArray(), b => Assert.Equal((byte)0xFF, b));
    }

    [Fact]
    public void IO設定payload使用自己的格式碼且其餘為零()
    {
        var payload = QctekPayloads.IoConfig();

        // 「QCTEK_RM」後面的 4 bytes 與設定／使用者不同（6d 3d 34 01）：共用一個
        // 常數會讓 QCC 把 IO 表當成設定表解讀。
        Assert.Equal(new byte[] { 0x6D, 0x3D, 0x34, 0x01 }, payload[8..12]);
        Assert.All(payload.AsSpan(12).ToArray(), b => Assert.Equal((byte)0, b));
    }
}
