using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>QCC 遠端設定頁的三個帳號寫入端點。</summary>
public enum QctekAccountAction
{
    /// <summary>不是帳號端點。</summary>
    None = 0,

    /// <summary><c>/QCRemoveAccount.bin?usr=&lt;序號&gt;</c>：刪除帳號。</summary>
    Remove = 1,

    /// <summary><c>/QCUpdateAccount.bin?&lt;base64&gt;</c>：新增或修改一個帳號。</summary>
    Update = 2,

    /// <summary><c>/QCCopyAccount.bin?&lt;base64&gt;</c>：把一個帳號的權限複製到勾選的帳號。</summary>
    Copy = 3,
}

/// <summary>
/// 解出來的 <c>__SQL_USER_LIST</c> 三欄——只有這三欄對 HeliVMS 有意義。
/// <paramref name="Sequence"/> 是裝置端的帳號序號（負值＝新增，非負＝既有帳號）。
/// </summary>
public sealed record QctekUserRow(int Permission, int Sequence, string Username);

/// <summary>
/// QCC 遠端設定頁的帳號寫入端點（<c>support_bin.cpp</c> 的
/// <c>HandleBinUpdateAccount</c>／<c>HandleBinRemoveAccount</c>／複製版）。
/// </summary>
/// <remarks>
/// <para>
/// 線路格式由真機（<c>QC_Server</c> 0x1800209c0／0x180020ba0／0x180020db0 與
/// <c>QC_Setting</c> 的 <c>UpdateAccountInfo</c>／<c>RemoveAccountInfo</c>）逐行還原。
/// 三個端點都<b>先授權再解析</b>：授權失敗回 401，之後才是 400（請求本身壞掉）與
/// 500（資料層拒絕）。順序本身就是規格的一部分——反過來會讓「帳密錯」看起來像
/// 「請求格式錯」，QCC 會改送另一種格式而不是提示密碼。
/// </para>
/// <para>
/// <b>查詢字串不可用表單解析。</b>QCC 把 base64 <b>直接嵌在 query 裡</b>、未經
/// URL 編碼（<c>FUN_180002cc0</c>），而標準 base64 會出現 <c>+</c>：表單解析會把
/// <c>+</c> 換成空白，解碼必然失敗且沒有任何錯誤訊息。這裡只做
/// <see cref="Uri.UnescapeDataString(string)"/>（還原 <c>%2B</c> 之類的百分號編碼，
/// 但保留 <c>+</c> 原樣）。
/// </para>
/// <para>
/// 位移全部來自 <c>__SQL_USER_LIST</c>：<c>+0x08</c> 權限、<c>+0x0c</c> 序號、
/// <c>+0x28</c> 的 <b>32-byte ANSI</b> 名稱。複製端點的 payload 是
/// 0x228（一列）＋128 bytes 位圖（1024 個勾選位，每個 byte 的 bit0..bit7 依序對應
/// <c>i*8+0..i*8+7</c>，即 LSB first），總長恰為 0x2a8。
/// </para>
/// </remarks>
public static class QctekAccountEndpoint
{
    /// <summary>刪除帳號的路徑（真機 <c>support_bin</c> 的 remove 處理器）。</summary>
    public const string RemovePath = "/QCRemoveAccount.bin";

    /// <summary>新增／修改帳號的路徑。</summary>
    public const string UpdatePath = "/QCUpdateAccount.bin";

    /// <summary>複製權限的路徑。</summary>
    public const string CopyPath = "/QCCopyAccount.bin";

    /// <summary>update 的 base64 解碼結果必須恰為一列（0x228）。</summary>
    public const int UpdatePayloadLength = QctekRemoteList.UserRowLength;

    /// <summary>copy 的 base64 解碼結果必須恰為「一列 ＋ 128 bytes 位圖」（0x2a8）。</summary>
    public const int CopyPayloadLength = 0x2a8;

    /// <summary>位圖展開後的勾選位數（128 bytes × 8）。</summary>
    public const int SelectionCount = 1024;

    /// <summary>權限分類遮罩（真機以 <c>&amp; 0x1c</c> 判 admin／viewer）。</summary>
    public const int PermissionClassMask = 0x1c;

    /// <summary>真機「一般帳號」的權限分類（<c>DefaultAccountSetting</c> 的 8）。</summary>
    public const int ViewerClass = 0x08;

