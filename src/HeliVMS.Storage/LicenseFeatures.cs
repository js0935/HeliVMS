namespace HeliVMS.Storage;

/// <summary>
/// 授權功能旗標（§19.3）與其使用者可讀名稱。
///
/// 旗標常數的權威定義在 <c>HeliVMS.Licensing</c>（<c>LicenseTiers</c>）；此處只補
/// 「顯示用名稱」與「未知旗標」判定，讓桌面端隱藏、CLI 與 WebApi 三邊共用同一句話。
/// </summary>
public static class LicenseFeatures
{
    /// <summary>核心即時監看與全時錄影。</summary>
    public const string Core = "core";

    /// <summary>排程錄影。</summary>
    public const string Schedule = "schedule";

    /// <summary>事件 AI（動作偵測、遮蔽偵測）。</summary>
    public const string Ai = "ai";

    /// <summary>L1 人員／車輛辨識。</summary>
    public const string AiL1 = "ai.l1";

    /// <summary>L2 車牌／人臉辨識。</summary>
    public const string AiL2 = "ai.l2";

    /// <summary>地圖與 IO。</summary>
    public const string Gis = "gis";

    /// <summary>遠程存取（分享、網頁主控台）。</summary>
    public const string Remote = "remote";

    /// <summary>企業 AD／SSO 登入。</summary>
    public const string AdSso = "ad";

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [Core] = "核心監看與錄影",
        [Schedule] = "排程錄影",
        [Ai] = "AI 事件偵測",
        [AiL1] = "L1 人員／車輛辨識",
        [AiL2] = "L2 車牌／人臉辨識",
        [Gis] = "地圖與 IO",
        [Remote] = "遠程存取",
        [AdSso] = "企業 AD／SSO 登入",
    };

    /// <summary>全部旗標，由低階到高階。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Core, Schedule, Ai, AiL1, AiL2, Gis, Remote, AdSso,
    ];

    /// <summary>使用者可讀名稱；未知旗標原樣回傳（不丟例外，方便對照上游新增的旗標）。</summary>
    public static string DisplayName(string? feature)
        => !string.IsNullOrWhiteSpace(feature) && Names.TryGetValue(feature, out var name)
            ? name
            : feature ?? string.Empty;

    /// <summary>是否為本產品已知的旗標。</summary>
    public static bool IsKnown(string? feature)
        => !string.IsNullOrWhiteSpace(feature) && Names.ContainsKey(feature);
}