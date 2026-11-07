namespace HeliVMS.Qctek.Nemo;

/// <summary>QCTek 相容層回應 NEMO 命令所需的裝置資訊與能力。</summary>
public interface IQctekDeviceContext
{
    /// <summary>對 CMS 宣告的產品名（QCV8001 回報 <c>QCV8001</c>）。</summary>
    string ServerVersion { get; }

    /// <summary>頻道數（<c>GET_NCHANNEL</c>）。</summary>
    int ChannelCount { get; }

    /// <summary>每頻道最大串流數（<c>GET_MAX_STREAM_NUM</c>；QCV8001 慣例為 2）。</summary>
    int MaxStreams { get; }

    /// <summary>是否提供 VNC（<c>CHECK_VNC_VERSION</c>；QCV8001 內建 TightVNC）。</summary>
    bool HasVnc { get; }
}

/// <summary>
/// NEMO 命令 → 回應 payload 的純函式派送器（不含任何網路 I/O）。
/// </summary>
/// <remarks>
/// <para>
/// 命令集與回應文字由 QCTek QCV8001 的 <c>QC_Server</c> NEMO 派送器反編譯還原。
/// 重點不變式：<b>回應多以空白結尾</b>（例：<c>"OK QCV8001 "</c>）——長度由 NEMO 標頭決定，
/// 尾端空白是協定慣例，不要「整理」掉。
/// </para>
/// <para>
/// 尚未還原完整語意的命令（<c>CONNECT</c>／<c>CLIENT_AUTH</c> 流程、
/// <c>GET_AUTH_USER</c>／<c>GET_CONFIG_QCTEK</c>／<c>GET_IOCONFIG</c>／<c>SEARCH_EVENT</c>）
/// 先不回 <c>null</c> 以外的猜測——待動態驗證後再實作，避免回錯內容讓 CMS 誤判。
/// </para>
/// </remarks>
public static class QctekCommandHandler
{
    /// <summary>回傳回應 payload；<c>null</c>＝未知命令，不回應。</summary>
    public static string? Handle(string command, IQctekDeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.StartsWith("CHECK_SERVER_VERSION", StringComparison.Ordinal))
        {
            return $"OK {context.ServerVersion} ";
        }

        if (command.StartsWith("CHECK_VNC_VERSION", StringComparison.Ordinal))
        {
            // VNC 未提供時回 FAIL：CMS 才不會後續去連一個不存在的 RFB 埠。
            return context.HasVnc ? "OK TVNC_2.7.10 " : "FAIL ";
        }

        if (command.StartsWith("GET_NCHANNEL", StringComparison.Ordinal))
        {
            return $"OK {context.ChannelCount} ";
        }

        if (command.StartsWith("GET_MAX_STREAM_NUM", StringComparison.Ordinal))
        {
            return $"OK {context.MaxStreams} ";
        }

        if (command.StartsWith("ALARM_ISALIVE", StringComparison.Ordinal))
        {
            return "ALIVE ";
        }

        if (command.StartsWith("ALARM_CANCEL", StringComparison.Ordinal))
        {
            return "OK ";
        }

        return null;
    }
}
