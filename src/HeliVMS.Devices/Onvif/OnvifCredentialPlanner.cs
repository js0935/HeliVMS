namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 新增攝影機時要預填哪一組 ONVIF 帳密（純函式，供精靈套用）。
/// </summary>
/// <remarks>
/// <para>
/// 現場的機隊幾乎都是同一組帳密（實測這台機器上 7 台 VIVOTEK 全是 <c>root</c>，兩台測試機是
/// <c>admin</c>），而精靈每次開窗都要操作員再打一次：位址／帳號還會記住，密碼每次重打。
/// 這種重複正是把操作員推向「短密碼」的原因，所以密碼改由系統裡已經在用的那一組帶入。
/// </para>
/// <para>
/// 兩條規則，優先序明確：<b>①位址指向已知設備</b>（操作員剛打的就是那台）用那台的帳密——
/// 這是最特定的訊號，也與「重新取得攝影機碼流」的既有語意一致；<b>②否則</b>用全機隊最常用的
/// 一組。上次記住的帳號若與機隊常用的不同（例如臨時測過一台 <c>admin</c> 的機器），
/// 帳號以記住的為準，但密碼留空——我們不知道那組密碼配哪個帳號，填錯的密碼是遮罩的、
/// 看不出來，比留空更糟。
/// </para>
/// <para>
/// 抽成純函式是為了讓上面三種情況都可測試：這一段在視窗程式碼裡就只能靠人眼驗證，
/// 而「預填錯的帳密」的症狀是「搜尋不到設備」——最容易被歸咎於網路。
/// </para>
/// </remarks>
public static class OnvifCredentialPlanner
{
    /// <summary>系統裡已知的一個端點（裝置）與它的帳密。</summary>
    public readonly record struct Endpoint(string Host, string? Username, string? Password);

    /// <summary>要填進精靈的帳密；<c>null</c>＝該欄留空。</summary>
    public readonly record struct Prefill(string? Username, string? Password);

    /// <summary>位址欄留空時要填的內容（沒有可用組合時為 <c>null</c>）。</summary>
    public static Prefill Resolve(
        string? addressText,
        string? rememberedUsername,
        IReadOnlyList<Endpoint> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        // ① 位址指向已知設備：那台的帳密就是答案（位址可能是 IP、主機名或帶 scheme／埠的寫法）。
        var host = HostOf(addressText);
        if (host is not null)
        {
            foreach (var device in devices)
            {
                if (HostOf(device.Host) is { } known
                    && string.Equals(known, host, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(device.Username))
                {
                    return new Prefill(device.Username, device.Password);
                }
            }
        }

        // ② 全機隊最常用的一組。
        if (Preferred(devices.Select(d => (d.Username, d.Password))) is not { } fleet)
        {
            return new Prefill(Remembered(rememberedUsername), null);
        }

        var remembered = Remembered(rememberedUsername);
        if (remembered is null)
        {
            return new Prefill(fleet.Username, fleet.Password);
        }

        // 記住的帳號與機隊常用的一致（大小寫不計）才連密碼一起填。
        return string.Equals(remembered, fleet.Username, StringComparison.OrdinalIgnoreCase)
            ? new Prefill(fleet.Username, fleet.Password)
            : new Prefill(remembered, null);
    }

    /// <summary>
    /// 最常出現的（帳號, 密碼）組合；同票時取先出現的那一組（呼叫端以最新在前排序）。
    /// </summary>
    /// <remarks>
    /// 帳號大小寫不計（<c>Admin</c> 與 <c>admin</c> 是同一組），但回傳原始大小寫——
    /// 攝影機對帳號大小寫敏感的型號不少，改寫它會讓本來會成功的登入變成 401。
    /// 空白帳號不列入（沒有帳號的裝置是「匿名 ONVIF」，不是一組可沿用的帳密）。
    /// </remarks>
    public static (string Username, string Password)? Preferred(
        IEnumerable<(string? Username, string? Password)> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        (string Username, string Password)? best = null;
        var bestCount = 0;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var original = new Dictionary<string, (string Username, string Password)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (username, password) in credentials)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                continue;
            }

            var key = username.Trim() + "\u0000" + (password ?? string.Empty);
            var count = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            counts[key] = count;
            original.TryAdd(key, (username.Trim(), password ?? string.Empty));

            // 嚴格大於才換手：同票保留先出現的那一組（穩定，且「最新在前」由呼叫端負責）。
            if (count > bestCount)
            {
                bestCount = count;
                best = original[key];
            }
        }

        return best;
    }

    /// <summary>
    /// 取出位址運算式裡的主機；空字串、或看起來是網段運算式（CIDR／末碼範圍／清單）時回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 位址欄允許網段運算式（見 <see cref="DiscoveryAddressRange"/>）：那種輸入不對應單一設備，
    /// 拿它去比對已知設備一定會認錯，所以一律當作「沒有指向任何一台」。
    /// </para>
    /// <para>
    /// 兩個容易寫反的細節：有 scheme 的網址要先取 authority 再判運算式（<c>rtsp://cam/live</c> 是
    /// 一台攝影機，不是網段——與 <see cref="DiscoveryAddressRange"/> 對 <c>/</c> 的判準一致）；
    /// 而「連字號」只在一段數字與另一段數字之間才是末碼範圍（<c>192.168.1.1-50</c>），
    /// 主機名自己就常有連字號（<c>vivotek-01.local</c>）。
    /// </para>
    /// </remarks>
    public static string? HostOf(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText))
        {
            return null;
        }

        var text = addressText.Trim();
        if (text.Contains(',') || text.Contains(' ') || text.Contains(';'))
        {
            return null;
        }

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            // 明確的網址：主機就是 authority（路徑與埠不影響是哪一台）。
            text = text[(scheme + 3)..];
            var authorityEnd = text.IndexOfAny("/?#".AsSpan());
            if (authorityEnd >= 0)
            {
                text = text[..authorityEnd];
            }
        }
        else if (text.Contains('/'))
        {
            // 沒有 scheme 卻有 '/'：是 CIDR（192.168.1.0/24），不是單一主機。
            return null;
        }

        if (IsLastOctetRange(text))
        {
            return null;
        }

        text = StripPort(text);
        return text.Length == 0 ? null : text;
    }

    /// <summary>末碼範圍（<c>192.168.1.1-50</c>）或完整範圍（<c>192.168.1.1-192.168.1.50</c>）。</summary>
    private static bool IsLastOctetRange(string text)
    {
        var dash = text.LastIndexOf('-');
        if (dash <= 0 || dash >= text.Length - 1)
        {
            return false;
        }

        return text.AsSpan(0, dash).ToString().All(IsDigitOrDot)
            && text.AsSpan(dash + 1).ToString().All(IsDigitOrDot);
    }

    /// <summary>切掉「最後一段是數字」的埠。</summary>
    /// <remarks>
    /// 只切最後一段：IPv6 的冒號不能當埠處理——寫死切第一個冒號會在 IPv6 位址上認錯主機。
    /// </remarks>
    private static string StripPort(string text)
    {
        var colon = text.LastIndexOf(':');
        return colon > 0 && colon < text.Length - 1 && text.AsSpan(colon + 1).ToString().All(char.IsAsciiDigit)
            ? text[..colon]
            : text;
    }

    private static bool IsDigitOrDot(char c) => char.IsAsciiDigit(c) || c == '.';

    private static string? Remembered(string? rememberedUsername)
        => string.IsNullOrWhiteSpace(rememberedUsername) ? null : rememberedUsername.Trim();
}
