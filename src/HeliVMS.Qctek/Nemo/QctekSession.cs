using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>請求的線路框法：raw（無 NEMO 標頭）或 NEMO 訊框。</summary>
public enum QctekFraming
{
    /// <summary>原始命令：ASCII 文字＋結尾 NUL（例：<c>CHECK_SERVER_VERSION …</c>）。</summary>
    Raw,

    /// <summary>16 bytes <c>NEMO &lt;len&gt;</c> 標頭＋payload。</summary>
    Nemo,
}

/// <summary>一條已解出框法的 NEMO／raw 命令。</summary>
/// <param name="Command">
/// 命令的文字前段。純文字命令就是完整內容；二進位命令（<c>SEARCH_EVENT</c>）只有前段
/// 可列印 ASCII，二進位本體在 <paramref name="Payload"/>。
/// </param>
/// <param name="Framing">線路框法。</param>
/// <param name="Payload">NEMO 框的完整 payload（raw 命令為空）；二進位命令靠它取參數。</param>
public readonly record struct QctekRequest(
    string Command,
    QctekFraming Framing,
    ReadOnlyMemory<byte> Payload = default)
{
    public bool IsFramed => Framing == QctekFraming.Nemo;

    /// <summary>第一個空白前的命令名（例：<c>GET_CONFIG_QCTEK</c>）。</summary>
    public string Verb => FirstToken(Command, 0);

    /// <summary>第 <paramref name="index"/> 個空白分隔欄位（0＝命令名）；缺少時回空字串。</summary>
    public string Token(int index) => FirstToken(Command, index);

    public static QctekRequest Raw(string command) => new(command, QctekFraming.Raw);

    public static QctekRequest Nemo(string command) => new(command, QctekFraming.Nemo);

    public static QctekRequest Nemo(string command, ReadOnlyMemory<byte> payload)
        => new(command, QctekFraming.Nemo, payload);

    private static string FirstToken(string command, int index)
    {
        if (index < 0)
        {
            return string.Empty;
        }

        var start = 0;
        for (var field = 0; field <= index; field++)
        {
            while (start < command.Length && command[start] == ' ')
            {
                start++;
            }

            var end = command.IndexOf(' ', start);
            if (end < 0)
            {
                end = command.Length;
            }

            if (field == index)
            {
                return start <= end ? command[start..end] : string.Empty;
            }

            start = end;
        }

        return string.Empty;
    }
}

/// <summary>
/// <see cref="QctekSession.Advance"/> 的結果：吃掉幾個位元組、要回什麼、以及是否該關閉連線。
/// </summary>
/// <param name="Consumed">已耗用的位元組數；0＝資料不足，呼叫端應繼續讀。</param>
/// <param name="Reply">要寫回對端的完整線路位元組；<c>null</c>＝不回覆。</param>
/// <param name="Command">這次解析出的命令（供診斷記錄；跳過孤立 NUL 時為 <c>null</c>）。</param>
/// <param name="Close">串流已不同步或無法辨識，應立即關閉連線。</param>
/// <param name="Live">
/// 這次回覆是 <c>GET_LIVE_START</c> 的成功回應：寫完 <see cref="Reply"/> 之後，
/// 同一條連線要交給 <see cref="HeliVMS.Qctek.Live.QctekLiveSession"/> 接手
/// （之後是 keepalive＋FRAM，不是文字命令）。
/// </param>
/// <param name="Playback">
/// 這次回覆是 <c>OPENFILE</c> 的成功回應：寫完 <see cref="Reply"/> 之後，
/// 同一條連線要交給 <see cref="HeliVMS.Qctek.Live.QctekPlaybackSession"/> 接手
/// （回放幀流，時戳＝錄影時刻）。
/// </param>
/// <param name="PlaybackStartUtc">回放的起播時刻（UTC；OPENFILE 的時間參數解析結果）。</param>
/// <param name="PlaybackChannel">回放的 0-based 頻道號。</param>
public readonly record struct QctekSessionResult(
    int Consumed,
    byte[]? Reply,
    string? Command,
    bool Close,
    bool Live = false,
    bool Playback = false,
    DateTime PlaybackStartUtc = default,
    int PlaybackChannel = 0);

