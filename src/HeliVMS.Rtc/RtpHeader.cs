namespace HeliVMS.Rtc;

/// <summary>
/// 最小化的 RTP 標頭解析（RFC 3550 §5.1）。
/// <para>
/// 只取出 SFU 轉送時真正需要的欄位：序號、時間戳、marker bit、payload type 與
/// payload 本體。<b>刻意不解碼媒體內容</b>——這正是 M244「SFU 不解碼」的關鍵：
/// payload 原封不動交給 SIPSorcery 做 SRTP 加密後轉給瀏覽器，因此這裡只需要
/// 12 bytes 的固定標頭加上一段跳過變長欄位的算術。
/// </para>
/// <para>
/// 做成純函式（不碰 socket、不碰時間、不碰亂數）除了可測，也是 repo 一貫的作法：
/// 參考 <c>RtspStreamResolver</c>——安全關鍵路徑要能直接單元測試。
/// </para>
/// </summary>
public readonly record struct RtpHeader(
    ushort SequenceNumber,
    uint Timestamp,
    bool MarkerBit,
    byte PayloadType,
    uint Ssrc,
    int PayloadOffset,
    int PayloadLength)
{
    /// <summary>RTP 固定標頭長度（不含 CSRC 清單與延伸標頭）。</summary>
    public const int FixedHeaderSize = 12;

    /// <summary>
    /// 嘗試解析一個 RTP 封包。
    /// </summary>
    /// <remarks>
    /// 刻意「寬容但安全」：版本不是 2、封包太短、或宣告的變長欄位超出封包時
    /// 一律視為不是 RTP 而丟棄，而不是猜測。丟一個壞封包遠比把壞資料
    /// 當成視訊轉給瀏覽器好。
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> packet, out RtpHeader header)
    {
        header = default;

        if (packet.Length < FixedHeaderSize) return false;

        // 版本必須是 2（低 2 bits）。
        if ((packet[0] >> 6) != 2) return false;

        // padding 位元：若宣告有 padding，結尾的padding長度欄位必須自洽，
        // 否則 payload 邊界算不出來，直接判定無效。
        var hasPadding = (packet[0] & 0x20) != 0;
        var hasExtension = (packet[0] & 0x10) != 0;
        var csrcCount = packet[0] & 0x0F;

        var offset = FixedHeaderSize + (csrcCount * 4);

        if (hasExtension)
        {
            // 延伸標頭：1 個 16-bit「以 32-bit 為單位」的長度欄位。
            if (offset + 4 > packet.Length) return false;
            var extensionWords = (packet[offset + 2] << 8) | packet[offset + 3];
            offset += 4 + (extensionWords * 4);
        }

        if (offset > packet.Length) return false;

        var length = packet.Length - offset;

        if (hasPadding)
        {
            if (length < 1) return false;
            var padding = packet[^1];
            if (padding == 0 || padding > length) return false;
            length -= padding;
        }

        header = new RtpHeader(
            SequenceNumber: (ushort)((packet[2] << 8) | packet[3]),
            Timestamp: (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]),
            MarkerBit: (packet[1] & 0x80) != 0,
            PayloadType: (byte)(packet[1] & 0x7F),
            Ssrc: (uint)((packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11]),
            PayloadOffset: offset,
            PayloadLength: length);

        return true;
    }
}
