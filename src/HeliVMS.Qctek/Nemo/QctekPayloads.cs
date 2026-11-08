using System.Buffers.Binary;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO 二進位回應的 payload 產生器（裝置設定、使用者權限、IO 設定）。
/// </summary>
/// <remarks>
/// <para>
/// 三個 payload 的版型全部來自 QCC ↔ 真機 QCV8001 的黃金抓包（<c>golden.pcapng</c>，
/// 已存成 <c>config.bin</c>／<c>authuser.bin</c>／<c>ioconfig.bin</c>），並與
/// <c>QC_Server</c> 的 <c>FUN_180006be0</c>（設定）、<c>FUN_180006980</c>（使用者）、
/// <c>FUN_180007060</c>（IO）對照過。共同開頭是 <c>"QCTEK_RM"</c>＋4 bytes 版型碼；
/// 這 4 bytes 在設定／使用者是 <c>1c 3b 34 01</c>，在 IO 是 <c>6d 3d 34 01</c>——不同
/// payload 不同值，不能共用一個常數。
/// </para>
/// <para>
/// <b>設定記錄的能力位元組照抄真機。</b>黃金記錄前 8 bytes 是
/// <c>01 00 01 01 01 01 00 01</c>（支援錄影／已連線／各項能力／正在錄影）。QCC 對這些
/// 欄位的語意定義在 <c>QC_Device.dll</c>（未反編譯），猜錯會讓 QCC 把頻道畫成不可用；
/// 在拿到語意之前，逐位元組沿用真機的值是唯一有證據的選擇。每位元組的意義寫在這裡是
/// 為了讓下一個改的人知道「這是照抄，不是設計」。
/// </para>
/// <para>
/// 名稱是<b>純 UTF-16LE</b>（無 BOM）：黃金 <c>1d 65 71 5f 5f 6a 30 00 30 00 31 00</c>
/// ＝<c>攝影機001</c>，位於記錄位移 <c>0x1C</c>（檔案位移 <c>0x34</c>），容量 0x41 個
/// wchar（64 字＋NUL）。
/// </para>
/// </remarks>
public static class QctekPayloads
{
    /// <summary>設定 payload 的標頭長度；記錄從這裡開始，每筆 <see cref="ConfigRecordLength"/> bytes。</summary>
    public const int ConfigHeaderLength = 0x18;

    /// <summary>單一頻道記錄的長度。</summary>
    public const int ConfigRecordLength = 0x120;

    /// <summary>頻道名稱在記錄內的位移（UTF-16LE）。</summary>
    public const int ConfigNameOffset = 0x1C;

    /// <summary>名稱欄容量（wchar 數，含結尾 NUL；真機 <c>qc_wcscpy(..., 0x41, ...)</c>）。</summary>
    public const int ConfigNameCapacity = 0x41;

    /// <summary>使用者 payload 的固定長度（真機 <c>GET_AUTH_USER</c> 實測）。</summary>
    public const int AuthUserLength = 152;

    /// <summary>IO 設定 payload 的固定長度（真機 <c>GET_IOCONFIG</c> 實測）。</summary>
    public const int IoConfigLength = 280;

    private static ReadOnlySpan<byte> Magic => "QCTEK_RM"u8;

    private static ReadOnlySpan<byte> ConfigFormat => [0x1C, 0x3B, 0x34, 0x01];

    private static ReadOnlySpan<byte> IoFormat => [0x6D, 0x3D, 0x34, 0x01];

    /// <summary>
    /// 真機記錄開頭的能力／狀態位元組：<c>01 00 01 01 01 01 00 01</c>。
    /// 逐位元組照抄黃金記錄（見型別說明）；語意未知，不要為了「看起來合理」而改。
    /// </summary>
    private static ReadOnlySpan<byte> RecordFlags => [0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x00, 0x01];

    /// <summary>
    /// <c>GET_CONFIG_QCTEK</c> 的 payload：24 bytes 標頭＋每頻道一筆 0x120 bytes 記錄。
    /// 總長度＝<c>0x18 + n * 0x120</c>（真機單頻道實測 312 bytes）。
    /// </summary>
    public static byte[] Config(IReadOnlyList<string> channelNames)
    {
        ArgumentNullException.ThrowIfNull(channelNames);

        var buffer = new byte[ConfigHeaderLength + (channelNames.Count * ConfigRecordLength)];
        WriteCommonHeader(buffer, channelNames.Count, ConfigFormat);

        for (var i = 0; i < channelNames.Count; i++)
        {
            var record = buffer.AsSpan(ConfigHeaderLength + (i * ConfigRecordLength));
            RecordFlags.CopyTo(record);
            WriteUtf16(record.Slice(ConfigNameOffset, ConfigNameCapacity * 2), channelNames[i]);
        }

        return buffer;
    }

    /// <summary>
    /// <c>GET_AUTH_USER</c> 的 payload：24 bytes 標頭（同設定版型）＋128 bytes 的
    /// <c>0xFF</c> 權限位圖；位移 20 的 u32 是真機的頻道數（實測 36）。
    /// </summary>
    public static byte[] AuthUser(int channelCount)
    {
        var buffer = new byte[AuthUserLength];
        WriteCommonHeader(buffer, channelCount, ConfigFormat);

        // 128 bytes（1024 bits）全開：QCC 用它判斷這個使用者能看哪些頻道；
        // 0xFF 是真機對 admin 的回應，代表不限制。
        buffer.AsSpan(ConfigHeaderLength).Fill(0xFF);

        return buffer;
    }

    /// <summary>
    /// <c>GET_IOCONFIG</c> 的 payload：<c>"QCTEK_RM"</c>＋<c>6d 3d 34 01</c>，其餘全零
    /// （沒有 DI／DO／警報接點）。長度固定 280 bytes。
    /// </summary>
    public static byte[] IoConfig()
    {
        var buffer = new byte[IoConfigLength];
        Magic.CopyTo(buffer);
        IoFormat.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    private static void WriteCommonHeader(Span<byte> buffer, int count, ReadOnlySpan<byte> format)
    {
        Magic.CopyTo(buffer);
        format.CopyTo(buffer[8..]);

        // 位移 12：真機為 0；位移 16：0xFFFFFFFF（常數，語意未知，照抄）。
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[20..], (uint)count);
    }

    /// <summary>把名稱以 UTF-16LE 寫進固定容量欄位（超過容量截斷；剩餘保持 0＝NUL）。</summary>
    private static void WriteUtf16(Span<byte> destination, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var maxChars = Math.Min(value.Length, (destination.Length / 2) - 1);
        if (maxChars <= 0)
        {
            return;
        }

        // 不用 Encoding.Unicode.GetBytes(value) 直接覆蓋：那會把超長名稱寫出欄位。
        Encoding.Unicode.GetBytes(value.AsSpan(0, maxChars), destination);
    }
}