/// <summary>
/// 單一 TCP 連線的 NEMO 協定狀態機：分辨 raw／NEMO 框、處理 <c>QCV8001_AUTH</c>
/// 挑戰交換，再把命令交給 <see cref="QctekCommandHandler"/>。
/// </summary>
/// <remarks>
/// <para>
/// 狀態機<b>不碰 socket</b>：<see cref="Advance"/> 只吃累積緩衝、回傳「吃掉多少／回什麼」。
/// 這樣黃金抓包可以用純位元組重播（不必開埠），也不必為了測試等待真實連線的時序。
/// </para>
/// <para>
/// QCC 的實測行為（<c>golden.pcapng</c>）：<b>每個命令一條短連線</b>，而且同一條連線上
/// 命令可能以 raw 或 NEMO 框送出。帶 <c>QCV8001_AUTH</c> 的保護命令先收到 27 bytes 的
/// 原始挑戰（無 NUL），用戶端再送 <c>QCV8001_AUTH_SENDCODE &lt;32hex&gt;</c>＋NUL；
/// 通過後才回原本命令的回應。<c>GET_IOCONFIG</c> 不經挑戰，直接回二進位 payload。
/// </para>
/// <para>
/// <b>安全邊界（已知且刻意）</b>：QCTek 的 digest 是
/// <c>MD5(專有32B轉換密碼 ++ ":" ++ user ++ ":" ++ deviceInfo)</c>，HeliVMS 不保存那份
/// 專有轉換值，因此<b>無法驗證 digest 本身</b>。這裡只驗證「使用者存在且可用」，
/// 並保留 <c>CONNECT</c>／<c>ALARM_REQUEST</c> 的既有密碼驗證。相容層是選用功能，
/// 啟用時應與 CMS 同處受信任網段（設定頁與文件都這樣寫）。
/// </para>
/// </remarks>
public sealed class QctekSession
{
    /// <summary>認證回應的前綴（用戶端在挑戰後送出的 raw 命令）。</summary>
    public const string AuthCodePrefix = "QCV8001_AUTH_SENDCODE ";

    /// <summary>raw 命令的長度上限；超過視為串流不同步（避免無限累積）。</summary>
    public const int MaxRawCommandLength = 4096;

    private const int AuthCodeLength = 32;

    private readonly IQctekDeviceContext _context;
    private readonly Func<string> _challengeFactory;

    private QctekRequest? _pending;
    private bool _authorized;

    /// <summary>
    /// 這條連線上最近一次 <c>SEARCH_EVENT</c> 的查詢。分頁命令（<c>NEXT</c>／<c>PREV</c>）
    /// 只送 4 bytes、不帶任何參數，所以頁碼與查詢條件<b>只能</b>留在連線層；放進
    /// <see cref="QctekCommandHandler"/> 會變成第二份狀態，兩邊遲早漂移。
    /// </summary>
    private QctekEventSearchRequest? _eventQuery;

    private int _eventPage;
    private int _eventTotal;
    private int _cachedEntries;

    /// <summary>已抓過的頁（頁碼 → 內容）。綁住上限，避免 1024 頁 × 大頁寬吃光記憶體。</summary>
    private readonly Dictionary<int, QctekEventPage> _eventPages = [];

    private const int MaxCachedEventEntries = 20_000;

    /// <param name="context">裝置資訊（頻道名、使用者驗證）。</param>
    /// <param name="challengeFactory">挑戰字串產生器；測試注入固定值以重播黃金對話。</param>
    public QctekSession(IQctekDeviceContext context, Func<string>? challengeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _challengeFactory = challengeFactory ?? QctekChallenge.Create;
    }

    /// <summary>是否正在等用戶端回傳 <c>QCV8001_AUTH_SENDCODE</c>。</summary>
    public bool AwaitingAuthorization => _pending is not null;

