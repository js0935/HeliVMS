namespace HeliVMS.Core.Ntp;

/// <summary>
/// NTP 幀構建/解析（RFC 5905，M68）。靜態函式與網路無關，可離線單元測試。
/// </summary>
public static class NtpFrames
{
    public const int PacketSize = 48;

    /// <summary>建 MODE3（client）請求：LI=0、VN=4、MODE=3 → 首位元組 0x23；transmit 欄填 T0。</summary>
    public static byte[] BuildRequestUtc(DateTime utcNow)
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x23; // 00 | 100 | 011
        var (seconds, fraction) = NtpClient.DateTimeToNtp(utcNow);
        WriteNtpTimestamp(packet, 40, seconds, fraction);
        return packet;
    }

    /// <summary>
    /// 解析回應：T2＝receive 欄（off 32）、T3＝transmit 欄（off 40）、T4＝本地收包時間。
    /// <paramref name="t1"/>＝本地發包時間（= 請求 transmit 欄）。非 48B／kiss-o'-death（stratum 0）
    /// ／負 delay（樣本無效）→ false。
    /// </summary>
    public static bool TryParseResponse(
        byte[] response,
        DateTime t1,
        DateTime t4,
        out NtpQuery query)
    {
        query = null!;
        if (response.Length < PacketSize)
        {
            return false;
        }

        var li = (byte)(response[0] >> 6);
        var version = (byte)((response[0] >> 3) & 0x7);
        var stratum = response[1];
        if (stratum == 0)
        {
            return false; // kiss-o'-death
        }

        var t2 = ReadNtpTimestamp(response, 32);
        var t3 = ReadNtpTimestamp(response, 40);

        var offset = ((t2 - t1) + (t3 - t4)) / 2;
        var delay = (t4 - t1) - (t3 - t2);
        if (delay < TimeSpan.Zero)
        {
            return false;
        }

        query = new NtpQuery(offset, delay, stratum, li, version);
        return true;
    }

    /// <summary>讀 8 位元組（秒＋分數）NTP 時間戳為 DateTime。</summary>
    public static DateTime ReadNtpTimestamp(byte[] buffer, int offset)
    {
        var seconds = ReadUInt32Big(buffer, offset);
        var fraction = ReadUInt32Big(buffer, offset + 4);
        return NtpClient.NtpSecondsToDateTime(seconds, fraction);
    }

    public static void WriteNtpTimestamp(byte[] buffer, int offset, uint seconds, uint fraction)
    {
        WriteUInt32Big(buffer, offset, seconds);
        WriteUInt32Big(buffer, offset + 4, fraction);
    }

    private static uint ReadUInt32Big(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24)
            | ((uint)buffer[offset + 1] << 16)
            | ((uint)buffer[offset + 2] << 8)
            | buffer[offset + 3];
    }

    private static void WriteUInt32Big(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}