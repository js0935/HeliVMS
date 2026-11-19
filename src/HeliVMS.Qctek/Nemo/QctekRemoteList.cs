using System.Buffers.Binary;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// QCC 遠端設定用的兩份清單：<c>/QCCamList.bin</c>（RMCL）與 <c>/QCUserList.bin</c>（RMUL）。
/// </summary>
/// <remarks>
/// <para>
/// 線路格式由真機兩端還原（<c>QC_RemoteSet</c>）：產生端是
/// <c>QC_rsServer::GetCamerasList</c>／<c>GetUsersList</c>，驗證端是
/// <c>QC_rsClient::CheckCameraList</c>／<c>CheckUserList</c>。版型是
/// <b>16 bytes 標頭 ＋ 定長列</b>：
/// </para>
/// <code>
/// +0x00 u32 magic      "RMCL"（0x4C434D52）／"RMUL"（0x4C554D52）
/// +0x04 u32 total      列數 × 列長 ＋ 0x10（驗證端要求逐位元組相符）
/// +0x08 u32 count      列數，必須 &lt; 0x400
/// +0x0c u32 rowLength  0x11c（相機 284）／0x228（使用者 552）
/// </code>
/// <para>
/// <b>兩份清單必須同時可用。</b><c>QC_rsClient::QueryUserSetting</c> 一次抓這兩支端點，
/// 任一 magic／列長／總長不符就整體判定失敗（<c>Info("{RMS} ReadUserListResp check
/// fail!")</c>），QCC 的遠端設定頁因此整頁空白，而且兩邊的日誌都不會說是哪一支壞掉。
/// </para>
/// <para>
/// 相機列是 <c>__XLS_CAMERA_LIST</c>（反編譯裡的 <c>pwVar + 0x82</c> 是 <b>wchar</b> 位移，
/// 換成位元組是 0x104）：名稱 <c>+0x00</c>、群組 <c>+0x82</c> 各 65 個 wchar，其後是
/// Excel 欄 2/3/4/5（預設 -1）、欄 7（顏色，預設 <see cref="DefaultCameraColor"/>）、
/// 欄 6（u16，預設 0）。使用者列是 <c>__SQL_USER_LIST</c>：<c>+0x00</c> 是帳號資料的
/// <c>0xEDE9</c> 標記、<c>+0x08</c> 是權限位元、<c>+0x0c</c> 是帳號序號（負值＝已刪除，
/// 產生端會跳過）、<c>+0x18</c> 那個 u32 的 <c>0x20</c> 位元標記「目前登入者」，名稱是
/// <c>+0x28</c> 的 <b>32-byte ANSI</b> 字串（<c>FindAccountIndexByName</c> 逐 byte 比對）。
/// </para>
/// <para>
/// 這裡只做「把已知事實寫成真機的形狀」：不編造群組、不編造建立／到期時間、也<b>不帶任何
/// 密碼雜湊</b>（真機列的 <c>+0x48</c> 是 32-byte 雜湊，我們一律留 0——這支端點沒有驗證，
/// 見 <see cref="QctekSupportBin"/> 的說明）。
/// </para>
/// </remarks>
public static class QctekRemoteList
{
    /// <summary>標頭長度（magic／總長／列數／列長）。</summary>
    public const int HeaderLength = 0x10;

    /// <summary>相機列長度（<c>__XLS_CAMERA_LIST</c>，284 bytes）。</summary>
    public const int CameraRowLength = 0x11c;

    /// <summary>使用者列長度（<c>__SQL_USER_LIST</c>，552 bytes）。</summary>
    public const int UserRowLength = 0x228;

    /// <summary>"RMCL"（little-endian 的 0x4C434D52）。</summary>
    public const uint CameraMagic = 0x4c434d52;

    /// <summary>"RMUL"（little-endian 的 0x4C554D52）。</summary>
    public const uint UserMagic = 0x4c554d52;

    /// <summary>驗證端要求列數 <c>&lt; 0x400</c>，所以可回的最大列數是 0x3FF。</summary>
    public const int MaxRowCount = 0x3ff;

