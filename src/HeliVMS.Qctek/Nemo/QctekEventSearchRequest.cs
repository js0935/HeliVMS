using System.Buffers.Binary;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>一筆要回給 CMS 的事件：已轉成本地時間、已映射成 CMS 認得的型別碼。</summary>
/// <param name="ChannelIndex">0-based 頻道號（QCC 的頻道欄位）。</param>
/// <param name="TypeCode">CMS 型別碼（<see cref="QctekEventTypes"/>）。</param>
/// <param name="LocalTime">事件發生的<b>本地</b>時間（回應字串直接顯示這個）。</param>
/// <param name="Text">事件描述（回應中以 UTF-16LE 送出）。</param>
public readonly record struct QctekEventEntry(
    int ChannelIndex,
    int TypeCode,
    DateTime LocalTime,
    string Text);

/// <summary>一頁事件，外加「符合條件的總筆數」——CMS 用總筆數算頁數，不是用本頁筆數。</summary>
public sealed record QctekEventPage(IReadOnlyList<QctekEventEntry> Entries, int Total)
{
    /// <summary>沒有任何符合條件的事件（QCC 端會得到乾淨的空清單）。</summary>
    public static readonly QctekEventPage Empty = new([], 0);
}

/// <summary>
/// CMS 的 <c>SEARCH_EVENT</c> 查詢本體（<c>__EVENT_SEARCH_PARAM</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 線路形狀由 <c>QC_RM_Alarm.dll</c> 的 <c>QC_RM_Event::SearchEvent</c>（送出端）與
/// <c>QC_Event::SearchEvent</c>（真機的接收端）雙向還原：NEMO payload 開頭是 16-byte
/// 命令名（<c>"SEARCH_EVENT"</c>＋NUL 補滿）、<c>+0x10</c> 本體長度（<c>0x150</c>）、
/// <c>+0x14</c> 每頁筆數，其後才是 336-byte 本體。
/// </para>
/// <para>
/// 本體就是 <c>__EVENT_SEARCH_PARAM</c> 去掉前面 4 bytes，所以本體 <c>+0x00</c> 對應
/// 結構 <c>+0x04</c>（起始 <c>tm</c>）、<c>+0x24</c> 對應 <c>+0x28</c>（結束 <c>tm</c>）、
/// <c>+0x48</c> 對應 <c>+0x4c</c>（型別）、<c>+0x4c</c> 對應 <c>+0x50</c>（頻道，
/// 負值＝全部）。其後的標籤文字只有 POS 查詢會用到，HeliVMS 沒有 POS 標籤，忽略。
/// </para>
/// <para>
/// <b>時間讀不懂就拒絕整筆查詢</b>：曾經在匯出精靈上吃過虧——把讀不懂的時刻默默放寬成
/// 整天，操作員要 5 秒卻拿到 50 分鐘。這裡寧可回 0 筆（QCC 顯示空清單），也不要回一個
/// 操作員沒有要求的時間範圍。
/// </para>
/// </remarks>
public sealed record QctekEventSearchRequest(
    DateTime FromLocal,
    DateTime ToLocal,
    int TypeCode,
    int ChannelIndex,
    int PageSize)
{
    /// <summary>NEMO 命令名。</summary>
    public const string CommandName = "SEARCH_EVENT";

    /// <summary>16-byte 命令名＋u32 本體長度＋u32 每頁筆數。</summary>
    public const int PayloadHeaderLength = 0x18;

    /// <summary>本體長度（真機固定 <c>0x150</c>；更長的版本也接受，只讀前 0x50）。</summary>
    public const int BodyLength = 0x150;

    /// <summary>本體中「起始 tm」的位移。</summary>
    private const int FromOffset = 0x00;

    /// <summary>本體中「結束 tm」的位移。</summary>
    private const int ToOffset = 0x24;

    /// <summary>本體中「型別」的位移（i32）。</summary>
    private const int TypeOffset = 0x48;

    /// <summary>本體中「頻道」的位移（i16；負值＝全部）。</summary>
    private const int ChannelOffset = 0x4c;

    /// <summary>本體中「標籤文字」的位移（150 bytes；HeliVMS 忽略）。</summary>
    private const int TagOffset = 0x4e;

    /// <summary>本體至少要含到標籤文字之前的欄位。</summary>
    private const int MinimumBodyLength = TagOffset;

    /// <summary><c>struct tm</c> 的大小（9 個 int）。</summary>
    private const int TmLength = 36;

    /// <summary>
    /// 每頁筆數上限。QCC 的 UI 不會送這麼大的值；擋下來是為了不讓一個惡意／壞掉的
    /// 用戶端用 <c>0x7fffffff</c> 要求我們一次組出整年份的回應（記憶體炸彈）。
    /// </summary>
    public const int MaxPageSize = 10_000;

    /// <summary>
    /// 解析 NEMO payload（<b>整段</b>，含 16-byte 命令名）。命令名不符、欄位不足、
    /// 時刻或每頁筆數不合理時回 <c>null</c>——呼叫端應回空頁，不要猜。
    /// </summary>
    public static QctekEventSearchRequest? TryParse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PayloadHeaderLength + MinimumBodyLength)
        {
            return null;
        }

        if (!payload[..CommandName.Length].SequenceEqual(Encoding.ASCII.GetBytes(CommandName)))
        {
            return null;
        }

        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[0x10..]);
        if (bodyLength < MinimumBodyLength)
        {
            return null;
        }

        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[0x14..]);
        if (pageSize is 0 or > MaxPageSize)
        {
            return null;
        }

        var body = payload[PayloadHeaderLength..];
        var from = ReadTm(body, FromOffset);
        var to = ReadTm(body, ToOffset);
        if (from is null || to is null)
        {
            return null;
        }

        var type = BinaryPrimitives.ReadInt32LittleEndian(body[TypeOffset..]);
        var channel = BinaryPrimitives.ReadInt16LittleEndian(body[ChannelOffset..]);

        return new QctekEventSearchRequest(from.Value, to.Value, type, channel, (int)pageSize);
    }

    /// <summary>把本體裡的 <c>struct tm</c> 讀成本地時間；越界或不可能的值回 <c>null</c>。</summary>
    private static DateTime? ReadTm(ReadOnlySpan<byte> body, int offset)
    {
        if (body.Length < offset + TmLength)
        {
            return null;
        }

        var second = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        var minute = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 4)..]);
        var hour = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 8)..]);
        var day = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 12)..]);
        var month = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 16)..]);
        var year = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 20)..]);

        // struct tm：tm_year 是「距 1900 年」、tm_mon 是 0-based。
        if (year is < 0 or > 8099 || month is < 0 or > 11 ||
            hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return null;
        }

        // 欄位各自合法但日期不存在（2 月 31 日、4 月 31 日…）：DateTime 建構子會丟
        // ArgumentOutOfRangeException，整條 NEMO 連線會被例外帶走。這裡先查天數。
        var fullYear = year + 1900;
        var fullMonth = month + 1;
        if (day < 1 || day > DateTime.DaysInMonth(fullYear, fullMonth))
        {
            return null;
        }

        return new DateTime(fullYear, fullMonth, day, hour, minute, second);
    }
}
