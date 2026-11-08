namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCC ↔ 真機 QCV8001 的黃金抓包位元組（<c>golden.pcapng</c>，32 條短連線）。
/// </summary>
/// <remarks>
/// <para>
/// 這些常數是<b>證據</b>，不是「看起來合理」的資料：它們直接取自抓包的 TCP payload，
/// 逐位元組貼進來。測試斷言線路位元組時一律比對這些常數，而不是比對自己產生的
/// 期望值——否則「實作改了、測試跟著改」會讓相容性悄悄失效。
/// </para>
/// <para>
/// 抓包裡每條連線是「請求＋（挑戰）＋認證碼＋回應」，這裡已依方向切成數段；
/// 段名後面的 stream 編號對應 <c>streams.txt</c>，方便回頭查證。
/// </para>
/// </remarks>
internal static class GoldenCapture
{
    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // ── stream 0：raw 版本查詢 ────────────────────────────────────────────────
    public const string CheckServerVersionRequest =
        "434845434b5f5345525645525f56455253494f4e20434c49454e545f4155544820514354454b2000";

    public const string CheckServerVersionReply = "4f4b20514356383030312000";

    // ── stream 2：framed 設定查詢（挑戰→認證碼→312 bytes 二進位） ──────────
    public const string ConfigRequest =
        "4e454d4f2033352020202020202020204745545f434f4e4649475f514354454b2061646d696e20514356383030315f41555448";

    public const string ConfigChallenge = "lf6AdoSqOMFtt3ln 1773482693";

    public const string ConfigSendCode =
        "514356383030315f415554485f53454e44434f444520663161613363653837343937353933613830336262313833303739663435353000";

    /// <summary>真機對 <c>GET_CONFIG_QCTEK</c> 的 312 bytes payload（單頻道 <c>攝影機001</c>）。</summary>
    public const string ConfigPayloadBase64 =
        "UUNURUtfUk0cOzQBAAAAAP////8BAAAAAQABAQEBAAEAAAAAAAAAAAAAAAAAAAAAAAAAAB1lcV9fajAAMAAxAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // ── stream 4：framed 使用者查詢（真機頻道數 36） ────────────────────────
    public const string AuthUserRequest =
        "4e454d4f2033322020202020202020204745545f415554485f555345522061646d696e20514356383030315f41555448";

    public const string AuthUserChallenge = "NHioFQ6S20tVVfNP 1773482693";

    public const string AuthUserSendCode =
        "514356383030315f415554485f53454e44434f444520393432353832376639336664393433623236653063333334393538313838333500";

    /// <summary>真機對 <c>GET_AUTH_USER</c> 的 152 bytes payload（位移 20 的計數為 36）。</summary>
    public const string AuthUserPayloadBase64 =
        "UUNURUtfUk0cOzQBAAAAAP////8kAAAA//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////8=";

    // ── stream 6：raw 即時監看啟動 ──────────────────────────────────────────
    public const string LiveStartRequest =
        "4745545f4c4956455f53544152542061646d696e20514356383030315f4155544800";

    public const string LiveStartChallenge = "93SY1AqCmkdFFP9/ 1773482693";

    public const string LiveStartSendCode =
        "514356383030315f415554485f53454e44434f444520636166323963623631326231666138373632636661376263326537303536636100";

    public const string LiveStartReply = "4f4b2000";

    // ── stream 16：raw 警報訂閱（Base64 密碼 <c>YWRtaW4=</c> ＝ admin） ──────
    public const string AlarmRequest =
        "414c41524d5f5245515545535420353132322076657273696f6e372e3720757365723a2061646d696e2059575274615734203d5143563030303030303030303000";

    public const string AlarmRequestReply = "4f4b203020312000";

    // ── stream 18：framed IO 設定（沒有挑戰） ────────────────────────────────
    public const string IoConfigRequest =
        "4e454d4f2031332020202020202020204745545f494f434f4e46494720";

    /// <summary>真機對 <c>GET_IOCONFIG</c> 的 280 bytes payload（無 DI／DO）。</summary>
    public const string IoConfigPayloadBase64 =
        "UUNURUtfUk1tPTQBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    // ── stream 24／25：framed 警報存活查詢（標頭宣告長度含 payload 內 NUL） ──
    public const string AlarmIsAliveSerial0Request =
        "4e454d4f203334202020202020202020414c41524d5f4953414c4956452035313232203d5143563030303030303030303000";

    public const string AlarmIsAliveSerial0Reply =
        "4e454d4f203620202020202020202020414c4956452000";

    public const string AlarmIsAliveSerial1Request =
        "4e454d4f203334202020202020202020414c41524d5f4953414c4956452035313232203d5143563030303030303030303100";

    /// <summary>真機對第二個序號回 <c>NOT ALIVE</c>；HeliVMS 刻意一律回 <c>ALIVE</c>（見 <see cref="HeliVMS.Qctek.Nemo.QctekCommandHandler"/>）。</summary>
    public const string AlarmIsAliveSerial1ReplyOnDevice =
        "4e454d4f2031302020202020202020204e4f5420414c4956452000";

    public static byte[] ConfigPayload => Convert.FromBase64String(ConfigPayloadBase64);

    public static byte[] AuthUserPayload => Convert.FromBase64String(AuthUserPayloadBase64);

    public static byte[] IoConfigPayload => Convert.FromBase64String(IoConfigPayloadBase64);
}
