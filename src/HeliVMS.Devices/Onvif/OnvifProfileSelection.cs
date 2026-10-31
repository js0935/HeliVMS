namespace HeliVMS.Devices.Onvif;

/// <summary>Profile 之碼流角色（主／子）。</summary>
public enum OnvifStreamRole
{
    /// <summary>尚未判定。</summary>
    Unknown,

    /// <summary>主要碼流。</summary>
    Main,

    /// <summary>子／次要碼流。</summary>
    Sub,
}

/// <summary>
/// 多廠牌主／子碼流判斷與排序。各廠牌 profile 命名差異極大
/// （MainStream／SubStream／Profile_1／Profile100／Channel1_1…），故採三層判斷：
/// 先看同一影像來源下的編號順序（最小＝主、第二小＝子），其次比對名稱／token 關鍵字，
/// 兩者皆無命中時再以「同一影像來源下解析度最低者視為子碼流」判定。
/// </summary>
public static class OnvifProfileSelection
{
    // 關鍵字一律以小寫字母或中文開頭，數字只能出現在結尾代表「第幾路」（stream1／profile2）。
    // 不要加 "101"／"102" 這種絕對數字：那會讓 Profile101 這種完全不同的編號命中，
    // 實機上造成次碼流被帶成串流3。結尾數字的邊界由 ContainsAnyKeyword 把關。
    private static readonly string[] SubKeywords =
    [
        "substream", "sub", "low", "lowres", "minor", "mobile", "lowquality",
        "stream2", "profile2", "子", "低", "次要", "遠端", "預覽",
    ];

    private static readonly string[] MainKeywords =
    [
        "mainstream", "main", "high", "hires", "highres", "major", "primary",
        "stream1", "profile1", "主", "高", "大",
    ];

    /// <summary>名稱或 token 是否命中子碼流關鍵字。</summary>
    public static bool HasSubStreamKeyword(OnvifProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ContainsAnyKeyword(profile.Name, SubKeywords) || ContainsAnyKeyword(profile.Token, SubKeywords);
    }

    /// <summary>名稱或 token 是否命中主碼流關鍵字。</summary>
    public static bool HasMainStreamKeyword(OnvifProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ContainsAnyKeyword(profile.Name, MainKeywords) || ContainsAnyKeyword(profile.Token, MainKeywords);
    }

    /// <summary>
    /// 判定整組 profile 之碼流角色：編號順序優先，其次名稱／token 關鍵字，
    /// 兩者皆無命中時再以「同一影像來源下解析度最低者視為子碼流」判定。
    /// 回傳新實例（原始物件不受影響）。
    /// </summary>
    /// <remarks>
    /// 編號必須先於關鍵字判斷，因為關鍵字無法表達「第幾路」：實機症狀是新增攝影機時
    /// 次碼流一律帶入「串流3」，而該機的 token 是 Profile100／Profile101／Profile102。
    /// 舊規則把「101」當主碼流、「102」當子碼流做絕對數字比對，於是串流2（Profile101）
    /// 被判成主碼流、串流3（Profile102）被當成唯一的子碼流，自動帶入的位址就是錯的。
    /// 同一個前綴下的編號順序才是跨廠牌都成立的訊號（Profile100／101／102、
    /// Profile_1／_2、Stream1／2、Channel1_1／_2 都是同一個規則）。
    /// </remarks>
    public static IReadOnlyList<OnvifProfile> Classify(IEnumerable<OnvifProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var source = profiles.ToList();
        var roles = new OnvifStreamRole[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            roles[i] = source[i].Role;
        }

        var indexed = source.Select((profile, index) => (Profile: profile, Index: index)).ToList();
        foreach (var group in indexed.GroupBy(x => x.Profile.VideoSourceToken, StringComparer.Ordinal))
        {
            var members = group.ToList();
            AssignNumberedSeries(members, roles);
            AssignKeywords(members, roles);
            AssignRemainingByResolution(members, roles);
        }

        var classified = new List<OnvifProfile>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            classified.Add(roles[i] == source[i].Role ? source[i] : source[i].WithRole(roles[i]));
        }