    /// <summary>
    /// 相機欄 7（顏色）在 Excel 讀不到時 <c>LoadCameraList</c> 寫入的值：
    /// <c>pwVar[0x86] = 0x0100</c>、<c>pwVar[0x87] = 0x00FF</c>，即 0x00FF0100。
    /// </summary>
    public const int DefaultCameraColor = 0x00ff0100;

    /// <summary>使用者列 <c>+0x00</c> 的帳號資料標記（<c>DefaultAccountSetting</c> 寫入 0xEDE9）。</summary>
    public const uint UserRowMagic = 0xede9;

    /// <summary>
    /// 使用者列 <c>+0x04</c> 的低 16 位（<c>DefaultAccountSetting</c> 寫入 3）。
    /// 與 <see cref="UserRowFlags"/> 共用同一個 u32：真機 <c>user.bin</c> 在
    /// <c>+0x04</c> 讀成 0x001E0003。
    /// </summary>
    public const uint UserRowVersion = 3;

    /// <summary>
    /// 使用者列 <c>+0x06</c>（<c>DefaultAccountSetting</c> 寫入 0x1E），即
    /// <c>+0x04</c> 那個 u32 的高 16 位。
    /// </summary>
    public const ushort UserRowFlags = 0x1e;

    /// <summary>
    /// 使用者列 <c>+0x18</c> 那個 u32 的「目前登入者」旗標（<c>isCurrentUser</c> 只找
    /// 這個位元）。注意它<b>不是</b>權限位元（<c>+0x08</c>）：真機的 <c>GetUsersList</c>
    /// 對 <c>+0x18</c> 做 <c>&amp;~0x20</c>／<c>|0x20</c>，寫在權限位元會被驗證端忽略。
    /// </summary>
    public const int CurrentUserBit = 0x20;

    /// <summary>
    /// 真機 <c>admin</c> 帳號實際持有的權限位元（實機 <c>setting\user.bin</c> 的
    /// <c>+0x18</c>＝28）。HeliVMS 的 admin 對齊這個值。
    /// </summary>
    public const int AdminPermission = 0x1c;

    /// <summary>
    /// 真機<b>新建</b>帳號的預設權限（<c>DefaultAccountSetting</c>：<c>*(param_1 + 8) = 8</c>）。
    /// HeliVMS 的 viewer 對齊這個值。
    /// </summary>
    public const int DefaultPermission = 8;

    /// <summary>
    /// HeliVMS 的帳號狀態換算成真機的權限位元：停用＝0（沒有任何權限）、admin＝
    /// <see cref="AdminPermission"/>、其餘＝<see cref="DefaultPermission"/>。
    /// </summary>
    /// <remarks>
    /// 停用的帳號仍然列出來（真機的產生端只跳過「已刪除」，也就是序號為負的帳號；停用不是
    /// 刪除，而且 HeliVMS 的刪除也不會經過這支端點）。權限給 0 是刻意的：QCC 若顯示權限欄，
    /// 至少看得出來這個帳號沒有任何權限，而不是偽裝成一般帳號。
    /// </remarks>
    public static int PermissionFor(bool enabled, bool administrator)
        => !enabled ? 0 : administrator ? AdminPermission : DefaultPermission;

