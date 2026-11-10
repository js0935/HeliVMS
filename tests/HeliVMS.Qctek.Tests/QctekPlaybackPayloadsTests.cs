using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Live;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 回放查詢回應 payload 的版型（QCC 端解析器的逐欄位對映）。
/// 釘的是<b>位移與總長</b>：MONTH_DATE 的月曆在 payload+0x10、CATALOG 的日別在 +0x10、
/// 小時矩陣在 +0x14、OPENFILE 是 raw 且以 NUL 結尾——任何一個位移錯了，
/// QCC 端讀到的就是別的欄位，症狀是「月曆全空／目錄全空」，日誌乾淨。
/// </summary>
public sealed class QctekPlaybackPayloadsTests
{
    [Fact]
    public void MonthDate的版型是OK加0x10起的31天旗標()
    {
        var days = new bool[31];
        days[0] = true;
        days[30] = true;

        var payload = QctekPlaybackPayloads.MonthDate(days);

        Assert.Equal(0x40, payload.Length);
        Assert.Equal("OK", Encoding.ASCII.GetString(payload, 0, 2));
        Assert.Equal(1, payload[0x10]);
        Assert.Equal(0, payload[0x11]);
        Assert.Equal(1, payload[0x10 + 30]);
    }

    [Fact]
    public void MonthDate超過31天的旗標不寫()
    {
        var days = new bool[40];
        Array.Fill(days, true);

        var payload = QctekPlaybackPayloads.MonthDate(days);

        // payload 只有 0x40＝0x10＋31 天＋padding：第 32 天之後的旗標沒有位置可寫。
        Assert.Equal(0x40, payload.Length);
        Assert.Equal(0, payload[0x10 + 31]);
    }

    [Fact]
    public void Catalog的版型是OK加日別加0x14起的768格矩陣()
    {
        var matrix = new byte[768];
        matrix[0] = 12;
        matrix[(5 - 1) * 24 + 13] = 7;

        var payload = QctekPlaybackPayloads.Catalog(matrix, day: 5);

        Assert.Equal(0x314, payload.Length);
        Assert.Equal("OK", Encoding.ASCII.GetString(payload, 0, 2));
        // 位移 0x10 是「查詢的日」（u32），讓 QCC 對齊它自己選的日期。
        Assert.True(
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x10)) == 5u,
            "hex[0x00..0x20]=" + Convert.ToHexString(payload, 0, 0x20));
        Assert.Equal(12, payload[0x14]);
        Assert.Equal(7, payload[0x14 + (5 - 1) * 24 + 13]);
    }

    [Fact]
    public void Catalog超過768格的資料不寫()
    {
        var matrix = new byte[800];
        Array.Fill(matrix, (byte)9);

        var payload = QctekPlaybackPayloads.Catalog(matrix, day: 1);

        Assert.Equal(0x314, payload.Length);
        Assert.Equal(9, payload[0x14 + 767]);
    }

    [Fact]
    public void OpenFileOk是raw格式且以NUL結尾()
    {
        var wire = QctekPlaybackPayloads.OpenFileOk(channelIndex: 0, fileSizeBytes: 12345, startUnixSeconds: 1794274281);

        // parserFileInfo 把四個數字填進 __PB_FILE_INFO；頻道是 1-based。
        Assert.Equal("OK 1 12345 1794274281 0 \0", Encoding.ASCII.GetString(wire));
        Assert.EndsWith("\0", Encoding.ASCII.GetString(wire), StringComparison.Ordinal);
    }

    [Fact]
    public void OpenFileOk的頻道從1起算()
    {
        var wire = QctekPlaybackPayloads.OpenFileOk(channelIndex: 6, fileSizeBytes: 1, startUnixSeconds: 0);

        Assert.StartsWith("OK 7 ", Encoding.ASCII.GetString(wire), StringComparison.Ordinal);
    }
}
