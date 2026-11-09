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
public readonly record struct QctekRequest(string Command, QctekFraming Framing)
{
    public bool IsFramed => Framing == QctekFraming.Nemo;

    /// <summary>第一個空白前的命令名（例：<c>GET_CONFIG_QCTEK</c>）。</summary>
    public string Verb => FirstToken(Command, 0);

    /// <summary>第 <paramref name="index"/> 個空白分隔欄位（0＝命令名）；缺少時回空字串。</summary>
    public string Token(int index) => FirstToken(Command, index);

    public static QctekRequest Raw(string command) => new(command, QctekFraming.Raw);

    public static QctekRequest Nemo(string command) => new(command, QctekFraming.Nemo);

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

        var command = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        var total = offset + consumed + extra;
        return Dispatch(QctekRequest.Nemo(command), total);
    }

    private QctekSessionResult AdvanceRaw(ReadOnlySpan<byte> data, int offset)
    {
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
