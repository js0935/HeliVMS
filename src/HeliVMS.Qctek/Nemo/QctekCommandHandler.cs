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

    /// <summary>
    /// 驗證 CMS 的登入（<c>CONNECT</c>／<c>ALARM_REQUEST</c> 內含的 user＋密碼原文）。
    /// 密碼已由 <see cref="QctekPassword.Decode"/> 解出；<c>guest</c>／<c>test</c>
    /// 字面預設也走這裡（由實作決定是否接受）。
    /// </summary>
    bool TryAuthenticate(string user, string password);
}

/// <summary>
/// NEMO 命令 → 回應 payload 的純函式派送器（不含任何網路 I/O）。
/// </summary>
/// <remarks>
/// <para>
/// 命令集與回應文字由 QCTek QCV8001 的 <c>QC_Server</c> NEMO 派送器、以及 CMS 端
/// <c>X86\NVDLL.dll</c>（實際的請求順序與欄位）雙向反編譯還原。
/// 連線序：<c>CHECK_SERVER_VERSION CLIENT_AUTH</c> → <c>CONNECT &lt;user&gt; &lt;b64(pass)&gt;</c>
/// → <c>GET_NCHANNEL</c> → <c>ALARM_REQUEST …</c>。
/// </para>
/// <para>
/// 重點不變式：<b>回應多以空白結尾</b>（例：<c>"OK QCV8001 "</c>）——長度由 NEMO 標頭決定，
/// 尾端空白是協定慣例，不要「整理」掉；<c>CONNECT</c> 成功的回應是
/// <c>"OK -1 -1 -1 "</c>（三個欄位；CMS 只檢查第一個整數 ≠ 0）。
/// </para>
/// <para>
/// 尚未還原完整語意的命令（<c>GET_AUTH_USER</c>／<c>GET_CONFIG_QCTEK</c>／<c>GET_IOCONFIG</c>／
/// <c>SEARCH_EVENT</c>）回 <c>null</c>——不猜內容，避免讓 CMS 誤判。
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

        if (command.StartsWith("CONNECT ", StringComparison.Ordinal))
        {
            return HandleConnect(command, context);
        }

        if (command.StartsWith("ALARM_REQUEST ", StringComparison.Ordinal))
        {
            return HandleAlarmRequest(command, context);
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

    /// <summary>
    /// <c>CONNECT &lt;user&gt; &lt;pass&gt; </c>：驗證憑證。
    /// 成功回 QCV8001 同款 <c>"OK -1 -1 -1 "</c>（CMS 只檢查第一個整數 ≠ 0）；
    /// 失敗回 <c>"Login Failed "</c>——CMS 判準是回應中沒有 "OK"，字串沿用參考實作。
    /// </summary>
    private static string HandleConnect(string command, IQctekDeviceContext context)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return "Login Failed ";
        }

        var user = parts[1];
        var password = QctekPassword.Decode(parts[2]);
        return context.TryAuthenticate(user, password) ? "OK -1 -1 -1 " : "Login Failed ";
    }

    /// <summary>
    /// <c>ALARM_REQUEST &lt;n&gt; version7.7 user: &lt;user&gt; &lt;pass&gt; =&lt;serial&gt;</c>：
    /// CMS 訂閱警報。憑證通過時回應兩個識別碼（CMS 原樣存下、之後於
    /// <c>ALARM_CANCEL</c> 帶回）；訊息沒帶 "user:" 的變體不擋。
    /// </summary>
    private static string HandleAlarmRequest(string command, IQctekDeviceContext context)
    {
        var marker = command.IndexOf("user:", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var parts = command[(marker + "user:".Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var user = parts[0];
                var password = QctekPassword.Decode(parts[1]);
                if (!context.TryAuthenticate(user, password))
                {
                    return "FAIL ";
                }
            }
        }

        return "OK 1 1 ";
    }
}