    /// <summary>組出 <c>/QCCamList.bin</c> 的 RMCL payload。</summary>
    public static byte[] CameraList(IReadOnlyList<QctekRemoteCamera> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        var count = Math.Min(cameras.Count, MaxRowCount);
        var buffer = Build(CameraMagic, CameraRowLength, count);

        for (var i = 0; i < count; i++)
        {
            var camera = cameras[i];
            var row = buffer.AsSpan(HeaderLength + (i * CameraRowLength), CameraRowLength);

            WriteWide(row.Slice(0x000, 0x82), camera.Name);
            // 群組欄留空：HeliVMS 沒有「群組」這個概念，編一個名字等於在設定頁說謊。
            WriteWide(row.Slice(0x082, 0x82), camera.Group);

            // Excel 欄 2/3/4/5 的預設值（LoadCameraList 在列不存在時寫入 -1）。
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x104), -1);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x108), -1);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x110), -1);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x114), -1);

            // 欄 7：顏色。真機的預設色，不是我們挑的。
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x10c), DefaultCameraColor);
            // 欄 6（+0x118 u16）預設 0，新配置的緩衝區已經是 0。
        }

        return buffer;
    }

    /// <summary>組出 <c>/QCUserList.bin</c> 的 RMUL payload。</summary>
    public static byte[] UserList(IReadOnlyList<QctekRemoteUser> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        var count = Math.Min(users.Count, MaxRowCount);
        var buffer = Build(UserMagic, UserRowLength, count);

        for (var i = 0; i < count; i++)
        {
            var user = users[i];
            var row = buffer.AsSpan(HeaderLength + (i * UserRowLength), UserRowLength);

            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(0x00), UserRowMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(0x04), UserRowVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(0x06), UserRowFlags);

            var permission = user.Permission;
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x08), permission);

            // 帳號序號：驗證端要求非負（負值＝已刪除）。這是<b>帳號自己的序號</b>，
            // 不是列的位置——QCC 就是拿它去呼叫 /QCRemoveAccount.bin?usr=<序號>
            // 與更新端點的 +0x0c，用列位置會刪到／改到另一個帳號。
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(0x0c), user.Sequence);

            // +0x10／+0x18／+0x20 是建立／到期／修改時間（真機 user.bin 是 Unix 秒）。
            // 我們不知道 HeliVMS 帳號在「裝置帳號」上的這三個時間，留 0 表示未知：
            // 編一個假期限會讓 QCC 顯示一個不存在的到期日，那比顯示 0 更糟。
            //
            // +0x18 同時是「目前登入者」旗標所在的 u32（isCurrentUser 讀的是它）。
            // 時間留 0 正好讓這個位元不會與時間欄混淆。
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(0x18),
                user.IsCurrent ? (uint)CurrentUserBit : 0u);

            WriteAnsi(row.Slice(0x28, 32), user.Username);

            // +0x48 起是真機的 32-byte 密碼雜湊。這裡刻意全留 0——HeliVMS 的雜湊不外流。
        }

        return buffer;
    }

    private static byte[] Build(uint magic, int rowLength, int rowCount)
    {
        var buffer = new byte[HeaderLength + (rowCount * rowLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)rowCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x0c), (uint)rowLength);
        return buffer;
    }

    /// <summary>
    /// 把字串寫進定長 UTF-16LE 欄位：容量含結尾 NUL，超長截斷，其餘位元組維持 0。
    /// </summary>
    private static void WriteWide(Span<byte> field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return; // 新配置的緩衝區已是 0，等同空字串＋NUL。
        }

        var capacity = field.Length / 2; // 含結尾 NUL
        var length = Math.Min(value.Length, capacity - 1);
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--; // 截斷不得切開代理對，否則裝置端拿到半個字。
        }

        Encoding.Unicode.GetBytes(value.AsSpan(0, length), field);
    }

    /// <summary>
    /// 把字串寫進定長 ANSI 欄位。非 ASCII 字元一律寫成 <c>?</c>：真機的欄位是
    /// <c>char[32]</c>（<c>FindAccountIndexByName</c> 逐 byte 比對），寫 UTF-8 會變成
    /// 一串裝置端讀不懂的位元組。HeliVMS 的帳號名稱實務上是 ASCII；真的有中文帳號時寧可
    /// 顯示 <c>?</c>，也不要讓它變成亂碼。
    /// </summary>
    private static void WriteAnsi(Span<byte> field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var length = Math.Min(value.Length, field.Length - 1);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            field[i] = c < 0x80 ? (byte)c : (byte)'?';
        }
    }
}

/// <summary>RMCL 的一列相機（目前只帶名稱；群組留空）。</summary>
public sealed record QctekRemoteCamera(string Name, string Group = "");

/// <summary>
/// RMUL 的一列帳號。<paramref name="Permission"/> 用 <see cref="QctekRemoteList.PermissionFor"/>
/// 換算；<paramref name="Sequence"/> 是帳號自己的序號（HeliVMS 用 <c>users.id</c>），
/// QCC 之後的刪除／更新都用它指名帳號；<paramref name="IsCurrent"/> 只在使用者真的用
/// 該帳號認證時才設（寫在 <c>+0x18</c>，不是權限位元）。
/// </summary>
public sealed record QctekRemoteUser(
    string Username,
    int Permission,
    int Sequence,
    bool IsCurrent = false);