    /// <summary>
    /// 從累積緩衝解析出「下一條命令」並產生回應。
    /// </summary>
    /// <param name="buffer">自連線讀入、尚未耗用的位元組。</param>
    /// <returns>
    /// <see cref="QctekSessionResult.Consumed"/> 為 0 時表示資料不足，呼叫端應繼續讀；
    /// <see cref="QctekSessionResult.Close"/> 為 <c>true</c> 時應立即關閉連線。
    /// </returns>
    public QctekSessionResult Advance(ReadOnlySpan<byte> buffer)
    {
        // 用戶端有時會在 NEMO 宣告長度之外多寫一個 NUL（例：ALARM_ISALIVE 標頭宣告 34、
        // 實際 35 bytes）。上一輪已把多餘的 NUL 一起吃掉；這裡再容忍連線開頭的孤立 NUL，
        // 否則它會被誤判成「非文字命令」而關閉連線。
        var offset = 0;
        while (offset < buffer.Length && buffer[offset] == 0)
        {
            offset++;
        }

        if (offset == buffer.Length)
        {
            return new QctekSessionResult(offset, null, null, false);
        }

        var data = buffer[offset..];

        if (data.StartsWith("NEMO "u8))
        {
            return AdvanceFramed(data, offset);
        }

        return AdvanceRaw(data, offset);
    }

    private QctekSessionResult AdvanceFramed(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < NemoMessage.HeaderLength)
        {
            return new QctekSessionResult(offset, null, null, false);
        }

        byte[] payload;
        int consumed;
        try
        {
            if (!NemoMessage.TryDecode(data, out payload, out consumed))
            {
                return new QctekSessionResult(offset, null, null, false);
            }
        }
        catch (FormatException)
        {
            return new QctekSessionResult(offset, null, null, true);
        }

        // 標頭長度之外的 NUL 是同一條命令的殘尾（見 Advance 的說明）。
        var extra = 0;
        while (consumed + extra < data.Length && data[consumed + extra] == 0)
        {
            extra++;
        }

