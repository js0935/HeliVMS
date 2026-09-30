namespace HeliVMS.Licensing;

/// <summary>
/// 授權等級定義（§19.3 等級 → 功能矩陣）。
///
/// 等級僅為簽發端的命名便利；產品端驗證時以 <see cref="LicensePayload.Features"/>
/// 與 <see cref="LicensePayload.Cameras"/> 為準，等級本身不寫入 payload，
/// 以維持 HELVMS-v2 格式穩定並避免同一組功能因名稱差異被誤判。
/// </summary>
public sealed record LicenseTier
{
    /// <summary>等級名稱（繁體中文，簽發時顯示）。</summary>
    public required string Name { get; init; }

    /// <summary>建議通道數上限。</summary>
    public required int DefaultCameras { get; init; }

    /// <summary>該等級預設啟用的功能旗標。</summary>
    public required string[] Features { get; init; }
}

/// <summary>等級矩陣（§19.3）。簽發工具應由此表推導通道數與功能旗標。</summary>
public static class LicenseTiers
{
    /// <summary>功能旗標：核心即時監看與全時錄影。</summary>
    public const string FeatureCore = "core";

    /// <summary>功能旗標：排程錄影。</summary>
    public const string FeatureSchedule = "schedule";

    /// <summary>功能旗標：事件 AI（含 motion）。</summary>
    public const string FeatureAi = "ai";

    /// <summary>功能旗標：L1 人員／車輛辨識。</summary>
    public const string FeatureAiL1 = "ai.l1";

    /// <summary>功能旗標：L2 車牌／人臉辨識。</summary>
    public const string FeatureAiL2 = "ai.l2";

    /// <summary>功能旗標：地圖與 IO。</summary>
    public const string FeatureGis = "gis";

    /// <summary>功能旗標：遠程存取。</summary>
    public const string FeatureRemote = "remote";

    /// <summary>功能旗標：企業 AD／SSO。</summary>
    public const string FeatureAdSso = "ad";

    /// <summary>等級上限（§19.3 客製版 ≤1024）。</summary>
    public const int MaxCameras = 1024;

    /// <summary>六個等級，由低到高。</summary>
    public static IReadOnlyList<LicenseTier> All { get; } =
    [
        new LicenseTier
        {
            Name = "基本版",
            DefaultCameras = 4,
            Features = [FeatureCore, FeatureAi],
        },
        new LicenseTier
        {
            Name = "標準版",
            DefaultCameras = 8,
            Features = [FeatureCore, FeatureAi, FeatureSchedule],
        },
        new LicenseTier
        {
            Name = "專業版",
            DefaultCameras = 16,
            Features = [FeatureCore, FeatureAi, FeatureSchedule, FeatureAiL1],
        },
        new LicenseTier
        {
            Name = "進階版",
            DefaultCameras = 32,
            Features = [FeatureCore, FeatureAi, FeatureSchedule, FeatureAiL1, FeatureGis],
        },
        new LicenseTier
        {
            Name = "企業版",
            DefaultCameras = 64,
            Features =
            [
                FeatureCore, FeatureAi, FeatureSchedule,
                FeatureAiL1, FeatureAiL2, FeatureGis, FeatureRemote, FeatureAdSso,
            ],
        },
        new LicenseTier
        {
            Name = "客製版",
            DefaultCameras = MaxCameras,
            Features =
            [
                FeatureCore, FeatureAi, FeatureSchedule,
                FeatureAiL1, FeatureAiL2, FeatureGis, FeatureRemote, FeatureAdSso,
            ],
        },
    ];

    /// <summary>依名稱查找等級；找不到時回傳 null。</summary>
    public static LicenseTier? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return All.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.Ordinal));
    }

    /// <summary>
    /// 依授權的功能旗標與通道數辨識等級。
    ///
    /// 旗標須**完整等於**某一等級的功能組合才認定為該等級；只要多一項或少一項，
    /// 即視為客製授權而回傳 null。理由：若以「涵蓋」判定，一張只有 core+ai 的 32 路授權
    /// 會被標成「基本版」，讀者會誤以為上限是 4 路。
    ///
    /// 企業版與客製版的功能旗標相同（§19.3），故再以通道數區分。
    /// </summary>
    /// <param name="features">授權的功能旗標。</param>
    /// <param name="cameras">授權通道數；傳入 0 或負值表示不以此條件篩選。</param>
    public static LicenseTier? Match(IEnumerable<string>? features, int cameras = 0)
    {
        if (features is null)
        {
            return null;
        }

        var actual = features.Where(f => !string.IsNullOrWhiteSpace(f)).ToHashSet(StringComparer.Ordinal);
        if (actual.Count == 0)
        {
            return null;
        }

        var candidates = All
            .Where(t => t.Features.Length == actual.Count && t.Features.All(actual.Contains))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        LicenseTier? match = null;
        foreach (var tier in candidates)
        {
            if (cameras > 0 && cameras < tier.DefaultCameras)
            {
                continue;
            }

            match = tier;
        }

        return match;
    }
}