        return classified;
    }

    /// <summary>
    /// 同一個影像來源下，名稱／token 結尾的編號最小者為主碼流、第二小者為子碼流。
    /// </summary>
    /// <remarks>
    /// 第三路以後（行動／第三方的更低碼流）刻意不標成子碼流：多分割與新增攝影機的
    /// 次碼流都取「子碼流」，標成子碼流就會再挑到它一次。
    /// </remarks>
    private static void AssignNumberedSeries(
        List<(OnvifProfile Profile, int Index)> members,
        OnvifStreamRole[] roles)
    {
        var numbered = members
            .Where(x => x.Profile.IsVideo)
            .Select(x => (x.Profile, x.Index, Number: StreamNumber(x.Profile)))
            .Where(x => x.Number is not null)
            .ToList();
        if (numbered.Count < 2)
        {
            return;
        }

        // 前綴要一致才算同一組編號（Profile100／101／102）；混用不同命名時退回關鍵字與解析度。
        var prefix = numbered[0].Number!.Value.Prefix;
        if (numbered.Any(x => !string.Equals(x.Number!.Value.Prefix, prefix, StringComparison.Ordinal)))
        {
            return;
        }

        var ordered = numbered
            .Select(x => x.Number!.Value.Number)
            .Distinct()
            .OrderBy(number => number)
            .ToList();
        if (ordered.Count < 2)
        {
            return;
        }

        foreach (var (_, index, number) in numbered)
        {
            if (roles[index] != OnvifStreamRole.Unknown)
            {
                continue;
            }

            roles[index] = ordered.IndexOf(number!.Value.Number) switch
            {
                0 => OnvifStreamRole.Main,
                1 => OnvifStreamRole.Sub,
                _ => roles[index],
            };
        }
    }

    /// <summary>解析名稱／token 結尾的串流編號（Profile101 → 101、Channel1_2 → 2）。</summary>
    private static (string Prefix, int Number)? StreamNumber(OnvifProfile profile)
        => ParseStreamNumber(profile.Token) ?? ParseStreamNumber(profile.Name);

    private static (string Prefix, int Number)? ParseStreamNumber(string value)
    {
        var text = value.Trim();
        var digitsAt = text.Length;
        while (digitsAt > 0 && char.IsAsciiDigit(text[digitsAt - 1]))
        {
            digitsAt--;
        }

        // 沒有以數字結尾，或數字超過四位（時間戳、序號）都不當成碼流編號。
        if (digitsAt == text.Length || text.Length - digitsAt > 4)
        {
            return null;
        }

        var prefix = text[..digitsAt];

        // 前綴只有一個字元時那是編碼格式的尾巴（H264／H265），不是碼流編號；
        // 純數字 token（101／102）整串就是編號，前綴為空是合法的。
        if (prefix.Length is > 0 and < 3)
        {
            return null;
        }

        return int.TryParse(text[digitsAt..], out var number)
            ? (prefix.ToLowerInvariant(), number)
            : null;
    }

    private static void AssignKeywords(
        List<(OnvifProfile Profile, int Index)> members,
        OnvifStreamRole[] roles)
    {
        foreach (var (profile, index) in members)
        {
            if (roles[index] != OnvifStreamRole.Unknown)
            {
                continue;
            }

            roles[index] = HasSubStreamKeyword(profile)
                ? OnvifStreamRole.Sub
                : HasMainStreamKeyword(profile)
                    ? OnvifStreamRole.Main
                    : OnvifStreamRole.Unknown;
        }
    }

    /// <summary>
    /// 完全沒有線索（無編號、無關鍵字）時才用解析度猜：同一影像來源內最低的是子碼流。
    /// </summary>
    /// <remarks>
    /// 已經有子碼流時不再猜：三路相機的第三路通常就是最低解析度，用解析度補位會多出
    /// 第二個子碼流候選，SelectSub 不論取「最接近主碼流」或「最低」都可能挑錯。
    /// </remarks>
    private static void AssignRemainingByResolution(
        List<(OnvifProfile Profile, int Index)> members,
        OnvifStreamRole[] roles)
    {
        if (members.Any(x => roles[x.Index] == OnvifStreamRole.Sub))
        {
            return;
        }

        var video = members.Where(x => x.Profile.IsVideo).ToList();
        if (video.Count == 0)
        {
            return;
        }

        var areas = video.Select(x => (long)x.Profile.Width * x.Profile.Height).ToList();
        var lowest = areas.Min();
        var highest = areas.Max();
        foreach (var (profile, index) in video)
        {
            if (roles[index] != OnvifStreamRole.Unknown)
            {
                continue;
            }

            var area = (long)profile.Width * profile.Height;
            roles[index] = video.Count > 1 && lowest != highest && area == lowest
                ? OnvifStreamRole.Sub
                : OnvifStreamRole.Main;
        }
    }

    /// <summary>依主碼流優先、解析度遞減排序（供 UI 顯示）。</summary>
    public static IReadOnlyList<OnvifProfile> Order(IEnumerable<OnvifProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .OrderBy(p => p.IsSubStream)
            .ThenByDescending(p => (long)p.Width * p.Height)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 選取最佳主碼流（已探測之解析度、已驗證可用者優先）。
    /// <para>
    /// 排序以「實際探測成功」為最高優先：即使被分為子碼流，已驗證可連的 profile
    /// 仍勝過探測失敗（保留 ONVIF 宣告值）的主碼流。實機：主碼流宣告 1920×1080
    /// 卻回 503、僅 640×360 子碼流可拉的攝影機，若只依宣告值就要挑腳本挑到壞掉的主碼流。
    /// 相機恢復後主碼流會重新探測成功，權重自然回到它身上，不需手動設定。
    /// </para>
    /// </summary>
    public static OnvifProfile? SelectMain(IEnumerable<OnvifProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        return profiles
            .Where(p => p.IsVideo)
            .OrderByDescending(p => p.ResolutionIsProbed)
            .ThenBy(p => p.IsSubStream)
            .ThenByDescending(p => (long)p.Width * p.Height)
            .FirstOrDefault()
            ?? profiles.FirstOrDefault();
    }

    /// <summary>
    /// 挑選最佳子碼流（供多分割即時監看；不存在時回傳 null）。
    /// <para>
    /// 具名子碼流（名稱／token 命中 SubStream／Stream2…）優先於僅由「同來源解析度最低」
    /// 推斷者。多碼流相機常另提供第三路（更低的行動／第三方串流）；若只取解析度最低者會誤選
    /// 第三路。實機症狀：新增攝影機時次碼流一律帶入「串流3」，但即時監看要用的是「串流2」。
    /// 具名者中取最接近主碼流（解析度最高）者；完全無具名子碼流時維持既有行為（取最低解析度）。
    /// </para>
    /// <para>
    /// 「第幾路」已由 <see cref="Classify"/> 的編號規則決定（同一前綴下第二小的編號＝子碼流），
    /// 這裡不再猜；三路以上相機的第三路因此在 <see cref="Classify"/> 後仍為 Unknown，
    /// 不會變成第二個子碼流候選。
    /// </para>
    /// </summary>
    public static OnvifProfile? SelectSub(IEnumerable<OnvifProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var subs = profiles.Where(p => p.IsVideo && p.IsSubStream).ToList();
        if (subs.Count == 0)
        {
            return null;
        }

        var named = subs.Where(HasSubStreamKeyword).ToList();
        if (named.Count > 0)
        {
            return named
                .OrderByDescending(p => (long)p.Width * p.Height)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        return subs
            .OrderBy(p => (long)p.Width * p.Height)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>
    /// 子字串比對關鍵字，但關鍵字結尾的數字必須是完整的編號。
    /// </summary>
    /// <remarks>
    /// 關鍵字表裡的「1」「2」代表「第一路／第二路」，不是任意出現的數字。少了結尾的邊界判斷，
    /// <c>profile1</c> 會命中 <c>Profile100</c>、<c>Profile102</c> 這些完全不同的串流編號
    /// （實機症狀：三碼流相機 token 為 Profile100／Profile101／Profile102，次碼流被帶成串流3）。
    /// 關鍵字一律不以數字開頭（見 <see cref="SubKeywords"/> 的註解），所以只需擋後面那一位。
    /// 找不到合法位置就繼續往後找，不能直接跳出迴圈。
    /// </remarks>
    private static bool ContainsAnyKeyword(string value, IReadOnlyList<string> keywords)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var normalized = Normalize(value);
        foreach (var keyword in keywords)
        {
            var normalizedKeyword = Normalize(keyword);
            if (normalizedKeyword.Length == 0)
            {
                continue;
            }

            var endsWithDigit = char.IsAsciiDigit(normalizedKeyword[^1]);
            for (var at = normalized.IndexOf(normalizedKeyword, StringComparison.Ordinal);
                 at >= 0;
                 at = normalized.IndexOf(normalizedKeyword, at + 1, StringComparison.Ordinal))
            {
                var end = at + normalizedKeyword.Length;
                if (endsWithDigit && end < normalized.Length && char.IsAsciiDigit(normalized[end]))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value) => value
        .Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();
}
