using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// 遠程 API 的授權合併檢查（M210／§19.4「遠程 API（WebApi）：合併檢查，避免遠程繞過」）。
///
/// 桌面端把未授權功能藏起來（M209），但遠程呼叫者不會看到那些按鈕——直接打 API 就繞過了。
/// 這支中介軟體讓遠端與本機**共用同一個** <see cref="LicenseService"/> 結論：同一張授權，
/// 同一套旗標規則，同一句拒絕理由，遠端沒有比較寬鬆的分支。
///
/// 狀態碼用 403 Forbidden 而非 402 Payment Required：本系統沒有金流語意，
/// 選一個前端既有 SPA 與 <c>fetch</c> 已會處理的既有碼，並在 body 給
/// <c>feature</c>／<c>decision</c>／<c>message</c> 讓呼叫端能分辨該買哪一級。
///
/// <para>
/// 刻意**不**對整個 <c>/api/*</c> 掛 <c>remote</c>：§19.3 的 <c>remote</c> 是「遠程存取
/// （分享、網頁主控台）」，但同一支 WebApi 也把 <c>HeliVMS.Web</c> 當成本機網頁主控台伺服器。
/// 若整個 API 都要求 <c>remote</c>，基本版（core+ai）客戶連自己機器上的網頁主控台都開不了。
/// 因此這裡只擋**旗標對應的能力端點**，把「網頁主控台本身要不要算遠程」留給產品定調——
/// 要改成全面封鎖只需在 <see cref="Rules"/> 加一條 <c>/api</c> 前綴規則。
/// </para>
/// </summary>
public sealed class LicenseGateMiddleware
{
    /// <summary>一律放行的路徑：健康檢查與授權狀態查詢本身不該被授權擋住。</summary>
    private static readonly string[] AlwaysAllowed =
    [
        "/api/health",
        "/api/license",
    ];

    /// <summary>
    /// 路徑前綴 → 所需功能旗標。第一個命中的規則生效，故由長到短排列。
    /// 讀取端點也列在內：<c>/api/recording/schedules</c> 已在 M208 由排程閘門封住開錄路徑，
    /// 但「看得到自己排了哪些排程」不該比「排得到」更寬鬆。
    /// </summary>
    private static readonly (string Prefix, string Feature)[] Rules =
    [
        // M244：即時串流與遠端回放（/api/stream/*）都屬於「遠程存取」。
        // 這條規則同時補上 M239 遠端 HLS 回放一直沒有被閘門保護的缺口。
        ("/api/stream", LicenseFeatures.Remote),
        ("/api/recording/schedules", LicenseFeatures.Schedule),
        ("/api/patrols", LicenseFeatures.Schedule),
        ("/api/detections", LicenseFeatures.Ai),
        ("/api/clip", LicenseFeatures.Ai),
        ("/api/audio", LicenseFeatures.Ai),
        ("/api/shares", LicenseFeatures.Remote),
        ("/api/auth/providers", LicenseFeatures.AdSso),
    ];

    private readonly RequestDelegate _next;
    private readonly LicenseService _license;
    private readonly TimeProvider _clock;

    public LicenseGateMiddleware(RequestDelegate next, LicenseService license, TimeProvider clock)
    {
        _next = next;
        _license = license;
        _clock = clock;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var required = RequiredFeature(context.Request.Path);
        if (required is not null)
        {
            var license = _license.Evaluate(_clock.GetUtcNow().UtcDateTime);
            if (!license.AllowsFeature(required))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = license.FeatureDenialMessage(required),
                    feature = required,
                    featureName = LicenseFeatures.DisplayName(required),
                    decision = license.Decision.ToString(),
                    maxCameras = license.MaxCameras,
                    features = license.Features,
                });
                return;
            }
        }

        await _next(context);
    }

    /// <summary>找出此路徑所需的功能旗標；不需授權則回傳 null。</summary>
    private static string? RequiredFeature(PathString path)
    {
        if (!path.StartsWithSegments("/api"))
        {
            return null;
        }

        foreach (var allowed in AlwaysAllowed)
        {
            if (path.StartsWithSegments(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        foreach (var (prefix, feature) in Rules)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return feature;
            }
        }

        return null;
    }
}
