using System.Buffers.Binary;
using System.Text;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// 回放查詢的 NEMO 回應 payload 組裝（QCC 端解析器的逐欄位對映）。
/// </summary>
public static class QctekPlaybackPayloads
{
    /// <summary>GET_MONTH_DATE 的回應 payload（0x40）：`"OK"`＋位移 0x10 起 31 天旗標。</summary>
    public static byte[] MonthDate(IReadOnlyList<bool> days)
    {
        var payload = new byte[0x40];
        payload[0] = (byte)'O';
        payload[1] = (byte)'K';
        for (var d = 0; d < 31 && d < days.Count; d++)
        {
            payload[0x10 + d] = days[d] ? (byte)1 : (byte)0;
        }

        return payload;
    }

    /// <summary>
    /// CATALOG 的回應 payload（0x314）：`"OK"`＋位移 0x14 起 32 天×24 小時矩陣
    /// （索引 = (day−1)×24 + hour，每 byte 該小時錄影量）。
    /// </summary>
    public static byte[] Catalog(IReadOnlyList<byte> hourMatrix, int day)
    {
        var payload = new byte[0x314];
        payload[0] = (byte)'O';
        payload[1] = (byte)'K';
        // 位移 0x10 是「查詢的日」（u32）。必須用 AsSpan：payload 是陣列，
        // `payload[0x10..]` 的 Range 切片會產生副本——寫進副本、原陣列不動，
        // 是一個靜默 no-op（AGENTS 的時間戳教訓同一形狀：測試才抓得到）。
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10), (uint)day);
        for (var i = 0; i < 768 && i < hourMatrix.Count; i++)
        {
            payload[0x14 + i] = hourMatrix[i];
        }

        return payload;
    }

    /// <summary>
    /// OPENFILE 的成功回應（raw）：`"OK d1 d2 d3 d4 \0"`——四個數字由 QCC 的
    /// parserFileInfo 填入 __PB_FILE_INFO（+4/+0/+0xc/+8）。參數語意待真機抓包
    /// 驗證；目前依回放進度需要給「頻道(1-based)、大小、起始秒、0」。
    /// </summary>
    public static byte[] OpenFileOk(int channelIndex, long fileSizeBytes, long startUnixSeconds)
        => Encoding.ASCII.GetBytes(
            $"OK {channelIndex + 1} {fileSizeBytes} {startUnixSeconds} 0 \0");
}
