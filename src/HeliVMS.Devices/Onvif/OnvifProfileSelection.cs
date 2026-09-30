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
/// （MainStream／SubStream／Profile_1／101／Channel1_1…），故採兩層判斷：
/// 先以名稱／token 關鍵字比對，關鍵字皆無命中時再以「同一影像來源下解析度最低者視為子碼流」判定。
/// </summary>
public static class OnvifProfileSelection
{
    private static readonly string[] SubKeywords =
    [
        "substream", "sub", "low", "lowres", "minor", "mobile", "lowquality",
        "stream2", "profile2", "102", "子", "低", "次要", "遠端", "預覽",
    ];

    private static readonly string[] MainKeywords =
    [
        "mainstream", "main", "high", "hires", "highres", "major", "primary",
        "stream1", "profile1", "101", "主", "高", "大",
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
    /// 判定整組 profile 之碼流角色：關鍵字優先；同一影像來源（VideoSourceToken）內
    /// 無關鍵字者，解析度最低者視為子碼流、其餘為主碼流。回傳新實例（原始物件不受影響）。
    /// </summary>
    public static IReadOnlyList<OnvifProfile> Classify(IEnumerable<OnvifProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var source = profiles.ToList();
        var classified = new List<OnvifProfile>(source.Count);
        foreach (var profile in source)
        {
            var role = profile.Role != OnvifStreamRole.Unknown
                ? profile.Role
                : HasSubStreamKeyword(profile)
                    ? OnvifStreamRole.Sub
                    : HasMainStreamKeyword(profile)
                        ? OnvifStreamRole.Main
                        : OnvifStreamRole.Unknown;

            if (role == OnvifStreamRole.Unknown)
            {
                role = ResolveByResolution(source, profile);
            }

            classified.Add(role == profile.Role ? profile : profile.WithRole(role));
        }

        return classified;
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

    /// <summary>挑選最佳子碼流（解析度最低者；不存在時回傳 null）。</summary>
    public static OnvifProfile? SelectSub(IEnumerable<OnvifProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .Where(p => p.IsVideo && p.IsSubStream)
            .OrderBy(p => (long)p.Width * p.Height)
            .FirstOrDefault();
    }

    private static OnvifStreamRole ResolveByResolution(IReadOnlyList<OnvifProfile> all, OnvifProfile profile)
    {
        if (!profile.IsVideo)
        {
            return OnvifStreamRole.Unknown;
        }

        var peers = all
            .Where(p => p.IsVideo && string.Equals(p.VideoSourceToken, profile.VideoSourceToken, StringComparison.Ordinal))
            .ToList();
        if (peers.Count < 2)
        {
            return OnvifStreamRole.Main;
        }

        var area = (long)profile.Width * profile.Height;
        var lowest = peers.Min(p => (long)p.Width * p.Height);
        var highest = peers.Max(p => (long)p.Width * p.Height);
        if (lowest == highest)
        {
            return OnvifStreamRole.Main;
        }

        return area == lowest ? OnvifStreamRole.Sub : OnvifStreamRole.Main;
    }

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
            if (normalizedKeyword.Length > 0 && normalized.Contains(normalizedKeyword, StringComparison.Ordinal))
            {
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