    private const int PermissionOffset = 0x08;
    private const int SequenceOffset = 0x0c;
    private const int NameOffset = 0x28;
    private const int NameLength = 32;

    /// <summary>這一條路徑是哪一個帳號端點；不是就回 <see cref="QctekAccountAction.None"/>。</summary>
    public static QctekAccountAction Classify(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return QctekAccountAction.None;
        }

        if (path.Equals(RemovePath, StringComparison.OrdinalIgnoreCase))
        {
            return QctekAccountAction.Remove;
        }

        if (path.Equals(UpdatePath, StringComparison.OrdinalIgnoreCase))
        {
            return QctekAccountAction.Update;
        }

        return path.Equals(CopyPath, StringComparison.OrdinalIgnoreCase)
            ? QctekAccountAction.Copy
            : QctekAccountAction.None;
    }

    /// <summary>是不是三個帳號寫入端點之一。</summary>
    public static bool IsAccountPath(string? path) => Classify(path) != QctekAccountAction.None;

    /// <summary>
    /// 取查詢字串裡「<c>?</c> 之後」的內容（真機是 <c>path + 0x15</c>／<c>+0x13</c>，
    /// 也就是整段 query）。沒有查詢字串回 <c>null</c>；<c>?</c> 後面是空的也回
    /// <c>null</c>——兩者在真機都會走到「格式錯誤」。
    /// </summary>
    public static string? Payload(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return null;
        }

        var raw = queryString[0] == '?' ? queryString[1..] : queryString;
        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            // 只還原百分號編碼：不能用 WebUtility.UrlDecode／表單解析，
            // 那會把 base64 的 '+' 換成空白（見類別說明）。
            return Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    /// <summary>
    /// 從 remove 的查詢取出 <c>usr</c> 序號。真機語意：以 <c>&amp;</c> 分項、以
    /// <c>=</c> 取名稱，找到名稱 <c>usr</c> 後把值交給 <c>atoi</c>；<c>usr</c>
    /// 沒有 <c>=</c>（或整個 query 沒有 <c>usr</c>）回 <c>false</c> → 呼叫端回 400。
    /// </summary>
    public static bool TryReadRemoveSequence(string? payload, out int sequence)
    {
        sequence = 0;
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        foreach (var token in payload.Split('&'))
        {
            var equals = token.IndexOf('=');
            var name = equals < 0 ? token : token[..equals];
            if (!name.Equals("usr", StringComparison.Ordinal))
            {
                continue;
            }

            if (equals < 0)
            {
                // 真機在這裡 break：`usr` 沒有值不是「找不到」，而是格式錯誤。
                return false;
            }

            sequence = Atoi(token[(equals + 1)..]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解 base64 payload 並要求長度恰為 <paramref name="expectedLength"/>。
    /// 缺結尾 <c>=</c> 會補上（QCC 兩種都送過）；其餘任何錯誤（非法字元、
    /// 長度不符）回 <c>null</c>——呼叫端一律回 400，與真機 <c>qc_base64_decode</c>
    /// 的「回傳長度不等於預期」同一條路徑。
    /// </summary>
    public static byte[]? DecodePayload(string? payload, int expectedLength)
    {
        var bytes = DecodeBase64(payload);
        return bytes is not null && bytes.Length == expectedLength ? bytes : null;
    }

    /// <summary>把查詢裡的 base64 解成位元組（補上缺少的結尾填充）；壞字元回 <c>null</c>。</summary>
    private static byte[]? DecodeBase64(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        var text = new StringBuilder(payload.Length + 3);
        foreach (var c in payload)
        {
            if (!char.IsWhiteSpace(c))
            {
                text.Append(c);
            }
        }

        while (text.Length % 4 != 0)
        {
            text.Append('=');
        }

        try
        {
            return Convert.FromBase64String(text.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 讀 <c>__SQL_USER_LIST</c> 的三個欄位。<paramref name="row"/> 必須至少一列長；
    /// 名稱是 ANSI 欄位，遇到 NUL 為止（真機以 C 字串處理）。
    /// </summary>
    public static QctekUserRow ReadUserRow(ReadOnlySpan<byte> row)
    {
        if (row.Length < UpdatePayloadLength)
        {
            throw new ArgumentException("使用者列長度不足 0x228", nameof(row));
        }

        var permission = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(PermissionOffset, 4));
        var sequence = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(SequenceOffset, 4));
        var name = Encoding.Latin1.GetString(row.Slice(NameOffset, NameLength));
        var nul = name.IndexOf('\0');
        return new QctekUserRow(permission, sequence, nul >= 0 ? name[..nul] : name);
    }

    /// <summary>
    /// 展開 copy 的位圖：<paramref name="payload"/> 是 0x2a8（一列 ＋ 128 bytes），
    /// 回傳 1024 個勾選位。<b>LSB first</b>：byte i 的 bit0 對應索引 <c>i*8</c>。
    /// </summary>
    public static bool[] ReadSelection(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CopyPayloadLength)
        {
            throw new ArgumentException("複製 payload 長度不足 0x2a8", nameof(payload));
        }

        var bitmap = payload.Slice(UpdatePayloadLength, CopyPayloadLength - UpdatePayloadLength);
        var selected = new bool[SelectionCount];
        for (var i = 0; i < bitmap.Length; i++)
        {
            var value = bitmap[i];
            if (value == 0)
            {
                continue;
            }

            for (var bit = 0; bit < 8; bit++)
            {
                if ((value & (1 << bit)) != 0)
                {
                    selected[(i * 8) + bit] = true;
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// 權限位元是否代表「已停用」。真機只把序號為負當成刪除，停用是「沒有任何權限」；
    /// 這裡以「遮掉目前登入者的 0x20 之後為 0」判定——QCC 的 <c>isCurrentUser</c>
    /// 會把 0x20 併進同一個 u32，但那是 <c>+0x18</c> 的旗標，不是帳號能力。
    /// </summary>
    public static bool IsEnabled(int permission)
        => (permission & ~QctekRemoteList.CurrentUserBit) != 0;

    /// <summary>是否為 admin：真機口徑是 <c>(perm &amp; 0x1c) == 0x1c</c>。</summary>
    public static bool IsAdministrator(int permission)
        => (permission & PermissionClassMask) == QctekRemoteList.AdminPermission;

    /// <summary>
    /// 是否為「一般帳號」：真機複製端點的來源條件是 <c>(perm &amp; 0x1c) == 8</c>，
    /// 不成立就整體失敗（500）。
    /// </summary>
    public static bool IsViewerClass(int permission)
        => (permission & PermissionClassMask) == ViewerClass;

    /// <summary>
    /// 真機複製權限的運算：<c>((dest ^ src) &amp; 1) ^ dest</c>——只把目標的 bit0
    /// 換成來源的 bit0，其餘位元、名稱、密碼雜湊都不動。HeliVMS 的三種權限
    /// （0／8／0x1c）bit0 都是 0，所以對我們是「驗證後 no-op」。
    /// </summary>
    public static int CopyPermission(int source, int destination)
        => ((destination ^ source) & 1) ^ destination;

    /// <summary>
    /// 從查詢的 <c>hBase64=</c> 取帳密（<c>base64("user:pass")</c>）。真機
    /// <c>FUN_180003340</c> 只在請求帶 <c>?</c> 時生效，且只認這一個參數名；
    /// 解碼後以第一個 <c>:</c> 切開使用者名稱與密碼。
    /// </summary>
    public static bool TryReadQueryCredentials(
        string? payload,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        foreach (var token in payload.Split('&'))
        {
            var equals = token.IndexOf('=');
            if (equals <= 0 || !token[..equals].Equals("hBase64", StringComparison.Ordinal))
            {
                continue;
            }

            var decoded = DecodeBase64(token[(equals + 1)..]);
            if (decoded is null)
            {
                return false;
            }

            var text = Encoding.UTF8.GetString(decoded);
            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            username = text[..colon];
            password = text[(colon + 1)..];
            return true;
        }

        return false;
    }

    /// <summary>
    /// C 的 <c>atoi</c>：略過前導空白、可帶正負號、讀到第一個非數字為止；
    /// 沒有數字回 0。QCC 送的是十進位序號，但真機對垃圾值也是回 0（→ 找不到帳號 → 500），
    /// 所以這裡不能「嚴格解析失敗就 400」。
    /// </summary>
    private static int Atoi(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        var value = 0L;
        var digits = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9')
        {
            value = Math.Min((value * 10) + (text[index] - '0'), int.MaxValue);
            digits++;
            index++;
        }

        if (digits == 0)
        {
            return 0;
        }

        return negative ? (int)-value : (int)value;
    }
}