        var command = DescribeCommand(payload);
        var total = offset + consumed + extra;
        return Dispatch(QctekRequest.Nemo(command, payload), total);
    }

    /// <summary>
    /// 從 NEMO payload 取出命令文字。
    /// <para>
    /// 純文字命令（<c>CONNECT</c>、<c>ALARM_ISALIVE</c>…）的 payload 全是可列印 ASCII，
    /// 後面可能有 NUL 補滿——舊行為是整段解 UTF-8 再削掉尾端 NUL。二進位命令
    /// （<c>SEARCH_EVENT</c>）在命令名之後就是本體，整段解碼會把本體變成亂碼字串，
    /// 所以命令只取<b>前段可列印 ASCII</b>，本體留在 <see cref="QctekRequest.Payload"/>。
    /// </para>
    /// <para>
    /// 遇到命令名之後不是 NUL 而是非 ASCII（理論上不會發生）就退回舊行為，避免把
    /// 一個純文字命令截斷成半個名字。
    /// </para>
    /// </summary>
    private static string DescribeCommand(byte[] payload)
    {
        var length = 0;
        while (length < payload.Length && payload[length] is >= 0x20 and <= 0x7E)
        {
            length++;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        if (length < payload.Length && payload[length] != 0)
        {
            return Encoding.UTF8.GetString(payload).TrimEnd('\0');
        }

        return Encoding.ASCII.GetString(payload, 0, length);
    }

    private QctekSessionResult AdvanceRaw(ReadOnlySpan<byte> data, int offset)
    {
        // 事件分頁命令只送 4 個位元組、不帶結尾 NUL（QCC 的 DAT_18001cf10 與其後 8 bytes
        // 就是 "NEXT"／"PREV"）。照一般 raw 命令等 NUL 的話會一直等不到，QCC 等 10 秒
        // 後放棄、清單停在第一頁——而兩邊日誌都只有「連線正常」。
        if (data.Length >= 4)
        {
            var head = data[..4];
            if (head.SequenceEqual("NEXT"u8) || head.SequenceEqual("PREV"u8))
            {
                return Dispatch(QctekRequest.Raw(Encoding.ASCII.GetString(head)), offset + 4);
            }
        }

        // 第一 byte 就不是可列印 ASCII：不是 raw 命令，也不是 NEMO 框——不猜重新同步點。
        if (data[0] is < 0x20 or > 0x7E)
        {
            return new QctekSessionResult(offset, null, null, true);
        }

        var nul = data.IndexOf((byte)0);
        var length = nul < 0 ? data.Length : nul;

        for (var i = 0; i < length; i++)
        {
            if (data[i] is < 0x20 or > 0x7E)
            {
                return new QctekSessionResult(offset, null, null, true);
            }
        }

        if (nul < 0)
        {
            // 尚未收到結尾 NUL：等下一包。超過上限代表對端不是講這個協定。
            return new QctekSessionResult(offset, null, null, data.Length > MaxRawCommandLength);
        }

        var command = Encoding.ASCII.GetString(data[..nul]);
        var total = offset + nul + 1;
        if (command.Length == 0)
        {
            return new QctekSessionResult(total, null, null, false);
        }

        return Dispatch(QctekRequest.Raw(command), total);
    }

    private QctekSessionResult Dispatch(QctekRequest request, int consumed)
    {
        if (request.Framing == QctekFraming.Raw &&
            request.Command.StartsWith(AuthCodePrefix, StringComparison.Ordinal))
        {
            // 沒有挑戰在前：不猜、不回覆（多送的認證碼不該被當成命令）。
            return _pending is null
                ? new QctekSessionResult(consumed, null, request.Command, false)
                : CompleteAuthorization(request, consumed);
        }

        // 挑戰交換途中來了別的命令：丟掉過期的待辦狀態，當成新命令處理。
        _pending = null;

        // SEARCH_EVENT 不經 QCV8001_AUTH 挑戰（QC_RM_Event::SearchEvent 直接送框），
        // 而且它的回應要分頁——由連線層處理，不進 QctekCommandHandler。
        if (request.Verb == QctekEventSearchRequest.CommandName)
        {
            return BeginEventSearch(request, consumed);
        }

        if (request.Verb is "NEXT" or "PREV")
        {
            return MoveEventPage(request, consumed);
        }

        if (!_authorized && QctekCommandHandler.RequiresAuthorization(request))
        {
            var challenge = _challengeFactory();
            _pending = request;
            return new QctekSessionResult(consumed, QctekReply.RawExact(challenge), request.Command, false);
        }

        // 已授權後再次 GET_LIVE_START：同一條連線直接交棒（客戶端重開即時影像時這樣做）。
        if (_authorized && QctekCommandHandler.IsLiveStart(request))
        {
            return new QctekSessionResult(
                consumed,
                QctekCommandHandler.Handle(request, _context),
                request.Command,
                false,
                Live: true);
        }

        if (_authorized && QctekCommandHandler.IsPlaybackStart(request))
        {
            return new QctekSessionResult(
                consumed,
                QctekCommandHandler.Handle(request, _context),
                request.Command,
                false,
                Playback: true,
                PlaybackStartUtc: QctekCommandHandler.PlaybackStartUtc(request),
                PlaybackChannel: QctekCommandHandler.PlaybackChannel(request));
        }

        return new QctekSessionResult(
            consumed,
            QctekCommandHandler.Handle(request, _context),
            request.Command,
            false);
    }

    /// <summary>
    /// <c>SEARCH_EVENT</c>：解析查詢、回第 0 頁，並記住查詢供後續 <c>NEXT</c>／<c>PREV</c> 使用。
    /// <para>
    /// 讀不懂的查詢回「0 筆」而不是替對端猜一個時間範圍（同匯出時刻的教訓：默默放寬
    /// 範圍比報錯更難發現）。
    /// </para>
    /// </summary>
    private QctekSessionResult BeginEventSearch(QctekRequest request, int consumed)
    {
        _eventPages.Clear();
        _cachedEntries = 0;
        _eventPage = 0;
        _eventTotal = 0;
        _eventQuery = QctekEventSearchRequest.TryParse(request.Payload.Span);

        if (_eventQuery is null)
        {
            return new QctekSessionResult(
                consumed,
                QctekReply.FramedBinary(QctekEventPayloads.Page(QctekEventPage.Empty)),
                request.Command,
                false);
        }

        var page = PageAt(0);
        return new QctekSessionResult(
            consumed,
            QctekReply.FramedBinary(QctekEventPayloads.Page(page)),
            request.Command,
            false);
    }

    /// <summary>NEXT／PREV：在同一條連線上換頁；超出範圍就停在最前／最後一頁。</summary>
    private QctekSessionResult MoveEventPage(QctekRequest request, int consumed)
    {
        // 沒有查詢在前（對端重用了連線狀態）：回一個乾淨的空頁，不要讓它等滿 10 秒。
        if (_eventQuery is null)
        {
            return new QctekSessionResult(
                consumed,
                QctekReply.FramedBinary(QctekEventPayloads.Page(QctekEventPage.Empty)),
                request.Command,
                false);
        }

        var pageSize = _eventQuery.PageSize;
        var pages = pageSize <= 0 ? 1 : (_eventTotal + pageSize - 1) / pageSize;
        var last = Math.Max(0, pages - 1);
        var target = request.Verb == "NEXT"
            ? Math.Min(_eventPage + 1, last)
            : Math.Max(_eventPage - 1, 0);

        var page = PageAt(target);
        _eventPage = target;
        return new QctekSessionResult(
            consumed,
            QctekReply.FramedBinary(QctekEventPayloads.Page(page)),
            request.Command,
            false);
    }

    /// <summary>
    /// 取得第 <paramref name="index"/> 頁（有快取就用快取）。
    /// <para>
    /// 總筆數以<b>第一頁</b>為準：QCC 用第一頁的 <c>"OK n"</c> 算頁數，後續頁若因為
    /// 期間有新事件而回不同的總數，它會算出不同的頁數、翻頁停在錯的位置。
    /// </para>
    /// </summary>
    private QctekEventPage PageAt(int index)
    {
        if (_eventPages.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var query = _eventQuery!;
        var page = _context.SearchEvents(query, index * query.PageSize, query.PageSize);

        if (index == 0)
        {
            _eventTotal = page.Total;
        }
        else if (page.Total != _eventTotal)
        {
            page = page with { Total = _eventTotal };
        }

        if (_cachedEntries + page.Entries.Count <= MaxCachedEventEntries)
        {
            _eventPages[index] = page;
            _cachedEntries += page.Entries.Count;
        }

        return page;
    }

    private QctekSessionResult CompleteAuthorization(QctekRequest request, int consumed)
    {
        var pending = _pending!.Value;
        _pending = null;

        var code = request.Command[AuthCodePrefix.Length..].Trim();
        var user = pending.Token(1);
        var shaped = code.Length == AuthCodeLength && IsHex(code);

        // digest 無法驗證（見型別說明）；能驗的只有「使用者存在/可用」與欄位形狀。
        if (!shaped || !_context.IsKnownUser(user))
        {
            // 真機的失敗回應：NEMO 12 ＋ "Login Failed"（12 bytes，無 NUL、無尾空白）。
            return new QctekSessionResult(consumed, QctekReply.Framed("Login Failed"), request.Command, false);
        }

        _authorized = true;

        // GET_LIVE_START 的成功回覆（"OK "＋NUL）之後，同一條連線就是即時影像通道：
        // 用戶端不再送文字命令，改送 0x90-byte keepalive；這裡必須告訴呼叫端交棒。
        if (QctekCommandHandler.IsLiveStart(pending))
        {
            return new QctekSessionResult(
                consumed,
                QctekCommandHandler.Handle(pending, _context),
                pending.Command,
                false,
                Live: true);
        }

        // OPENFILE/OPENSECOND 的成功回覆（"OK <檔案資訊>"）之後，同一條連線是回放
        // 通道（FRAM 幀流、時戳＝錄影時刻）；同樣必須告訴呼叫端交棒。
        if (QctekCommandHandler.IsPlaybackStart(pending))
        {
            return new QctekSessionResult(
                consumed,
                QctekCommandHandler.Handle(pending, _context),
                pending.Command,
                false,
                Playback: true,
                PlaybackStartUtc: QctekCommandHandler.PlaybackStartUtc(pending),
                PlaybackChannel: QctekCommandHandler.PlaybackChannel(pending));
        }

        return new QctekSessionResult(
            consumed,
            QctekCommandHandler.Handle(pending, _context),
            pending.Command,
            false);
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
