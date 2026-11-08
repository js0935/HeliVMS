using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Nemo;

/// <summary>QCTek 相容層回應 NEMO 命令所需的裝置資訊與能力。</summary>
public interface IQctekDeviceContext
{
    /// <summary>對 CMS 宣告的產品名（QCV8001 回報 <c>QCV8001</c>）。</summary>
    string ServerVersion { get; }

    /// <summary>頻道數（<c>GET_NCHANNEL</c>；<c>GET_AUTH_USER</c> 的權限筆數）。</summary>
    int ChannelCount { get; }

    /// <summary>頻道名稱（依頻道順序；<c>GET_CONFIG_QCTEK</c> 的每筆記錄用）。</summary>
    IReadOnlyList<string> ChannelNames { get; }

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

    /// <summary>
    /// 使用者是否存在且可用（<c>QCV8001_AUTH</c> 挑戰交換後唯一的驗證）。
    /// <para>
    /// QCTek 的 digest 無法驗證（見 <see cref="QctekSession"/> 的安全說明），所以這裡
    /// <b>不是</b>密碼驗證；本機未啟用登入驗證時應回 <c>true</c>（與
    /// <see cref="TryAuthenticate"/> 的政策一致）。
    /// </para>
    /// </summary>
    bool IsKnownUser(string user);

    /// <summary>
    /// 開一路即時影像的 H.264 來源（Annex-B 基本流；<see cref="QctekLiveSession"/> 消費）。
    /// <para>
    /// <paramref name="channelIndex"/> 是 QCV8001 的 0-based 頻道號（keepalive 位圖的
    /// bit i）；<paramref name="subStream"/>：0＝主碼流、1＝子碼流（QCC keepalive 的
    /// stream type 欄位；子碼流未設定時實作應退回主碼流）。頻道不存在或無法建立
    /// （ffmpeg 不在、攝影機離線）時回 <c>null</c>——live 迴圈對該頻道每秒回狀態幀
    /// 0x8002，不會再對同一頻道立刻重試。
    /// </para>
    /// </summary>
    IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream);
}

/// <summary>
/// NEMO 命令 → 線路回應位元組的純函式派送器（不含任何網路 I/O 與認證狀態）。
/// </summary>
/// <remarks>
/// <para>
/// 命令集、回應文字與<b>每條命令各自的框法</b>由 QCTek QCV8001 的 <c>QC_Server</c>
/// NEMO 派送器、以及 QCC ↔ 真機的黃金抓包（<c>golden.pcapng</c>）雙向還原。
/// 框法的四種型式見 <see cref="QctekReply"/>；不要把它們統一成一種——真機不是統一的。
/// </para>
/// <para>
/// 認證挑戰不由這裡產生：<see cref="QctekSession"/> 先攔下需要授權的命令、完成
/// <c>QCV8001_AUTH_SENDCODE</c> 交換後，才把原本的命令送進來。所以本類別對
/// <c>GET_CONFIG_QCTEK</c> 等命令永遠回「已授權」的 payload。
/// </para>
/// </remarks>
public static class QctekCommandHandler
{
    /// <summary>回傳完整線路回應；<c>null</c>＝未知命令，不回應。</summary>
    public static byte[]? Handle(QctekRequest request, IQctekDeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var command = request.Command;
        var framed = request.IsFramed;

        if (command.StartsWith("CHECK_SERVER_VERSION", StringComparison.Ordinal))
        {
            return Text(request, $"OK {context.ServerVersion} ", withNul: true);
        }

        if (command.StartsWith("CONNECT ", StringComparison.Ordinal))
        {
            return HandleConnect(request, context);
        }

        if (command.StartsWith("ALARM_REQUEST ", StringComparison.Ordinal))
        {
            return HandleAlarmRequest(request, context);
        }

        if (command.StartsWith("CHECK_VNC_VERSION", StringComparison.Ordinal))
        {
            // VNC 未提供時回 FAIL：CMS 才不會後續去連一個不存在的 RFB 埠。
            var text = context.HasVnc ? "OK TVNC_2.7.10 " : "FAIL ";
            return framed ? QctekReply.FramedWithNul(text) : QctekReply.Raw(text);
        }

        if (command.StartsWith("GET_NCHANNEL", StringComparison.Ordinal))
        {
            return Text(request, $"OK {context.ChannelCount} ", withNul: false);
        }

        if (command.StartsWith("GET_MAX_STREAM_NUM", StringComparison.Ordinal))
        {
            // 真機的 framed 回應只寫 strlen("OK %d ") 個位元組，沒有結尾 NUL。
            return Text(request, $"OK {context.MaxStreams} ", withNul: false);
        }

        if (command.StartsWith("ALARM_ISALIVE", StringComparison.Ordinal))
        {
            // 真機依「該 CMS IP 是否還有警報訂閱」回答；HeliVMS 一律接受訂閱，
            // 所以一律 ALIVE（QCC 只用它決定要不要重送 ALARM_REQUEST）。
            return Text(request, "ALIVE ", withNul: true);
        }

        if (command.StartsWith("ALARM_CANCEL", StringComparison.Ordinal))
        {
            return Text(request, "OK ", withNul: true);
        }

        if (command.StartsWith("GET_LIVE_START", StringComparison.Ordinal))
        {
            // 即時影像交棒：用戶端（dvrProxy_live::AfterAuthorization）只檢查前兩個字元
            // "OK"，但之後立刻在「同一條連線」上改讀 0x90-byte keepalive 與 FRAM——回應
            // 的長度必須是剛好 4 bytes（"OK "＋NUL，黃金抓包 4f4b2000）。多一個或少一個
            // 位元組，客戶端的 keepalive 解析就會錯位，症狀是「即時影像永遠連不上」。
            return Text(request, "OK ", withNul: true);
        }

        // 二進位回應一律用 NEMO 框（真機如此，與請求是 raw 或 framed 無關）。
        if (command.StartsWith("GET_CONFIG_QCTEK", StringComparison.Ordinal))
        {
            return QctekReply.FramedBinary(QctekPayloads.Config(context.ChannelNames));
        }

        if (command.StartsWith("GET_AUTH_USER", StringComparison.Ordinal))
        {
            return QctekReply.FramedBinary(QctekPayloads.AuthUser(context.ChannelNames.Count));
        }

        if (command.StartsWith("GET_IOCONFIG", StringComparison.Ordinal))
        {
            return QctekReply.FramedBinary(QctekPayloads.IoConfig());
        }

        return null;
    }

