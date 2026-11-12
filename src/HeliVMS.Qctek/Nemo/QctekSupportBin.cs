using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// QCC 的「裝置模組狀態」端點（<c>/QCSystemStatus.bin</c>／<c>/QCCameraStatus.bin</c>／
/// <c>/QCBackupSystem.bin</c>）：QCC 連上裝置後會輪詢這三個路徑，404 會讓 QCC 無限重試
/// （實測 3 秒一次）並把對應的裝置功能停在「未就緒」。
/// </summary>
/// <remarks>
/// <para>
/// 版型由 QCC 端的回應解析器（<c>qc_center_decompiled</c>）還原：
/// </para>
/// <list type="bullet">
/// <item><c>QCSystemStatus</c>（<c>0x180004500</c>）：HTTP 200、body 開頭
/// <c>"NSSB"</c>＋u32 0、且<b>回應長度恰為 0x400（1024）bytes</b>——兩個條件都過了，
/// QCC 才會接著抓 <c>/QCBackupSystem.bin</c>。</item>
/// <item><c>QCCameraStatus</c>（<c>0x18000f740</c>）：body 開頭 <c>"NCSB"</c>＋u32 0＋
/// u32 列數；其後為每列 412 bytes 的相機狀態（名稱／IP／埠／連線旗標——欄位位移由
/// 解析器還原，但真機抓包缺跨欄位的對照，先回 0 列：QCC 收到「0 台」是合法狀態，
/// 不會重試；回錯誤的列反而可能讓解析器吃進垃圾）。</item>
/// <item><c>QCBackupSystem</c>（真機 <c>qc_web_server/support_bin.cpp</c>）：回應
/// <c>application/octet-stream</c> 的設定檔（真機是 <c>QC_Config::Export</c> 的 .cfg）；
/// QCC 的 BackupSystem 模組拿它做設定備份。HeliVMS 以自身的設定快照（JSON）替代——
/// 內容不同但「可下載的設定檔」語意相同，模組不再卡在重試。</item>
/// </list>
/// </remarks>
public static class QctekSupportBin
{
    /// <summary>SystemStatus 回應的固定長度（QCC 檢查 0x400）。</summary>
    public const int SystemStatusLength = 0x400;

    /// <summary>裝置 是否可辨識為支援 bin 端點的路徑。</summary>
    public static bool IsSupportBinPath(string path)
        => path.Equals("/QCSystemStatus.bin", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/QCCameraStatus.bin", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/QCBackupSystem.bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>組出 SystemStatus 的 1024-byte NSSB payload。</summary>
    public static byte[] SystemStatus()
    {
        var buffer = new byte[SystemStatusLength];
        "NSSB"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0);
        // 其餘位元組維持 0：解析器只驗魔術與長度；未知欄位照「未提供」的零值語意。
        return buffer;
    }

    /// <summary>組出 CameraStatus 的 NCSB payload（0 列——目前碼流狀態未對接）。</summary>
    public static byte[] CameraStatus()
    {
        var buffer = new byte[12];
        "NCSB"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0);
        return buffer;
    }

    /// <summary>組出 BackupSystem 的設定檔內容（QCC 的設定備份下載）。</summary>
    public static byte[] BackupSystem(string remoteConsolePort, int totalChannels)
    {
        var text = new StringBuilder();
        text.AppendLine("[HeliVMS]");
        text.AppendLine(CultureInfo.InvariantCulture, $"Product={QctekHttpResponder.ProductName}");
        text.AppendLine(CultureInfo.InvariantCulture, $"RemoteConsolePort={remoteConsolePort}");
        text.AppendLine(CultureInfo.InvariantCulture, $"TotalChannel={totalChannels}");
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
