using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// <c>SEARCH_EVENT</c> 的 NEMO 回應 payload 組裝（QCC 端 <c>QC_RM_Event::ResponsePageEvent</c>
/// 的逐欄位對映）。
/// </summary>
/// <remarks>
/// <para>
/// payload = 16-byte 標頭（<c>"OK "</c>＋ASCII 十進位<b>總筆數</b>、NUL 補滿）＋連續紀錄。
/// 紀錄的固定欄位是 0x38 bytes：<c>+0x00</c> i16 頻道、<c>+0x02</c> i16 型別、
/// <c>+0x04</c> 寬字元時間（<c>YYYY/MM/DD HH:MM:SS</c>，44-byte 區）、<c>+0x30</c> u16 len1、
/// <c>+0x32</c> u16 len2、<c>+0x34</c> u32 len3，可變資料緊接在後依序為 len1／len2／len3 bytes。
/// </para>
/// <para>
/// 三個長度欄位的判讀（照抄 CMS 的條件）：
/// <c>len1 &lt; 2</c>、<c>len2 &lt; 3</c>、<c>len3 &lt; 3</c> 都視為空。我們送 len1=0、
/// len2＝UTF-16LE 描述＋結束 NUL、len3=0，所以只有描述會出現。
/// </para>
/// <para>
/// <b>總筆數必須是「符合條件的全部筆數」，不是本頁筆數</b>：QCC 用
/// <c>ceil(總數 / 每頁筆數)</c> 算頁數，送成本頁筆數會讓它以為只有一頁，第二頁之後
/// 永遠不會被要求，事件清單看起來就是「只查到前 N 筆」。
/// </para>
/// <para>
/// 0 筆時<b>只回 16 bytes</b>（整框 0x20）：CMS 的 <c>ResponsePageEvent</c> 開頭就是
/// <c>if (length &lt; 0x21) return false</c>，乾淨的空清單；多送任何紀錄反而會被解讀成
/// 一筆不存在的事件。
/// </para>
/// </remarks>
public static class QctekEventPayloads
{
    /// <summary>payload 標頭長度（<c>"OK &lt;total&gt;"</c> 補零到 16 bytes）。</summary>
    public const int HeaderLength = 0x10;

    /// <summary>單筆紀錄的固定欄位長度；可變資料接在後面。</summary>
    public const int RecordFixedLength = 0x38;

    /// <summary>紀錄裡時間字串的區域（0x04–0x2f）。</summary>
    private const int TimeRegionLength = 0x2c;

    /// <summary>寬字元描述的最大位元組數（含結束 NUL）；CMS 端以 512 為界。</summary>
    public const int MaxTextBytes = 512;

    /// <summary>描述最多幾個字元（254 字元＝508 bytes＋NUL＝510，留 2 bytes 餘裕）。</summary>
    public const int MaxTextChars = 254;

    /// <summary>組出一頁的回應 payload。</summary>
    public static byte[] Page(QctekEventPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var entries = page.Entries ?? [];
        var total = Math.Max(page.Total, entries.Count);

        using var stream = new MemoryStream(HeaderLength + entries.Count * (RecordFixedLength + 64));

        var header = new byte[HeaderLength];
        "OK "u8.CopyTo(header);
        var digits = Encoding.ASCII.GetBytes(total.ToString(CultureInfo.InvariantCulture));
        digits.AsSpan(0, Math.Min(digits.Length, HeaderLength - 3)).CopyTo(header.AsSpan(3));
        stream.Write(header);

        foreach (var entry in entries)
        {
            WriteEntry(stream, entry);
        }

        return stream.ToArray();
    }

    /// <summary>把一筆事件寫成「固定欄位＋可變資料」。</summary>
    private static void WriteEntry(Stream stream, QctekEventEntry entry)
    {
        var record = new byte[RecordFixedLength];

        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(0x00), (short)entry.ChannelIndex);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(0x02), (short)entry.TypeCode);

        // 時間字串是本地時間（真機 ConvertLog 先 qc_UtcToLocal 才格式化）。
        var clock = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy/MM/dd HH:mm:ss}",
            entry.LocalTime);
        var clockBytes = Encoding.Unicode.GetBytes(clock);
        clockBytes.AsSpan(0, Math.Min(clockBytes.Length, TimeRegionLength - 2))
            .CopyTo(record.AsSpan(0x04));

        var text = EncodeText(entry.Text);

        // len1（ANSI 型別字串）留空：CMS 會用 GetEventTypeString(type) 覆寫這個欄位。
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x32), (ushort)text.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x34), 0);

        stream.Write(record);
        stream.Write(text);
    }

    /// <summary>UTF-16LE 描述＋結束 NUL（長度上限 <see cref="MaxTextBytes"/>）。</summary>
    private static byte[] EncodeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var take = Math.Min(text.Length, MaxTextChars);

        // 截斷不能切開代理對：留著半個代理對會變成 U+FFFD，QCC 的清單上就是一個問號。
        if (take > 0 && char.IsHighSurrogate(text[take - 1]))
        {
            take--;
        }

        if (take == 0)
        {
            return [];
        }

        var bytes = Encoding.Unicode.GetBytes(text[..take]);
        var result = new byte[bytes.Length + 2];
        bytes.CopyTo(result, 0);
        return result;
    }
}
