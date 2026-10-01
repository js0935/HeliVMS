using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 授權功能閘門（M209／§19.4「未授權之等級功能於 UI 隱藏」）。
///
/// 兩層防護：
/// <list type="number">
/// <item><b>隱藏</b>——未授權的入口直接不顯示，使用者不會看到推不開的功能。</item>
/// <item><b>拒絕</b>——事件處理器與 CLI 仍必須檢查，因為鍵盤、自動化與命令列都能繞過隱藏。
/// 只做隱藏等於把安全性寄託在「按不到按鈕」上。</item>
/// </list>
///
/// 判斷一律走 <see cref="LicenseService.Evaluate"/>（唯讀授權快取），故本類別不會寫資料庫。
/// </summary>
public sealed class LicenseUiGate
{
    private readonly LicenseService _license;

    public LicenseUiGate(LicenseService license)
    {
        ArgumentNullException.ThrowIfNull(license);
        _license = license;
    }

    public LicenseUiGate(SqliteStore store)
        : this(new LicenseService(store))
    {
    }

    /// <summary>目前授權結論（每次存取重新評估，故匯入授權後立即生效）。</summary>
    public LicenseApplyResult Current => _license.Evaluate(DateTime.UtcNow);

    /// <summary>是否授權某功能。</summary>
    public bool Allows(string feature) => Current.AllowsFeature(feature);

    /// <summary>拒絕原因（允許時為 null），直接顯示給使用者。</summary>
    public string? DenialMessage(string feature) => Current.FeatureDenialMessage(feature);

    /// <summary>
    /// 依授權隱藏／顯示入口，並在隱藏時補上 ToolTip 說明缺哪個功能——
    /// 使用者日後升級授權時才不會以為是程式故障。
    /// </summary>
    /// <param name="element">入口控制項（Button、MenuItem 等）。</param>
    /// <param name="feature">對應的功能旗標。</param>
    /// <param name="availableToolTip">已授權時顯示的 ToolTip；null 則不清除原有值。</param>
    public void Apply(FrameworkElement element, string feature, string? availableToolTip = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var allowed = Allows(feature);
        element.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        if (allowed)
        {
            if (availableToolTip is not null)
            {
                element.ToolTip = availableToolTip;
            }

            return;
        }

        element.ToolTip = DenialMessage(feature);
    }
}