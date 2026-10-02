namespace HeliVMS.Rtc;

/// <summary>
/// 把 answer SDP 裡的 host 候選改寫成對外位址。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：伺服器在 NAT 背後時，SIPSorcery 從網卡讀到的是內網位址，
/// 瀏覽器拿到後會去連一個它根本路由不到的位址——症狀是「同網段可以連、跨網段連不上」。
/// 維運端知道自己的對外位址，所以讓 <c>HELIVMS_WHEP_PUBLIC_HOST</c> 決定要廣告什麼。
/// </para>
/// <para>
/// 刻意做成<b>純字串函式</b>：改寫 SDP 聽起來很脆弱，但這一段的規則其實很窄
/// （只動 <c>a=candidate:</c> 行、只動 <c>typ host</c>、只換 address 與 port），
/// 而純函式才能被測試鎖住。srflx／relay 候選一個字都不碰——它們由 STUN／TURN 產生，
/// 本來就是對外位址，覆寫只會把它弄錯。
/// </para>
/// </remarks>
public static class IceCandidateRewriter
{
    /// <summary>改寫 SDP 中的 host 候選；<paramref name="publicPort"/> 為 <c>null</c> 時保留原本的埠。</summary>
    /// <returns>改寫後的 SDP；沒有可改寫的 host 候選時原樣回傳。</returns>
    public static string Rewrite(string sdp, string publicHost, int? publicPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdp);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicHost);

        // SDP 規範要求 CRLF，但實務上兩種都會遇到；跟著原文件的換行走，不要順手「修正」它。
        var newLine = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = sdp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = RewriteLine(lines[i], publicHost, publicPort);
        }

        return string.Join(newLine, lines);
    }

    /// <summary>SDP 裡是否存在可改寫的 host 候選。</summary>
    public static bool HasHostCandidate(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);

        foreach (var line in sdp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (IsHostCandidate(line)) return true;
        }

        return false;
    }

    private static bool IsHostCandidate(string line)
    {
        if (!line.StartsWith("a=candidate:", StringComparison.Ordinal)) return false;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            if (fields[i] == "typ" && fields[i + 1] == "host") return true;
        }

        return false;
    }

    private static string RewriteLine(string line, string publicHost, int? publicPort)
    {
        if (!IsHostCandidate(line)) return line;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 格式：candidate:<foundation> <component> <transport> <priority> <address> <port> typ host ...
        // address 與 port 的位置由 ICE 規範固定，因此可以直接索引；
        // 少於六個欄位代表這行不是我們認得的候選格式，原樣保留比猜測安全。
        if (fields.Length < 6) return line;

        fields[4] = publicHost;
        if (publicPort is { } port) fields[5] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // raddr/rport 描述的是 NAT 前的 base 位址；改寫它會讓瀏覽器拿到自相矛盾的候選。
        return string.Join(' ', fields);
    }
}