    /// <summary>
    /// 這條命令是否必須先完成 <c>QCV8001_AUTH</c> 挑戰交換。
    /// <para>
    /// 只有黃金抓包證明需要的命令才列入。<c>GET_IOCONFIG</c> 在同一次對話中<b>沒有</b>
    /// 挑戰，直接回二進位——把它一併擋下會讓 QCC 卡在等挑戰。
    /// </para>
    /// </summary>
    public static bool RequiresAuthorization(QctekRequest request)
        => request.Verb is "GET_CONFIG_QCTEK" or "GET_AUTH_USER" or "GET_LIVE_START";

    /// <summary>
    /// 這條命令是否為 <c>GET_LIVE_START</c>（即時影像）：成功回覆後，同一條連線
    /// 就交給 <see cref="QctekLiveSession"/> 接手（不再回文字命令）。
    /// </summary>
    public static bool IsLiveStart(QctekRequest request)
        => request.Verb == "GET_LIVE_START";

    private static byte[] Text(QctekRequest request, string text, bool withNul)
        => request.IsFramed
            ? (withNul ? QctekReply.FramedWithNul(text) : QctekReply.Framed(text))
            : QctekReply.Raw(text);

    /// <summary>
    /// <c>CONNECT &lt;user&gt; &lt;pass&gt; </c>：驗證憑證。
    /// 成功回 QCV8001 同款 <c>"OK -1 -1 -1 "</c>（CMS 只檢查第一個整數 ≠ 0）；
    /// 失敗回 <c>"Login Failed "</c>——CMS 判準是回應中沒有 "OK"，字串沿用參考實作。
    /// 真機的 framed 失敗是 <c>NEMO 13</c>＋13 bytes（無 NUL），raw 是 14 bytes（含 NUL）。
    /// </summary>
    private static byte[] HandleConnect(QctekRequest request, IQctekDeviceContext context)
    {
        var parts = request.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ok = parts.Length >= 3 &&
                 context.TryAuthenticate(parts[1], QctekPassword.Decode(parts[2]));

        if (ok)
        {
            // 真機：raw 是 13 bytes（含 NUL）；framed 是 NEMO 12＋12 bytes（無 NUL）。
            return request.IsFramed
                ? QctekReply.Framed("OK -1 -1 -1 ")
                : QctekReply.Raw("OK -1 -1 -1 ");
        }

        return request.IsFramed
            ? QctekReply.Framed("Login Failed ")   // NEMO 13＋13 bytes，無 NUL
            : QctekReply.Raw("Login Failed ");     // 14 bytes，含 NUL
    }

    /// <summary>
    /// <c>ALARM_REQUEST &lt;n&gt; version7.7 user: &lt;user&gt; &lt;pass&gt; =&lt;serial&gt;</c>：
    /// CMS 訂閱警報。憑證通過時回應真機實測的 <c>OK 0 1 </c>（兩個整數，CMS 原樣存下）；
    /// 訊息沒帶 "user:" 的變體不擋。失敗時 raw 回 <c>FAIL </c>＋NUL，framed 回
    /// <c>NEMO 5</c>＋<c>FAIL </c>＋NUL（真機的兩種框法不同，照抄）。
    /// </summary>
    private static byte[] HandleAlarmRequest(QctekRequest request, IQctekDeviceContext context)
    {
        var marker = request.Command.IndexOf("user:", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var parts = request.Command[(marker + "user:".Length)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var user = parts[0];
                var password = QctekPassword.Decode(parts[1]);
                if (!context.TryAuthenticate(user, password))
                {
                    return request.IsFramed
                        ? QctekReply.FramedWithNul("FAIL ")
                        : QctekReply.Raw("FAIL ");
                }
            }
        }

        return request.IsFramed
            ? QctekReply.Framed("OK 0 1 ")   // 真機 framed 成功：標頭＋7 bytes，無 NUL
            : QctekReply.Raw("OK 0 1 ");     // raw 成功：8 bytes（含 NUL）
    }
}
