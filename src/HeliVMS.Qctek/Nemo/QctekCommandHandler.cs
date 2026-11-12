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

    /// <summary>查詢某頻道在某月的「每天有無錄影」：31 個元素（索引 0 對應 1 日）。</summary>
    IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month);

    /// <summary>查詢某頻道在某月的 32 天×24 小時矩陣（索引 = (day−1)×24 + hour；每 byte 該小時錄影量）。</summary>
    IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month);

    /// <summary>找「fromUtc 之後（含）」的第一個錄影段；找不到回 null。</summary>
    QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc);

    /// <summary>找「含 fromUtc」的錄影段（從段開頭播）——回放跳轉/續播用。</summary>
    QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc);

    /// <summary>
    /// 事件查詢（<c>SEARCH_EVENT</c>）：回第 <paramref name="offset"/> 筆起、最多
    /// <paramref name="limit"/> 筆，並附「符合條件的總筆數」——CMS 用總筆數算頁數，
    /// 不是用本頁筆數。
    /// <para>
    /// <see cref="QctekEventSearchRequest.FromLocal"/>／<see cref="QctekEventSearchRequest.ToLocal"/>
    /// 是<b>本地</b>時間（CMS 端就是本地時間），實作負責換成 UTC 查詢；
    /// <see cref="QctekEventSearchRequest.ChannelIndex"/> 負值＝全部頻道。
    /// </para>
    /// </summary>
    QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit);

    /// <summary>
    /// 開一路「錄影檔回放」的 H.264 來源（Annex-B；回放幀流用）。路徑無效或
    /// ffmpeg 不可用時回 null。
    /// </summary>
    IQctekLiveSource? CreatePlaybackSource(string filePath);
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

        if (IsSilentHeartbeat(command))
        {
            // 真機 QC_RM_Alarm（FUN_180004830 → FUN_180005f10）收到 ALARM_HELLO 時只把
            // 回應緩衝區首位寫 0（空字串），線路上等於 0 bytes；QCC 端（qc_alarm
            // FUN_180015620）送出後只累加計數，**從不讀回應**。
            // 所以這裡必須回 null（不送位元組），不能回一個空的 NEMO 框——多送一框會被
            // 客戶端當成「下一條命令的回應」，之後每一條命令的回應就全部錯位一格，
            // 症狀是「連得上、命令都送得出去、但畫面與清單全空」，兩邊日誌都乾淨。
            return null;
        }

        if (command.StartsWith("GET_LIVE_START", StringComparison.Ordinal))
        {
            // 即時影像交棒：用戶端（dvrProxy_live::AfterAuthorization）只檢查前兩個字元
            // "OK"，但之後立刻在「同一條連線」上改讀 0x90-byte keepalive 與 FRAM——回應
            // 的長度必須是剛好 4 bytes（"OK "＋NUL，黃金抓包 4f4b2000）。多一個或少一個
            // 位元組，客戶端的 keepalive 解析就會錯位，症狀是「即時影像永遠連不上」。
            return Text(request, "OK ", withNul: true);
        }

        if (command.StartsWith("GET_MONTH_DATE", StringComparison.Ordinal))
        {
            return HandleMonthDate(request, context);
        }

        if (command.StartsWith("CATALOG", StringComparison.Ordinal))
        {
            return HandleCatalog(request, context);
        }

        if (command.StartsWith("OPENFILE", StringComparison.Ordinal) ||
            command.StartsWith("OPENSECOND", StringComparison.Ordinal))
        {
            return HandleOpenFile(request, context);
        }

        if (command.StartsWith("SEEKFILE", StringComparison.Ordinal) ||
            command.StartsWith("CLOSEFILE", StringComparison.Ordinal))
        {
            // 回放通道內由 QctekPlaybackSession 處理（SEEK 重定位、CLOSE 結束）；
            // 未在回放中收到時不回應（維持 null 語意，QCC 端自行逾時）。
            return null;
        }

        // 二進位回應一律用 NEMO 框（真機如此，與請求是 raw 或 framed 無關）。
        if (command.StartsWith("GET_CONFIG_QCTEK", StringComparison.Ordinal))
        {
            return QctekReply.FramedBinary(QctekPayloads.Config(context.ChannelNames));
        }

        if (command.StartsWith("GET_AUTH_USER", StringComparison.Ordinal))
        {
            // 位移 20 是機種容量常數，不是實際頻道數：真機（黃金 authuser.bin）在
            // 1 個頻道的裝置上也回 36（QCV8001 的路數），QCC 以它初始化權限表——
            // 回實際頻道數會讓 QCC 的即時影像在授權後立刻關線（ReadDVR 視為權限
            // 不完整）。
            return QctekReply.FramedBinary(QctekPayloads.AuthUser(36));
        }

        if (command.StartsWith("GET_IOCONFIG", StringComparison.Ordinal))
        {
            return QctekReply.FramedBinary(QctekPayloads.IoConfig());
        }

        return null;
    }

    /// <summary>
    /// 已知、但真機連一個位元組都不回應的命令（CMS 的週期性心跳）。
    /// <para>
    /// 目前只有 <c>ALARM_HELLO &lt;n&gt; =&lt;server&gt;</c>：QCC 每 5 秒送一次，單一工作階段
    /// 實測 1,341 次。它與「未知命令」在線路上同樣不回覆，但意義完全相反——把心跳當成
    /// 缺命令去補，等於自己製造一個協定錯位。所以要能分辨，見
    /// <c>NemoSessionLoop</c> 的記錄方式。
    /// </para>
    /// </summary>
    public static bool IsSilentHeartbeat(string command)
        => QctekRequest.Raw(command).Verb is "ALARM_HELLO";

    /// <summary>keepalive 交棒時在診斷記錄裡用的命令名（<c>QctekSession</c> 的
    /// live 交棒結果帶這個名字；它不是線路上的文字命令，只出現在日誌）。</summary>
    public const string KeepAliveCommandName = "QCTEK_RM (keepalive)";

    /// <summary>
    /// 這條命令是否必須先完成 <c>QCV8001_AUTH</c> 挑戰交換。
    /// <para>
    /// 只有黃金抓包證明需要的命令才列入。<c>GET_IOCONFIG</c> 在同一次對話中<b>沒有</b>
    /// 挑戰，直接回二進位——把它一併擋下會讓 QCC 卡在等挑戰。
    /// </para>
    /// </summary>
    public static bool RequiresAuthorization(QctekRequest request)
        => request.Verb is "GET_CONFIG_QCTEK" or "GET_AUTH_USER" or "GET_LIVE_START"
            or "OPENFILE" or "OPENSECOND";

    /// <summary>
    /// 這條命令是否為 <c>GET_LIVE_START</c>（即時影像）：成功回覆後，同一條連線
    /// 就交給 <see cref="QctekLiveSession"/> 接手（不再回文字命令）。
    /// </summary>
    public static bool IsLiveStart(QctekRequest request)
        => request.Verb == "GET_LIVE_START";

    /// <summary>這條命令是否為回放開檔（OPENFILE/OPENSECOND）：成功回覆後交棒到回放通道。</summary>
    public static bool IsPlaybackStart(QctekRequest request)
        => request.Verb is "OPENFILE" or "OPENSECOND";

    /// <summary>
    /// OPENFILE 的起播時刻（UTC）：命令參數為寬鬆解析（年/月/日從數字辨識），
    /// 時分秒目前未含在命令中（OPENSECOND 的秒語意待真機抓包驗證），一律從當日 00:00 起。
    /// </summary>
    public static DateTime PlaybackStartUtc(QctekRequest request)
        => QctekPlaybackRequest.Parse(ExtractNumbers(request.Command)) is { } q
            ? new DateTime(q.Year, q.Month, q.Day, 0, 0, 0, DateTimeKind.Utc)
            : default;

    /// <summary>OPENFILE 的 0-based 頻道號（參數解析；未解析出時 0）。</summary>
    public static int PlaybackChannel(QctekRequest request)
        => QctekPlaybackRequest.Parse(ExtractNumbers(request.Command))?.ChannelIndex ?? 0;

    private static readonly System.Text.RegularExpressions.Regex NumberRegex =
        new(@"-?\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int[] ExtractNumbers(string command)
        => NumberRegex.Matches(command)
            .Select(m => int.TryParse(m.Value, out var n) ? n : int.MaxValue)
            .Where(n => n != int.MaxValue)
            .ToArray();

    private static byte[] Text(QctekRequest request, string text, bool withNul)
        => request.IsFramed
            ? (withNul ? QctekReply.FramedWithNul(text) : QctekReply.Framed(text))
            : QctekReply.Raw(text);

    /// <summary>
    /// <c>GET_MONTH_DATE</c>：月曆查詢（哪些天有錄影）。回應為 NEMO 框
    /// （payload 0x40："OK"＋位移 0x10 起 31 天旗標）。
    /// </summary>
    private static byte[]? HandleMonthDate(QctekRequest request, IQctekDeviceContext context)
    {
        var query = QctekPlaybackRequest.Parse(ExtractNumbers(request.Command));
        if (query is null)
        {
            return QctekReply.FramedBinary(QctekPlaybackPayloads.MonthDate([]));
        }

        var days = context.GetRecordingMonthDays(query.ChannelIndex, query.Year, query.Month);
        return QctekReply.FramedBinary(QctekPlaybackPayloads.MonthDate(days));
    }

    /// <summary>
    /// <c>CATALOG</c>：某天的 32 天×24 小時矩陣查詢。回應為 NEMO 框（payload 0x314）。
    /// </summary>
    private static byte[]? HandleCatalog(QctekRequest request, IQctekDeviceContext context)
    {
        var query = QctekPlaybackRequest.Parse(ExtractNumbers(request.Command));
        if (query is null)
        {
            return QctekReply.FramedBinary(QctekPlaybackPayloads.Catalog([], 0));
        }

        var hours = context.GetRecordingHourMatrix(query.ChannelIndex, query.Year, query.Month);
        return QctekReply.FramedBinary(QctekPlaybackPayloads.Catalog(hours, query.Day));
    }

    /// <summary>
    /// <c>OPENFILE/OPENSECOND</c>：回放開檔（經挑戰授權）。成功回
    /// <c>"OK 頻道 大小 起始秒 0 "</c>（raw，parserFileInfo 解析），
    /// 之後同一條連線交棒到回放幀流（QctekSession 以 Playback 標記驅動）；找不到段回
    /// <c>"FAIL "</c>（QCC 端 FailProcessPb(-0xf) 同款失敗路徑）。
    /// </summary>
    private static byte[]? HandleOpenFile(QctekRequest request, IQctekDeviceContext context)
    {
        var query = QctekPlaybackRequest.Parse(ExtractNumbers(request.Command));
        if (query is null)
        {
            return QctekReply.Raw("FAIL ");
        }

        var start = new DateTime(query.Year, query.Month, query.Day, 0, 0, 0, DateTimeKind.Utc);
        var segment = context.FindSegmentCovering(query.ChannelIndex, start)
            ?? context.FindSegmentFrom(query.ChannelIndex, start);
        if (segment is null)
        {
            return QctekReply.Raw("FAIL ");
        }

        return QctekPlaybackPayloads.OpenFileOk(
            segment.ChannelIndex,
            segment.SizeBytes,
            new DateTimeOffset(segment.StartUtc).ToUnixTimeSeconds());
    }

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
